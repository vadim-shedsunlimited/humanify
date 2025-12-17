import fs from "fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { ensureFileExists } from "./file-utils.js";
import { webcrack } from "./plugins/webcrack.js";
import { verbose } from "./verbose.js";
import { configureProgress, runWithProgressContext, setOverallProgress } from "./progress.js";
import {
  getTask,
  initTaskState,
  loadTaskState,
  saveTaskState,
  toRelativeTaskPath,
  upsertTasks
} from "./task-state.js";

type UnminifyOptions = {
  resume?: boolean;
  continueOnError?: boolean;
  agents?: number;
  pipelineId?: string;
};

function sha1HexFromUtf8(text: string) {
  return createHash("sha1").update(text, "utf8").digest("hex");
}

async function sha1HexFromFile(absPath: string) {
  const buf = await fs.readFile(absPath);
  return createHash("sha1").update(buf).digest("hex");
}

function createMutex() {
  let last = Promise.resolve();
  return async <T>(fn: () => Promise<T>) => {
    const run = last.then(fn, fn);
    last = run.then(
      () => undefined,
      () => undefined
    );
    return run;
  };
}

function sortExtractedFiles(files: { path: string }[]) {
  return [...files].sort((a, b) => {
    const aBase = path.basename(a.path);
    const bBase = path.basename(b.path);
    const aNum = aBase.match(/^(\d+)\.js$/)?.[1];
    const bNum = bBase.match(/^(\d+)\.js$/)?.[1];
    if (aNum && bNum) return Number(aNum) - Number(bNum);
    return aBase.localeCompare(bBase);
  });
}

async function listOutputDirJsFilesRecursive(outputDir: string) {
  const found: { path: string }[] = [];

  const walk = async (dir: string) => {
    const entries = await fs.readdir(dir, { withFileTypes: true });
    for (const ent of entries) {
      const abs = path.join(dir, ent.name);

      if (ent.isDirectory()) {
        // Avoid recursing into hidden/system-ish dirs (best-effort).
        if (ent.name === "node_modules" || ent.name.startsWith(".")) continue;
        await walk(abs);
        continue;
      }

      if (!ent.isFile()) continue;
      if (!ent.name.endsWith(".js")) continue;
      if (ent.name === ".humanify.tasks.json") continue;

      found.push({ path: abs });
    }
  };

  await walk(outputDir);
  return found;
}

export async function unminify(
  input: string | string[],
  outputDir: string,
  plugins: ((code: string) => Promise<string>)[] = [],
  options: UnminifyOptions = {}
) {
  const resume = options.resume ?? false;
  const continueOnError = options.continueOnError ?? false;
  const agents = Math.max(1, Math.floor(options.agents ?? 1));
  const pipelineId = options.pipelineId;

  const withStateLock = createMutex();

  let processedThisRun = 0;
  let updatedThisRun = 0;
  let unchangedThisRun = 0;
  let failedThisRun = 0;
  let totalJobsThisRun = 0;

  const printRunSummary = (reason?: string) => {
    const suffix = reason ? ` (${reason})` : "";
    // Ensure we print after any multi-line progress block.
    try {
      process.stdout.write("\n");
    } catch {
      // ignore
    }
    console.log(
      `Run summary${suffix}: processed ${processedThisRun}/${totalJobsThisRun}, updated ${updatedThisRun}, unchanged ${unchangedThisRun}, failed ${failedThisRun}`
    );
  };

  let cancellationHandled = false;
  const onSigInt = () => {
    if (cancellationHandled) return;
    cancellationHandled = true;
    abort = true;
    printRunSummary("cancelled");
    process.exit(130);
  };
  const onSigTerm = () => {
    if (cancellationHandled) return;
    cancellationHandled = true;
    abort = true;
    printRunSummary("terminated");
    process.exit(143);
  };

  process.once("SIGINT", onSigInt);
  process.once("SIGTERM", onSigTerm);

  configureProgress({ agents });

  await fs.mkdir(outputDir, { recursive: true });

  const inputFiles = Array.isArray(input) ? input : [input];

  if (resume && inputFiles.some((f) => String(f).trim().length > 0)) {
    console.warn(
      "Warning: --resume was set, so input bundle files are ignored. " +
      "Resume mode only processes already-extracted .js modules in --outputDir."
    );
  }

  // In non-resume mode, we will extract into outputDir.
  // Avoid overwriting an existing run's output (which would revert already-processed files).
  if (!resume) {
    if (inputFiles.length === 0 || inputFiles.every((f) => !String(f).trim())) {
      throw new Error(
        "No input files provided. Provide one or more minified bundle files, or use --resume with an existing --outputDir."
      );
    }

    const existingState = await loadTaskState(outputDir);
    if (existingState) {
      throw new Error(
        `Refusing to extract into an outputDir that already contains .humanify.tasks.json: ${outputDir}. ` +
        `Use --resume to continue, or choose a new --outputDir (or delete the existing one).`
      );
    }

    // Also refuse when there are already extracted .js files present.
    const existingJs = await listOutputDirJsFilesRecursive(outputDir);
    if (existingJs.length > 0) {
      throw new Error(
        `Refusing to extract into a non-empty outputDir (found ${existingJs.length} .js files): ${outputDir}. ` +
        `Use --resume to process existing extracted modules, or choose a new --outputDir.`
      );
    }
  }

  let extractedFiles: { path: string }[] = [];
  const existingState = await loadTaskState(outputDir);

  if (resume) {
    const originalsDir = path.join(outputDir, "originals");
    extractedFiles = await listOutputDirJsFilesRecursive(originalsDir);

    // If we can't discover files on disk (or originals dir was empty), fall back to
    // the task-state list. This makes resume robust and prevents accidental 0/0 no-ops.
    if (extractedFiles.length === 0 && existingState?.tasks?.length) {
      extractedFiles = existingState.tasks.map((t) => ({
        path: path.join(outputDir, "originals", t.path)
      }));
    }
  } else {
    // Extract all bundles into originals/ subdirectory.
    const originalsDir = path.join(outputDir, "originals");
    await fs.mkdir(originalsDir, { recursive: true });

    for (const file of inputFiles) {
      const trimmed = String(file).trim();
      if (!trimmed) continue;
      ensureFileExists(trimmed);
      const bundledCode = await fs.readFile(trimmed, "utf-8");
      await webcrack(bundledCode, originalsDir);
    }

    extractedFiles = await listOutputDirJsFilesRecursive(originalsDir);
  }

  extractedFiles = sortExtractedFiles(extractedFiles);
  const taskPaths = extractedFiles.map((f) => toRelativeTaskPath(outputDir, f.path));

  if (resume && extractedFiles.length === 0) {
    throw new Error(
      `--resume was set but no extracted .js modules were found in outputDir: ${outputDir}. ` +
      `Either point --outputDir at an existing extraction output, or run once without --resume to extract first.`
    );
  }

  const state = existingState ?? initTaskState(taskPaths, resume ? undefined : inputFiles[0]);
  upsertTasks(state, taskPaths);

  // Pipeline invalidation:
  // If the caller provides a pipeline id, persist a hash of it and re-queue already
  // completed/failed tasks when it changes (or when missing in older state files).
  if (pipelineId != null) {
    const now = new Date().toISOString();
    const pipelineSha1 = sha1HexFromUtf8(JSON.stringify({ id: pipelineId }));
    const previousSha1 = state.pipeline?.sha1;

    if (previousSha1 == null || previousSha1 !== pipelineSha1) {
      for (const t of state.tasks) {
        if (t.status === "completed" || t.status === "failed" || t.status === "in_progress") {
          t.status = "pending";
          t.lastError = "Pipeline changed; re-queued";
        }
      }
    }

    state.pipeline = { id: pipelineId, sha1: pipelineSha1, updatedAt: now };
  }

  // Resume integrity check:
  // If a task is marked completed but the file is missing or doesn't match the
  // expected output (by size and/or content hash), re-queue it.
  for (const t of state.tasks) {
    if (t.status !== "completed") continue;

    // If we don't have an output hash for a completed task, we can't reliably verify it.
    // Re-queue once so we reprocess and store both input+output hashes.
    if (t.outputSha1 == null) {
      t.status = "pending";
      t.lastError = "Completed task missing output hash; re-queued for verification";
      continue;
    }

    const absPath = path.join(outputDir, t.path);
    try {
      const st = await fs.stat(absPath);
      if (!st.isFile()) {
        t.status = "pending";
        t.lastError = "Completed task points to non-file; re-queued";
        continue;
      }

      // For older task-state files, learn the size so future resumes can verify.
      if (t.outputBytes == null) {
        t.outputBytes = st.size;
      } else if (st.size !== t.outputBytes) {
        t.status = "pending";
        t.lastError = `Output size mismatch (expected ${t.outputBytes}, got ${st.size}); re-queued`;
        continue;
      }

      const actualSha1 = await sha1HexFromFile(absPath);
      if (actualSha1 !== t.outputSha1) {
        t.status = "pending";
        if (t.inputSha1 && actualSha1 === t.inputSha1) {
          t.lastError =
            `Output hash mismatch and file matches the recorded input (expected ${t.outputSha1}); re-queued`;
        } else {
          t.lastError = `Output hash mismatch (expected ${t.outputSha1}, got ${actualSha1}); re-queued`;
        }
      }
    } catch {
      t.status = "pending";
      t.lastError = "Completed task output file missing; re-queued";
    }
  }

  await withStateLock(() => saveTaskState(outputDir, state));

  const countProcessed = () =>
    state.tasks.filter((t) => t.status === "completed" || t.status === "failed").length;
  setOverallProgress(countProcessed(), extractedFiles.length);

  const jobs = extractedFiles.map((file, index) => {
    const relTaskPath = toRelativeTaskPath(outputDir, file.path);
    return { file, index, relTaskPath };
  });
  totalJobsThisRun = jobs.length;

  let cursor = 0;
  let abort = false;
  let firstError: unknown;

  const worker = async (workerId: number) => {
    while (true) {
      if (abort) return;

      const myIndex = cursor++;
      if (myIndex >= jobs.length) return;

      const job = jobs[myIndex];
      const task = getTask(state, job.relTaskPath);

      if (task?.status === "completed") {
        verbose.log(`Skipping already completed file ${job.file.path}`);
        continue;
      }

      processedThisRun += 1;

      const progressFileName = path.basename(job.relTaskPath);
      const progressFileNumber = `${job.index + 1}/${extractedFiles.length}`;

      // In multi-agent non-verbose mode, per-agent progress is handled by the renderer.
      // Printing additional lines will garble the progress block.
      if (!(agents > 1 && !verbose.enabled)) {
        console.log(
          `Processing file ${job.index + 1}/${extractedFiles.length}: ${job.relTaskPath}`
        );
      }

      if (task) {
        task.status = "in_progress";
        task.attempts += 1;
        task.lastStartedAt = new Date().toISOString();
        task.lastError = undefined;
        task.skippedReason = undefined;

        // Track active agent work
        if (!state.agents) state.agents = {};
        state.agents[workerId] = {
          file: job.file.path,
          taskPath: job.relTaskPath,
          taskIndex: myIndex,
          progress: 0
        };
        await withStateLock(() => saveTaskState(outputDir, state));
      }

      try {
        const code = await fs.readFile(job.file.path, "utf-8");
        const inputSha1 = sha1HexFromUtf8(code);

        if (code.trim().length === 0) {
          verbose.log(`Skipping empty file ${job.file.path}`);
          if (task) {
            task.status = "completed";
            task.skippedReason = "empty";
            task.inputSha1 = sha1HexFromUtf8("");
            task.changed = false;
            task.outputBytes = 0;
            task.outputSha1 = sha1HexFromUtf8("");
            task.lastFinishedAt = new Date().toISOString();

            // Clear agent state
            if (state.agents) delete state.agents[workerId];
            await withStateLock(() => saveTaskState(outputDir, state));
            setOverallProgress(countProcessed(), extractedFiles.length);
          }
          continue;
        }

        const formattedCode = await runWithProgressContext(
          {
            workerId,
            fileName: progressFileName,
            fileNumber: progressFileNumber,
            outputDir
          },
          async () =>
            await plugins.reduce(
              (p, next) => p.then(next),
              Promise.resolve(code)
            )
        );

        verbose.log("Input: ", code);
        verbose.log("Output: ", formattedCode);

        // Write to modified/ subdirectory instead of back to originals/
        const modifiedDir = path.join(outputDir, "modified");
        await fs.mkdir(modifiedDir, { recursive: true });
        const modifiedPath = path.join(modifiedDir, path.relative(path.join(outputDir, "originals"), job.file.path));
        await fs.mkdir(path.dirname(modifiedPath), { recursive: true });
        await fs.writeFile(modifiedPath, formattedCode);

        if (task) {
          const outputSha1 = sha1HexFromUtf8(formattedCode);
          const changed = outputSha1 !== inputSha1;

          task.status = "completed";
          task.inputSha1 = inputSha1;
          task.changed = changed;
          task.outputBytes = Buffer.byteLength(formattedCode, "utf-8");
          task.outputSha1 = outputSha1;
          task.lastFinishedAt = new Date().toISOString();

          // Clear agent state
          if (state.agents) delete state.agents[workerId];
          await withStateLock(() => saveTaskState(outputDir, state));
          setOverallProgress(countProcessed(), extractedFiles.length);

          if (changed) updatedThisRun += 1;
          else unchangedThisRun += 1;
        }
      } catch (e) {
        if (task) {
          task.status = "failed";
          task.lastFinishedAt = new Date().toISOString();
          task.lastError = e instanceof Error ? (e.stack ?? e.message) : String(e);

          // Clear agent state
          if (state.agents) delete state.agents[workerId];
          await withStateLock(() => saveTaskState(outputDir, state));
          setOverallProgress(countProcessed(), extractedFiles.length);
        }

        failedThisRun += 1;

        if (!continueOnError) {
          abort = true;
          firstError = e;
          return;
        }
      }
    }
  };

  const workerCount = Math.min(agents, jobs.length);
  try {
    await Promise.all(
      Array.from({ length: workerCount }, (_, i) => worker(i + 1))
    );

    if (firstError) {
      printRunSummary("error");
      throw firstError;
    }

    console.log(`Done! You can find your unminified code in ${outputDir}`);
    printRunSummary();
  } finally {
    process.off("SIGINT", onSigInt);
    process.off("SIGTERM", onSigTerm);
  }
}
