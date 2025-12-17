import OpenAI from "openai";
import { visitAllIdentifiers } from "../local-llm-rename/visit-all-identifiers.js";
import { showPercentage } from "../../progress.js";
import { verbose } from "../../verbose.js";

function extractJsonObject(text: string): unknown {
  // Best-effort extraction for models that sometimes wrap JSON in prose/codefences.
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

  // Be tolerant of minor field name differences across OpenAI-compatible servers.
  const direct = parsed?.newName ?? parsed?.new_name ?? parsed?.name;
  if (typeof direct === "string" && direct.length > 0) {
    return direct;
  }

  // Last-resort: regex extraction if the server returns something JSON-ish.
  const match = /"newName"\s*:\s*"([^"]+)"/m.exec(content);
  if (match?.[1]) {
    return match[1];
  }

  throw new Error("Rename response missing `newName`");
}

function localHeuristicRename(surroundingCode: string, name: string): string {
  try {
    const raw = surroundingCode || "";
    const tokens = (raw.match(/[A-Za-z_][A-Za-z0-9_]+/g) || [])
      .map(t => t.replace(/^_+|_+$/g, ""))
      .filter(t => t && t.length > 2 && t !== name);
    const stop = new Set([
      "function",
      "return",
      "const",
      "let",
      "var",
      "if",
      "else",
      "for",
      "while",
      "switch",
      "case",
      "break",
      "new",
      "class",
      "this",
      "true",
      "false",
      "null",
      "undefined"
    ]);
    const filtered = tokens.filter(t => !stop.has(t.toLowerCase()));
    const freq: Record<string, number> = {};
    for (const t of filtered) freq[t] = (freq[t] || 0) + 1;
    const candidates = Object.keys(freq).sort((a, b) => freq[b] - freq[a]);
    if (candidates.length >= 2) {
      const pick = candidates.slice(0, 2);
      const camel = pick
        .map((s, i) => (i === 0 ? s : s.charAt(0).toUpperCase() + s.slice(1)))
        .join("");
      if (/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(camel)) return camel;
    }
    if (candidates.length === 1) {
      const c = candidates[0];
      if (/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(c)) return c;
    }
  } catch { }
  return `${name}_auto`;
}
function isUnsupportedResponseFormatError(error: unknown): boolean {
  const message =
    typeof (error as any)?.message === "string" // eslint-disable-line @typescript-eslint/no-explicit-any
      ? (error as any).message // eslint-disable-line @typescript-eslint/no-explicit-any
      : "";
  return (
    /response_format/i.test(message) ||
    /json_schema/i.test(message) ||
    /unknown\s+parameter/i.test(message) ||
    /unsupported/i.test(message)
  );
}

export function openaiRename({
  apiKey,
  baseURL,
  model,
  contextWindowSize
}: {
  apiKey: string;
  baseURL: string;
  model: string;
  contextWindowSize: number;
}) {
  // For LM Studio (e.g., http://127.0.0.1:1234) we need to use the /v1 API path
  const effectiveBaseURL = baseURL.replace(/\/+$/, "");
  const client = new OpenAI({ apiKey, baseURL: effectiveBaseURL.includes("127.0.0.1:1234") ? `${effectiveBaseURL}/v1` : effectiveBaseURL });

  return async (code: string): Promise<string> => {
    return await visitAllIdentifiers(
      code,
      async (name, surroundingCode) => {
        verbose.log(`Renaming ${name}`);
        verbose.log("Context: ", surroundingCode);

        let response: OpenAI.Chat.Completions.ChatCompletion;
        try {
          // Preferred path: JSON Schema response formatting (OpenAI-compatible servers may not support this).
          response = await client.chat.completions.create(
            toRenamePrompt(name, surroundingCode, model)
          );
        } catch (e) {
          if (!isUnsupportedResponseFormatError(e)) {
            throw e;
          }

          // Fallback path for LM Studio / other servers: ask for raw JSON.
          response = await client.chat.completions.create(
            toRenamePromptCompat(name, surroundingCode, model)
          );
        }
        // Be defensive: different OpenAI-compatible servers and SDK versions
        // return slightly different shapes. Attempt several common access patterns
        // and surface a helpful error if none are present.
        let result: string | undefined;

        // If the provider returned an error object (some self-hosted servers do)
        // and specifically complains about the chat/completions endpoint, try
        // the Responses API immediately as a fallback.
        const respError = (response as any)?.error;
        if (typeof respError === "string" && /chat\/completions/i.test(respError)) {
          if (verbose.enabled) verbose.log(`Provider rejected chat/completions: ${respError}. Trying Responses API.`);
          try {
            const resp2 = await client.responses.create({
              model,
              input:
                surroundingCode +
                "\n\nRename the variable/function '" +
                name +
                "' to a descriptive name. Reply with ONLY a JSON object like {\"newName\":\"someDescriptiveName\"}."
            });
            if (verbose.enabled) {
              try {
                verbose.log(`Responses fallback raw: ${JSON.stringify(resp2).slice(0, 1024)}`);
              } catch { }
            }
            if (Array.isArray((resp2 as any).output) && (resp2 as any).output[0]) {
              const out = (resp2 as any).output[0];
              if (typeof out === "string") result = out;
              else if (typeof out?.content === "string") result = out.content;
              else if (Array.isArray(out?.content)) {
                for (const c of out.content) {
                  if (typeof c?.text === "string") {
                    result = c.text;
                    break;
                  }
                  if (typeof c?.text === "object" && typeof c.text?.value === "string") {
                    result = c.text.value;
                    break;
                  }
                }
              }
            } else if (typeof (resp2 as any).output_text === "string") {
              result = (resp2 as any).output_text;
            }
          } catch (e2) {
            if (verbose.enabled) verbose.log(`Responses API fallback failed: ${String(e2)}`);
          }
        }

        if (!result) {
          if (Array.isArray((response as any).choices) && (response as any).choices[0]) {
            // Standard OpenAI shape: choices[].message.content
            result = (response as any).choices[0].message?.content ?? (response as any).choices[0].text;
          } else if (Array.isArray((response as any).output) && (response as any).output[0]) {
            // Some providers expose `output` or similar
            const out = (response as any).output[0];
            if (typeof out === "string") result = out;
            else if (typeof out?.content === "string") result = out.content;
          } else if (typeof (response as any).content === "string") {
            result = (response as any).content;
          }
        }

        if (!result) {
          // Try alternate Responses API as a fallback for providers that
          // don't implement the /chat/completions endpoint and we haven't
          // already tried it.
          try {
            const resp2 = await client.responses.create({
              model,
              input:
                surroundingCode +
                "\n\nRename the variable/function '" +
                name +
                "' to a descriptive name. Reply with ONLY a JSON object like {\"newName\":\"someDescriptiveName\"}."
            });

            if (verbose.enabled) {
              try {
                verbose.log(`Responses fallback raw: ${JSON.stringify(resp2).slice(0, 1024)}`);
              } catch { }
            }

            if (Array.isArray((resp2 as any).output) && (resp2 as any).output[0]) {
              const out = (resp2 as any).output[0];
              if (typeof out === "string") result = out;
              else if (typeof out?.content === "string") result = out.content;
              else if (Array.isArray(out?.content)) {
                for (const c of out.content) {
                  if (typeof c?.text === "string") {
                    result = c.text;
                    break;
                  }
                  if (typeof c?.text === "object" && typeof c.text?.value === "string") {
                    result = c.text.value;
                    break;
                  }
                }
              }
            } else if (typeof (resp2 as any).output_text === "string") {
              result = (resp2 as any).output_text;
            }
          } catch (e2) {
            if (verbose.enabled) verbose.log(`Responses API fallback failed: ${String(e2)}`);
          }

          // Don't fail the whole run for an unexpected response shape; log and use local fallback.
          if (verbose.enabled) {
            const preview = (() => {
              try {
                return JSON.stringify(response).slice(0, 1024);
              } catch {
                return String(response);
              }
            })();
            verbose.log(`Unexpected response shape while renaming ${name}: ${preview}`);
          }
          const fallback = localHeuristicRename(surroundingCode, name);
          if (verbose.enabled) verbose.log(`Using local heuristic fallback rename for ${name}: ${fallback}`);
          return fallback;
        }

        let renamed: string;
        try {
          renamed = parseRenameResponse(result);
        } catch {
          if (verbose.enabled) {
            const preview = result.length > 800 ? `${result.slice(0, 800)}…` : result;
            verbose.log(
              `Failed to parse rename response for ${name}. Keeping original name. Response preview: ${preview}`
            );
          }
          const fallback = localHeuristicRename(surroundingCode, name);
          if (verbose.enabled) verbose.log(`Using local heuristic fallback rename for ${name}: ${fallback}`);
          return fallback;
        }

        verbose.log(`Renamed to ${renamed}`);

        return renamed;
      },
      contextWindowSize,
      showPercentage
    );
  };
}

function toRenamePrompt(
  name: string,
  surroundingCode: string,
  model: string
): OpenAI.Chat.Completions.ChatCompletionCreateParamsNonStreaming {
  return {
    model,
    messages: [
      {
        role: "system",
        content: `Rename Javascript variables/function \`${name}\` to have descriptive name based on their usage in the code."`
      },
      {
        role: "user",
        content: surroundingCode
      }
    ],
    response_format: {
      type: "json_schema",
      json_schema: {
        strict: true,
        name: "rename",
        schema: {
          type: "object",
          properties: {
            newName: {
              type: "string",
              description: `The new name for the variable/function called \`${name}\``
            }
          },
          required: ["newName"],
          additionalProperties: false
        }
      }
    }
  };
}

function toRenamePromptCompat(
  name: string,
  surroundingCode: string,
  model: string
): OpenAI.Chat.Completions.ChatCompletionCreateParamsNonStreaming {
  return {
    model,
    messages: [
      {
        role: "system",
        content:
          `Rename Javascript variables/function \`${name}\` to have descriptive name based on their usage in the code. ` +
          `Reply with ONLY a JSON object like {"newName":"someDescriptiveName"}.`
      },
      {
        role: "user",
        content: surroundingCode
      }
    ]
  };
}
