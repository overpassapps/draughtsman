/**
 * Local sign-in, users and API tokens (docs/api.md "## Auth"; build-spec-v0.1.md section 7). Same style as
 * documentClient.ts: one small function per call, `fetchImpl` last for test injection, `!response.ok` throws a
 * generic Error except where the server's own Problem+JSON `detail` is worth surfacing (`AuthError`).
 */

import { csrfHeaders } from './csrf.ts';
import { noteSessionEnded, parseReason, SESSION_ENDED_HEADER } from './sessionEnded.ts';

export type UserRole = 'Admin' | 'Editor';

/** Draughtsman.Server.Auth.PasswordHasher.MinimumLength - kept here so every form validates against the same value. */
export const MIN_PASSWORD_LENGTH = 12;

/** What the server answers (`UserEndpoints`, `AuthEndpoints`) for a password under the minimum, so a form can say it without a round trip. */
export const PASSWORD_TOO_SHORT_MESSAGE = `Password must be at least ${String(MIN_PASSWORD_LENGTH)} characters.`;

/** A user as `UserResponse` serialises it - never a password. */
export interface AuthUser {
  id: string;
  email: string;
  displayName: string;
  role: UserRole;
  createdAt: string;
  lastSignInAt?: string;
  active: boolean;
}

/** Body of `GET /api/auth/status`. */
export interface AuthStatus {
  firstRunRequired: boolean;
  user: AuthUser | null;
  /** Present only while first-run is open (docs/api.md): where the data lives and what kind of host this looks like. */
  firstRun?: FirstRunHints;
  /** What the sign-in page needs to know about password recovery. */
  passwordReset: PasswordResetHints;
}

/** Whether "Forgot password?" is offered: emailed reset links are set up and on (docs/api.md "Password reset"). */
export interface PasswordResetHints {
  selfService: boolean;
}

/** How the server was started, guessed from its environment; it only chooses which setup-code hint opens. */
export type HostKind = 'console' | 'container' | 'systemd' | 'launchd' | 'windowsService';

export interface FirstRunHints {
  dataFolder: string;
  host: HostKind;
}

/** Raised when the server refused an auth call with a Problem+JSON detail worth showing (400/401/409/429). */
export class AuthError extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'AuthError';
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

/**
 * What a person is told when the server accepted their sign-in (or first run) but the next status check still shows
 * nobody signed in: the browser threw the session cookie away. The usual cause is `server.forceHttps` reached over
 * plain http, where the Secure cookie is never stored (docs/troubleshooting.md).
 */
export const SESSION_NOT_KEPT_MESSAGE =
  'Signed in, but your browser did not keep the session. If this address starts with http:// while forceHttps is on, use https://.';

/** Whether first-run is needed, and the caller's own user if already signed in. Anonymous; safe to call on load. */
export async function fetchAuthStatus(fetchImpl: typeof fetch = fetch): Promise<AuthStatus> {
  const response = await fetchImpl('/api/auth/status', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Checking sign-in status failed: the server returned ${String(response.status)}`);
  }
  // The server's JSON options omit a null `user` entirely rather than writing `"user": null` (docs/api.md
  // Conventions: "nulls omitted"), so a signed-out response has no `user` key at all - normalise `undefined` to
  // `null` here so every caller can rely on the exact AuthStatus type instead of each re-deriving this.
  const body = (await response.json()) as { firstRunRequired: boolean; user?: AuthUser; firstRun?: FirstRunHints; passwordReset?: PasswordResetHints };
  // A reload after the session was ended still presents the dead cookie, and the server says why (S9).
  const ended = response.headers.get(SESSION_ENDED_HEADER);
  if (body.user === undefined && ended !== null) {
    noteSessionEnded(parseReason(ended));
  }
  // An older server sends no `passwordReset`: nothing is offered then, which is also what a missing answer should mean.
  const passwordReset = { selfService: body.passwordReset?.selfService === true };
  return body.firstRun === undefined
    ? { firstRunRequired: body.firstRunRequired, user: body.user ?? null, passwordReset }
    : { firstRunRequired: body.firstRunRequired, user: body.user ?? null, firstRun: body.firstRun, passwordReset };
}

/**
 * Creates the first administrator and signs them in. `setupCode` is the one printed to the server log at startup
 * (security review F1.2) - required even for the very first attempt, since there is no other gate on this endpoint.
 * 409 if an account already exists; 400 for a short password; 403 for a wrong or missing setup code.
 */
export async function firstRun(email: string, displayName: string, password: string, setupCode: string, fetchImpl: typeof fetch = fetch): Promise<AuthUser> {
  const response = await fetchImpl('/api/auth/first-run', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ email, displayName, password, setupCode }),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not create the administrator account.'));
  }
  return (await response.json()) as AuthUser;
}

/** Signs in; 401 is generic either way ("Email or password is incorrect."), 429 when throttled. */
export async function login(email: string, password: string, fetchImpl: typeof fetch = fetch): Promise<AuthUser> {
  const response = await fetchImpl('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not sign in.'));
  }
  return (await response.json()) as AuthUser;
}

/** The path of the page both kinds of reset link open. The secret follows it in the URL fragment, which the browser never sends to the server. */
export const RESET_PASSWORD_PATH = '/reset-password';

/** What `POST /api/users/{id}/password-reset-link` answers. `token` is the only copy of the secret there will ever be. */
export interface CreatedResetLink {
  token: string;
  /** The finished address when the server knows its own (`server.publicUrl`); absent otherwise, and the page builds it from its own origin. */
  link?: string;
  expiresAt: string;
  expiresInHours: number;
  userEmail: string;
}

/** The link to hand over: the server's when it has one, else this page's own origin plus the path and the fragment. */
export function resetLinkOf(created: CreatedResetLink, origin: string): string {
  return created.link ?? `${origin}${RESET_PASSWORD_PATH}#token=${created.token}`;
}

/** Creates a single-use reset link for another person (administrator, cookie session). 409 for a deactivated account, 404 for an unknown one. */
export async function createResetLink(userId: string, fetchImpl: typeof fetch = fetch): Promise<CreatedResetLink> {
  const response = await fetchImpl(`/api/users/${encodeURIComponent(userId)}/password-reset-link`, {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not create the reset link.'));
  }
  return (await response.json()) as CreatedResetLink;
}

/**
 * "Forgot password?": asks for a link to be emailed. The server answers every address with the same sentence, which is
 * returned as it came; 429 when the caller is asking too often, 404 when emailed links are not set up.
 */
export async function requestPasswordReset(email: string, fetchImpl: typeof fetch = fetch): Promise<string> {
  const response = await fetchImpl('/api/auth/password-reset/request', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ email }),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not ask for a reset link.'));
  }
  return ((await response.json()) as { message: string }).message;
}

/** Raised by `completePasswordReset`; `linkRefused` is true when the link itself was refused (unknown, used, expired), false for a problem with the password. */
export class ResetLinkError extends AuthError {
  readonly linkRefused: boolean;

  constructor(status: number, detail: string, linkRefused: boolean) {
    super(status, detail);
    this.name = 'ResetLinkError';
    this.linkRefused = linkRefused;
  }
}

/** Spends a reset link and sets the password. The secret goes in the body, never the address. */
export async function completePasswordReset(token: string, newPassword: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl('/api/auth/password-reset/complete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ token, newPassword }),
  });
  if (!response.ok) {
    let problem: ProblemBody = {};
    try {
      problem = (await response.json()) as ProblemBody;
    } catch {
      // A body that is not JSON (a proxy's error page) gets the generic wording below.
    }
    throw new ResetLinkError(response.status, problem.detail ?? problem.title ?? 'Could not change the password.', response.status === 400 && problem.title === 'Link not valid');
  }
}

/** Revokes the current session. */
export async function logout(fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl('/api/auth/logout', {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (!response.ok) {
    throw new Error(`Signing out failed: the server returned ${String(response.status)}`);
  }
}

/** Lists every user (admin only). */
export async function listUsers(fetchImpl: typeof fetch = fetch): Promise<AuthUser[]> {
  const response = await fetchImpl('/api/users', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Listing users failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as AuthUser[];
}

export interface CreateUserInput {
  email: string;
  displayName: string;
  role: UserRole;
  password: string;
}

/** Creates a local user (admin only). 409 on a duplicate email, 400 on invalid input. */
export async function createUser(input: CreateUserInput, fetchImpl: typeof fetch = fetch): Promise<AuthUser> {
  const response = await fetchImpl('/api/users', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not create the user.'));
  }
  return (await response.json()) as AuthUser;
}

/** Any subset of role/password/active; only the fields present are applied (docs/api.md). */
export interface UpdateUserInput {
  role?: UserRole;
  password?: string;
  active?: boolean;
}

/** Changes role, resets password, or (de)activates a user (admin only). 400 if an admin targets their own account in a way that would lock them out. */
export async function updateUser(id: string, input: UpdateUserInput, fetchImpl: typeof fetch = fetch): Promise<AuthUser> {
  const response = await fetchImpl(`/api/users/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify(input),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not update the user.'));
  }
  return (await response.json()) as AuthUser;
}

export type TokenScope = 'full' | 'read';

/**
 * An API token as the API lists it - never the secret. `expiresAt` is absent for a token that pre-dates expiry
 * (grandfathered: it never expires until revoked); `expired` is the server's own verdict against its clock.
 */
export interface ApiTokenSummary {
  id: string;
  name: string;
  createdAt: string;
  lastUsedAt?: string;
  revoked: boolean;
  expiresAt?: string;
  expired?: boolean;
  scope?: TokenScope;
}

/** One row of `GET /api/admin/tokens`: a token and who owns it (never a secret). */
export interface AdminApiTokenSummary extends ApiTokenSummary {
  ownerId: string;
  ownerEmail?: string;
  ownerName?: string;
}

/** Every person's tokens, for an administrator's credential list (QA S6). 403 for anyone else. */
export async function listAllTokens(fetchImpl: typeof fetch = fetch): Promise<AdminApiTokenSummary[]> {
  const response = await fetchImpl('/api/admin/tokens', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Listing every token failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as AdminApiTokenSummary[];
}

/** Body of `POST /api/tokens`: the only time the raw secret is ever returned. */
export interface CreatedApiToken {
  id: string;
  name: string;
  createdAt: string;
  expiresAt?: string;
  scope?: TokenScope;
  token: string;
}

export interface CreateTokenOptions {
  /** Whole days until it expires; omitted means the server's default. The server refuses more than its ceiling. */
  expiresInDays?: number;
  scope?: TokenScope;
}

/** Lists the caller's own tokens. */
export async function listTokens(fetchImpl: typeof fetch = fetch): Promise<ApiTokenSummary[]> {
  const response = await fetchImpl('/api/tokens', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Listing tokens failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as ApiTokenSummary[];
}

/** Creates a token for the caller and returns its raw secret, shown exactly once. */
export async function createToken(name: string, options: CreateTokenOptions = {}, fetchImpl: typeof fetch = fetch): Promise<CreatedApiToken> {
  const response = await fetchImpl('/api/tokens', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ name, ...options }),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not create the token.'));
  }
  return (await response.json()) as CreatedApiToken;
}

/** Revokes a token (owner, or any admin); 404 (never 403) when the caller may not see it exists. */
export async function revokeToken(id: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(`/api/tokens/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (!response.ok) {
    throw new Error(`Revoking the token failed: the server returned ${String(response.status)}`);
  }
}

/**
 * Changes the signed-in user's own password (AU-03). The current one is required; the server ends every other session
 * of theirs. 400 carries the reason (wrong current password, too short, unchanged), 429 when guessing is throttled.
 */
export async function changePassword(currentPassword: string, newPassword: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl('/api/auth/password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ currentPassword, newPassword }),
  });
  if (!response.ok) {
    throw new AuthError(response.status, await problemDetail(response, 'Could not change the password.'));
  }
}

/** One row of the instance security log (docs/api.md `GET /api/admin/security-events`). Never a secret. */
export interface SecurityEvent {
  id: number;
  at: string;
  type: string;
  actorUserId?: string;
  actorLabel?: string;
  targetUserId?: string;
  targetLabel?: string;
  sourceIp?: string;
  method?: string;
  detail?: string;
}

export interface SecurityEventPage {
  items: SecurityEvent[];
  total: number;
  offset: number;
  limit: number;
}

/** The security log, newest first, optionally narrowed to one event type (admin only). */
export async function listSecurityEvents(
  query: { type?: string; offset?: number; limit?: number } = {},
  fetchImpl: typeof fetch = fetch,
): Promise<SecurityEventPage> {
  const params = new URLSearchParams();
  if (query.type !== undefined && query.type !== '') {
    params.set('type', query.type);
  }
  if (query.offset !== undefined) {
    params.set('offset', String(query.offset));
  }
  if (query.limit !== undefined) {
    params.set('limit', String(query.limit));
  }
  const suffix = params.size === 0 ? '' : `?${params.toString()}`;
  const response = await fetchImpl(`/api/admin/security-events${suffix}`, { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Listing security events failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as SecurityEventPage;
}

/** Readable names for the event types the server records; an id not listed here is shown as it came. */
export const SECURITY_EVENT_LABELS: Record<string, string> = {
  'sign_in.succeeded': 'Signed in',
  'sign_in.failed': 'Sign-in failed',
  'sign_in.throttled': 'Sign-in throttled',
  sign_out: 'Signed out',
  'first_run.completed': 'First administrator created',
  'first_run.failed': 'First-run attempt failed',
  'user.created': 'Account created',
  'user.role_changed': 'Role changed',
  'user.deactivated': 'Account deactivated',
  'user.reactivated': 'Account reactivated',
  'password.reset': 'Password reset by an administrator',
  'password.changed': 'Password changed',
  'password.change_failed': 'Password change refused',
  'password_reset.requested': 'Password reset requested',
  'password_reset.link_issued': 'Reset link created by an administrator',
  'password_reset.email_sent': 'Reset email sent',
  'password_reset.email_failed': 'Reset email could not be sent',
  'password_reset.link_used': 'Reset link used',
  'password_reset.link_refused': 'Reset link refused',
  'password_reset.completed': 'Password changed with a reset link',
  'email.test_sent': 'Test email sent',
  'email.test_failed': 'Test email failed',
  'email.settings_changed': 'Email settings changed',
  'email.settings_reset': 'Email settings removed',
  'ai.endpoint_host_changed': 'AI endpoint host changed',
  'ai.endpoint_refused': 'AI endpoint refused',
  'theme.url_fetch': 'Page fetched for a theme',
  'token.created': 'API token created',
  'token.revoked': 'API token revoked',
  'admin.recovered': 'Administrator recovered from the host',
  'data.exported': 'Whole instance exported',
  'document.deleted': 'Document moved to the bin',
  'document.restored': 'Document restored from the bin',
  'document.purged': 'Document deleted for good',
};
