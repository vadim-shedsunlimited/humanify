import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { randomUUID } from "node:crypto";

export type Provider = "copilot" | "openai" | "gemini" | "local";

export type WebUiProfile = {
  id: string;
  name: string;
  provider: Provider;

  inputs: string[];
  outputDir: string;
  resume: boolean;
  continueOnError: boolean;
  agents: number;
  contextSize: number;

  model?: string;

  // OpenAI/Gemini extras
  apiKey?: string;
  baseURL?: string;

  // Local extras
  seed?: string;
  disableGpu?: boolean;
};

type ProfilesFile = {
  version: 1;
  updatedAt: string;
  profiles: WebUiProfile[];
};

function profilesDir() {
  return path.join(os.homedir(), ".humanify");
}

function profilesPath() {
  return path.join(profilesDir(), "webui-profiles.json");
}

export async function loadProfiles(): Promise<WebUiProfile[]> {
  try {
    const raw = await fs.readFile(profilesPath(), "utf-8");
    const parsed = JSON.parse(raw) as any;
    // Support two formats:
    // - legacy: the file was an array of profiles (stored directly)
    // - current: an object with { version: 1, updatedAt, profiles: [...] }
    if (Array.isArray(parsed)) return parsed as WebUiProfile[];
    if (parsed && parsed.version === 1 && Array.isArray(parsed.profiles)) return parsed.profiles as WebUiProfile[];
    return [];
  } catch {
    return [];
  }
}

export async function saveProfiles(profiles: WebUiProfile[]): Promise<void> {
  await fs.mkdir(profilesDir(), { recursive: true });
  const payload: ProfilesFile = {
    version: 1,
    updatedAt: new Date().toISOString(),
    profiles
  };
  await fs.writeFile(profilesPath(), JSON.stringify(payload, null, 2), "utf-8");
}

export function newProfileId() {
  return randomUUID();
}
