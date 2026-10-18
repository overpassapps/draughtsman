#!/usr/bin/env node
// Real-Chrome smoke test that the SPA still loads and works under the security headers (AU-13): sign-in page, the
// About, status, account and admin pages, the sample diagrams, the editor, the routing web worker, saving, PNG/SVG/PDF
// export and the standalone fly-through HTML, with every Content-Security-Policy violation and console error collected. Needs a running server with web/dist built, an administrator already created,
// and (for the PDF step) export.chromium enabled. Never point it at a port you do not own.
//
//   BASE=http://127.0.0.1:PORT EMAIL=... PASSWORD=... [SMOKE_RESET_DOCUMENTS=1] node scripts/security-headers-smoke.mjs
//
// SMOKE_RESET_DOCUMENTS=1 deletes every document first, so the empty-state sample button can be exercised: only ever on a
// scratch instance.
//
// playwright-core comes from the global @playwright/cli install (not a dependency of this repo); a headed Chrome, because the
// browser-extension tab reports a hidden document and never runs timers.
import { existsSync, readFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// PLAYWRIGHT_CORE is the path to playwright-core's index.mjs, e.g. <npm root -g>/@playwright/cli/node_modules/playwright-core/index.mjs.
const playwrightPath = process.env.PLAYWRIGHT_CORE ?? 'playwright-core';
const { chromium } = await import(playwrightPath);

const base = process.env.BASE;
const email = process.env.EMAIL;
const password = process.env.PASSWORD;
if (!base || !email || !password) {
  console.error('Set BASE, EMAIL and PASSWORD.');
  process.exit(2);
}

const failures = [];
const check = (name, ok, detail = '') => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ` - ${detail}` : ''}`);
  if (!ok) failures.push(name);
};

const browser = await chromium.launch({ headless: false, channel: 'chrome' });
const context = await browser.newContext({ acceptDownloads: true });
const page = await context.newPage();
// Chrome's own favicon fetches are invisible to Playwright's request events; the protocol's network domain sees them.
const cdp = await context.newCDPSession(page);
await cdp.send('Network.enable');
cdp.on('Network.responseReceived', (e) => { if (e.response.status === 404 && new URL(e.response.url).pathname === '/favicon.ico') faviconRequests += 1; });
let faviconRequests = 0;
const cspViolations = [];
const consoleErrors = [];
const failedRequests = [];
await page.addInitScript(() => {
  window.__csp = [];
  document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(`${e.violatedDirective} blocked ${e.blockedURI}`));
});
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
context.on('response', (r) => { if (r.status() >= 400 && r.status() !== 401) consoleErrors.push(`HTTP ${r.status()} ${r.url()}`); });
page.on('requestfailed', (r) => failedRequests.push(`${r.url()} ${r.failure()?.errorText}`));
const collectCsp = async () => { cspViolations.push(...await page.evaluate(() => window.__csp.splice(0))); };

// 1. The shell and the headers it arrived with.
const shell = await page.goto(`${base}/login`);
const headers = shell.headers();
check('SPA shell carries a CSP with frame-ancestors none', (headers['content-security-policy'] ?? '').includes("frame-ancestors 'none'"));
check('SPA shell is nosniff, same-origin referrer, DENY framing', headers['x-content-type-options'] === 'nosniff' && headers['referrer-policy'] === 'same-origin' && headers['x-frame-options'] === 'DENY');
check('No Server header', headers.server === undefined);

// 2. Sign in through the real form.
await page.waitForSelector('input[type=email], input[name=email]', { timeout: 15000 });
await page.fill('input[type=email], input[name=email]', email);
await page.fill('input[type=password]', password);
await page.click('button[type=submit]');
await page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 15000 });
check('Sign-in works under the policy', true, new URL(page.url()).pathname);
consoleErrors.length = 0; // the 401 from the signed-out status probe before sign-in is expected
failedRequests.length = 0;

// 2b. The pages around the editor, each of which must render something under the policy.
for (const path of ['/about', '/admin/status', '/account', '/admin/users', '/admin/security', '/admin/tokens', '/admin/branding', '/admin/themes', '/admin/ai', '/example']) {
  await page.goto(`${base}${path}`);
  await page.waitForTimeout(1200);
  const text = (await page.evaluate(() => document.body.innerText)).trim();
  check(`Page ${path} renders`, text.length > 60 && !/Page not found/.test(text), `${text.length} chars`);
  await collectCsp();
}

// 2c. Opt-in samples from the empty state (needs an empty instance).
const cookies0 = await context.cookies(base);
const xsrf0 = cookies0.find((c) => c.name === 'XSRF-TOKEN')?.value ?? '';
if (process.env.SMOKE_RESET_DOCUMENTS === '1') {
  const list = await (await context.request.get(`${base}/api/documents`)).json();
  for (const item of list.items) await context.request.delete(`${base}/api/documents/${item.id}`, { headers: { 'X-XSRF-TOKEN': xsrf0 } });
  await page.goto(`${base}/`);
  const samples = page.getByRole('button', { name: 'Add four sample diagrams' });
  await samples.waitFor({ timeout: 15000 });
  await samples.click();
  await page.waitForFunction(async () => (await (await fetch('/api/documents')).json()).items.length >= 4, null, { timeout: 20000 });
  check('Four sample diagrams are added from the empty state', true);
  await collectCsp();
}

// 3. A document to open: the shipped example (5 nodes, orthogonal edges, a fly-through), created through the API.
const cookies = await context.cookies(base);
const xsrf = cookies.find((c) => c.name === 'XSRF-TOKEN')?.value ?? '';
const example = JSON.parse(readFileSync(new URL('../docs/examples/order-service.json', import.meta.url), 'utf8'));
delete example.id;
const created = await context.request.post(`${base}/api/documents`, { data: example, headers: { 'X-XSRF-TOKEN': xsrf } });
check('Create document', created.status() === 201, String(created.status()));
const id = (await created.json()).id;
const apiHeaders = created.headers();
check('API response is no-store', (apiHeaders['cache-control'] ?? '').includes('no-store'), apiHeaders['cache-control']);

// 4. The editor: canvas, routing worker, autosave.
const puts = [];
page.on('response', (r) => { if (r.request().method() === 'PUT' && r.url().includes('/api/documents/')) puts.push(r.status()); });
await page.goto(`${base}/d/${id}`);
await page.waitForSelector('g[data-id]', { timeout: 20000 });
const nodeCount = await page.locator('g[data-id]').count();
check('Editor draws the diagram', nodeCount >= 8, `${nodeCount} elements`);
await page.waitForTimeout(2500);
await collectCsp();

const node = page.locator('g[data-id="n_web"]').first();
await node.dblclick();
await page.keyboard.press(process.platform === 'darwin' ? 'Meta+A' : 'Control+A');
await page.keyboard.type('Web app edited');
await page.keyboard.press('Enter');
await page.waitForTimeout(800);
await page.getByRole('button', { name: 'Save', exact: true }).click();
await page.waitForTimeout(2500);
check('Saving the edit reaches the server (PUT 200)', puts.includes(200), `PUT statuses ${JSON.stringify(puts)}`);

// 5. Exports through the command palette.
const download = async (label) => {
  await page.keyboard.press(process.platform === 'darwin' ? 'Meta+K' : 'Control+K');
  await page.keyboard.type(label);
  await page.waitForTimeout(400);
  const [file] = await Promise.all([page.waitForEvent('download', { timeout: 20000 }), page.keyboard.press('Enter')]);
  const path = join(mkdtempSync(join(tmpdir(), 'dm-smoke-')), file.suggestedFilename());
  await file.saveAs(path);
  return { name: file.suggestedFilename(), path, bytes: readFileSync(path) };
};
try {
  const png = await download('Export PNG');
  check('Export PNG downloads a real PNG', png.bytes.subarray(0, 4).toString('hex') === '89504e47', `${png.name} ${png.bytes.length} bytes`);
  const svg = await download('Export SVG');
  check('Export SVG downloads an SVG', svg.bytes.toString('utf8', 0, 300).includes('<svg'), `${svg.name} ${svg.bytes.length} bytes`);
  await page.keyboard.press(process.platform === 'darwin' ? 'Meta+K' : 'Control+K');
  await page.keyboard.type('Export PDF');
  await page.waitForTimeout(400);
  await page.keyboard.press('Enter');
  const [pdfFile] = await Promise.all([
    page.waitForEvent('download', { timeout: 60000 }),
    page.getByRole('dialog').getByRole('button', { name: 'Export PDF', exact: true }).click(),
  ]);
  const pdfPath = join(mkdtempSync(join(tmpdir(), 'dm-smoke-')), pdfFile.suggestedFilename());
  await pdfFile.saveAs(pdfPath);
  const pdf = { name: pdfFile.suggestedFilename(), bytes: readFileSync(pdfPath) };
  check('Export PDF downloads a real PDF (hardened Chromium)', pdf.bytes.toString('latin1', 0, 4) === '%PDF', `${pdf.name} ${pdf.bytes.length} bytes`);
  const fly = await download('Export fly-through');
  const html = fly.bytes.toString('utf8');
  check('Export fly-through downloads a standalone HTML page', html.toLowerCase().includes('<!doctype html') && html.length > 2000, `${fly.name} ${fly.bytes.length} bytes`);

  // The exported page is opened from disk, as a recipient would: it must play with no errors of its own.
  const player = await context.newPage();
  const playerErrors = [];
  player.on('pageerror', (e) => playerErrors.push(e.message));
  player.on('console', (m) => { if (m.type() === 'error') playerErrors.push(m.text()); });
  await player.goto(`file://${fly.path}`);
  await player.waitForTimeout(2000);
  const drawn = await player.locator('svg').count();
  check('Exported fly-through opens and draws', drawn > 0 && playerErrors.length === 0, `svg elements ${drawn}, errors ${JSON.stringify(playerErrors)}`);
  await player.close();
} catch (error) {
  check('Exports', false, String(error));
}

await collectCsp();
check('No Content-Security-Policy violation anywhere', cspViolations.length === 0, JSON.stringify(cspViolations));
// Chrome asks for /favicon.ico before the page's own icon is set, and the server answers 404 once signed in: a console line
// with no URL in it, present before this hardening work. One generic 404 line is allowed per such request.
let allowedFaviconLines = faviconRequests;
const relevantErrors = consoleErrors.filter((e) => {
  if (e.includes('favicon')) return false;
  if (allowedFaviconLines > 0 && e === 'Failed to load resource: the server responded with a status of 404 (Not Found)') { allowedFaviconLines -= 1; return false; }
  return true;
});
check('No console errors', relevantErrors.length === 0, JSON.stringify(relevantErrors.slice(0, 5)));
const realFailures = failedRequests.filter((f) => !f.endsWith('net::ERR_ABORTED')); // aborted polls on navigation are not failures
check('No failed requests', realFailures.length === 0, JSON.stringify(realFailures.slice(0, 5)));

await browser.close();
console.log(failures.length === 0 ? '\nSMOKE OK' : `\nSMOKE FAILED: ${failures.join('; ')}`);
process.exit(failures.length === 0 ? 0 : 1);
