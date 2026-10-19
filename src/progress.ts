import { Readable } from "stream";
import readline from "node:readline";
import { AsyncLocalStorage } from "node:async_hooks";
import { verbose } from "./verbose.js";

type ProgressContext = {
  workerId?: number;
  fileName?: string;
  fileNumber?: string;
  outputDir?: string;
};

const progressContext = new AsyncLocalStorage<ProgressContext>();

let configuredAgents = 1;

type AgentLine = {
  percentage: number;
  fileName?: string;
  fileNumber?: string;
  lastRenderAt: number;
};

function createRenderMutex() {
  let last = Promise.resolve();
  return async (fn: () => void) => {
    const run = last.then(
      async () => fn(),
      async () => fn()
    );
    last = run.then(
      () => undefined,
      () => undefined
    );
    await run;
  };
}

class MultiLineProgressRenderer {
  private readonly lines: AgentLine[];
  private renderedOnce = false;
  private readonly withRenderLock = createRenderMutex();
  private overallDone = 0;
  private overallTotal = 0;
  private overallLastRenderAt = 0;

  constructor(private readonly agentCount: number) {
    this.lines = Array.from({ length: agentCount }, () => ({
      percentage: 0,
      label: undefined,
      lastRenderAt: 0
    }));
  }

  updateOverall(done: number, total: number) {
    this.overallDone = Math.max(0, Math.floor(done));
    this.overallTotal = Math.max(0, Math.floor(total));

    const now = Date.now();
    if (now - this.overallLastRenderAt < 80 && done !== total) return;
    this.overallLastRenderAt = now;

    void this.withRenderLock(() => this.render());
  }

  update(
    workerId: number,
    percentage: number,
    ctx?: { fileName?: string; fileNumber?: string }
  ) {
    const idx = workerId - 1;
    if (idx < 0 || idx >= this.lines.length) return;

    const now = Date.now();
    const line = this.lines[idx];
    line.percentage = percentage;
    if (ctx?.fileName != null) line.fileName = ctx.fileName;
    if (ctx?.fileNumber != null) line.fileNumber = ctx.fileNumber;

    // Throttle redraws to avoid spamming the terminal.
    if (now - line.lastRenderAt < 80 && percentage !== 1) return;
    line.lastRenderAt = now;

    void this.withRenderLock(() => this.render());
  }

  clear() {
    if (!this.renderedOnce) return;
    const stdout = process.stdout;
    if (!stdout.isTTY) return;

    // Move to first line of the block and clear all lines.
    readline.moveCursor(stdout, 0, -(this.rowCount() - 1));
    for (let i = 0; i < this.rowCount(); i++) {
      readline.clearLine(stdout, 0);
      readline.cursorTo(stdout, 0);
      if (i < this.rowCount() - 1) stdout.write("\n");
    }
    readline.cursorTo(stdout, 0);
  }

  private render() {
    const stdout = process.stdout;
    if (!stdout.isTTY) return;

    // First render prints the block. Subsequent renders rewrite in-place.
    if (!this.renderedOnce) {
      stdout.write(this.formatOverallLine());
      stdout.write("\n");
      for (let i = 0; i < this.agentCount; i++) {
        stdout.write(this.formatLine(i + 1));
        if (i < this.agentCount - 1) stdout.write("\n");
      }
      this.renderedOnce = true;
      return;
    }

    // Cursor is expected to be at the last line of the block.
    readline.moveCursor(stdout, 0, -(this.rowCount() - 1));

    // Overall row
    readline.clearLine(stdout, 0);
    readline.cursorTo(stdout, 0);
    stdout.write(this.formatOverallLine());
    stdout.write("\n");

    for (let i = 0; i < this.agentCount; i++) {
      readline.clearLine(stdout, 0);
      readline.cursorTo(stdout, 0);
      stdout.write(this.formatLine(i + 1));
      if (i < this.agentCount - 1) stdout.write("\n");
    }
  }

  private rowCount() {
    return this.agentCount + 1;
  }

  private formatOverallLine() {
    const total = this.overallTotal;
    const done = Math.min(this.overallDone, total);
    const pct = total === 0 ? 0 : Math.round((done / total) * 100);

    const percentStr = `${pct}%`.padStart(4, " ");
    const label = `Overall:`.padEnd(11, " ");

    const fileNameWidth = 18;
    const name = "all".padEnd(fileNameWidth, " ");

    const barWidth = 22;
    const filled = total === 0 ? 0 : Math.round((done / total) * barWidth);
    const bar = `[${"#".repeat(filled)}${"-".repeat(barWidth - filled)}]`;

    const fileNumber = `${done}/${total}`.padStart(9, " ");
    return `${label} ${name} ${bar} ${percentStr} ${fileNumber}`;
  }

  private formatLine(workerId: number) {
    const line = this.lines[workerId - 1];

    const pct = Math.max(0, Math.min(100, Math.round(line.percentage * 100)));
    const percentStr = `${pct}%`.padStart(4, " ");

    const agentLabel = `Agent ${workerId}:`.padEnd(11, " ");

    const fileNameWidth = 18;
    const fileNameRaw = line.fileName ?? "";
    const fileName =
      fileNameRaw.length <= fileNameWidth
        ? fileNameRaw.padEnd(fileNameWidth, " ")
        : `${fileNameRaw.slice(0, fileNameWidth - 1)}…`;

    const barWidth = 22;
    const filled = Math.round((pct / 100) * barWidth);
    const bar = `[${"#".repeat(filled)}${"-".repeat(barWidth - filled)}]`;

    const fileNumber = (line.fileNumber ?? "").padStart(9, " ");

    // Requested format (table-aligned):
    // {Agent X}: {fileName} {progress bar} {percent} {file number}
    return `${agentLabel} ${fileName} ${bar} ${percentStr} ${fileNumber}`;
  }
}

let renderer: MultiLineProgressRenderer | null = null;

export function configureProgress({ agents }: { agents: number }) {
  configuredAgents = Math.max(1, Math.floor(agents));

  // Multi-line rendering only when:
  // - multiple agents
  // - interactive terminal
  // - not in verbose mode (verbose uses normal logs)
  if (configuredAgents > 1 && process.stdout.isTTY && !verbose.enabled) {
    renderer = new MultiLineProgressRenderer(configuredAgents);
  } else {
    renderer = null;
  }
}

export function setOverallProgress(done: number, total: number) {
  if (!renderer) return;
  renderer.updateOverall(done, total);
}

export async function runWithProgressContext<T>(
  ctx: ProgressContext,
  fn: () => Promise<T>
): Promise<T> {
  return await progressContext.run(ctx, fn);
}

export function showProgress(stream: Readable) {
  let bytes = 0;
  let i = 0;
  stream.on("data", (data) => {
    bytes += data.length;
    if (i++ % 1000 !== 0) return;
    process.stdout.clearLine?.(0);
    process.stdout.write(`\rDownloaded ${formatBytes(bytes)}`);
  });
}

function formatBytes(numBytes: number) {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let unitIndex = 0;
  while (numBytes > 1024 && unitIndex < units.length) {
    numBytes /= 1024;
    unitIndex++;
  }
  return `${numBytes.toFixed(2)} ${units[unitIndex]}`;
}

export async function showPercentage(percentage: number) {
  const percentageStr = Math.round(percentage * 100);
  const ctx = progressContext.getStore();

  // Persist progress to task state file
  if (ctx?.outputDir && ctx?.workerId !== undefined) {
    try {
      const { saveTaskState, loadTaskState } = await import("./task-state.js");
      const state = await loadTaskState(ctx.outputDir);
      if (state) {
        if (!state.agents) state.agents = {};
        const agent = state.agents[ctx.workerId];
        if (agent) {
          agent.progress = Math.max(0, Math.min(100, percentageStr));
          
          // Also update the task's progress field
          const taskIndex = agent.taskIndex;
          if (typeof taskIndex === 'number' && state.tasks && state.tasks[taskIndex]) {
            state.tasks[taskIndex].progress = Math.max(0, Math.min(100, percentageStr));
          }
          
          await saveTaskState(ctx.outputDir, state);
        }
      }
    } catch {
      // ignore errors writing progress
    }
  }

  if (renderer && ctx?.workerId) {
    renderer.update(ctx.workerId, percentage, {
      fileName: ctx.fileName,
      fileNumber: ctx.fileNumber
    });
    return;
  }

  // Single-agent / non-interactive fallback.
  if (!verbose.enabled) {
    process.stdout.clearLine?.(0);
    process.stdout.cursorTo(0);
    process.stdout.write(`Processing: ${percentageStr}%`);
  } else {
    verbose.log(`Processing: ${percentageStr}%`);
  }
  if (percentage === 1) {
    process.stdout.write("\n");
  }
}
