import fs from "node:fs/promises";
import { existsSync } from "node:fs";
import path from "node:path";

export type TaskStatus = "pending" | "in_progress" | "completed" | "failed";

export type TaskRecord = {
    path: string; // path relative to outputDir, normalized with '/'
    status: TaskStatus;
    attempts: number;
    lastError?: string;
    lastStartedAt?: string;
    lastFinishedAt?: string;
    skippedReason?: string;
    inputSha1?: string;
    changed?: boolean;
    outputBytes?: number;
    outputSha1?: string;
    progress?: number; // 0-100 percentage
};

export type TaskState = {
    version: 1;
    createdAt: string;
    updatedAt: string;
    inputFile?: string;
    pipeline?: {
        id?: string;
        sha1: string;
        updatedAt: string;
    };
    tasks: TaskRecord[];
    agents?: {
        [workerId: number]: {
            file: string;
            taskPath: string;
            taskIndex: number; // 0-based index in the jobs list
            progress: number; // 0-100 percentage
        };
    };
};

const STATE_FILENAME = ".humanify.tasks.json";

export function statePath(outputDir: string) {
    return path.join(outputDir, STATE_FILENAME);
}

function normalizeRelativePath(p: string) {
    return p.replace(/\\/g, "/");
}

export function toRelativeTaskPath(outputDir: string, absoluteFilePath: string) {
    const rel = path.relative(outputDir, absoluteFilePath);
    return normalizeRelativePath(rel);
}

export async function loadTaskState(outputDir: string): Promise<TaskState | null> {
    const filePath = statePath(outputDir);
    if (!existsSync(filePath)) return null;

    try {
        const raw = await fs.readFile(filePath, "utf-8");
        const parsed = JSON.parse(raw) as TaskState;
        if (!parsed || parsed.version !== 1 || !Array.isArray(parsed.tasks)) {
            return null;
        }
        return parsed;
    } catch {
        return null;
    }
}

export async function saveTaskState(outputDir: string, state: TaskState) {
    const filePath = statePath(outputDir);
    state.updatedAt = new Date().toISOString();

    // Use a unique tmp file name to avoid collisions with other processes
    // and to reduce the chance of transient locks causing persistent failure.
    const tmpPath = `${filePath}.tmp-${process.pid}-${Date.now()}-${Math.random()
        .toString(16)
        .slice(2)}`;

    await fs.writeFile(tmpPath, JSON.stringify(state, null, 2), "utf-8");

    const maxAttempts = 10;
    let lastErr: unknown;

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
        try {
            // Best case: atomic-ish move into place.
            await fs.rename(tmpPath, filePath);
            return;
        } catch (err) {
            lastErr = err;
            const code = (err as any)?.code as string | undefined;

            // On Windows, rename over an existing file can fail with EPERM/EACCES,
            // often due to AV/indexers briefly holding the destination.
            if (code === "EPERM" || code === "EACCES" || code === "EBUSY" || code === "EEXIST") {
                try {
                    await fs.rm(filePath, { force: true });
                } catch {
                    // ignore; we'll retry
                }

                // Backoff with a small cap.
                const delayMs = Math.min(500, 25 * attempt * attempt);
                await new Promise((r) => setTimeout(r, delayMs));
                continue;
            }

            break;
        }
    }

    // Final fallback: attempt to write directly in place (non-atomic), then cleanup tmp.
    try {
        await fs.writeFile(filePath, JSON.stringify(state, null, 2), "utf-8");
        await fs.rm(tmpPath, { force: true });
        return;
    } catch {
        // Ensure tmp doesn't accumulate if we can.
        try {
            await fs.rm(tmpPath, { force: true });
        } catch {
            // ignore
        }
    }

    const detail = lastErr instanceof Error ? (lastErr.stack ?? lastErr.message) : String(lastErr);
    throw new Error(
        `Failed to persist task state file after ${maxAttempts} attempts. ` +
            `This is usually caused by another process temporarily locking the file (OneDrive, antivirus, indexer).\n` +
            `State file: ${filePath}\n` +
            `Last error: ${detail}`
    );
}

export function initTaskState(taskPaths: string[], inputFile?: string): TaskState {
    const now = new Date().toISOString();
    return {
        version: 1,
        createdAt: now,
        updatedAt: now,
        inputFile,
        tasks: taskPaths.map((p) => ({
            path: normalizeRelativePath(p),
            status: "pending",
            attempts: 0
        }))
    };
}

export function upsertTasks(state: TaskState, taskPaths: string[]) {
    const existing = new Set(state.tasks.map((t) => t.path));
    for (const p of taskPaths) {
        const normalized = normalizeRelativePath(p);
        if (!existing.has(normalized)) {
            state.tasks.push({ path: normalized, status: "pending", attempts: 0 });
            existing.add(normalized);
        }
    }
}

export function getTask(state: TaskState, taskPath: string) {
    const normalized = normalizeRelativePath(taskPath);
    return state.tasks.find((t) => t.path === normalized);
}
