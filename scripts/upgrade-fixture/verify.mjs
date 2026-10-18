#!/usr/bin/env node
// Checks a RUNNING server against the expected.json that populate.mjs wrote for an earlier build: the same data, read back
// through the real HTTP API, after an upgrade (docs/upgrade-backup.md). Used three ways:
//   * make-upgrade-fixture.sh runs it with --self-check against the build that just wrote the data (the script and the
//     expectations must agree before the data is kept as a fixture);
//   * the archive-swap and Docker-volume proofs (scripts/upgrade-path-proof.sh) run it against the NEW binary on the OLD data;
//   * the xunit upgrade tests do the same checks in process (tests/Draughtsman.Server.Tests/UpgradeFixtureTests.cs).
//
//   node verify.mjs --base http://127.0.0.1:5180 --expected expected.json [--self-check] [--same-install-path] [--secrets-readable]
//
// Exit 0 when every check passes, 1 when any fails (each failure is printed), 2 on a usage error.
import { readFileSync } from 'node:fs';
import { Client, hashJson, sha256 } from './client.mjs';

const args = parseArgs(process.argv.slice(2));
const base = required('base');
const expected = JSON.parse(readFileSync(required('expected'), 'utf8'));
const selfCheck = Boolean(args['self-check']);
const sameInstallPath = selfCheck || Boolean(args['same-install-path']);
// Builds before the Data Protection application name was pinned bound the stored AI key and SMTP password to the install path, so only a run from
// the same path could read them back (--same-install-path). This build binds them to the key ring alone: --secrets-readable asks for them to read
// from ANY path (the pin proof, scripts/dataprotection-pin-proof.sh).
const secretsReadable = sameInstallPath || Boolean(args['secrets-readable']);

let passed = 0;
const failures = [];
const check = (name, ok, detail = '') => {
  if (ok) passed++;
  else failures.push(`${name}${detail ? `: ${detail}` : ''}`);
};

async function main() {
  const byKey = Object.fromEntries(expected.users.map((u) => [u.key, u]));
  const admin = new Client(base, 'admin');
  const signedIn = await admin.post('/api/auth/login', { email: byKey.admin.email, password: byKey.admin.password });
  check('the administrator signs in with the same password', signedIn.status === 200, `status ${signedIn.status}`);
  if (signedIn.status !== 200) return;

  await users(admin);
  await tokens(admin);
  await documents(admin);
  await bin(admin);
  await assets(admin);
  await branding(admin);
  await themes(admin);
  await ai(admin);
  await email(admin);
  await securityEvents(admin);
  await audit(admin);
}

async function users(admin) {
  const list = await admin.get('/api/users');
  check('GET /api/users answers', list.status === 200, `status ${list.status}`);
  const byEmail = new Map((list.json ?? []).map((u) => [u.email, u]));
  for (const u of expected.users) {
    const row = byEmail.get(u.email);
    check(`user ${u.email} is still there`, Boolean(row));
    if (!row) continue;
    check(`user ${u.email} keeps id ${u.id}`, row.id === u.id, `now ${row.id}`);
    check(`user ${u.email} keeps role ${u.role}`, row.role === u.role, `now ${row.role}`);
    check(`user ${u.email} keeps active=${u.active}`, (row.active !== false) === u.active, `now ${row.active}`);
    if (u.key === 'admin') continue;
    const client = new Client(base, u.key);
    const login = await client.post('/api/auth/login', { email: u.email, password: u.password });
    if (u.active) check(`${u.email} signs in with the same password`, login.status === 200, `status ${login.status}`);
    else check(`${u.email} (deactivated) is still refused`, login.status === 401 || login.status === 403, `status ${login.status}`);
  }
}

async function tokens(admin) {
  for (const t of expected.tokens) {
    const client = Client.withBearer(base, t.secret, t.name);
    const read = await client.get('/api/documents?limit=1');
    if (t.revoked) {
      check(`revoked token "${t.name}" is still refused`, read.status === 401, `status ${read.status}`);
      continue;
    }
    check(`token "${t.name}" still authenticates`, read.status === 200, `status ${read.status}`);
    if (t.scope === 'read') {
      // A read-scoped token may only GET; a write is refused before the body is even looked at, so nothing is stored.
      const write = await client.post('/api/documents', { title: 'must not be stored', type: 'freeform', nodes: [], edges: [] });
      check(`read-scoped token "${t.name}" is still refused a write`, write.status === 403, `status ${write.status}`);
    }
  }
  const mine = await admin.get('/api/tokens');
  for (const t of expected.tokens.filter((x) => x.owner === 'admin' && !x.revoked)) {
    const row = (mine.json ?? []).find((r) => r.id === t.id);
    check(`token "${t.name}" is listed for its owner`, Boolean(row));
    if (row && t.expiresAt) check(`token "${t.name}" keeps its expiry`, row.expiresAt === t.expiresAt, `now ${row.expiresAt}`);
    if (row && t.scope) check(`token "${t.name}" keeps its scope`, row.scope === t.scope, `now ${row.scope}`);
  }
}

async function documents(admin) {
  const list = await admin.get('/api/documents?limit=200');
  check('the document list answers', list.status === 200);
  check('the document count is unchanged', list.json?.total === expected.documentListTotal, `${list.json?.total} vs ${expected.documentListTotal}`);
  for (const d of expected.documents) {
    const res = await admin.get(`/api/documents/${d.id}`);
    check(`document ${d.id} opens`, res.status === 200, `status ${res.status}`);
    if (res.status !== 200) continue;
    // expected.json keeps the hash of what the old build served, not its bytes (the xunit tests read the old snapshot from
    // the fixture's own database and can name the member that differs); here any difference is a failure.
    const now = hashJson(res.json);
    check(`document ${d.id} content hash is unchanged`, now === d.sha256, `${d.sha256.slice(0, 12)} -> ${now.slice(0, 12)} (nodes ${d.nodes}->${res.json.nodes.length}, edges ${d.edges}->${res.json.edges.length})`);
    if (d.dslSha256) {
      const dsl = await admin.request('POST', '/api/export/dsl', { json: res.json, headers: { Accept: 'text/plain' } });
      check(`document ${d.id} prints the same DSL`, dsl.status === 200 && sha256(dsl.bytes) === d.dslSha256, `status ${dsl.status}`);
    }
    check(`document ${d.id} is saveable (serves an ETag)`, Boolean(res.headers.get('etag')));
    const versions = await admin.get(`/api/documents/${d.id}/versions?limit=200`);
    const expectedVersions = expected.versions.filter((v) => v.documentId === d.id);
    const have = new Map((versions.json?.items ?? []).map((v) => [v.id, v]));
    for (const v of expectedVersions) {
      check(`version ${v.id} of ${d.id} is still listed`, have.has(v.id));
      if (!have.has(v.id)) continue;
      const full = await admin.get(`/api/documents/${d.id}/versions/${v.id}`);
      check(`version ${v.id} of ${d.id} has the same snapshot`, full.status === 200 && hashJson(full.json.document) === v.documentSha256);
      check(`version ${v.id} keeps its name and actor`, have.get(v.id).name === v.name && have.get(v.id).actor === v.actor);
    }
  }
}

async function bin(admin) {
  if (expected.binned.length === 0) return;
  const res = await admin.get('/api/bin?limit=200');
  check('the bin answers', res.status === 200, `status ${res.status}`);
  const have = new Map((res.json?.items ?? []).map((i) => [i.id, i]));
  for (const b of expected.binned) {
    check(`binned document ${b.id} is still in the bin`, have.has(b.id));
    const live = await admin.get(`/api/documents/${b.id}`);
    check(`binned document ${b.id} is not live`, live.status === 404, `status ${live.status}`);
  }
}

async function assets(admin) {
  for (const a of expected.assets) {
    const res = await admin.get(`/api/assets/${a.id}`);
    check(`uploaded image ${a.id} is served`, res.status === 200, `status ${res.status}`);
    if (res.status === 200) check(`uploaded image ${a.id} has the same bytes`, sha256(res.bytes) === a.sha256);
  }
}

async function branding(admin) {
  if (!expected.branding) return;
  const res = await admin.get('/api/admin/branding');
  check('branding answers', res.status === 200);
  check('the product name is unchanged', res.json?.productName === expected.branding.productName, `now ${res.json?.productName}`);
  check('the overridden slots are unchanged', JSON.stringify(res.json?.overridden) === JSON.stringify(expected.branding.state?.overridden));
  for (const [slot, path] of [['logo', '/api/theme/logo'], ['logo-light', '/api/theme/logo-light'], ['favicon', '/api/theme/favicon']]) {
    const want = expected.branding.served[slot];
    if (!want?.sha256) continue;
    const served = await admin.get(path);
    check(`branding ${slot} serves the same bytes`, served.status === 200 && sha256(served.bytes) === want.sha256, `status ${served.status}`);
  }
}

async function themes(admin) {
  if (!expected.themes) return;
  const res = await admin.get('/api/admin/themes');
  check('organisation themes answer', res.status === 200);
  check('the default organisation theme is unchanged', res.json?.defaultId === expected.themes.defaultId, `now ${res.json?.defaultId}`);
  // updatedAt is the theme file's modification time, which a copy or restore of the data folder does not keep.
  const strip = (list) => ({ ...list, themes: (list.themes ?? []).map(({ updatedAt, ...rest }) => rest) });
  check('the organisation themes are unchanged', hashJson(strip(res.json)) === hashJson(strip(expected.themes.list)));
}

async function ai(admin) {
  if (!expected.ai) return;
  const res = await admin.get('/api/admin/ai');
  check('AI settings answer', res.status === 200);
  const want = expected.ai.state ?? {};
  for (const field of ['provider', 'model', 'endpoint']) {
    const have = res.json?.[field] ?? res.json?.settings?.[field] ?? res.json?.effective?.[field];
    const wantValue = want[field] ?? want.settings?.[field] ?? want.effective?.[field];
    if (wantValue !== undefined) check(`the AI ${field} is unchanged`, have === wantValue, `now ${have}`);
  }
  const key = res.json?.key ?? {};
  check('the stored AI key is still in the settings file', key.storedSet === true || key.storedUnreadable === true, JSON.stringify(key));
  // Builds before the pin bound the stored key to the install folder: it opened only from the same path (an in-place swap, a Docker image's /app).
  // --same-install-path says this run is such a case; --secrets-readable says it must read from any path.
  if (secretsReadable) check('the stored AI key is still readable', key.storedSet === true && key.storedUnreadable === false, JSON.stringify(key));
}

async function email(admin) {
  if (!expected.email) return;
  const res = await admin.get('/api/admin/email');
  check('email settings answer', res.status === 200, `status ${res.status}`);
  const want = expected.email.state ?? {};
  for (const field of ['from', 'host', 'port', 'tls', 'user', 'publicUrl']) {
    check(`the email ${field} is unchanged`, res.json?.[field] === want[field], `now ${res.json?.[field]}`);
  }
  // The saved password is a Data Protection payload: it opens only under the install path it was protected under (see ai above).
  check('the saved email password is still there', res.json?.passwordSet === true || res.json?.passwordUnreadable === true, JSON.stringify({ set: res.json?.passwordSet, unreadable: res.json?.passwordUnreadable }));
  if (secretsReadable) check('the saved email password is still readable', res.json?.passwordSet === true && !res.json?.passwordUnreadable);
}

async function securityEvents(admin) {
  if (!expected.securityEvents) return;
  const res = await admin.get('/api/admin/security-events?limit=200');
  check('the security log answers', res.status === 200);
  const items = res.json?.items ?? res.json ?? [];
  const have = new Map(items.map((e) => [e.id, hashJson(e)]));
  const missing = expected.securityEvents.filter((e) => !have.has(e.id));
  check('every security event is still in the log', missing.length === 0, `${missing.length} missing of ${expected.securityEvents.length}`);
  const changed = expected.securityEvents.filter((e) => have.has(e.id) && have.get(e.id) !== e.hash);
  check('no security event has changed', changed.length === 0, `${changed.length} changed`);
}

async function audit(admin) {
  for (const [documentId, rows] of Object.entries(expected.audit)) {
    const res = await admin.get(`/api/admin/audit?documentId=${documentId}&limit=200`);
    if (res.status !== 200) {
      check(`audit rows of ${documentId} answer`, false, `status ${res.status}`);
      continue;
    }
    const now = res.json.items ?? res.json;
    check(`audit rows of ${documentId} are all still there`, now.length >= rows.length, `${now.length} vs ${rows.length}`);
  }
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) continue;
    const key = argv[i].slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) out[key] = true;
    else { out[key] = next; i++; }
  }
  return out;
}

function required(name) {
  if (!args[name] || args[name] === true) {
    console.error(`verify: --${name} is required`);
    process.exit(2);
  }
  return args[name];
}

await main();
for (const f of failures) console.error(`  FAIL ${f}`);
console.log(`[verify] ${passed} checks passed, ${failures.length} failed`);
process.exit(failures.length === 0 ? 0 : 1);
