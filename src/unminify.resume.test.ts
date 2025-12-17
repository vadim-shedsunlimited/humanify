import test from "node:test";
import assert from "node:assert";
import { mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { unminify } from "./unminify.js";

const TEST_OUTPUT_DIR = "test-output-resume";

test.afterEach(async () => {
    await rm(TEST_OUTPUT_DIR, { recursive: true, force: true });
});

test("Resume mode skips already completed files", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const file1 = path.join(TEST_OUTPUT_DIR, "1.js");
    const file2 = path.join(TEST_OUTPUT_DIR, "2.js");

    await writeFile(file1, "var a=1;\n", "utf-8");
    await writeFile(file2, "var b=2;\n", "utf-8");

    const marker = "// humanify-test-marker\n";
    const plugin = async (code: string) => `${code}${marker}`;

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 2,
        pipelineId: "test:pipeline=v1"
    });

    const once1 = await readFile(file1, "utf-8");
    const once2 = await readFile(file2, "utf-8");
    assert.equal(once1.includes(marker), true);
    assert.equal(once2.includes(marker), true);

    // Run again: should skip completed tasks and not append marker twice
    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 2,
        pipelineId: "test:pipeline=v1"
    });

    const twice1 = await readFile(file1, "utf-8");
    const twice2 = await readFile(file2, "utf-8");

    assert.equal(twice1.split(marker).length - 1, 1);
    assert.equal(twice2.split(marker).length - 1, 1);

    const stateRaw = await readFile(
        path.join(TEST_OUTPUT_DIR, ".humanify.tasks.json"),
        "utf-8"
    );
    const state = JSON.parse(stateRaw) as {
        version: number;
        tasks: Array<{ path: string; status: string }>;
    };

    assert.equal(state.version, 1);
    assert.equal(
        state.tasks.filter((t) => t.status === "completed").length,
        2
    );
});

test("ContinueOnError records failures and allows rerun", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const file1 = path.join(TEST_OUTPUT_DIR, "1.js");
    const file2 = path.join(TEST_OUTPUT_DIR, "2.js");

    await writeFile(file1, "var a=1;\n", "utf-8");
    await writeFile(file2, "var b=2;\n", "utf-8");

    const marker = "// ok\n";
    const flakyPlugin = async (code: string) => {
        if (code.includes("var a")) throw new Error("boom");
        return `${code}${marker}`;
    };

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [flakyPlugin], {
        resume: true,
        continueOnError: true,
        agents: 2,
        pipelineId: "test:pipeline=v1"
    });

    const afterFirstRun1 = await readFile(file1, "utf-8");
    const afterFirstRun2 = await readFile(file2, "utf-8");
    assert.equal(afterFirstRun1.includes(marker), false);
    assert.equal(afterFirstRun2.includes(marker), true);

    const stablePlugin = async (code: string) => `${code}${marker}`;

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [stablePlugin], {
        resume: true,
        agents: 2,
        pipelineId: "test:pipeline=v1"
    });

    const afterSecondRun1 = await readFile(file1, "utf-8");
    const afterSecondRun2 = await readFile(file2, "utf-8");
    assert.equal(afterSecondRun1.includes(marker), true);
    // File 2 should not get duplicated marker
    assert.equal(afterSecondRun2.split(marker).length - 1, 1);
});

test("Resume re-queues completed tasks on content hash mismatch", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const file1 = path.join(TEST_OUTPUT_DIR, "1.js");

    await writeFile(file1, "abc\n", "utf-8");

    const marker = "X";
    const plugin = async (code: string) => `${code}${marker}`;

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    const once = await readFile(file1, "utf-8");
    assert.equal(once.split(marker).length - 1, 1);

    // Mutate file content without changing its byte length
    const tampered = once.replace("abc", "abd");
    assert.equal(Buffer.byteLength(tampered, "utf-8"), Buffer.byteLength(once, "utf-8"));
    await writeFile(file1, tampered, "utf-8");

    // Resume should detect hash mismatch and reprocess the file
    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    const twice = await readFile(file1, "utf-8");
    assert.equal(twice.split(marker).length - 1, 2);
});

test("Resume re-queues completed tasks missing output hash", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const file1 = path.join(TEST_OUTPUT_DIR, "1.js");

    await writeFile(file1, "abc\n", "utf-8");

    const marker = "X";
    const plugin = async (code: string) => `${code}${marker}`;

    // First run creates a task state with output hash.
    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    // Simulate an older/partial state file: mark completed but remove outputSha1.
    const statePath = path.join(TEST_OUTPUT_DIR, ".humanify.tasks.json");
    const stateRaw = await readFile(statePath, "utf-8");
    const state = JSON.parse(stateRaw) as any;
    const t = state.tasks.find((x: any) => x.path === "1.js");
    assert.ok(t);
    t.status = "completed";
    delete t.outputSha1;
    await writeFile(statePath, JSON.stringify(state, null, 2), "utf-8");

    // Resume should re-queue and reprocess, resulting in marker appended again.
    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    const after = await readFile(file1, "utf-8");
    assert.equal(after.split(marker).length - 1, 2);
});

test("Pipeline change re-queues completed tasks", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const file1 = path.join(TEST_OUTPUT_DIR, "1.js");
    await writeFile(file1, "abc\n", "utf-8");

    const marker = "X";
    const plugin = async (code: string) => `${code}${marker}`;

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    const once = await readFile(file1, "utf-8");
    assert.equal(once.split(marker).length - 1, 1);

    // Same outputDir, but pipelineId changes: should reprocess even though task was completed.
    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v2"
    });

    const twice = await readFile(file1, "utf-8");
    assert.equal(twice.split(marker).length - 1, 2);
});

test("Resume discovers js files in nested dirs", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    const nestedDir = path.join(TEST_OUTPUT_DIR, "nested");
    await mkdir(nestedDir, { recursive: true });

    const file1 = path.join(nestedDir, "1.js");
    await writeFile(file1, "abc\n", "utf-8");

    const marker = "N";
    const plugin = async (code: string) => `${code}${marker}`;

    await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
        resume: true,
        agents: 1,
        pipelineId: "test:pipeline=v1"
    });

    const after = await readFile(file1, "utf-8");
    assert.equal(after.split(marker).length - 1, 1);
});

test("Resume with empty outputDir throws helpful error", async () => {
    await mkdir(TEST_OUTPUT_DIR, { recursive: true });
    // Ensure output dir is empty.
    const entries = await readdir(TEST_OUTPUT_DIR);
    for (const e of entries) {
        await rm(path.join(TEST_OUTPUT_DIR, e), { recursive: true, force: true });
    }

    const plugin = async (code: string) => code;

    await assert.rejects(
        async () =>
            await unminify("does-not-exist.js", TEST_OUTPUT_DIR, [plugin], {
                resume: true,
                agents: 1,
                pipelineId: "test:pipeline=v1"
            }),
        (err: unknown) =>
            err instanceof Error &&
            err.message.includes("--resume was set but no extracted .js modules were found")
    );
});
