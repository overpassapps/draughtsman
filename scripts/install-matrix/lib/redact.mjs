// Evidence hygiene for the install matrix: what may be written into a report, and what must never be. A report is read by people and pasted
// into issues, so it never holds a cookie, a token, a password or a setup code. Pure functions, covered by test/redact.test.mjs.

// The only response headers that are evidence of how a request travelled. Anything else (above all Set-Cookie) is left out by name, not scrubbed.
const EVIDENCE_HEADERS = [
  'content-type', 'content-encoding', 'content-length', 'transfer-encoding', 'vary', 'cache-control',
  'etag', 'x-document-version', 'location', 'retry-after', 'www-authenticate', 'server', 'via',
  'x-draughtsman-csrf', 'x-draughtsman-session-ended', 'x-draughtsman-export-warnings',
  'cf-ray', 'cf-cache-status', 'x-forwarded-proto', 'x-forwarded-for', 'x-forwarded-host',
];

/** The evidence-worthy headers of a Headers-like object (anything with .entries() or a plain object), lower-cased, nothing else. */
export function pickEvidenceHeaders(headers) {
  const out = {};
  const entries = typeof headers?.entries === 'function' ? [...headers.entries()] : Object.entries(headers ?? {});
  for (const [name, value] of entries) {
    const key = String(name).toLowerCase();
    if (EVIDENCE_HEADERS.includes(key)) out[key] = Array.isArray(value) ? value.join(', ') : String(value);
  }
  return out;
}

/**
 * A Set-Cookie line reduced to what is safe and useful: the cookie's NAME and its attributes, never the value.
 *   `__Host-ds_session=abc; path=/; secure; httponly; samesite=lax`  ->  `__Host-ds_session (Secure, HttpOnly, SameSite=lax, Path=/)`
 */
export function describeSetCookie(line) {
  const parts = String(line).split(';').map((p) => p.trim()).filter(Boolean);
  if (parts.length === 0) return '(empty Set-Cookie)';
  const name = parts[0].split('=')[0];
  const flags = [];
  for (const attribute of parts.slice(1)) {
    const [key, ...rest] = attribute.split('=');
    const k = key.toLowerCase();
    if (k === 'secure') flags.push('Secure');
    else if (k === 'httponly') flags.push('HttpOnly');
    else if (k === 'samesite') flags.push(`SameSite=${rest.join('=')}`);
    else if (k === 'path') flags.push(`Path=${rest.join('=')}`);
    else if (k === 'domain') flags.push(`Domain=${rest.join('=')}`);
    else if (k === 'max-age' && rest.join('=').trim() === '0') flags.push('Max-Age=0');
  }
  return flags.length > 0 ? `${name} (${flags.join(', ')})` : name;
}

const PATTERNS = [
  [/\bdst_[A-Za-z0-9_-]{16,}/g, 'dst_[redacted]'],                       // API token
  [/\bdpr_[A-Za-z0-9_-]{16,}/g, 'dpr_[redacted]'],                       // password-reset link secret
  [/\b(Bearer)\s+[A-Za-z0-9._~+/=-]{8,}/gi, '$1 [redacted]'],
  [/\b((?:__Host-)?ds_session|XSRF-TOKEN|\.AspNetCore\.[A-Za-z0-9.]+|ds_selfcheck|__Host-ds_selfcheck)=[^;\s"',]+/g, '$1=[redacted]'],
  [/(Set-Cookie|Cookie|Authorization)\s*:[^\n]*/gi, '$1: [redacted]'],
  [/("(?:password|newPassword|currentPassword|setupCode|token|secret|apiKey)"\s*:\s*)"(?:[^"\\]|\\.)*"/gi, '$1"[redacted]"'],
  [/(First-run setup code:\s*)[A-Za-z0-9]+/gi, '$1[redacted]'],
  [/(setup code[^\n]{0,40}?[:=]\s*)[A-Z0-9]{8,}/gi, '$1[redacted]'],
];

/** Scrub text before it goes into evidence: known secret shapes, plus any exact `secrets` the caller holds (the generated password, the setup code). */
export function redactText(text, secrets = []) {
  let out = String(text ?? '');
  for (const secret of secrets) {
    if (typeof secret === 'string' && secret.length >= 6) out = out.split(secret).join('[redacted]');
  }
  for (const [pattern, replacement] of PATTERNS) out = out.replace(pattern, replacement);
  return out;
}

/** A response body shortened for a report: redacted first, then cut. */
export function bodyExcerpt(text, secrets = [], max = 1200) {
  const clean = redactText(text, secrets);
  return clean.length > max ? `${clean.slice(0, max)}... [${clean.length - max} more characters]` : clean;
}
