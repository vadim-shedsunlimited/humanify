import { spawn } from "node:child_process";
import fs from "node:fs";
import { dirname, extname, join } from "node:path";
import { visitAllIdentifiers } from "./local-llm-rename/visit-all-identifiers.js";
import { showPercentage } from "../progress.js";
import { verbose } from "../verbose.js";

let resolvedCopilotBin: string | undefined;

function createConcurrencyLimiter(maxConcurrency: number) {
    const max = Math.max(1, Math.floor(maxConcurrency));
    let active = 0;
    const queue: Array<() => void> = [];

    const acquire = async () => {
        if (active < max) {
            active += 1;
            return;
        }
        await new Promise<void>((resolve) => queue.push(resolve));
        active += 1;
    };

    const release = () => {
        active = Math.max(0, active - 1);
        const next = queue.shift();
        if (next) next();
    };

    return async <T>(fn: () => Promise<T>): Promise<T> => {
        await acquire();
        try {
            return await fn();
        } finally {
            release();
        }
    };
}

const copilotConcurrency = (() => {
    const raw = process.env.HUMANIFY_COPILOT_CONCURRENCY;
    const parsed = raw ? Number(raw) : NaN;
    if (Number.isFinite(parsed) && parsed > 0) return Math.floor(parsed);
    // Copilot CLI tends to be fragile under parallel load (especially on Windows).
    return process.platform === "win32" ? 1 : 2;
})();

const withCopilotLimit = createConcurrencyLimiter(copilotConcurrency);

async function resolveCopilotBin(): Promise<string> {
    if (resolvedCopilotBin) return resolvedCopilotBin;

    // Allow override for environments where `copilot` isn't on PATH for child processes.
    const override = process.env.HUMANIFY_COPILOT_BIN ?? process.env.COPILOT_BIN;
    if (override && override.trim().length > 0) {
        resolvedCopilotBin = override.trim();
        return resolvedCopilotBin;
    }

    if (process.platform !== "win32") {
        resolvedCopilotBin = "copilot";
        return resolvedCopilotBin;
    }

    // On Windows, `copilot` is often installed as a shim (.cmd) and may not be directly spawnable
    // unless we resolve it to an absolute path.
    const whereOutput = await new Promise<string>((resolve, reject) => {
        const child = spawn("where.exe", ["copilot"], {
            shell: false,
            windowsHide: true,
            env: process.env
        });

        let stdout = "";
        let stderr = "";
        child.stdout.setEncoding("utf8");
        child.stderr.setEncoding("utf8");
        child.stdout.on("data", (c) => (stdout += c));
        child.stderr.on("data", (c) => (stderr += c));
        child.on("error", reject);
        child.on("close", (code) => {
            if (code === 0) return resolve(stdout);
            reject(new Error(stderr.trim() || `where.exe exited with code ${code}`));
        });
    });

    const candidates = whereOutput
        .split(/\r?\n/)
        .map((s) => s.trim())
        .filter(Boolean);

    // Prefer actual executables or more spawn-friendly shims.
    // (VS Code may install a .bat/.ps1 bootstrapper; npm often provides a .cmd shim.)
    const byPreference = (a: string, b: string) => {
        const score = (p: string) => {
            const e = extname(p).toLowerCase();
            if (e === ".exe") return 0;
            if (e === ".cmd") return 1;
            if (e === ".bat") return 2;
            if (e === ".ps1") return 3;
            return 4;
        };
        return score(a) - score(b);
    };

    const preferred = candidates.sort(byPreference)[0];

    resolvedCopilotBin = preferred ?? "copilot";
    return resolvedCopilotBin;
}

function extractJsonObject(text: string): unknown {
    const start = text.indexOf("{");
    const end = text.lastIndexOf("}");
    if (start === -1 || end === -1 || end <= start) {
        throw new Error("Response did not contain a JSON object");
    }
    return JSON.parse(text.slice(start, end + 1));
}

function parseRenameResponse(content: string): string {
    let parsed: any; // eslint-disable-line @typescript-eslint/no-explicit-any
    try {
        parsed = JSON.parse(content);
    } catch {
        parsed = extractJsonObject(content);
    }

    const newName = parsed?.newName ?? parsed?.new_name ?? parsed?.name;
    if (typeof newName !== "string" || newName.length === 0) {
        throw new Error("Rename response missing `newName`");
    }
    return newName;
}

async function runCopilotPrompt(opts: {
    prompt: string;
    model?: string;
}): Promise<string> {
    const args: string[] = [
        "-p",
        opts.prompt,
        "--silent",
        "--stream",
        "off",
        "--no-color",
        "--log-level",
        "error",
        // Copilot CLI requires explicit tool approval for non-interactive mode.
        // We also disable built-in MCPs and custom instructions to avoid surprises.
        "--allow-all-tools",
        "--disable-builtin-mcps",
        "--no-custom-instructions",
        "--disallow-temp-dir"
    ];

    if (opts.model) {
        args.push("--model", opts.model);
    }

    const copilotBin = await resolveCopilotBin();

    const spawnCommandAndArgs = (() => {
        // On Windows, `copilot` is often a shim script (e.g. .bat/.cmd/.ps1). These cannot be spawned
        // directly with `shell: false`, so we explicitly invoke the appropriate host.
        if (process.platform !== "win32") {
            return { command: copilotBin, args };
        }

        const ext = extname(copilotBin).toLowerCase();

        // If we resolved an npm-style .cmd shim, run its JS entrypoint directly with node.exe.
        // This avoids cmd.exe parsing special characters (e.g. &&, |, <, >) inside the prompt.
        if (ext === ".cmd") {
            const dp0 = dirname(copilotBin);
            const entry = join(dp0, "node_modules", "@github", "copilot", "index.js");
            if (fs.existsSync(entry)) {
                return { command: process.execPath, args: [entry, ...args] };
            }
        }

        if (ext === ".cmd" || ext === ".bat") {
            // Fallback for unusual installs: invoke via cmd.exe.
            // Note: cmd.exe will interpret special characters unless properly quoted/escaped,
            // so the .cmd direct node path above is strongly preferred.
            return {
                command: "cmd.exe",
                args: ["/d", "/s", "/c", copilotBin, ...args]
            };
        }

        if (ext === ".ps1") {
            return {
                command: "powershell.exe",
                args: [
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    copilotBin,
                    ...args
                ]
            };
        }

        return { command: copilotBin, args };
    })();

    return await withCopilotLimit(async () =>
        await new Promise<string>((resolve, reject) => {
            const child = spawn(spawnCommandAndArgs.command, spawnCommandAndArgs.args, {
                shell: false,
                windowsHide: true,
                env: {
                    ...process.env,
                    // Some environments rely on this instead of the flag.
                    COPILOT_ALLOW_ALL: process.env.COPILOT_ALLOW_ALL ?? "1"
                }
            });

            let stdout = "";
            let stderr = "";

            child.stdout.setEncoding("utf8");
            child.stderr.setEncoding("utf8");

            child.stdout.on("data", (chunk) => {
                stdout += chunk;
            });

            child.stderr.on("data", (chunk) => {
                stderr += chunk;
            });

            child.on("error", (err) => {
                if (verbose.enabled) {
                    verbose.log(`Failed to spawn copilot binary: ${copilotBin}`, err);
                }
                reject(err);
            });
            child.on("close", (code) => {
                if (code === 0) {
                    resolve(stdout.trim());
                    return;
                }

                const errOut = stderr.trim();
                const out = stdout.trim();
                const detail = [
                    errOut.length ? `stderr: ${errOut}` : "stderr: <empty>",
                    out.length ? `stdout: ${out.slice(0, 4000)}` : "stdout: <empty>",
                    `note: you can set HUMANIFY_COPILOT_CONCURRENCY=${copilotConcurrency} (currently) to reduce flakiness under parallel load`
                ].join("\n");
                reject(new Error(`copilot exited with code ${code}:\n${detail}`));
            });
        })
    );
}

export function copilotRename({
    model,
    contextWindowSize
}: {
    model?: string;
    contextWindowSize: number;
}) {
    const renameCache = new Map<string, string>();

    return async (code: string): Promise<string> => {
        return await visitAllIdentifiers(
            code,
            async (name, surroundingCode) => {
                // Cache to avoid repeated copilot calls for identical prompts.
                const cacheKey = `${model ?? ""}::${name}::${surroundingCode.slice(0, 500)}`;
                const cached = renameCache.get(cacheKey);
                if (cached) return cached;

                const prompt =
                    `Rename the JavaScript identifier "${name}" to a more descriptive, valid JS identifier based on its usage. ` +
                    `Return ONLY a JSON object like {"newName":"someDescriptiveName"}. ` +
                    `Do not include markdown or any extra text.\n\n` +
                    `Context:\n${surroundingCode}`;

                try {
                    const output = await runCopilotPrompt({ prompt, model });
                    const renamed = parseRenameResponse(output);

                    renameCache.set(cacheKey, renamed);
                    return renamed;
                } catch (e) {
                    if (verbose.enabled) {
                        verbose.log(`Copilot rename failed for ${name}; keeping original`, e);
                    }
                    return name;
                }
            },
            contextWindowSize,
            showPercentage
        );
    };
}
