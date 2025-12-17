import { cli } from "../cli.js";
import prettier from "../plugins/prettier.js";
import { unminify } from "../unminify.js";
import babel from "../plugins/babel/babel.js";
import { verbose } from "../verbose.js";
import { DEFAULT_CONTEXT_WINDOW_SIZE } from "./default-args.js";
import { parseNumber } from "../number-utils.js";
import { copilotRename } from "../plugins/copilot-rename.js";

export const copilot = cli()
    .name("copilot")
    .description("Use the locally installed `copilot` CLI to rename identifiers")
    .option("--model <model>", "The Copilot model to use (optional)")
    .option("-o, --outputDir <output>", "The output directory", "output")
    .option(
        "--resume",
        "Resume processing an existing --outputDir (skip re-extracting the bundle)"
    )
    .option(
        "--continueOnError",
        "Continue processing remaining files even if one fails"
    )
    .option(
        "--agents <agents>",
        "Number of concurrent workers to process extracted modules",
        "1"
    )
    .option("--verbose", "Show verbose output")
    .option(
        "--contextSize <contextSize>",
        "How much surrounding code to send per rename request (characters)",
        `${DEFAULT_CONTEXT_WINDOW_SIZE}`
    )
    .argument("[inputs...]", "One or more input minified Javascript files")
    .action(async (inputs: string[], opts) => {
        if (opts.verbose) {
            verbose.enabled = true;
        }

        const contextWindowSize = parseNumber(opts.contextSize);

        await unminify(inputs ?? [], opts.outputDir, [
            babel,
            copilotRename({ model: opts.model, contextWindowSize }),
            prettier
        ], {
            resume: opts.resume,
            continueOnError: opts.continueOnError,
            agents: parseNumber(opts.agents),
            pipelineId: `copilot:model=${opts.model ?? ""};contextSize=${contextWindowSize}`
        });
    });
