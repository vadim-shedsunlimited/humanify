import { cli } from "../cli.js";
import prettier from "../plugins/prettier.js";
import { unminify } from "../unminify.js";
import babel from "../plugins/babel/babel.js";
import { openaiRename } from "../plugins/openai/openai-rename.js";
import { verbose } from "../verbose.js";
import { env } from "../env.js";
import { parseNumber } from "../number-utils.js";
import { DEFAULT_CONTEXT_WINDOW_SIZE } from "./default-args.js";
import { err } from "../cli-error.js";

export const openai = cli()
  .name("openai")
  .description("Use OpenAI's API to unminify code")
  .option("-m, --model <model>", "The model to use", "gpt-4o-mini")
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
    "-k, --apiKey <apiKey>",
    "The OpenAI API key. Alternatively use OPENAI_API_KEY environment variable"
  )
  .option(
    "--baseURL <baseURL>",
    "The OpenAI base server URL.",
    env("OPENAI_BASE_URL") ?? "https://api.openai.com/v1"
  )
  .option("--verbose", "Show verbose output")
  .option(
    "--contextSize <contextSize>",
    "The context size to use for the LLM",
    `${DEFAULT_CONTEXT_WINDOW_SIZE}`
  )
  .argument("[inputs...]", "One or more input minified Javascript files")
  .action(async (inputs: string[], opts) => {
    if (opts.verbose) {
      verbose.enabled = true;
    }

    const apiKey =
      opts.apiKey ?? env("OPENAI_API_KEY") ?? env("OPENROUTER_API_KEY");
    if (!apiKey) {
      err(
        "Missing API key. Provide --apiKey or set OPENAI_API_KEY / OPENROUTER_API_KEY (for example via a .env file)."
      );
    }
    const baseURL = opts.baseURL;
    const contextWindowSize = parseNumber(opts.contextSize);
    await unminify(inputs ?? [], opts.outputDir, [
      babel,
      openaiRename({
        apiKey,
        baseURL,
        model: opts.model,
        contextWindowSize
      }),
      prettier
    ], {
      resume: opts.resume,
      continueOnError: opts.continueOnError,
      agents: parseNumber(opts.agents),
      pipelineId: `openai:model=${opts.model};contextSize=${contextWindowSize};baseURL=${baseURL}`
    });
  });
