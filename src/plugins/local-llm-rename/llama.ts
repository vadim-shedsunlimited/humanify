import { Gbnf } from "./gbnf.js";
import { err } from "../../cli-error.js";
import { getModelChatWrapper, getModelPath } from "../../local-models.js";
import { verbose } from "../../verbose.js";

export type Prompt = (
  systemPrompt: string,
  userPrompt: string,
  responseGrammar: Gbnf
) => Promise<string>;

const IS_CI = process.env["CI"] === "true";

export async function llama(opts: {
  seed?: number;
  model: string;
  disableGpu?: boolean;
}): Promise<Prompt> {
  let nodeLlama;
  try {
    nodeLlama = await import("node-llama-cpp");
  } catch {
    err(
      "Local mode requires the optional dependency \"node-llama-cpp\", which failed to install on this machine. " +
      "Use Node.js 20/22 LTS and install Visual Studio 'Desktop development with C++' build tools, then reinstall dependencies."
    );
  }

  const disableGpu = opts.disableGpu ?? IS_CI;
  const llama = await nodeLlama.getLlama({ gpu: disableGpu ? false : "auto" });
  const modelOpts = {
    modelPath: getModelPath(opts?.model),
    gpuLayers: disableGpu ? 0 : undefined
  };
  verbose.log("Loading model with options", modelOpts);
  const model = await llama.loadModel(modelOpts);

  const context = await model.createContext({ seed: opts?.seed });

  return async (systemPrompt, userPrompt, responseGrammar) => {
    const session = new nodeLlama.LlamaChatSession({
      contextSequence: context.getSequence(),
      autoDisposeSequence: true,
      systemPrompt,
      chatWrapper: await getModelChatWrapper(opts.model)
    });
    const response = await session.promptWithMeta(userPrompt, {
      temperature: 0.8,
      grammar: new nodeLlama.LlamaGrammar(llama, {
        grammar: `${responseGrammar}`
      }),
      stopOnAbortSignal: true
    });
    session.dispose();
    return responseGrammar.parseResult(response.responseText);
  };
}
