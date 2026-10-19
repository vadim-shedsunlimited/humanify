import http from "node:http";
import path from "node:path";
import fs from "node:fs/promises";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { loadProfiles, newProfileId, saveProfiles, type WebUiProfile } from "./profiles.js";

type RunState =
  | {
    status: "idle";
  }
  | {
    status: "running";
    id: string;
    startedAt: string;
    command: string;
    args: string[];
    cwd: string;
    pid?: number;
    outputDir: string;
    lines: string[];
    lastActivityAt: string;
  }
  | {
    status: "finished";
    id: string;
    startedAt: string;
    finishedAt: string;
    exitCode: number | null;
    command: string;
    args: string[];
    cwd: string;
    outputDir: string;
    lines: string[];
  };

function nowIso() {
  return new Date().toISOString();
}

function json(res: http.ServerResponse, status: number, obj: unknown) {
  const body = JSON.stringify(obj);
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "content-length": Buffer.byteLength(body)
  });
  res.end(body);
}

function text(res: http.ServerResponse, status: number, body: string, contentType = "text/plain; charset=utf-8") {
  res.writeHead(status, {
    "content-type": contentType,
    "cache-control": "no-store",
    "content-length": Buffer.byteLength(body)
  });
  res.end(body);
}

async function readJsonBody(req: http.IncomingMessage) {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(Buffer.isBuffer(c) ? c : Buffer.from(c));
  const raw = Buffer.concat(chunks).toString("utf8");
  if (!raw.trim()) return null;
  return JSON.parse(raw);
}

function fallbackHtml(relPath: string) {
  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Humanify Web UI</title>
  <style>body{font-family:system-ui,Segoe UI,Helvetica,Arial;margin:24px;color:#333}</style>
</head>
<body>
  <h1>Humanify Web UI</h1>
  <p>Unable to load <code>/${relPath}</code> from disk in this runtime.</p>
  <p>If you run this in a CommonJS environment or with a bundler, ensure the file exists at <code>${relPath}</code>.</p>
</body>
</html>`;
}

function tailLines(lines: string[], max: number) {
  if (lines.length <= max) return lines;
  return lines.slice(lines.length - max);
}

async function summarizeTaskState(outputDir: string) {
  try {
    const statePath = path.join(outputDir, ".humanify.tasks.json");
    const raw = await fs.readFile(statePath, "utf-8");
    const parsed = JSON.parse(raw) as any;
    const tasks: any[] = Array.isArray(parsed?.tasks) ? parsed.tasks : [];
    const total = tasks.length;
    let pending = 0;
    let in_progress = 0;
    let completed = 0;
    let failed = 0;
    let changed = 0;
    for (const t of tasks) {
      switch (t.status) {
        case "pending":
          pending++;
          break;
        case "in_progress":
          in_progress++;
          break;
        case "completed":
          completed++;
          if (t.changed) changed++;
          break;
        case "failed":
          failed++;
          break;
      }
    }

    // Extract active agent info
    const agents: Array<{ workerId: number; file: string; taskPath: string; progress: number }> = [];
    const agentsState = parsed?.agents || {};
    for (const [workerId, agentInfo] of Object.entries(agentsState)) {
      const wid = Number(workerId);
      const info = agentInfo as any;
      if (info && info.file && info.taskPath) {
        // Use the real progress from agent state if available, otherwise estimate from taskIndex
        let progress = 0;
        if (typeof info.progress === 'number') {
          progress = Math.round(info.progress);
        } else if (typeof info.taskIndex === 'number' && total > 0) {
          progress = Math.round((info.taskIndex / total) * 100);
        }
        agents.push({
          workerId: wid,
          file: info.file,
          taskPath: info.taskPath,
          progress
        });
      }
    }
    agents.sort((a, b) => a.workerId - b.workerId);

    return {
      total,
      pending,
      in_progress,
      completed,
      failed,
      changed,
      updatedAt: parsed?.updatedAt,
      agents,
      tasks: tasks.map((t: any) => ({
        taskPath: t.path,
        status: t.status,
        changed: t.changed || false,
        progress: typeof t.progress === 'number' ? Math.round(t.progress) : 0
      }))
    };
  } catch {
    return null;
  }
}

function buildCliArgs(config: WebUiProfile) {
  const args: string[] = [];

  // Use the same CLI interface the user already uses.
  args.push(config.provider);

  if (config.provider === "openai" || config.provider === "gemini" || config.provider === "copilot" || config.provider === "local") {
    if (config.model && config.model.trim().length) {
      args.push("--model", config.model.trim());
    }
  }

  if (config.provider === "openai") {
    if (config.baseURL && config.baseURL.trim().length) {
      args.push("--baseURL", config.baseURL.trim());
    }
    if (config.apiKey && config.apiKey.trim().length) {
      args.push("--apiKey", config.apiKey.trim());
    }
  }

  if (config.provider === "gemini") {
    if (config.apiKey && config.apiKey.trim().length) {
      args.push("--apiKey", config.apiKey.trim());
    }
  }

  if (config.provider === "local") {
    if (config.seed && config.seed.trim().length) {
      args.push("--seed", config.seed.trim());
    }
    if (config.disableGpu) {
      args.push("--disableGpu");
    }
  }

  args.push("--outputDir", config.outputDir);
  args.push("--contextSize", String(config.contextSize));
  args.push("--agents", String(config.agents));

  if (config.resume) args.push("--resume");
  if (config.continueOnError) args.push("--continueOnError");

  // Keep logs simple and consistent for UI.
  args.push("--verbose");

  if (!config.resume) {
    for (const f of config.inputs) args.push(f);
  }

  return args;
}

function findRunner() {
  // Prefer dist build if present (works when installed). Otherwise run TS via tsx.
  const distEntrypoint = path.join(process.cwd(), "dist", "index.mjs");
  const tsEntrypoint = path.join(process.cwd(), "src", "index.ts");
  const tsxCli = path.join(process.cwd(), "node_modules", "tsx", "dist", "cli.mjs");

  return {
    distEntrypoint,
    tsEntrypoint,
    tsxCli
  };
}

async function exists(p: string) {
  try {
    await fs.stat(p);
    return true;
  } catch {
    return false;
  }
}

async function spawnHumanify(config: WebUiProfile): Promise<{ child: ChildProcessWithoutNullStreams; command: string; args: string[]; cwd: string }> {
  const { distEntrypoint, tsEntrypoint, tsxCli } = findRunner();
  const cliArgs = buildCliArgs(config);

  const cwd = process.cwd();
  // If both dist and ts entrypoints exist, prefer the tsx/dev runner when
  // the source (`src/index.ts`) is newer than the built `dist/index.mjs`.
  const hasDist = await exists(distEntrypoint);
  const hasTs = await exists(tsEntrypoint);

  let useDist = hasDist;
  if (hasDist && hasTs) {
    try {
      const distStat = await fs.stat(distEntrypoint);
      const tsStat = await fs.stat(tsEntrypoint);
      if (tsStat.mtimeMs > distStat.mtimeMs) {
        useDist = false;
      }
    } catch {
      // ignore and fall back to default behavior
    }
  }

  if (useDist) {
    const command = process.execPath;
    const args = [distEntrypoint, ...cliArgs];
    const child = spawn(command, args, { cwd, windowsHide: true, shell: false, env: process.env });
    return { child, command, args, cwd };
  }

  // Dev fallback: run via tsx so changes in `src` are picked up immediately.
  const command = process.execPath;
  const args = [tsxCli, tsEntrypoint, ...cliArgs];
  const child = spawn(command, args, { cwd, windowsHide: true, shell: false, env: process.env });
  return { child, command, args, cwd };
}

export async function startWebUiServer(opts: { port: number }) {
  let runState: RunState = { status: "idle" };
  let currentChild: ChildProcessWithoutNullStreams | null = null;

  const server = http.createServer((req, res) => {
    (async () => {
      try {
        const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
        // Serve static files from src/webui (styles, JS, assets)
        const webuiRoot = path.join(process.cwd(), "src", "webui");
        if (req.method === "GET") {
          // map request path to file under webuiRoot, but keep `/` routing below
          const requested = url.pathname === "/" ? "/index.html" : url.pathname;
          // strip leading slash
          const rel = requested.replace(/^\//, "");
          const fullPath = path.join(webuiRoot, rel);
          const normalized = path.normalize(fullPath);
          if (normalized.startsWith(webuiRoot)) {
            try {
              const buf = await fs.readFile(normalized);
              const ext = path.extname(normalized).toLowerCase();
              const mime: Record<string, string> = {
                '.html': 'text/html; charset=utf-8',
                '.css': 'text/css; charset=utf-8',
                '.js': 'application/javascript; charset=utf-8',
                '.json': 'application/json; charset=utf-8',
                '.map': 'application/octet-stream',
                '.svg': 'image/svg+xml',
                '.png': 'image/png',
                '.jpg': 'image/jpeg',
                '.jpeg': 'image/jpeg',
                '.gif': 'image/gif',
                '.woff': 'font/woff',
                '.woff2': 'font/woff2',
                '.ttf': 'font/ttf'
              };
              const ct = mime[ext] || 'application/octet-stream';
              res.writeHead(200, { 'content-type': ct, 'cache-control': 'no-store' });
              return res.end(buf);
            } catch (e) {
              // continue to other handlers (falls back to special / route and APIs)
            }
          }
        }

        if (req.method === "GET" && url.pathname === "/") {
          const relPath = path.join("src", "webui", "index.html");
          const full = path.join(process.cwd(), relPath);
          try {
            const html = await fs.readFile(full, "utf8");
            return text(res, 200, html, "text/html; charset=utf-8");
          } catch (err) {
            // Fallback when the file isn't available at runtime
            return text(res, 200, fallbackHtml(relPath), "text/html; charset=utf-8");
          }
        }

        if (req.method === "GET" && url.pathname === "/api/profiles") {
          const profiles = await loadProfiles();
          // Normalize input paths for the UI: trim and remove surrounding quotes
          const normalized = (profiles || []).map(function (p) {
            const inputs = Array.isArray(p.inputs)
              ? p.inputs.map(function (s) { return String(s).trim().replace(/^"(.*)"$/, '$1').replace(/^'(.*)'$/, '$1'); })
              : [];
            return { ...p, inputs };
          });
          return json(res, 200, { profiles: normalized });
        }

        if (req.method === "POST" && url.pathname === "/api/profiles") {
          const body = (await readJsonBody(req)) as any;
          const incoming = body?.profile as Partial<WebUiProfile> | undefined;
          if (!incoming) return json(res, 400, { error: "Missing profile" });

          const profiles = await loadProfiles();

          const id = (incoming.id && String(incoming.id)) || newProfileId();
          const normalized: WebUiProfile = {
            id,
            name: String(incoming.name ?? "Untitled"),
            provider: (incoming.provider as any) ?? "copilot",
            inputs: Array.isArray(incoming.inputs)
              ? incoming.inputs.map(function (s) { return String(s).trim().replace(/^"(.*)"$/, '$1').replace(/^'(.*)'$/, '$1'); })
              : [],
            outputDir: String(incoming.outputDir ?? "").trim(),
            resume: !!incoming.resume,
            continueOnError: !!incoming.continueOnError,
            agents: Math.max(1, Math.floor(Number(incoming.agents ?? 1))),
            contextSize: Math.max(0, Math.floor(Number(incoming.contextSize ?? 0))),
            model: incoming.model ? String(incoming.model) : undefined,
            apiKey: incoming.apiKey ? String(incoming.apiKey) : undefined,
            baseURL: incoming.baseURL ? String(incoming.baseURL) : undefined,
            seed: incoming.seed ? String(incoming.seed) : undefined,
            disableGpu: !!incoming.disableGpu
          };

          const existingIdx = profiles.findIndex((p) => p.id === id);
          if (existingIdx >= 0) profiles[existingIdx] = normalized;
          else profiles.push(normalized);

          await saveProfiles(profiles);
          return json(res, 200, { profiles, selectedId: id });
        }

        if (req.method === "DELETE" && url.pathname.startsWith("/api/profiles/")) {
          const id = decodeURIComponent(url.pathname.slice("/api/profiles/".length));
          const profiles = await loadProfiles();
          const next = profiles.filter((p) => p.id !== id);
          await saveProfiles(next);
          return json(res, 200, { profiles: next });
        }

        if (req.method === "POST" && url.pathname === "/api/run/start") {
          if (runState.status === "running") {
            return json(res, 409, { error: "A run is already in progress" });
          }

          const body = (await readJsonBody(req)) as any;
          const cfg = body?.config as Partial<WebUiProfile> | undefined;
          if (!cfg) return json(res, 400, { error: "Missing config" });

          const outputDir = String(cfg.outputDir ?? "").trim();
          if (!outputDir) return json(res, 400, { error: "outputDir is required" });

          const resume = !!cfg.resume;
          const inputs = Array.isArray(cfg.inputs)
            ? cfg.inputs.map(function (s) { return String(s).trim().replace(/^"(.*)"$/, '$1').replace(/^'(.*)'$/, '$1'); }).filter((s) => s.trim())
            : [];
          if (!resume && inputs.length === 0) {
            return json(res, 400, { error: "At least one input file is required unless Resume is enabled" });
          }

          const profile: WebUiProfile = {
            id: cfg.id ? String(cfg.id) : newProfileId(),
            name: String(cfg.name ?? "Untitled"),
            provider: (cfg.provider as any) ?? "copilot",
            inputs,
            outputDir,
            resume,
            continueOnError: !!cfg.continueOnError,
            agents: Math.max(1, Math.floor(Number(cfg.agents ?? 1))),
            contextSize: Math.max(0, Math.floor(Number(cfg.contextSize ?? 0))),
            model: cfg.model ? String(cfg.model) : undefined,
            apiKey: cfg.apiKey ? String(cfg.apiKey) : undefined,
            baseURL: cfg.baseURL ? String(cfg.baseURL) : undefined,
            seed: cfg.seed ? String(cfg.seed) : undefined,
            disableGpu: !!cfg.disableGpu
          };

          const runId = newProfileId();
          const startedAt = nowIso();

          const { child, command, args, cwd } = await spawnHumanify(profile);
          currentChild = child;

          const lines: string[] = [];
          const pushLine = (line: string) => {
            const ts = nowIso();
            lines.push(`[${ts}] ${line}`);
            if (lines.length > 2000) lines.splice(0, lines.length - 2000);
          };

          child.stdout.setEncoding("utf8");
          child.stderr.setEncoding("utf8");

          child.stdout.on("data", (chunk: string) => {
            for (const l of chunk.split(/\\r?\\n/)) {
              if (l.trim().length) pushLine(l);
            }
            if (runState.status === "running") runState.lastActivityAt = nowIso();
          });
          child.stderr.on("data", (chunk: string) => {
            for (const l of chunk.split(/\\r?\\n/)) {
              if (l.trim().length) pushLine(`stderr: ${l}`);
            }
            if (runState.status === "running") runState.lastActivityAt = nowIso();
          });

          child.on("close", (code) => {
            const finishedAt = nowIso();
            const finalLines = [...lines];
            runState = {
              status: "finished",
              id: runId,
              startedAt,
              finishedAt,
              exitCode: code,
              command,
              args,
              cwd,
              outputDir,
              lines: finalLines
            };
            currentChild = null;
          });

          runState = {
            status: "running",
            id: runId,
            startedAt,
            command,
            args,
            cwd,
            pid: child.pid,
            outputDir,
            lines,
            lastActivityAt: nowIso()
          };

          const warning = resume && inputs.length ? "Resume is enabled: input files are ignored (processing outputDir only)." : undefined;
          return json(res, 200, { runId, warning });
        }

        if (req.method === "POST" && url.pathname === "/api/run/stop") {
          if (runState.status !== "running" || !currentChild) {
            return json(res, 200, { ok: true, message: "No run in progress" });
          }

          // Try graceful first.
          try {
            currentChild.kill("SIGINT");
          } catch {
            try {
              currentChild.kill();
            } catch {
              // ignore
            }
          }

          return json(res, 200, { ok: true });
        }

        if (req.method === "GET" && url.pathname === "/api/run/status") {
          if (runState.status === "idle") {
            return json(res, 200, { status: runState, logs: [] });
          }

          const logs = tailLines(runState.lines, 200);
          const outputDir = (runState as any).outputDir as string | undefined;
          const taskState = outputDir ? await summarizeTaskState(outputDir) : null;
          return json(res, 200, {
            status: { ...runState, taskState },
            logs
          });
        }

        if (req.method === "GET" && url.pathname.startsWith("/api/diff")) {
          const query = new URLSearchParams(url.search);
          let outputDir = query.get("outputDir");
          let taskPath = query.get("taskPath");

          // Support simple filename query like /api/diff?7.js
          if (!taskPath && !outputDir) {
            // Get the first query parameter as filename
            const searchParams = url.search.substring(1);
            if (searchParams && !searchParams.includes("=")) {
              const filename = decodeURIComponent(searchParams);

              // Try to find the file in the current run state's outputDir
              if (runState.status === "running" || runState.status === "finished") {
                outputDir = runState.outputDir;

                try {
                  const statePath = path.join(outputDir, ".humanify.tasks.json");
                  const raw = await fs.readFile(statePath, "utf-8");
                  const parsed = JSON.parse(raw) as any;
                  const tasks = Array.isArray(parsed?.tasks) ? parsed.tasks : [];

                  // Find task matching the filename
                  // Try multiple matching strategies:
                  // 1. Exact taskPath match
                  // 2. Filename match (last part of path)
                  // 3. Ends with filename (handles paths like "dir/7.js")
                  let matchedTask = tasks.find((t: any) => t.path === filename);

                  if (!matchedTask) {
                    matchedTask = tasks.find((t: any) => {
                      const taskFileName = t.path ? t.path.split(/[\\\/]/).pop() : "";
                      return taskFileName === filename;
                    });
                  }

                  if (!matchedTask) {
                    matchedTask = tasks.find((t: any) => {
                      return t.path && (t.path.endsWith(filename) || t.path.endsWith("/" + filename) || t.path.endsWith("\\" + filename));
                    });
                  }

                  if (matchedTask) {
                    taskPath = matchedTask.path;
                  } else {
                    const taskList = tasks.map((t: any) => `${t.path || "(empty)"}`).join(", ");
                    return json(res, 404, { error: `File not found: ${filename}. Available tasks: ${taskList || "(none)"}` });
                  }
                } catch (e) {
                  const msg = e instanceof Error ? e.message : String(e);
                  return json(res, 400, { error: `Could not read task state: ${msg}` });
                }
              } else {
                return json(res, 400, { error: "No active run" });
              }
            }
          }

          if (!outputDir || !taskPath) {
            return json(res, 400, { error: "Missing outputDir or taskPath" });
          }

          try {
            // Normalize taskPath: remove any leading "originals/" or "modified/" prefixes
            let normalizedTaskPath = taskPath;
            if (normalizedTaskPath.startsWith("originals/")) {
              normalizedTaskPath = normalizedTaskPath.substring("originals/".length);
            } else if (normalizedTaskPath.startsWith("modified/")) {
              normalizedTaskPath = normalizedTaskPath.substring("modified/".length);
            }

            const originalPath = path.join(outputDir, "originals", normalizedTaskPath);
            const modifiedPath = path.join(outputDir, "modified", normalizedTaskPath);

            // Security: ensure paths are within outputDir
            const origNorm = path.normalize(originalPath);
            const modNorm = path.normalize(modifiedPath);
            const outNorm = path.normalize(outputDir);
            if (!origNorm.startsWith(outNorm) || !modNorm.startsWith(outNorm)) {
              return json(res, 403, { error: "Invalid path" });
            }

            // Try to read files, with fallbacks for different directory structures
            let original = "";
            let modified = "";

            try {
              original = await fs.readFile(originalPath, "utf-8");
            } catch {
              // Fallback: try reading from root outputDir if originals/ doesn't exist
              try {
                original = await fs.readFile(path.join(outputDir, taskPath), "utf-8");
              } catch {
                original = "";
              }
            }

            try {
              modified = await fs.readFile(modifiedPath, "utf-8");
            } catch {
              // If no modified version, use original as modified (no changes)
              modified = original;
            }

            return json(res, 200, {
              original,
              modified,
              taskPath
            });
          } catch (e) {
            const msg = e instanceof Error ? e.message : String(e);
            return json(res, 500, { error: msg });
          }
        }

        return json(res, 404, { error: "Not found" });
      } catch (e) {
        const message = e instanceof Error ? (e.stack ?? e.message) : String(e);
        console.error("Request handler error:", message);
        try {
          return json(res, 500, { error: message });
        } catch (sendErr) {
          console.error("Failed to send error response:", sendErr);
          res.writeHead(500, { "content-type": "application/json" });
          res.end(JSON.stringify({ error: "Internal server error" }));
        }
      }
    })().catch((err) => {
      console.error("Unhandled async error in request handler:", err);
      try {
        res.writeHead(500, { "content-type": "application/json" });
        res.end(JSON.stringify({ error: "Internal server error" }));
      } catch (e) {
        console.error("Failed to send error:", e);
      }
    });
  });

  await new Promise<void>((resolve) => {
    server.listen(opts.port, () => resolve());
  });

  return {
    port: opts.port,
    close: async () => {
      await new Promise<void>((resolve, reject) => {
        server.close((err) => (err ? reject(err) : resolve()));
      });
    }
  };
}
