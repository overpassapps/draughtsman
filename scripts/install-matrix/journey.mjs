#!/usr/bin/env node
// The first journey, scripted: what a customer does in their first hour on a fresh install, run against a LIVE Draughtsman instance and judged
// step by step. It is the one test body every install-matrix cell runs (docs/testing-installs.md); matrix.sh decides WHERE it runs.
//
//   node scripts/install-matrix/journey.mjs --base-url https://localhost:8443 --setup-code-from docker-logs:dmx-app --container dmx-app \
//        [--cli draughtsman] [--expect c="session not kept: forceHttps over http"] [--expect-selfcheck secure=fail] [--json out.json]
//
//   --base-url URL                  the address a browser would use (http or https). Required.
//   --setup-code-from SRC           docker-logs:NAME | file:PATH | none. Where the first-run setup code is read from.
//   --container NAME                optional: a Docker container to run the CLI verbs in (step l) and to restart.
//   --cli PATH                      the draughtsman command inside the container (default: draughtsman).
//   --data-dir DIR                  the data folder inside the container (default: /data); the support bundle is written there.
//   --sign-in-as EMAIL --password-env VAR
//                                   for an instance that already has users (the upgrade cell): skip first run, sign in as this administrator.
//   --expect STEP=REASON            declare a step EXPECTED to fail (steps a to m). A declared step that passes is itself a failure.
//   --expect-selfcheck ID=STATUS    declare a Connection check row's status (pass|warn|fail|info); any other row that fails is a failure.
//   --lenient-cookies               keep Secure cookies over plain http (like curl), instead of dropping them like a browser. Default: strict.
//   --json PATH                     write the full machine-readable result (redacted) to PATH.
//
// It generates its own random email and 24-character password and never prints the password (it prints "password: generated"). No cookie, token,
// password or setup code is ever written to the output or to the --json file: evidence passes through lib/redact.mjs when it is captured.
// Exit 0 only if every step passed or is a declared EXPECTED-FAIL.
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Client } from './lib/http.mjs';
import { exitCodeFor, judgeSelfcheck, parseExpectations, parseSelfcheckExpectations, settleStep } from './lib/expect.mjs';
import { formatStepDetails, formatStepTable } from './lib/report.mjs';
import { bodyExcerpt, redactText } from './lib/redact.mjs';
import { compareVersionHeaders, quotedStrong } from './lib/versions.mjs';
import { zipEntryNames } from './lib/zip.mjs';

function parseArgs(argv) {
  const out = { expect: [], expectSelfcheck: [] };
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i];
    const take = () => { if (i + 1 >= argv.length) throw new Error(`${flag} needs a value`); return argv[++i]; };
    switch (flag) {
      case '--base-url': out.baseUrl = take(); break;
      case '--setup-code-from': out.setupCodeFrom = take(); break;
      case '--container': out.container = take(); break;
      case '--cli': out.cli = take(); break;
      case '--data-dir': out.dataDir = take(); break;
      case '--sign-in-as': out.signInAs = take(); break;
      case '--password-env': out.passwordEnv = take(); break;
      case '--expect': out.expect.push(take()); break;
      case '--expect-selfcheck': out.expectSelfcheck.push(take()); break;
      case '--lenient-cookies': out.lenient = true; break;
      case '--json': out.json = take(); break;
      case '-h': case '--help': out.help = true; break;
      default: throw new Error(`unknown option ${flag}`);
    }
  }
  return out;
}

const randomText = (length, alphabet) => { const bytes = randomBytes(length); let s = ''; for (let i = 0; i < length; i++) s += alphabet[bytes[i] % alphabet.length]; return s; };
const ALNUM = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function docker(args, opts = {}) {
  return spawnSync('docker', args, { encoding: 'utf8', timeout: opts.timeout ?? 120000, maxBuffer: 16 * 1024 * 1024 });
}

/** The last "First-run setup code: X" in `text`, or undefined. The last, because a restart before first run issues a fresh code. */
function lastSetupCode(text) {
  let code;
  for (const m of String(text).matchAll(/First-run setup code:\s*([A-Za-z0-9]+)/g)) code = m[1];
  return code;
}

async function readSetupCode(source) {
  if (!source || source === 'none') return undefined;
  const deadline = Date.now() + 45000;
  while (true) {
    let text = '';
    if (source.startsWith('docker-logs:')) {
      const r = docker(['logs', source.slice('docker-logs:'.length)]);
      text = `${r.stdout ?? ''}\n${r.stderr ?? ''}`;
    } else if (source.startsWith('file:')) {
      try { text = readFileSync(source.slice('file:'.length), 'utf8'); } catch { text = ''; }
    } else throw new Error(`--setup-code-from must be docker-logs:NAME, file:PATH or none, got "${source}"`);
    const code = lastSetupCode(text);
    if (code) return code;
    if (Date.now() > deadline) return undefined;
    await sleep(1000);
  }
}

async function waitHealthy(baseUrl, seconds = 120) {
  const deadline = Date.now() + seconds * 1000;
  let last = 'no answer';
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${baseUrl}/api/health`, { signal: AbortSignal.timeout(5000), redirect: 'manual' });
      if (response.status === 200) return { ok: true };
      last = `HTTP ${response.status}`;
    } catch (error) { last = `${error.cause?.code ?? error.name}`; }
    await sleep(1000);
  }
  return { ok: false, last };
}

const MINIMAL_DOCUMENT = {
  title: 'Install matrix journey',
  type: 'architecture',
  nodes: [
    { id: 'n_a', kind: 'service', shape: 'rounded', label: 'A' },
    { id: 'n_b', kind: 'service', shape: 'rounded', label: 'B' },
  ],
  edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
};

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help || !args.baseUrl) {
    console.log(readFileSync(new URL(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).slice(0, 26).map((l) => l.replace(/^\/\/ ?/, '')).join('\n'));
    process.exit(args.help ? 0 : 2);
  }
  const expected = parseExpectations(args.expect);
  const selfcheckExpected = parseSelfcheckExpectations(args.expectSelfcheck);
  const existing = args.signInAs !== undefined;
  const cli = args.cli ?? 'draughtsman';
  const dataDir = args.dataDir ?? '/data';
  const scratch = mkdtempSync(join(tmpdir(), 'dmx-journey-'));
  const baseUrl = args.baseUrl.replace(/\/+$/, '');

  let email;
  let password;
  if (existing) {
    email = args.signInAs;
    password = process.env[args.passwordEnv ?? ''];
    if (!password) throw new Error('--sign-in-as needs --password-env naming an environment variable that holds the password');
  } else {
    email = `matrix-${randomText(8, 'abcdefghjkmnpqrstuvwxyz23456789')}@example.test`;
    password = randomText(24, ALNUM);
  }
  const secrets = [password];
  const startedAt = new Date().toISOString();
  console.log(`journey: ${baseUrl}  email: ${email}  password: ${existing ? 'from the environment' : 'generated'}`);

  const client = new Client(baseUrl, { strict: !args.lenient, secrets });
  const steps = [];
  let blocked; // the id of a step whose failure stops the steps that need a session
  let selfcheckRows = [];
  const state = { doc: undefined, id: undefined, title: undefined };

  /** Run one step. `fn` returns { ok, detail?, http?, notes?, checks?, evidence? } or throws. */
  async function step(id, title, fn, { needsSession = true } = {}) {
    if (needsSession && blocked) {
      steps.push(settleStep({ id, title, status: 'SKIP', detail: `not run: step ${blocked} left no usable session` }, expected));
      return;
    }
    let result;
    try { result = await fn(); } catch (error) { result = { ok: false, detail: `${error.name}: ${redactText(error.message, secrets)}${error.cause?.code ? ` (${error.cause.code})` : ''}` }; }
    if (result.skip) { steps.push(settleStep({ id, title, status: 'SKIP', detail: result.detail }, expected)); return; }
    const raw = { id, title, status: result.ok ? 'PASS' : 'FAIL', http: result.http, detail: result.detail, notes: result.notes ?? [], checks: result.checks ?? [], evidence: result.ok && !result.evidenceAlways ? undefined : result.evidence };
    steps.push(settleStep(raw, expected));
    const last = steps[steps.length - 1];
    console.log(`  [${id}] ${last.status}${last.http !== undefined ? ` (HTTP ${last.http})` : ''}: ${title}${last.detail && last.status !== 'PASS' ? `\n        ${last.detail}` : ''}`);
    if (!result.ok && result.blocks) blocked = blocked ?? id;
  }

  const versionOf = (response) => compareVersionHeaders({ etag: response.headers.get('etag'), xDocumentVersion: response.headers.get('x-document-version') });
  const bestIfMatch = (response) => {
    const v = versionOf(response);
    return v.version === null ? null : quotedStrong(v.version);
  };
  const problem = (r) => `${r.status}${r.json?.title ? ` ${r.json.title}` : ''}${r.json?.detail ? `: ${r.json.detail}` : ''}`;

  // (a) first run required (or, for an existing instance, not required)
  await step('a', existing ? 'GET /api/auth/status shows an existing instance (sign-in, no first run)' : 'GET /api/auth/status shows first run required', async () => {
    const r = await client.request('GET', '/api/auth/status', { anonymous: true });
    if (r.status !== 200) return { ok: false, http: r.status, detail: `expected 200, got ${problem(r)}`, evidence: r.evidence(), blocks: true };
    const want = !existing;
    const ok = r.json?.firstRunRequired === want;
    return { ok, http: r.status, detail: ok ? `firstRunRequired: ${r.json.firstRunRequired}${r.json.firstRun ? ` (host guessed: ${r.json.firstRun.host})` : ''}` : `firstRunRequired is ${r.json?.firstRunRequired}, expected ${want}`, evidence: r.evidence(), blocks: !ok };
  }, { needsSession: false });

  // (b) first run, with the setup code
  await step('b', existing ? 'first run (not applicable: the instance already has users)' : 'POST /api/auth/first-run with the setup code gives 201', async () => {
    if (existing) return { skip: true, detail: 'not applicable: the instance already has users' };
    const code = await readSetupCode(args.setupCodeFrom);
    if (!code) return { ok: false, detail: `no setup code found (--setup-code-from ${args.setupCodeFrom ?? '(not given)'}); the log must show "First-run setup code: ..." for a fresh instance`, blocks: true };
    secrets.push(code);
    const r = await client.request('POST', '/api/auth/first-run', { json: { email, displayName: 'Matrix Journey', password, setupCode: code }, anonymous: true });
    return { ok: r.status === 201, http: r.status, detail: r.status === 201 ? 'administrator created' : `expected 201, got ${problem(r)}`, evidence: r.evidence(), blocks: r.status !== 201 };
  }, { needsSession: false });

  // (c) sign in on a fresh jar; the session must actually work afterwards
  await step('c', 'sign in gives 200 and the session actually works', async () => {
    client.jar.clear();
    const login = await client.request('POST', '/api/auth/login', { json: { email, password }, anonymous: true });
    if (login.status !== 200) return { ok: false, http: login.status, detail: `expected 200, got ${problem(login)}`, evidence: login.evidence(), blocks: true };
    const status = await client.request('GET', '/api/auth/status');
    const signedIn = status.json?.user?.email?.toLowerCase() === email.toLowerCase();
    if (signedIn) return { ok: true, http: login.status, detail: `signed in as ${email}; the session was kept (cookies: ${client.jar.names().join(', ') || 'none'})`, evidence: login.evidence() };
    const droppedNames = [...new Set(client.jar.dropped.map((d) => d.name))];
    const dropped = droppedNames.length === 0 ? [] : [`${droppedNames.join(', ')}: ${client.jar.dropped[0].reason}`];
    const scheme = client.scheme;
    const detail = `signed in but session not kept: sign-in answered 200 and the very next GET /api/auth/status shows no user. The page scheme is ${scheme}://; ` +
      (dropped.length > 0
        ? `a browser would refuse the cookie(s) the server set (${dropped.join('; ')}). If server.forceHttps is true the session cookie is Secure and is only kept over https:// - reach the instance over https, or turn forceHttps off (Admin > Connection check, "Secure cookie").`
        : login.setCookies.length === 0
          ? 'the server set NO cookie on the sign-in response (a proxy or CDN that removes Set-Cookie, or a different server answering).'
          : 'a cookie was set but did not come back (check the proxy and the clock).');
    const evidence = { signIn: login.evidence(), thenStatus: status.evidence(), cookiesRefusedByAStrictBrowser: dropped };
    // Carry on with a lenient (curl-like) jar so the rest of the journey still says something about the server.
    if (login.setCookies.length > 0 && client.jar.strict) {
      client.jar.strict = false;
      const again = await client.request('POST', '/api/auth/login', { json: { email, password }, anonymous: true });
      const check = again.status === 200 ? await client.request('GET', '/api/auth/status') : undefined;
      if (check?.json?.user) {
        evidence.carriedOn = 'the remaining steps ran with a lenient cookie jar (like curl) that keeps Secure cookies over plain http';
        return { ok: false, http: login.status, detail, evidence };
      }
    }
    return { ok: false, http: login.status, detail, evidence, blocks: true };
  }, { needsSession: false });

  // (d) create a document
  await step('d', 'create a document', async () => {
    const r = await client.request('POST', '/api/documents', { json: MINIMAL_DOCUMENT });
    if (r.status !== 201 || !r.json?.id) return { ok: false, http: r.status, detail: `expected 201 with an id, got ${problem(r)}`, evidence: r.evidence() };
    state.id = r.json.id;
    state.doc = r.json;
    return { ok: true, http: r.status, detail: `created ${r.json.id}` };
  });
  const documentBlocked = () => (state.id === undefined ? 'd' : undefined);

  // (e) read it; compare the ETag with X-Document-Version
  let read; // the latest GET response
  let current; // { doc, etagRaw, xdvRaw }
  const refresh = async () => {
    const r = await client.request('GET', `/api/documents/${state.id}`);
    if (r.status === 200) { current = { doc: r.json, etagRaw: r.headers.get('etag'), xdvRaw: r.headers.get('x-document-version') }; read = r; }
    return r;
  };
  await step('e', 'read the document; take its version from X-Document-Version and from the ETag', async () => {
    if (documentBlocked()) return { skip: true, detail: 'not run: step d did not create a document' };
    const r = await refresh();
    if (r.status !== 200) return { ok: false, http: r.status, detail: `expected 200, got ${problem(r)}`, evidence: r.evidence() };
    const v = versionOf(r);
    const detail = `ETag: ${v.etagState}; X-Document-Version: ${v.xdvState}${v.agree === null ? '' : v.agree ? '; both name the same version' : '; they DIFFER'}`;
    return { ok: v.usable && v.agree !== false, http: r.status, detail: v.usable ? detail : `no version header arrived (${detail}): a save has nothing to quote in If-Match`, notes: v.notes, evidence: { headers: r.evidence().headers }, evidenceAlways: false };
  });

  // (f) save once with the ETag exactly as received, once with X-Document-Version
  let staleTag; // a version that is about to be out of date, for step h
  await step('f', 'save (PUT with If-Match) with the ETag exactly as received, and again with X-Document-Version', async () => {
    if (documentBlocked() || !current) return { skip: true, detail: 'not run: step d or e left no document to save' };
    const checks = [];
    const notes = [];
    const put = async (label, ifMatch, title) => {
      const body = { ...current.doc, title };
      const r = await client.request('PUT', `/api/documents/${state.id}`, { json: body, headers: { 'If-Match': ifMatch } });
      const ok = r.status === 200;
      checks.push({ name: label, ok, detail: ok ? `200 with If-Match ${ifMatch.replace(/"dv1:([0-9a-f]{6})[0-9a-f]*"/, '"dv1:$1..."')}` : `${problem(r)}` });
      if (ok) { current = { doc: r.json, etagRaw: r.headers.get('etag'), xdvRaw: r.headers.get('x-document-version') }; state.title = title; }
      return { r, ok };
    };
    let allOk = true;
    let lastEvidence;
    if (current.etagRaw) {
      const { r, ok } = await put('save with the ETag exactly as received', current.etagRaw, 'Journey save 1 (ETag)');
      if (!ok) { allOk = false; lastEvidence = r.evidence(); }
    } else { checks.push({ name: 'save with the ETag exactly as received', ok: true, detail: 'not possible: no ETag arrived (reported in step e)' }); notes.push('no ETag arrived, so the "ETag as received" save could not be made'); }
    const fresh = await refresh();
    if (fresh.status !== 200) return { ok: false, http: fresh.status, detail: `re-reading before the second save: ${problem(fresh)}`, checks, evidence: fresh.evidence() };
    const xdvQuoted = current.xdvRaw ? quotedStrong(current.xdvRaw) : null;
    staleTag = xdvQuoted ?? (current.etagRaw ?? null);
    if (xdvQuoted) {
      const { r, ok } = await put('save with X-Document-Version (quoted)', xdvQuoted, 'Journey save 2 (X-Document-Version)');
      if (!ok) { allOk = false; lastEvidence = r.evidence(); }
    } else if (current.etagRaw) {
      notes.push('X-Document-Version did not arrive; the second save used the ETag');
      const { r, ok } = await put('save with the ETag (X-Document-Version missing)', current.etagRaw, 'Journey save 2 (ETag)');
      if (!ok) { allOk = false; lastEvidence = r.evidence(); }
    } else {
      return { ok: false, detail: 'no version header (neither ETag nor X-Document-Version) arrived, so no save can be made: an intermediary strips both', checks, evidence: fresh.evidence() };
    }
    return { ok: allOk, http: 200, detail: allOk ? 'both saves were accepted' : 'a save was refused (see the failed check)', notes, checks, evidence: lastEvidence };
  });

  // (g) rename
  let beforeRename;
  await step('g', 'rename (PATCH)', async () => {
    if (documentBlocked() || !current) return { skip: true, detail: 'not run: no document' };
    await refresh();
    const ifMatch = current.xdvRaw ? quotedStrong(current.xdvRaw) : current.etagRaw;
    if (!ifMatch) return { ok: false, detail: 'no version header to quote in If-Match' };
    beforeRename = ifMatch;
    const r = await client.request('PATCH', `/api/documents/${state.id}`, { json: { title: 'Journey renamed' }, headers: { 'If-Match': ifMatch } });
    if (r.status !== 200 || r.json?.title !== 'Journey renamed') return { ok: false, http: r.status, detail: `expected 200 and the new title, got ${problem(r)}`, evidence: r.evidence() };
    current = { doc: r.json, etagRaw: r.headers.get('etag'), xdvRaw: r.headers.get('x-document-version') };
    state.title = 'Journey renamed';
    return { ok: true, http: r.status, detail: 'renamed' };
  });

  // (h) a stale save is refused
  await step('h', 'a stale save gives 412', async () => {
    if (documentBlocked() || !beforeRename) return { skip: true, detail: 'not run: no earlier version to be stale' };
    const r = await client.request('PUT', `/api/documents/${state.id}`, { json: { ...current.doc, title: 'Stale write' }, headers: { 'If-Match': beforeRename } });
    return { ok: r.status === 412, http: r.status, detail: r.status === 412 ? 'refused as stale (412 Document has changed)' : `expected 412, got ${problem(r)}`, evidence: r.evidence() };
  });

  // (i) name a version, change the document, restore the version
  await step('i', 'name a version, change the document, restore the version', async () => {
    if (documentBlocked() || !current) return { skip: true, detail: 'not run: no document' };
    const snap = await client.request('POST', `/api/documents/${state.id}/versions`, { json: { name: 'Journey snapshot', reason: 'install matrix' } });
    if (snap.status !== 201 || !snap.json?.id) return { ok: false, http: snap.status, detail: `naming a version: expected 201 with an id, got ${problem(snap)}`, evidence: snap.evidence() };
    await refresh();
    const base = current.xdvRaw ? quotedStrong(current.xdvRaw) : current.etagRaw;
    if (!base) return { ok: false, detail: 'no version header (neither ETag nor X-Document-Version) arrived, so the document cannot be changed after the snapshot' };
    const rename = await client.request('PATCH', `/api/documents/${state.id}`, { json: { title: 'After the snapshot' }, headers: { 'If-Match': base } });
    if (rename.status !== 200) return { ok: false, http: rename.status, detail: `changing the document after the snapshot: ${problem(rename)}`, evidence: rename.evidence() };
    current = { doc: rename.json, etagRaw: rename.headers.get('etag'), xdvRaw: rename.headers.get('x-document-version') };
    const restoreBase = current.xdvRaw ? quotedStrong(current.xdvRaw) : current.etagRaw;
    if (!restoreBase) return { ok: false, detail: 'no version header arrived after the change, so the restore has nothing to quote in If-Match' };
    const restore = await client.request('POST', `/api/documents/${state.id}/versions/${snap.json.id}/restore`, { headers: { 'If-Match': restoreBase } });
    if (restore.status !== 200) return { ok: false, http: restore.status, detail: `restore: expected 200, got ${problem(restore)}`, evidence: restore.evidence() };
    const back = restore.json?.title === 'Journey renamed';
    if (back) current = { doc: restore.json, etagRaw: restore.headers.get('etag'), xdvRaw: restore.headers.get('x-document-version') };
    return { ok: back, http: restore.status, detail: back ? 'restored: the title is back to the snapshot\'s' : `restored, but the title is "${restore.json?.title}", not "Journey renamed"`, evidence: back ? undefined : restore.evidence() };
  });

  // (j) exports: SVG over MCP, DSL and Mermaid text, PDF only when health says it works; PNG is client-side only
  await step('j', 'export: SVG (server render), DSL and Mermaid text; PDF only if the instance says it can', async () => {
    if (documentBlocked() || !current) return { skip: true, detail: 'not run: no document' };
    const checks = [];
    const notes = [];
    const text = await client.request('POST', '/api/export/dsl', { json: current.doc });
    checks.push({ name: 'POST /api/export/dsl', ok: text.status === 200 && text.text.length > 0, detail: String(text.status) });
    const mermaid = await client.request('POST', '/api/export/mermaid', { json: current.doc });
    checks.push({ name: 'POST /api/export/mermaid', ok: mermaid.status === 200 && mermaid.text.length > 0, detail: String(mermaid.status) });
    // SVG: MCP render_document (needs the sidecar), with a throw-away API token so the bearer path is exercised too.
    const token = await client.request('POST', '/api/tokens', { json: { name: `matrix-${randomText(6, 'abcdefghjkmnpqrstuvwxyz')}` } });
    let svgOk = false;
    let svgDetail = `token ${token.status}`;
    if (token.status === 201 && token.json?.token) {
      secrets.push(token.json.token);
      const mcp = new Client(baseUrl, { strict: false, secrets });
      mcp.bearer = token.json.token;
      const call = async (message, session) => {
        const r = await mcp.request('POST', '/mcp', { json: message, anonymous: false, headers: { Accept: 'application/json, text/event-stream', ...(session ? { 'Mcp-Session-Id': session } : {}) } });
        const body = r.text.trim().startsWith('{') ? r.text : (r.text.split('\n').find((l) => l.startsWith('data:')) ?? '').slice(5).trim();
        let parsed; try { parsed = body ? JSON.parse(body) : undefined; } catch { parsed = undefined; }
        return { r, parsed };
      };
      const init = await call({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'install-matrix', version: '1' } } });
      const session = init.r.headers.get('mcp-session-id') ?? undefined;
      await call({ jsonrpc: '2.0', method: 'notifications/initialized' }, session);
      const rendered = await call({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'render_document', arguments: { id: state.id } } }, session);
      let svg = '';
      try { svg = JSON.parse(rendered.parsed?.result?.content?.[0]?.text ?? '{}').svg ?? ''; } catch { svg = ''; }
      svgOk = svg.startsWith('<svg');
      svgDetail = svgOk ? `${svg.length} characters of SVG` : `no SVG: MCP answered ${rendered.r.status} ${bodyExcerpt(rendered.r.text, secrets, 300)}`;
    }
    checks.push({ name: 'SVG (MCP render_document over the sidecar)', ok: svgOk, detail: svgDetail });
    notes.push('PNG is drawn in the browser only (web/src/editor/exportDiagram.tsx); the server has no PNG export, so it is not exercised by a scripted journey');
    const health = await client.request('GET', '/api/health');
    if (health.json?.export?.pdf === true) {
      const pdf = await client.request('GET', `/api/documents/${state.id}/pdf`, { raw: true });
      checks.push({ name: 'PDF (health says export.pdf is true)', ok: pdf.status === 200 && pdf.bytes.subarray(0, 4).toString() === '%PDF', detail: String(pdf.status) });
    } else notes.push('PDF skipped: GET /api/health says export.pdf is false (the optional Chromium component is not enabled)');
    const ok = checks.every((c) => c.ok);
    return { ok, detail: ok ? checks.map((c) => c.name).join('; ') : checks.filter((c) => !c.ok).map((c) => `${c.name}: ${c.detail}`).join('; '), checks, notes };
  });

  // (k) the server's own Connection check, and the browser half (cookie, ETag and encoding as this client saw them)
  await step('k', 'GET /api/admin/selfcheck (and its browser round trip): print each row', async () => {
    const pageScheme = client.scheme;
    const r = await client.request('GET', `/api/admin/selfcheck?pageScheme=${pageScheme}`);
    if (r.status !== 200 || !Array.isArray(r.json?.items)) return { ok: false, http: r.status, detail: `expected 200 and items, got ${problem(r)}`, evidence: r.evidence() };
    const rows = [...r.json.items];
    const notes = [];
    // The echo half: only a client can see what a proxy did to a response.
    const echo = async (n) => {
      const e = await client.request('GET', `/api/admin/selfcheck/echo?step=${n}`);
      return { e, view: { status: e.status, etagHeader: e.headers.get('etag'), contentEncoding: e.headers.get('content-encoding'), bodyTag: e.json?.tag ?? null, cookieReceived: e.json?.cookieReceived === true, cookieWasSecure: e.json?.cookieWasSecure === true, serverTime: e.json?.serverTime ?? new Date().toISOString() } };
    };
    try {
      const first = await echo(1);
      const second = await echo(2);
      const round = await client.request('POST', '/api/admin/selfcheck/roundtrip', { json: { pageScheme, first: first.view, second: second.view, clientTimeMs: Date.now() } });
      if (round.status === 200 && Array.isArray(round.json?.items)) {
        for (const item of round.json.items) if (!rows.some((x) => x.id === item.id)) rows.push(item);
      } else notes.push(`the browser round trip answered ${problem(round)}`);
    } catch (error) { notes.push(`the browser round trip could not run: ${error.message}`); }
    selfcheckRows = rows.map((i) => ({ id: i.id, status: i.status, title: i.title, summary: i.summary }));
    const verdict = judgeSelfcheck(rows, selfcheckExpected);
    for (const row of selfcheckRows) console.log(`        ${row.status.padEnd(5)} ${row.id.padEnd(8)} ${row.title}`);
    return { ok: verdict.ok, http: r.status, detail: verdict.ok ? `${rows.length} rows: ${rows.map((i) => `${i.id}=${i.status}`).join(', ')}` : verdict.problems.join(' | '), notes, evidence: rows.map((i) => ({ id: i.id, status: i.status, summary: i.summary })), evidenceAlways: false };
  });

  // (l) the CLI verbs, in the container, and a restart
  let currentPassword = password;
  await step('l', 'in the container: doctor, support-bundle, reset-admin --generate then sign in, docker restart then sign in', async () => {
    if (!args.container) return { skip: true, detail: 'not run: no --container given' };
    const name = args.container;
    const checks = [];
    const notes = [];
    const exec = (...rest) => docker(['exec', name, cli, ...rest], { timeout: 180000 });
    const doctor = exec('doctor');
    const doctorOk = doctor.status === 0 || doctor.status === 1;
    checks.push({ name: 'doctor', ok: doctorOk, detail: `exit ${doctor.status} (0 no problems, 1 warnings, 2 failures)` });
    if (doctor.status === 1) notes.push(`doctor warned: ${redactText((doctor.stdout || '').split('\n').filter((l) => /warn/i.test(l)).slice(0, 3).join(' | '), secrets)}`);
    if (!doctorOk) {
      // The findings that failed (the lines marked FAIL or ERROR, with the line that explains them), then the start of the output for context.
      const out = `${doctor.stdout}\n${doctor.stderr}`.split('\n');
      const failing = out.flatMap((l, i) => (/^\s*(FAIL|ERROR|FAILED)\b/i.test(l) ? [l.trim(), (out[i + 1] ?? '').trim()] : [])).filter(Boolean);
      notes.push(`doctor failed: ${bodyExcerpt(failing.join(' | ') || out.slice(0, 12).join(' | '), secrets, 900)}`);
    }

    const bundlePath = `${dataDir}/b.zip`;
    const bundle = exec('support-bundle', '--output', bundlePath);
    let entries = [];
    if (bundle.status === 0) {
      const copy = join(scratch, 'b.zip');
      const cp = docker(['cp', `${name}:${bundlePath}`, copy]);
      if (cp.status === 0) { try { entries = zipEntryNames(readFileSync(copy)); } catch (error) { notes.push(`the bundle would not read as a zip: ${error.message}`); } }
      docker(['exec', name, 'rm', '-f', bundlePath]);
    }
    const leaked = entries.filter((n) => /(^|\/)(data\.db|licence\.key|keys\/|.*\.pem)/i.test(n) && !/listing/i.test(n));
    checks.push({ name: 'support-bundle', ok: bundle.status === 0 && entries.length > 0 && leaked.length === 0, detail: bundle.status === 0 ? `${entries.length} entries: ${entries.join(', ')}${leaked.length ? `; SENSITIVE-LOOKING ENTRY: ${leaked.join(', ')}` : ''}` : `exit ${bundle.status}: ${bodyExcerpt(bundle.stderr || bundle.stdout, secrets, 400)}` });

    const reset = exec('reset-admin', email, '--generate');
    const fresh = /New password \(shown once\):\s*(\S+)/.exec(reset.stdout ?? '')?.[1];
    if (fresh) secrets.push(fresh);
    let resetOk = false;
    let resetDetail = `exit ${reset.status}${fresh ? '' : `: ${bodyExcerpt(reset.stderr || reset.stdout, secrets, 300)}`}`;
    if (reset.status === 0 && fresh) {
      const c2 = new Client(baseUrl, { strict: client.jar.strict, secrets });
      const login = await c2.request('POST', '/api/auth/login', { json: { email, password: fresh }, anonymous: true });
      const who = login.status === 200 ? await c2.request('GET', '/api/auth/status') : login;
      resetOk = login.status === 200 && who.json?.user?.email?.toLowerCase() === email.toLowerCase();
      resetDetail = resetOk ? 'reset-admin --generate gave a password that signs in (the old session and tokens were revoked, as documented)' : `signing in with the generated password: ${problem(login)}`;
      if (resetOk) { currentPassword = fresh; client.jar.clear(); }
    }
    checks.push({ name: 'reset-admin --generate, then sign in with the new password', ok: resetOk, detail: resetDetail });

    const restart = docker(['restart', name], { timeout: 180000 });
    let restartOk = false;
    let restartDetail = `docker restart exit ${restart.status}`;
    if (restart.status === 0) {
      const health = await waitHealthy(baseUrl);
      if (!health.ok) restartDetail = `the instance did not answer /api/health again after the restart (${health.last})`;
      else {
        const c3 = new Client(baseUrl, { strict: client.jar.strict, secrets });
        const login = await c3.request('POST', '/api/auth/login', { json: { email, password: currentPassword }, anonymous: true });
        const who = login.status === 200 ? await c3.request('GET', '/api/auth/status') : login;
        restartOk = login.status === 200 && who.json?.user?.email?.toLowerCase() === email.toLowerCase();
        restartDetail = restartOk ? 'after docker restart the instance is healthy and sign-in works' : `after the restart, sign-in: ${problem(login)}`;
        if (restartOk) client.jar.clear();
      }
    }
    checks.push({ name: 'docker restart, then sign in again', ok: restartOk, detail: restartDetail });
    const ok = checks.every((c) => c.ok);
    return { ok, detail: ok ? checks.map((c) => c.name).join('; ') : checks.filter((c) => !c.ok).map((c) => `${c.name}: ${c.detail}`).join(' | '), checks, notes, evidenceAlways: false };
  }, { needsSession: false });

  // (m) sign out and sign in again
  await step('m', 'sign out, then sign in again', async () => {
    const out = new Client(baseUrl, { strict: client.jar.strict, secrets });
    const login = await out.request('POST', '/api/auth/login', { json: { email, password: currentPassword }, anonymous: true });
    if (login.status !== 200) return { ok: false, http: login.status, detail: `signing in before signing out: ${problem(login)}`, evidence: login.evidence() };
    const logout = await out.request('POST', '/api/auth/logout');
    const after = await out.request('GET', '/api/auth/status');
    const signedOut = logout.status === 204 && !after.json?.user;
    const again = await out.request('POST', '/api/auth/login', { json: { email, password: currentPassword }, anonymous: true });
    const who = again.status === 200 ? await out.request('GET', '/api/auth/status') : again;
    const back = again.status === 200 && who.json?.user?.email?.toLowerCase() === email.toLowerCase();
    const ok = signedOut && back;
    return { ok, http: logout.status, detail: ok ? 'signed out (204, no user), then signed in again with a working session' : `${signedOut ? '' : `sign-out: ${problem(logout)}, user after: ${after.json?.user ? 'still signed in' : 'none'}; `}${back ? '' : `sign-in again: ${problem(again)}`}`, evidence: ok ? undefined : { logout: logout.evidence(), again: again.evidence() } };
  }, { needsSession: false });

  const finishedAt = new Date().toISOString();
  const exitCode = exitCodeFor(steps);
  console.log(`\n${formatStepTable(steps)}`);
  const details = formatStepDetails(steps);
  if (details) console.log(`\n${details}`);
  console.log(`\njourney: ${exitCode === 0 ? 'PASS' : 'FAIL'} (${steps.filter((s) => s.status === 'PASS').length} passed, ${steps.filter((s) => s.status === 'EXPECTED-FAIL').length} expected-fail, ${steps.filter((s) => s.status === 'FAIL' || s.status === 'UNEXPECTED-PASS').length} failed, ${steps.filter((s) => s.status === 'SKIP').length} skipped)`);
  if (args.json) {
    const result = { baseUrl, startedAt, finishedAt, email, steps, selfcheck: selfcheckRows, exitCode };
    writeFileSync(args.json, redactText(JSON.stringify(result, null, 2), secrets));
  }
  rmSync(scratch, { recursive: true, force: true });
  process.exit(exitCode);
}

main().catch((error) => { console.error(`journey: ${error.message}`); process.exit(2); });
