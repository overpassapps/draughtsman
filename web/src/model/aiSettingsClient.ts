/**
 * The admin AI settings (M3; docs/api.md "## AI settings"). Same style as brandingClient.ts: one small function per
 * call, `fetchImpl` last for test injection, every mutating call carrying the CSRF header. The API key is
 * write-only: `setAiKey` sends it, and nothing here (or on the server) ever reads one back - `AiKeyState` says only
 * whether a key is set and where the one in effect comes from.
 */

import { csrfHeaders } from './csrf.ts';

export type AiProviderName = 'ollama' | 'openai' | 'anthropic' | 'azure-openai';

export interface AiSettingsValues {
  provider: string | null;
  model: string | null;
  endpoint: string | null;
  apiVersion: string | null;
}

/** Where the key in effect comes from: the environment variable wins over the encrypted key stored here. */
export interface AiKeyState {
  source: 'environment' | 'stored' | 'none';
  environmentVariable: string;
  environmentSet: boolean;
  storedSet: boolean;
  storedUnreadable: boolean;
  required: boolean;
  /**
   * Plain English for why a key that exists is not being sent to this endpoint (the environment key goes only to the host in
   * draughtsman.yaml or the provider's own; no key goes over plain http except to this machine). Absent when nothing is held back.
   */
  withheld?: string;
}

/**
 * How the key ring that encrypts the stored key is protected at rest (src/Draughtsman.Server/KeyRing/KeyRingStatus.cs).
 * `unprotected` means anyone who can read the server's data folder can also decrypt the stored key; `message` says why
 * and what to do. `backup` names the pre-migration copy of the old, plain-text ring while it still exists. Nothing in
 * here is a key: identifiers, plain English, a count and a folder name.
 */
export interface KeyRingState {
  state: 'protected' | 'unprotected';
  provider: 'dpapi' | 'keychain' | 'key' | 'certificate' | 'none';
  reason?: 'no-provider' | 'disabled' | 'unavailable' | 'migration-failed';
  message: string;
  unreadableKeys: number;
  backup?: string;
  migration: 'none' | 'migrated' | 'failed' | 'rolled-back';
  /**
   * Each secret kept under the ring (the AI API key, the saved SMTP password) and whether it can be read, with the reason and what to do in `message`:
   * `current` (protected by the key ring alone), `reprotected` (an earlier version tied it to the install folder and this start moved it), `legacy` (readable, but
   * could not be moved this start) or `unreadable`. Only secrets that are stored are listed; absent from a server that predates this.
   */
  storedSecrets?: StoredSecretState[];
}

export interface StoredSecretState {
  name: string;
  file: string;
  state: 'current' | 'reprotected' | 'legacy' | 'unreadable';
  message: string;
}

/** What the server says about one stored secret, when it says anything worth showing (not the quiet `current` case). */
export function storedSecretNote(keyRing: KeyRingState | undefined, name: string): StoredSecretState | undefined {
  const secret = keyRing?.storedSecrets?.find((s) => s.name === name);
  return secret === undefined || secret.state === 'current' ? undefined : secret;
}

/** Body of every AI settings response; see src/Draughtsman.Server/Ai/AiSettingsService.cs. */
export interface AiSettingsState {
  enabled: boolean;
  problem: string | null;
  effective: AiSettingsValues;
  file: AiSettingsValues;
  overrides: AiSettingsValues;
  key: AiKeyState;
  providers: AiProviderName[];
  /** Absent only from a server that predates key ring protection. */
  keyRing?: KeyRingState;
}

export interface AiConnectionResult {
  ok: boolean;
  message: string;
}

/** Raised when the server refused an AI settings call with a Problem+JSON detail worth showing. */
export class AiSettingsError extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'AiSettingsError';
    this.status = status;
  }
}

interface ProblemBody {
  title?: string;
  detail?: string;
}

async function problemDetail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as ProblemBody;
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (!response.ok) {
    throw new AiSettingsError(response.status, await problemDetail(response, fallback));
  }
  return (await response.json()) as T;
}

const JSON_HEADERS = { 'Content-Type': 'application/json', Accept: 'application/json' };

/** The current settings and which parts are overridden (admin only). */
export async function fetchAiSettings(fetchImpl: typeof fetch = fetch): Promise<AiSettingsState> {
  const response = await fetchImpl('/api/admin/ai', { headers: { Accept: 'application/json' } });
  return readJson<AiSettingsState>(response, 'Could not read the AI settings.');
}

/** Replaces the provider/model/endpoint/API-version overrides; a blank field clears its override. */
export async function saveAiSettings(
  values: { provider: string; model: string; endpoint: string; apiVersion: string },
  fetchImpl: typeof fetch = fetch,
): Promise<AiSettingsState> {
  const response = await fetchImpl('/api/admin/ai', {
    method: 'PUT',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify(values),
  });
  return readJson<AiSettingsState>(response, 'Could not save the AI settings.');
}

/** Stores the API key (encrypted on the server). The response carries no key. */
export async function setAiKey(apiKey: string, fetchImpl: typeof fetch = fetch): Promise<AiSettingsState> {
  const response = await fetchImpl('/api/admin/ai/key', {
    method: 'PUT',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify({ apiKey }),
  });
  return readJson<AiSettingsState>(response, 'Could not save the API key.');
}

export async function clearAiKey(fetchImpl: typeof fetch = fetch): Promise<AiSettingsState> {
  const response = await fetchImpl('/api/admin/ai/key', {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return readJson<AiSettingsState>(response, 'Could not clear the API key.');
}

/** Drops every override and the stored key, returning to draughtsman.yaml. */
export async function resetAiSettings(fetchImpl: typeof fetch = fetch): Promise<AiSettingsState> {
  const response = await fetchImpl('/api/admin/ai/reset', {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return readJson<AiSettingsState>(response, 'Could not reset the AI settings.');
}

/** One minimal request with the settings in effect; the server explains a failure in plain English. */
export async function testAiConnection(fetchImpl: typeof fetch = fetch): Promise<AiConnectionResult> {
  const response = await fetchImpl('/api/admin/ai/test', {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return readJson<AiConnectionResult>(response, 'Could not run the connection test.');
}
