/**
 * The admin Email page's calls (docs/api.md "## Email"): what outgoing email is set to, save it, remove what was saved, and send a test
 * message to the signed-in administrator (of the saved settings, or of the form as typed without saving it). Same style as
 * aiSettingsClient.ts. The SMTP password is write-only: `saveEmailSettings` sends one, and nothing here (or on the server) ever reads one back.
 * `passwordSet` and `passwordKind` say whether one is available and which source is in effect.
 */
import { csrfHeaders } from './csrf.ts';
import type { KeyRingState } from './aiSettingsClient.ts';

/** The fields a form edits, in the server's names. */
export type EmailField = 'provider' | 'from' | 'replyTo' | 'host' | 'port' | 'tls' | 'user' | 'publicUrl' | 'password';

export interface EmailSettings {
  configured: boolean;
  provider?: string;
  from?: string;
  replyTo?: string;
  host?: string;
  port: number;
  tls: string;
  user?: string;
  /** A password is available from any source. */
  passwordSet: boolean;
  /** Which source is in effect: the environment or a file named in draughtsman.yaml (both lock the field), the one saved here, or none. */
  passwordKind: 'environment' | 'file' | 'saved' | 'none';
  /** A saved password could not be decrypted (the server's key ring changed): enter it again. */
  passwordUnreadable: boolean;
  passwordSource?: string;
  outboxFolder?: string;
  publicUrl?: string;
  /** Field to the reason draughtsman.yaml (or the environment) sets it, so it cannot be edited here. */
  locked: Partial<Record<EmailField, string>>;
  /** Something is saved at Admin, Email, so there is something to remove. */
  hasSaved: boolean;
  selfServiceEnabled: boolean;
  selfServiceAvailable: boolean;
  emailedLinkMinutes: number;
  adminLinkHours: number;
  problems: string[];
  /** Absent only from a server that predates key ring protection. */
  keyRing?: KeyRingState;
}

export interface EmailTestResult {
  ok: boolean;
  message: string;
}

export type PasswordAction = 'keep' | 'replace' | 'clear';

/** What the form sends to save or to test. Text fields are as typed; a blank one clears its saved value. */
export interface EmailFormInput {
  from: string;
  replyTo: string;
  host: string;
  port: string;
  tls: string;
  user: string;
  publicUrl: string;
  passwordAction: PasswordAction;
  /** Only with `passwordAction: 'replace'`; dropped from state the moment it is sent. */
  password: string;
}

/** Raised when the server refused a call with a Problem+JSON detail worth showing, with its per-field sentences when it gave them. */
export class EmailSettingsError extends Error {
  readonly status: number;
  readonly fieldErrors: Partial<Record<string, string>>;

  constructor(status: number, detail: string, fieldErrors: Partial<Record<string, string>> = {}) {
    super(detail);
    this.name = 'EmailSettingsError';
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

interface ProblemBody {
  title?: string;
  detail?: string;
  errors?: { field?: string; message?: string }[];
}

async function failure(response: Response, fallback: string): Promise<EmailSettingsError> {
  let detail = fallback;
  const fieldErrors: Partial<Record<string, string>> = {};
  try {
    const problem = (await response.json()) as ProblemBody;
    detail = problem.detail ?? problem.title ?? fallback;
    for (const error of problem.errors ?? []) {
      if (error.field !== undefined && error.message !== undefined) {
        fieldErrors[error.field] = fieldErrors[error.field] === undefined ? error.message : `${fieldErrors[error.field] ?? ''} ${error.message}`;
      }
    }
  } catch {
    // Keep the fallback wording.
  }
  return new EmailSettingsError(response.status, detail, fieldErrors);
}

const JSON_HEADERS = { 'Content-Type': 'application/json', Accept: 'application/json' };

/** The text and numbers the server reads, from the form: a blank port is "the default", and a password travels only when it is being replaced. */
export function toRequestBody(input: EmailFormInput): Record<string, unknown> {
  const port = input.port.trim() === '' ? undefined : Number(input.port);
  return {
    from: input.from,
    replyTo: input.replyTo,
    host: input.host,
    port: port !== undefined && Number.isFinite(port) ? port : undefined,
    tls: input.tls,
    user: input.user,
    publicUrl: input.publicUrl,
    passwordAction: input.passwordAction,
    password: input.passwordAction === 'replace' ? input.password : undefined,
  };
}

export async function fetchEmailSettings(fetchImpl: typeof fetch = fetch): Promise<EmailSettings> {
  const response = await fetchImpl('/api/admin/email', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw await failure(response, `Reading the email settings failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as EmailSettings;
}

/** Validates, stores and applies the form at once (no restart). The response is the settings as they now are. */
export async function saveEmailSettings(input: EmailFormInput, fetchImpl: typeof fetch = fetch): Promise<EmailSettings> {
  const response = await fetchImpl('/api/admin/email', {
    method: 'PUT',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify(toRequestBody(input)),
  });
  if (!response.ok) {
    throw await failure(response, 'Could not save the email settings.');
  }
  return (await response.json()) as EmailSettings;
}

/** Forgets every saved field and the saved password: back to draughtsman.yaml alone. */
export async function removeEmailSettings(fetchImpl: typeof fetch = fetch): Promise<EmailSettings> {
  const response = await fetchImpl('/api/admin/email', { method: 'DELETE', headers: { Accept: 'application/json', ...csrfHeaders() } });
  if (!response.ok) {
    throw await failure(response, 'Could not remove the saved email settings.');
  }
  return (await response.json()) as EmailSettings;
}

/**
 * Sends one message to the administrator's own address. With `values` it tests the form as typed and stores nothing; without, what is saved.
 * A failure comes back as `ok: false` with a short class of reason (never the mail server's own words); 400 and 429 throw.
 */
export async function sendTestEmail(values?: EmailFormInput, fetchImpl: typeof fetch = fetch): Promise<EmailTestResult> {
  const response = await fetchImpl('/api/admin/email/test', {
    method: 'POST',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    ...(values === undefined ? {} : { body: JSON.stringify({ values: toRequestBody(values) }) }),
  });
  if (!response.ok) {
    throw await failure(response, `The server returned ${String(response.status)}`);
  }
  return (await response.json()) as EmailTestResult;
}
