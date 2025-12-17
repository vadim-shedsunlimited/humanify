import { readdir } from "node:fs/promises";
import path from "node:path";
import { spawn } from "node:child_process";
import fs from "node:fs";

async function walk(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  const files = [];

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await walk(fullPath)));
    } else if (entry.isFile()) {
      files.push(fullPath);
    }
  }

  return files;
}

function usage() {
  console.error(
    "Usage: node scripts/run-tests.mjs <unit|e2e|llm|openai|gemini>"
  );
  process.exitCode = 2;
}

const mode = process.argv[2];
if (!mode) {
  usage();
  process.exit(2);
}

const modeToSuffix = {
  unit: ".test.ts",
  e2e: ".e2etest.ts",
  llm: ".llmtest.ts",
  openai: ".openaitest.ts",
  gemini: ".geminitest.ts"
};

const suffix = modeToSuffix[mode];
if (!suffix) {
  usage();
  process.exit(2);
}

const srcDir = path.resolve("src");
const all = await walk(srcDir);
const testFiles = all
  .filter((f) => f.endsWith(suffix))
  .sort((a, b) => a.localeCompare(b));

if (testFiles.length === 0) {
  console.log(`No ${mode} test files found.`);
  process.exit(0);
}

// Keep compatibility with existing CI behavior.
const tsxCli = path.resolve("node_modules", "tsx", "dist", "cli.mjs");
if (!fs.existsSync(tsxCli)) {
  console.error(`Missing tsx CLI at ${tsxCli}. Did you run npm install?`);
  process.exit(1);
}

const args = [tsxCli, "--test", ...testFiles];
if (mode !== "unit") {
  args.push("--test-concurrency=1");
}

const child = spawn(process.execPath, args, {
  stdio: "inherit",
  shell: false,
  env: process.env
});

child.on("exit", (code) => {
  process.exitCode = code ?? 1;
});
