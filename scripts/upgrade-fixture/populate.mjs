#!/usr/bin/env node
// Fills a RUNNING Draughtsman server with realistic, throwaway data through its real HTTP API and writes expected.json:
// what a later build must still show for that data after an upgrade (docs/upgrade-backup.md, tests/upgrade-fixtures/README.md).
//
//   node scripts/upgrade-fixture/populate.mjs --base http://127.0.0.1:5180 --setup-code ABCD234567 \
//        --examples <checkout>/docs/examples --out expected.json --label <label> ...
//
// It is written to run against a build of ANY era: each feature is tried, and one the build does not have (a route that
// answers 404, a schema that refuses a member) is recorded in `features` as skipped with the reason, never as a failure.
// Only the steps every build can do (first-run, documents) abort the run. Everything it creates is throwaway: the
// addresses end in .invalid, the passwords, tokens and AI key say so, and no real secret is read or written.
import { readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { Client, hashJson, sha256, tinyPng, multipartFile } from './client.mjs';

const args = parseArgs(process.argv.slice(2));
const base = required('base');
const setupCode = args['setup-code'] ?? null;
const examplesDir = required('examples');
const outFile = required('out');
const label = args.label ?? 'unlabelled';

const PEOPLE = {
  admin: { email: 'fixture-admin@example.invalid', displayName: 'Fixture Admin (throwaway)', password: 'Fixture-Admin-THROWAWAY-pw-1', role: 'Admin' },
  editor: { email: 'fixture-editor@example.invalid', displayName: 'Fixture Editor (throwaway)', password: 'Fixture-Editor-THROWAWAY-pw-2', role: 'Editor' },
  retired: { email: 'fixture-retired@example.invalid', displayName: 'Fixture Retired (throwaway)', password: 'Fixture-Retired-THROWAWAY-pw-3', role: 'Editor' },
};
const AI_KEY = 'sk-THROWAWAY-upgrade-fixture-not-a-real-key-0123456789';
const AGENT_ACTOR = 'agent:upgrade-fixture/throwaway-model';

const expected = {
  formatVersion: 1,
  label,
  createdAt: new Date().toISOString(),
  features: {},
  users: [],
  tokens: [],
  documents: [],
  binned: [],
  versions: [],
  assets: [],
  audit: {},
  securityEvents: null,
  skippedDocuments: [],
};

const features = expected.features;
const log = (...parts) => console.log('[populate]', ...parts);

async function main() {
  const health = await (await fetch(`${base}/api/health`)).json();
  expected.build = { reportedVersion: health.version ?? null };
  log(`server ${JSON.stringify(health).slice(0, 160)}`);

  const admin = new Client(base, 'admin');
  await firstRun(admin);
  const editor = await createUser(admin, PEOPLE.editor);
  const retired = await createUser(admin, PEOPLE.retired);
  const editorClient = await signIn(PEOPLE.editor, 'editor');
  await retireUser(admin, retired);
  await recordUsers(admin);

  const tokens = await makeTokens(admin, editorClient);
  const agent = tokens.agent ? Client.withBearer(base, tokens.agent.secret, 'agent', AGENT_ACTOR) : null;

  const created = await createDocuments(admin, editorClient, agent);
  await makeVersions(admin, agent, created);
  await makeAssetsAndImageDocument(admin);
  await binSome(admin, editorClient, created);
  await brandingStep(admin);
  await themesStep(admin);
  await aiStep(admin);
  await emailStep(admin);
  await securityEventsStep(admin);

  await recordDocuments(admin);
  await recordAudit(admin);

  writeFileSync(outFile, `${JSON.stringify(expected, null, 2)}\n`);
  log(`wrote ${outFile}: ${expected.documents.length} documents, ${expected.users.length} users, ${expected.tokens.length} tokens, ${expected.versions.length} versions`);
}

// ---------------------------------------------------------------------------------------------------------------- users

async function firstRun(admin) {
  const status = await admin.get('/api/auth/status');
  if (status.status !== 200) throw new Error(`/api/auth/status answered ${status.status}; this build has no local sign-in, which the fixtures need`);
  if (status.json?.firstRunRequired === false) throw new Error('The server already has users; populate needs a fresh data folder');
  const body = { email: PEOPLE.admin.email, displayName: PEOPLE.admin.displayName, password: PEOPLE.admin.password };
  if (setupCode) body.setupCode = setupCode;
  const res = await admin.post('/api/auth/first-run', body);
  if (res.status !== 201 && res.status !== 200) throw new Error(`first-run answered ${res.status}: ${res.text.slice(0, 300)}`);
  log('first-run done; admin signed in');
}

async function signIn(person, name) {
  const client = new Client(base, name);
  const res = await client.post('/api/auth/login', { email: person.email, password: person.password });
  if (res.status !== 200) throw new Error(`sign-in as ${name} answered ${res.status}: ${res.text.slice(0, 300)}`);
  return client;
}

async function createUser(admin, person) {
  const res = await admin.post('/api/users', { email: person.email, displayName: person.displayName, password: person.password, role: person.role });
  if (res.status !== 201) throw new Error(`creating ${person.email} answered ${res.status}: ${res.text.slice(0, 300)}`);
  return res.json;
}

async function retireUser(admin, user) {
  const res = await admin.patch(`/api/users/${user.id}`, { active: false });
  if (res.status !== 200) throw new Error(`deactivating answered ${res.status}: ${res.text.slice(0, 300)}`);
}

async function recordUsers(admin) {
  const list = await admin.get('/api/users');
  const byEmail = new Map((list.json ?? []).map((u) => [u.email, u]));
  for (const [key, person] of Object.entries(PEOPLE)) {
    const row = byEmail.get(person.email);
    if (!row) throw new Error(`user ${person.email} is missing from /api/users`);
    expected.users.push({
      key,
      id: row.id,
      email: person.email,
      displayName: row.displayName,
      role: row.role,
      password: person.password,
      active: row.active !== false,
    });
  }
}

// ---------------------------------------------------------------------------------------------------------------- tokens

async function makeTokens(admin, editorClient) {
  const out = {};
  const make = async (client, owner, name, extra = {}) => {
    const res = await client.post('/api/tokens', { name, ...extra });
    if (res.status !== 201) {
      log(`token ${name} answered ${res.status}: ${res.text.slice(0, 200)}`);
      return null;
    }
    const t = res.json;
    return {
      id: t.id,
      name,
      owner,
      secret: t.token,
      // What this build reported at creation. A build from before token lifetimes omits both; the migration then
      // grandfathers the row (expires_at null), which is exactly the case the upgrade tests must keep working.
      scope: t.scope ?? null,
      expiresAt: t.expiresAt ?? null,
      revoked: false,
    };
  };
  out.agent = await make(admin, 'admin', 'fixture-agent (throwaway)');
  const withExpiry = await make(admin, 'admin', 'fixture-expiring-90d (throwaway)', { expiresInDays: 90 });
  const readOnly = await make(admin, 'admin', 'fixture-read-only (throwaway)', { scope: 'read' });
  const editorToken = await make(editorClient, 'editor', 'fixture-editor-token (throwaway)');
  const toRevoke = await make(admin, 'admin', 'fixture-revoked (throwaway)');
  if (toRevoke) {
    const res = await admin.delete(`/api/tokens/${toRevoke.id}`);
    toRevoke.revoked = res.status === 204;
  }
  for (const t of [out.agent, withExpiry, readOnly, editorToken, toRevoke]) if (t) expected.tokens.push(t);
  features.tokenExpiry = withExpiry?.expiresAt ? true : 'skipped: this build issues no token lifetimes';
  features.tokenScope = readOnly?.scope ? true : 'skipped: this build has no read scope';
  return out;
}

// ---------------------------------------------------------------------------------------------------------------- documents

function nodesAndEdges(count) {
  const nodes = [];
  const edges = [];
  for (let i = 0; i < count; i++) {
    nodes.push({
      id: `n_${i}`,
      kind: i % 3 === 0 ? 'service' : i % 3 === 1 ? 'database' : 'queue',
      label: `Node ${i}`,
      shape: i % 3 === 1 ? 'cylinder' : 'rounded',
      geometry: { x: 40 + (i % 8) * 200, y: 40 + Math.floor(i / 8) * 120, w: 160, h: 80 },
    });
    if (i > 0) edges.push({ id: `e_${i}`, kind: 'flow', from: { node: `n_${i - 1}` }, to: { node: `n_${i}` } });
  }
  return { nodes, edges };
}

function syntheticDocuments() {
  const docs = [];
  docs.push({
    key: 'unicode',
    doc: {
      id: 'fx_unicode',
      title: 'Unicode & quoting: café ☕ 日本語 العربية "quoted" <angle> & ampersand',
      type: 'freeform',
      nodes: [
        { id: 'n_cafe', kind: 'box', label: 'Café ☕ — naïve résumé', shape: 'rounded', geometry: { x: 0, y: 0, w: 200, h: 80 } },
        { id: 'n_jp', kind: 'box', label: '日本語のラベル', shape: 'rectangle', geometry: { x: 260, y: 0, w: 200, h: 80 } },
        { id: 'n_ar', kind: 'box', label: 'مرحبا بالعالم', shape: 'rectangle', geometry: { x: 520, y: 0, w: 200, h: 80 } },
        { id: 'n_quote', kind: 'box', label: 'He said "hello" & <left>\\back', shape: 'rectangle', geometry: { x: 0, y: 140, w: 240, h: 80 } },
        { id: 'n_emoji', kind: 'box', label: '🚀 emoji 👩‍💻 ZWJ', shape: 'pill', geometry: { x: 300, y: 140, w: 240, h: 80 } },
      ],
      edges: [{ id: 'e_u1', kind: 'flow', label: 'élan → 速い', from: { node: 'n_cafe' }, to: { node: 'n_jp' } }],
    },
  });
  docs.push({ key: 'empty', doc: { id: 'fx_empty', title: 'An empty diagram', type: 'architecture', nodes: [], edges: [] } });
  const { nodes, edges } = nodesAndEdges(40);
  docs.push({ key: 'medium', doc: { id: 'fx_medium_chain', title: 'A 40-node chain', type: 'architecture', nodes, edges } });
  docs.push({
    key: 'flythrough',
    doc: {
      id: 'fx_flythrough',
      title: 'Order flow with a tour',
      type: 'architecture',
      nodes: [
        { id: 'n_web', kind: 'service', label: 'Web', shape: 'rounded', geometry: { x: 0, y: 0, w: 160, h: 80 } },
        { id: 'n_api', kind: 'service', label: 'API', shape: 'rounded', geometry: { x: 260, y: 0, w: 160, h: 80 } },
        { id: 'n_db', kind: 'database', label: 'Database', shape: 'cylinder', geometry: { x: 520, y: 0, w: 160, h: 80 } },
      ],
      edges: [
        { id: 'e_1', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_api' } },
        { id: 'e_2', kind: 'data', from: { node: 'n_api' }, to: { node: 'n_db' } },
      ],
      flythrough: {
        loop: true,
        keyframes: [
          { id: 'k_1', view: { x: -40, y: -40, w: 760, h: 200 }, caption: 'The whole path' },
          { id: 'k_2', view: { x: 0, y: 0, w: 200, h: 100 }, caption: 'Traffic enters here', durationMs: 1600, holdMs: 3000, easing: 'easeOut' },
          { id: 'k_3', view: { x: 480, y: 0, w: 240, h: 100 }, caption: 'and lands in the database', durationMs: 1800 },
        ],
      },
    },
    optional: 'flythrough',
  });
  return docs;
}

async function createDocuments(admin, editorClient, agent) {
  const created = new Map();
  const post = async (client, key, doc, source, optionalFeature = null) => {
    const res = await client.post('/api/documents', doc);
    if (res.status !== 201) {
      const reason = `${res.status} ${res.json?.title ?? ''} ${(res.json?.errors ?? []).slice(0, 2).join('; ') || res.json?.detail || ''}`.trim();
      log(`skipped ${doc.id}: ${reason}`);
      expected.skippedDocuments.push({ id: doc.id, source, reason });
      if (optionalFeature) features[optionalFeature] = `skipped: ${reason}`;
      return null;
    }
    if (optionalFeature) features[optionalFeature] = true;
    created.set(key, res.json.id);
    return res.json;
  };

  const files = existsSync(examplesDir) ? readdirSync(examplesDir).filter((f) => f.endsWith('.json')).sort() : [];
  let i = 0;
  for (const file of files) {
    const doc = JSON.parse(readFileSync(join(examplesDir, file), 'utf8'));
    const base = file.replace(/\.json$/, '');
    doc.id = `fx_${base.replace(/[^A-Za-z0-9_.-]/g, '_')}`;
    // The examples carry the repository author's meta; dropping it lets the server record the real creator.
    delete doc.meta;
    // Spread authorship: admin, editor and an agent token each create some, so createdBy differs in the audit trail.
    const client = i % 3 === 0 ? admin : i % 3 === 1 ? editorClient : (agent ?? admin);
    await post(client, base, doc, `docs/examples/${file}`);
    i++;
  }
  for (const synthetic of syntheticDocuments()) {
    await post(admin, synthetic.key, synthetic.doc, 'generated', synthetic.optional ?? null);
  }
  if (created.size === 0) throw new Error('no document could be created; nothing to upgrade');
  return created;
}

async function makeVersions(admin, agent, created) {
  // An agent save (a token with the X-Draughtsman-Actor annotation) checkpoints the document first: that is how every
  // build before the named-snapshot route has versions at all. Three documents get two agent edits each.
  const targets = ['order-service', 'decision-flow', 'medium', 'perf-2000n-2500e'].filter((k) => created.has(k)).slice(0, 4);
  let savedByAgent = 0;
  if (agent) {
    for (const key of targets) {
      const id = created.get(key);
      for (let round = 1; round <= 2; round++) {
        const read = await agent.get(`/api/documents/${id}`);
        if (read.status !== 200) break;
        const doc = read.json;
        if (!Array.isArray(doc.nodes) || doc.nodes.length === 0) break;
        doc.nodes[0].label = `${String(doc.nodes[0].label).replace(/ \(agent edit \d\)$/, '')} (agent edit ${round})`;
        const put = await agent.put(`/api/documents/${id}`, doc, { headers: { 'If-Match': read.headers.get('etag') ?? '' } });
        if (put.status === 200) savedByAgent++;
        else log(`agent save of ${id} answered ${put.status}: ${put.text.slice(0, 200)}`);
      }
    }
  }
  features.agentCheckpoints = savedByAgent > 0 ? true : 'skipped: no agent save succeeded';
  // A person's own edit is a plain save (no checkpoint): the diagram's title.
  const id = created.get('order-service');
  if (id) {
    const read = await admin.get(`/api/documents/${id}`);
    if (read.status === 200) {
      const doc = read.json;
      doc.nodes[doc.nodes.length - 1].label = `${doc.nodes[doc.nodes.length - 1].label} (edited by a person)`;
      await admin.put(`/api/documents/${id}`, doc, { headers: { 'If-Match': read.headers.get('etag') ?? '' } });
    }
    // Named snapshots exist from the version-history work; an older build answers 404/405 and is skipped.
    const named = await admin.post(`/api/documents/${id}/versions`, { name: 'Fixture: before the rework', reason: 'throwaway named snapshot' });
    features.namedSnapshots = named.status === 201 ? true : `skipped: POST .../versions answered ${named.status}`;
  }
}

async function makeAssetsAndImageDocument(admin) {
  const png = await tinyPng([20, 120, 200], 24);
  const part = multipartFile('file', 'fixture-pixel.png', 'image/png', png);
  const res = await admin.request('POST', '/api/assets', { body: part.body, headers: part.headers });
  if (res.status !== 201 && res.status !== 200) {
    features.assets = `skipped: POST /api/assets answered ${res.status}`;
    return;
  }
  const asset = res.json;
  expected.assets.push({ id: asset.id, sha256: sha256(png), bytes: png.length, contentType: 'image/png' });
  const doc = {
    id: 'fx_image_node',
    title: 'A diagram with an uploaded image',
    type: 'freeform',
    nodes: [{ id: 'n_img', kind: 'image', label: 'Pixel', shape: 'image', geometry: { x: 0, y: 0, w: 96, h: 96 }, props: { assetId: asset.id } }],
    edges: [],
  };
  const made = await admin.post('/api/documents', doc);
  features.assets = made.status === 201 ? true : `skipped: image document answered ${made.status}`;
}

async function binSome(admin, editorClient, created) {
  const probe = await admin.get('/api/bin');
  if (probe.status !== 200) {
    features.bin = `skipped: GET /api/bin answered ${probe.status} (deleting is permanent in this build, so nothing is deleted)`;
    return;
  }
  const victims = [
    { client: editorClient, key: 'empty', who: 'editor' },
    { client: admin, key: 'unicode', who: 'admin' },
  ];
  for (const v of victims) {
    const id = created.get(v.key);
    if (!id) continue;
    const before = await v.client.get(`/api/documents/${id}`);
    const res = await v.client.delete(`/api/documents/${id}`);
    if (res.status === 204 && before.status === 200) {
      expected.binned.push({ id, deletedBy: v.who, sha256: hashJson(before.json), title: before.json.title });
    }
  }
  features.bin = expected.binned.length > 0 ? true : 'skipped: nothing could be binned';
}

// ---------------------------------------------------------------------------------------------------------------- instance settings

async function brandingStep(admin) {
  const probe = await admin.get('/api/admin/branding');
  if (probe.status !== 200) {
    features.branding = `skipped: GET /api/admin/branding answered ${probe.status}`;
    return;
  }
  const png = await tinyPng([180, 30, 60], 32);
  const svg = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 32"><rect width="64" height="32" fill="#eeeeee"/><text x="4" y="21" font-size="14" fill="#222222">FIXTURE</text></svg>');
  await admin.put('/api/admin/branding', { productName: 'Fixture Diagrams (throwaway)' });
  const uploads = [
    ['logo', 'fixture-logo.png', 'image/png', png],
    ['logo-light', 'fixture-logo-light.svg', 'image/svg+xml', svg],
    ['favicon', 'fixture-favicon.png', 'image/png', await tinyPng([30, 160, 90], 16)],
  ];
  const slots = {};
  for (const [slot, name, type, bytes] of uploads) {
    const part = multipartFile('file', name, type, bytes);
    const res = await admin.request('POST', `/api/admin/branding/${slot}`, { body: part.body, headers: part.headers });
    slots[slot] = { status: res.status, sha256: sha256(bytes), bytes: bytes.length };
  }
  const state = await admin.get('/api/admin/branding');
  const served = {};
  for (const [slot, path] of [['logo', '/api/theme/logo'], ['logo-light', '/api/theme/logo-light'], ['favicon', '/api/theme/favicon']]) {
    const res = await admin.get(path);
    served[slot] = res.status === 200 ? { sha256: sha256(res.bytes), contentType: res.headers.get('content-type') } : { status: res.status };
  }
  const theme = await admin.get('/api/theme');
  expected.branding = { productName: 'Fixture Diagrams (throwaway)', uploads: slots, served, state: state.json, themeProductName: theme.json?.productName ?? null };
  features.branding = true;
}

async function themesStep(admin) {
  const probe = await admin.get('/api/admin/themes');
  if (probe.status !== 200) {
    features.orgThemes = `skipped: GET /api/admin/themes answered ${probe.status}`;
    return;
  }
  const made = await admin.post('/api/admin/themes', { name: 'Fixture Theme (throwaway)' });
  if (made.status !== 201) {
    features.orgThemes = `skipped: creating a theme answered ${made.status}: ${made.text.slice(0, 200)}`;
    return;
  }
  const theme = made.json;
  theme.chrome.dark.brand = '#c0392b';
  theme.chrome.dark.accent = '#e04832';
  theme.diagram = { ...(theme.diagram ?? {}), base: 'overpass', palette: { red: '#00aa55' }, canvasBackground: '#0b1f2a' };
  const put = await admin.put(`/api/admin/themes/${theme.id}`, theme);
  const second = await admin.post('/api/admin/themes', { name: 'Fixture Second Theme (throwaway)' });
  const makeDefault = await admin.put('/api/admin/themes/default', { id: theme.id });
  const list = await admin.get('/api/admin/themes');
  expected.themes = {
    defaultId: theme.id,
    putStatus: put.status,
    defaultStatus: makeDefault.status,
    secondId: second.json?.id ?? null,
    // The theme definitions as the admin page reads them; updatedAt is part of what must not change.
    list: list.json,
    listHash: hashJson(list.json),
  };
  features.orgThemes = true;
}

async function aiStep(admin) {
  const probe = await admin.get('/api/admin/ai');
  if (probe.status !== 200) {
    features.aiSettings = `skipped: GET /api/admin/ai answered ${probe.status}`;
    return;
  }
  const set = await admin.put('/api/admin/ai', { provider: 'openai', model: 'fixture-model-throwaway', endpoint: 'https://ai.example.invalid/v1' });
  const key = await admin.put('/api/admin/ai/key', { apiKey: AI_KEY });
  const state = await admin.get('/api/admin/ai');
  expected.ai = { provider: 'openai', model: 'fixture-model-throwaway', endpoint: 'https://ai.example.invalid/v1', apiKey: AI_KEY, putStatus: set.status, keyStatus: key.status, state: state.json };
  features.aiSettings = set.status === 200 && key.status < 300 ? true : `skipped: settings ${set.status}, key ${key.status}`;
}

const SMTP_PASSWORD = 'THROWAWAY-smtp-password-not-real-0123456789';

// Outgoing email set in the browser (Admin > Email): the fields as text under <data>/email/, the password only as a Data Protection payload.
async function emailStep(admin) {
  const probe = await admin.get('/api/admin/email');
  if (probe.status !== 200 || probe.json?.passwordKind === undefined) {
    features.emailSettings = `skipped: this build has no email settings editor (GET /api/admin/email answered ${probe.status})`;
    return;
  }
  const put = await admin.put('/api/admin/email', {
    from: 'Draughtsman Fixture <noreply@example.invalid>',
    host: 'smtp.example.invalid',
    port: 587,
    tls: 'starttls',
    user: 'fixture-mailer',
    publicUrl: 'https://draughtsman.example.invalid',
    passwordAction: 'replace',
    password: SMTP_PASSWORD,
  });
  const state = await admin.get('/api/admin/email');
  expected.email = { putStatus: put.status, password: SMTP_PASSWORD, state: state.json };
  features.emailSettings = put.status === 200 && state.json?.passwordSet === true ? true : `skipped: PUT answered ${put.status}: ${put.text.slice(0, 200)}`;
}

async function securityEventsStep(admin) {
  // A wrong password is the cheapest event every build with a security log writes; one is far below the throttle.
  await new Client(base, 'anon').post('/api/auth/login', { email: PEOPLE.admin.email, password: 'definitely-not-the-password-THROWAWAY' });
  const res = await admin.get('/api/admin/security-events?limit=200');
  if (res.status !== 200) {
    features.securityEvents = `skipped: GET /api/admin/security-events answered ${res.status}`;
    return;
  }
  const items = res.json.items ?? res.json;
  expected.securityEvents = items.map((e) => ({ id: e.id, type: e.type, at: e.at ?? e.createdAt ?? null, hash: hashJson(e) }));
  features.securityEvents = items.length > 0 ? true : 'skipped: the log is empty';
}

// ---------------------------------------------------------------------------------------------------------------- recording

async function recordDocuments(admin) {
  const list = await admin.get('/api/documents?limit=200');
  const items = list.json?.items ?? [];
  expected.documentListTotal = list.json?.total ?? items.length;
  for (const item of items) {
    const res = await admin.get(`/api/documents/${item.id}`);
    if (res.status !== 200) throw new Error(`reading ${item.id} answered ${res.status}`);
    const doc = res.json;
    const dsl = await dslOf(admin, doc);
    expected.documents.push({
      id: doc.id,
      title: doc.title,
      type: doc.type,
      nodes: doc.nodes.length,
      edges: doc.edges.length,
      bytes: res.bytes.length,
      hasFlythrough: Boolean(doc.flythrough),
      createdBy: doc.meta?.createdBy ?? null,
      updatedAt: doc.meta?.updatedAt ?? null,
      etag: res.headers.get('etag'),
      sha256: hashJson(doc),
      dslSha256: dsl,
    });
    const versions = await admin.get(`/api/documents/${doc.id}/versions?limit=200`);
    for (const v of versions.json?.items ?? []) {
      const full = await admin.get(`/api/documents/${doc.id}/versions/${v.id}`);
      expected.versions.push({
        documentId: doc.id,
        id: v.id,
        name: v.name ?? null,
        actor: v.actor ?? null,
        reason: v.reason ?? null,
        size: v.size ?? null,
        createdAt: v.createdAt ?? null,
        documentSha256: full.status === 200 && full.json?.document ? hashJson(full.json.document) : null,
      });
    }
  }
  // Binned documents: their snapshot hashes were taken before the delete; the bin keeps them exactly.
  expected.dslExport = expected.documents.some((d) => d.dslSha256) ? true : 'skipped: no DSL export route';
}

async function dslOf(admin, doc) {
  const res = await admin.request('POST', '/api/export/dsl', { json: doc, headers: { Accept: 'text/plain' } });
  return res.status === 200 ? sha256(res.bytes) : null;
}

async function recordAudit(admin) {
  for (const d of expected.documents.concat(expected.binned.map((b) => ({ id: b.id })))) {
    const res = await admin.get(`/api/admin/audit?documentId=${d.id}&limit=200`);
    if (res.status !== 200) {
      features.audit = `skipped: GET /api/admin/audit answered ${res.status}`;
      return;
    }
    const rows = res.json.items ?? res.json;
    expected.audit[d.id] = rows.map((r) => ({ action: r.action, actor: r.actor ?? null, at: r.at ?? r.createdAt ?? null, actorUserId: r.actorUserId ?? null }));
  }
  features.audit = true;
}

// ---------------------------------------------------------------------------------------------------------------- args

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('--')) continue;
    const key = a.slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) out[key] = true;
    else { out[key] = next; i++; }
  }
  return out;
}

function required(name) {
  if (!args[name] || args[name] === true) {
    console.error(`populate: --${name} is required`);
    process.exit(2);
  }
  return args[name];
}

main().catch((error) => {
  console.error('[populate] FAILED:', error.stack ?? error);
  process.exit(1);
});
