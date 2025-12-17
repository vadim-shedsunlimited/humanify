import { visitAllIdentifiers } from "./local-llm-rename/visit-all-identifiers.js";
import { verbose } from "../verbose.js";
import { showPercentage } from "../progress.js";
import {
  GoogleGenerativeAI,
  ModelParams,
  SchemaType
} from "@google/generative-ai";

type GeminiErrorDetails = { [key: string]: unknown };

function sleep(ms: number) {
  return new Promise<void>((resolve) => setTimeout(resolve, ms));
}

function parseRetryDelaySeconds(input: unknown): number | undefined {
  if (typeof input !== "string") return undefined;
  // Common formats: "3s", "12s".
  const match = /^\s*(\d+(?:\.\d+)?)\s*s\s*$/i.exec(input);
  if (!match) return undefined;
  const seconds = Number(match[1]);
  return Number.isFinite(seconds) ? seconds : undefined;
}

function getRetryDelayMs(error: unknown): number | undefined {
  const anyError = error as {
    status?: unknown;
    errorDetails?: unknown;
    message?: unknown;
  };

  // Prefer explicit RetryInfo from Google RPC error details.
  // Note: Sometimes retryDelay is "0s" while the message includes a fractional delay.
  let delayFromDetails: number | undefined;
  const details = anyError?.errorDetails;
  if (Array.isArray(details)) {
    for (const d of details as GeminiErrorDetails[]) {
      const type = (d as any)?.["@type"]; // eslint-disable-line @typescript-eslint/no-explicit-any
      if (typeof type === "string" && type.includes("RetryInfo")) {
        const retryDelay = (d as any)?.retryDelay; // eslint-disable-line @typescript-eslint/no-explicit-any
        const seconds = parseRetryDelaySeconds(retryDelay);
        if (seconds != null) delayFromDetails = Math.ceil(seconds * 1000);
      }
    }
  }

  // Fallback: parse from message "Please retry in 3.4s".
  let delayFromMessage: number | undefined;
  if (typeof anyError?.message === "string") {
    const match = /Please retry in\s+(\d+(?:\.\d+)?)s/i.exec(anyError.message);
    if (match) {
      const seconds = Number(match[1]);
      if (Number.isFinite(seconds)) delayFromMessage = Math.ceil(seconds * 1000);
    }
  }

  if (delayFromDetails == null) return delayFromMessage;
  if (delayFromMessage == null) return delayFromDetails;
  return Math.max(delayFromDetails, delayFromMessage);
}

function isRateLimitError(error: unknown): boolean {
  const anyError = error as { status?: unknown };
  return anyError?.status === 429;
}

function isDailyQuotaError(error: unknown): boolean {
  const anyError = error as { errorDetails?: unknown; message?: unknown };
  const details = anyError?.errorDetails;

  if (Array.isArray(details)) {
    for (const d of details as any[]) {
      const type = d?.["@type"];
      if (typeof type === "string" && type.includes("QuotaFailure")) {
        const violations = d?.violations;
        if (Array.isArray(violations)) {
          for (const v of violations) {
            const quotaId = v?.quotaId;
            if (typeof quotaId === "string" && /PerDay/i.test(quotaId)) {
              return true;
            }
          }
        }
      }
    }
  }

  if (typeof anyError?.message === "string") {
    return /per\s*day/i.test(anyError.message);
  }

  return false;
}

async function withRateLimitRetries<T>(fn: () => Promise<T>): Promise<T> {
  const maxAttempts = 10;
  let lastError: unknown;

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      return await fn();
    } catch (error) {
      lastError = error;
      if (!isRateLimitError(error) || attempt === maxAttempts) {
        throw error;
      }

      // Daily quota exhaustion won't resolve with retries.
      if (isDailyQuotaError(error)) {
        throw error;
      }

      const retryDelayMs = getRetryDelayMs(error);
      const baseDelay = retryDelayMs ?? Math.min(60_000, 1000 * attempt * attempt);
      const jitter = Math.floor(Math.random() * 250);
      const delay = baseDelay + jitter;

      verbose.log(`Gemini rate-limited (429). Retry ${attempt}/${maxAttempts} in ${delay}ms`);
      await sleep(delay);
    }
  }

  throw lastError;
}

export function geminiRename({
  apiKey,
  model: modelName,
  contextWindowSize
}: {
  apiKey: string;
  model: string;
  contextWindowSize: number;
}) {
  const client = new GoogleGenerativeAI(apiKey);
  const renameCache = new Map<string, string>();

  return async (code: string): Promise<string> => {
    return await visitAllIdentifiers(
      code,
      async (name, surroundingCode) => {
        // Same identifier name appears many times; caching dramatically reduces calls.
        // Key includes model + name + a slice of the surrounding code to avoid incorrect reuse.
        const cacheKey = `${modelName}::${name}::${surroundingCode.slice(0, 500)}`;
        const cached = renameCache.get(cacheKey);
        if (cached) {
          return cached;
        }

        verbose.log(`Renaming ${name}`);
        verbose.log("Context: ", surroundingCode);

        const model = client.getGenerativeModel(
          toRenameParams(name, modelName)
        );

        const result = await withRateLimitRetries(() =>
          model.generateContent(surroundingCode)
        );

        const renamed = JSON.parse(result.response.text()).newName;

        verbose.log(`Renamed to ${renamed}`);

        renameCache.set(cacheKey, renamed);

        return renamed;
      },
      contextWindowSize,
      showPercentage
    );
  };
}

function toRenameParams(name: string, model: string): ModelParams {
  return {
    model,
    systemInstruction: `Rename Javascript variables/function \`${name}\` to have descriptive name based on their usage in the code."`,
    generationConfig: {
      responseMimeType: "application/json",
      responseSchema: {
        nullable: false,
        description: "The new name for the variable/function",
        type: SchemaType.OBJECT,
        properties: {
          newName: {
            type: SchemaType.STRING,
            nullable: false,
            description: `The new name for the variable/function called \`${name}\``
          }
        },
        required: ["newName"]
      }
    }
  };
}
