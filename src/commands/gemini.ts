import { cli } from "../cli.js";
import prettier from "../plugins/prettier.js";
import { unminify } from "../unminify.js";
import babel from "../plugins/babel/babel.js";
import { verbose } from "../verbose.js";
import { geminiRename } from "../plugins/gemini-rename.js";
import { env } from "../env.js";
import { DEFAULT_CONTEXT_WINDOW_SIZE } from "./default-args.js";
import { parseNumber } from "../number-utils.js";
import { err } from "../cli-error.js";

export const azure = cli()
  .name("gemini")
  .description("Use Google Gemini/AIStudio API to unminify code")
  .option("-m, --model <model>", "The model to use", "gemini-2.5-flash")
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
  .option(
    "--contextSize <contextSize>",
    "The context size to use for the LLM",
    `${DEFAULT_CONTEXT_WINDOW_SIZE}`
  )
  .option(
    "-k, --apiKey <apiKey>",
    "The Google Gemini/AIStudio API key. Alternatively use GEMINI_API_KEY environment variable"
  )
  .option("--verbose", "Show verbose output")
  .argument("[inputs...]", "One or more input minified Javascript files")
  .action(async (inputs: string[], opts) => {
    if (opts.verbose) {
      verbose.enabled = true;
    }

    const apiKey = opts.apiKey ?? env("GEMINI_API_KEY");
    if (!apiKey) {
      err(
        "Missing Gemini API key. Provide --apiKey or set GEMINI_API_KEY (for example via a .env file)."
      );
    }
    const contextWindowSize = parseNumber(opts.contextSize);

    try {
      await unminify(inputs ?? [], opts.outputDir, [
        babel,
        geminiRename({ apiKey, model: opts.model, contextWindowSize }),
        prettier
      ], {
        resume: opts.resume,
        continueOnError: opts.continueOnError,
        agents: parseNumber(opts.agents),
        pipelineId: `gemini:model=${opts.model};contextSize=${contextWindowSize}`
      });
    } catch (e) {
      // Avoid unhandled rejection / hard crash. Let Node exit naturally.
      const anyError = e as { status?: unknown; message?: unknown };
      if (anyError?.status === 429 && typeof anyError?.message === "string") {
        console.error(anyError.message);
        console.error(
          "Rate limits / quota are enforced by Gemini. If you see a PerDay quota error, you must wait for quota reset or enable billing / increase limits."
        );
      } else {
        console.error(e);
      }
      process.exitCode = 1;
    }
  });
