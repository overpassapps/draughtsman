// What a person and an agent do with a default Draughtsman, run INSIDE a container that has no network (scripts/docker-offline-smoke.sh pipes
// this file to `node -` in it): first run, sign-in, an API token, create, save, layout, the text exports, the server's own SVG render over MCP,
// the PDF and AI routes (which must answer "not here"), and the single page the browser would load. Node 22's own fetch, no packages.
// BASE overrides the address (default http://127.0.0.1:5180) for a run against a server that is not in a container.
// Prints one line per step and exits non-zero at the first thing that is not as a default air-gapped install must answer.
const base = process.env.BASE ?? 'http://127.0.0.1:5180';
const setupCode = process.env.SETUP_CODE;
if (!setupCode) throw new Error('SETUP_CODE is not set');

let cookies = '';
let xsrf = '';
let bearer = '';

function step(text) {
  console.log(`ok: ${text}`);
}

function fail(text) {
  console.error(`FAIL: ${text}`);
  process.exit(1);
}

async function call(method, path, { body, headers = {}, raw, anonymous } = {}) {
  const response = await fetch(base + path, {
    method,
    headers: {
      ...(anonymous ? {} : bearer ? { Authorization: `Bearer ${bearer}` } : cookies ? { Cookie: cookies, ...(method === 'GET' ? {} : { 'X-XSRF-TOKEN': xsrf }) } : {}),
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...headers,
    },
    body: body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body),
    redirect: 'manual',
  });
  const text = await response.text();
  if (raw) return { response, text };
  let json;
  try { json = JSON.parse(text); } catch { json = undefined; }
  return { response, text, json };
}

function remember(response) {
  const jar = new Map(cookies.split('; ').filter(Boolean).map((pair) => pair.split(/=(.*)/s).slice(0, 2)));
  for (const line of response.headers.getSetCookie()) {
    const [pair] = line.split(';');
    const [name, value] = pair.split(/=(.*)/s);
    jar.set(name, value);
    if (name === 'XSRF-TOKEN') xsrf = decodeURIComponent(value);
  }
  cookies = [...jar].map(([name, value]) => `${name}=${value}`).join('; ');
}

// The page the browser loads: relative references only.
{
  const { response, text } = await call('GET', '/', { raw: true });
  if (response.status !== 200) fail(`GET / answered ${response.status}`);
  const external = [...text.matchAll(/(?:src|href)\s*=\s*"([^"]+)"/g)].map((m) => m[1]).filter((url) => /^(?:[a-z]+:)?\/\//i.test(url));
  if (external.length > 0) fail(`the page loads from another host: ${external.join(', ')}`);
  const csp = response.headers.get('content-security-policy') ?? '';
  if (!csp.includes("connect-src 'self'")) fail(`no connect-src 'self' in the Content-Security-Policy: ${csp}`);
  step("the app's page loads only from this server, under connect-src 'self'");
}

{
  const { response, json } = await call('GET', '/api/health');
  if (response.status !== 200 || json?.status !== 'ok') fail('health is not ok');
  step('health answers');
}

{
  const { response, json } = await call('POST', '/api/auth/first-run', { body: { email: 'offline@example.com', displayName: 'Offline', password: 'a-long-enough-password', setupCode } });
  if (response.status >= 300) fail(`first run answered ${response.status}: ${JSON.stringify(json)}`);
  remember(response);
  step('first run created the administrator and signed them in');
}

{
  const { response } = await call('POST', '/api/auth/logout');
  if (response.status >= 300) fail(`sign out answered ${response.status}`);
  cookies = '';
  xsrf = '';
  const login = await call('POST', '/api/auth/login', { body: { email: 'offline@example.com', password: 'a-long-enough-password' } });
  if (login.response.status !== 200) fail(`sign in answered ${login.response.status}`);
  remember(login.response);
  step('sign out, then sign in again by password');
}

{
  const { response, json } = await call('POST', '/api/tokens', { body: { name: 'offline smoke' } });
  if (response.status !== 201 || !json?.token) fail(`creating a token answered ${response.status}`);
  bearer = json.token;
  step('an API token, as an agent would use');
}

const document = { title: 'Offline', type: 'architecture', nodes: [{ id: 'n_a', kind: 'service', shape: 'rounded', label: 'A' }, { id: 'n_b', kind: 'service', shape: 'rounded', label: 'B' }], edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }] };
let id;
let saved;
{
  const { response, json } = await call('POST', '/api/documents', { body: document });
  if (response.status !== 201) fail(`create answered ${response.status}`);
  id = json.id;
  json.title = 'Offline, saved';
  const put = await call('PUT', `/api/documents/${id}`, { body: json, headers: { 'If-Match': response.headers.get('etag') } });
  if (put.response.status !== 200) fail(`save answered ${put.response.status}: ${put.text}`);
  saved = put.json;
  step('create and save a document');
}

{
  const { response, json } = await call('POST', '/api/layout', { body: saved });
  if (response.status !== 200 || json.document.nodes.filter((n) => n.geometry).length !== 2) fail('the sidecar did not lay the document out');
  step('the Node sidecar lays it out');
}

for (const path of ['/api/export/mermaid', '/api/export/dsl']) {
  const { response, text } = await call('POST', path, { body: saved });
  if (response.status !== 200 || text.length < 10) fail(`${path} answered ${response.status}`);
}
step('Mermaid and DSL export');

{
  // Streamable HTTP MCP: initialize, then initialized, then a tool call; the answer is JSON or one server-sent event.
  const mcp = async (message, session) => {
    const response = await fetch(`${base}/mcp`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', Authorization: `Bearer ${bearer}`, ...(session ? { 'Mcp-Session-Id': session } : {}) },
      body: JSON.stringify(message),
    });
    const text = await response.text();
    const data = text.startsWith('{') ? text : (text.split('\n').find((line) => line.startsWith('data:')) ?? '').slice(5).trim();
    return { response, result: data ? JSON.parse(data) : undefined };
  };
  const init = await mcp({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'offline-smoke', version: '1' } } });
  if (init.response.status !== 200) fail(`MCP initialize answered ${init.response.status}`);
  const session = init.response.headers.get('mcp-session-id') ?? undefined;
  await mcp({ jsonrpc: '2.0', method: 'notifications/initialized' }, session);
  const rendered = await mcp({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'render_document', arguments: { id } } }, session);
  const text = rendered.result?.result?.content?.[0]?.text ?? '';
  if (!JSON.parse(text || '{}').svg?.startsWith('<svg')) fail(`render_document gave no SVG: ${text.slice(0, 200)}`);
  step('the server draws the SVG (render_document over MCP)');
}

{
  const pdf = await call('GET', `/api/documents/${id}/pdf`);
  if (pdf.response.status !== 501) fail(`PDF answered ${pdf.response.status}, not 501 (the browser component is not installed)`);
  const ai = await call('POST', '/api/ai/generate-from-text', { body: { description: 'a login flow', diagramType: 'flowchart' } });
  if (ai.response.status !== 404) fail(`AI answered ${ai.response.status}, not 404 (no provider is set)`);
  const email = await call('POST', '/api/auth/password-reset/request', { body: { email: 'offline@example.com' }, anonymous: true });
  if (email.response.status !== 404) fail(`password-reset request answered ${email.response.status}, not 404 (no mail server is set)`);
  step('PDF, AI and emailed reset links answer "not here": they are off');
}

{
  const { response, json } = await call('GET', '/api/health');
  if (json.ai.enabled !== false || json.export.pdf !== false || json.storage !== 'sqlite') fail(`health says ai=${json.ai.enabled} pdf=${json.export.pdf} storage=${json.storage}`);
  if (response.status !== 200) fail('health after the work is not 200');
  step('health still says: SQLite, no AI, no PDF');
}

console.log('PASS: a default instance did all of that with no network');
