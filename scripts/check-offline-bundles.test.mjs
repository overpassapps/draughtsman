// Run with: node --test scripts/check-offline-bundles.test.mjs
// Every rule of scripts/check-offline-bundles.mjs is shown both ways on small synthetic bundles: the clean one passes, and each
// defect is caught on its own, naming the file and the text. The last tests run the checker on the repository's own builds when
// they exist (verify-local.sh and publish.sh always build them first).
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { checkSidecar, checkWeb, INERT_URLS, scanText } from './check-offline-bundles.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const script = join(root, 'scripts', 'check-offline-bundles.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'offline-bundles-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

let counter = 0;
/** A web build folder holding the given files (name -> text), always with an index.html unless one is given. */
function webBuild(files) {
  const dir = join(scratch, `web-${counter++}`);
  mkdirSync(join(dir, 'assets'), { recursive: true });
  const all = { 'index.html': '<!doctype html><html><head><script type="module" crossorigin src="/assets/app.js"></script><link rel="stylesheet" href="/assets/app.css"></head><body><div id="root"></div></body></html>', 'assets/app.js': 'console.log("hi")', 'assets/app.css': 'body{margin:0}', ...files };
  for (const [name, text] of Object.entries(all)) writeFileSync(join(dir, name), text);
  return dir;
}

function sidecarBundle(text) {
  const file = join(scratch, `sidecar-${counter++}.js`);
  writeFileSync(file, text);
  return file;
}

function assertClean(findings) {
  assert.deepEqual(findings, []);
}

function assertFinding(findings, ...needles) {
  assert.ok(findings.length > 0, 'the checker passed a bundle it should have refused');
  for (const needle of needles) assert.ok(findings.some((finding) => finding.includes(needle)), `no finding mentions ${needle}:\n${findings.join('\n')}`);
}

// ---- the web app -------------------------------------------------------------------------------------------------------------

test('web: a bundle with only relative loads, same-origin fetches and the inert strings passes', () => {
  const dir = webBuild({
    'assets/app.js': `fetch("/api/health"); const ns="http://www.w3.org/2000/svg"; const id="https://json-schema.org/draft/2020-12/schema"; const e="https://reactjs.org/docs/error-decoder.html?invariant="; const p="https://…"; const q="https://draughtsman.example.com"; const r="https://draughtsman.example.com/reset"; new URL("http://"+host); const m="If this address starts with http:// while forceHttps is on, use https://."`,
    'assets/app.css': '@font-face{font-family:Inter;src:url(/assets/inter.woff2) format("woff2")} .a{background:url(data:image/png;base64,AAAA)}',
  });
  assertClean(checkWeb(dir));
});

test('web: a script, stylesheet or image from another host is caught even when its host is on the allow-list', () => {
  assertFinding(checkWeb(webBuild({ 'index.html': '<html><script src="https://cdn.example.com/lib.js"></script></html>' })), 'index.html', 'cdn.example.com');
  assertFinding(checkWeb(webBuild({ 'index.html': '<html><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter"></html>' })), 'fonts.googleapis.com');
  assertFinding(checkWeb(webBuild({ 'index.html': '<html><img src="//tracker.example.net/pixel.gif"></html>' })), 'tracker.example.net');
  assertFinding(checkWeb(webBuild({ 'index.html': '<html><script src="http://www.w3.org/not-a-namespace.js"></script></html>' })), 'loads from another host');
  assertFinding(checkWeb(webBuild({ 'index.html': '<html><meta http-equiv="refresh" content="0; url=https://example.org/"></html>' })), 'loads from another host');
});

test('web: a scheme followed only by a sentence full stop is inert, a host after the dot is not', () => {
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const m = "use https://.evil.example";' })), 'evil.example');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const m = "use https://..";' })), 'https://..');
});

test('web: CSS that imports or loads from another host is caught', () => {
  assertFinding(checkWeb(webBuild({ 'assets/app.css': '@import url("https://fonts.googleapis.com/css?family=Inter");' })), 'app.css', 'fonts.googleapis.com');
  assertFinding(checkWeb(webBuild({ 'assets/app.css': '@import "//cdn.example.com/a.css";' })), 'cdn.example.com');
  assertFinding(checkWeb(webBuild({ 'assets/app.css': '@font-face{src:url(https://fonts.gstatic.com/s/inter.woff2)}' })), 'fonts.gstatic.com');
  assertFinding(checkWeb(webBuild({ 'assets/app.css': '.a{background:url( //cdn.example.com/x.png )}' })), 'cdn.example.com');
});

test('web: a host that is not on the allow-list is caught in JavaScript, whatever the code does with it', () => {
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'fetch("https://api.segment.io/v1/track")' })), 'app.js', 'api.segment.io');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const s = "https://browser.sentry-cdn.com/7.0.0/bundle.min.js";' })), 'sentry-cdn.com');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const u = "wss://sync.example.com/socket";' })), 'sync.example.com');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const u = "https://www.google-analytics.com/collect";' })), 'google-analytics.com');
});

test('web: a lookalike of an allow-listed address is not allow-listed', () => {
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'x = "https://reactjs.org.evil.example/docs/error-decoder.html"' })), 'reactjs.org.evil.example');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'x = "http://www.w3.org.evil.example/2000/svg"' })), 'w3.org.evil.example');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'x = "https://json-schema.org@evil.example/x"' })), 'evil.example');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'x = "https://draughtsman.example.com.evil.net/collect"' })), 'example.com.evil.net');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'x = "https://reactjs.org/docs/error-decoder.htmlx"' })), 'error-decoder.htmlx');
});

test('web: APIs that send data to a host of the page\'s choosing are caught', () => {
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'navigator.sendBeacon("/t", data)' })), 'sendBeacon');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const s = new WebSocket(url)' })), 'WebSocket');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const s = new EventSource(url)' })), 'EventSource');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'const r = new XMLHttpRequest()' })), 'XMLHttpRequest');
  assertFinding(checkWeb(webBuild({ 'assets/app.js': 'importScripts(u)' })), 'importScripts');
});

test('web: the licence notices an archive copies into wwwroot name project home pages and are not scanned, but no other text file is excused', () => {
  const notice = 'Konscious: https://github.com/kmaragon/Konscious.Security.Cryptography\nReact: https://github.com/facebook/react\n';
  assertClean(checkWeb(webBuild({ 'THIRD-PARTY-NOTICES.txt': notice })));
  assertFinding(checkWeb(webBuild({ 'NOTICES.txt': notice })), 'NOTICES.txt', 'github.com/facebook/react');
  assertFinding(checkWeb(webBuild({ 'assets/THIRD-PARTY-NOTICES.js': 'fetch("https://github.com/x")' })), 'THIRD-PARTY-NOTICES.js');
});

test('web: nothing to scan is an error, never a pass', () => {
  assert.throws(() => checkWeb(join(scratch, 'does-not-exist')), /not a folder/);
  const empty = join(scratch, 'empty');
  mkdirSync(empty);
  assert.throws(() => checkWeb(empty), /no index\.html/);
});

test('web: every allow-list entry carries a reason and an absolute prefix', () => {
  for (const [prefix, reason] of INERT_URLS) {
    assert.match(prefix, /^(?:https?):\/\//);
    assert.ok(reason.length > 10, `${prefix} has no reason`);
  }
});

// ---- the sidecar -------------------------------------------------------------------------------------------------------------

test('sidecar: crypto and readline only, and the inert strings, pass', () => {
  assertClean(checkSidecar(sidecarBundle('var c=require("node:crypto"),r=require("node:readline");const x="http://www.eclipse.org/elk/ElkGraph";const y="http:///org/eclipse/emf/ecore/util/ExtendedMetaData"')));
});

for (const module of ['node:http', 'node:https', 'node:net', 'node:tls', 'node:dgram', 'node:dns', 'node:child_process', 'node:worker_threads', 'node:http2', 'node:fs']) {
  test(`sidecar: requiring ${module} is caught`, () => {
    assertFinding(checkSidecar(sidecarBundle(`var c=require("node:crypto");var n=require("${module}");`)), module);
  });
}

test('sidecar: an import statement or a dynamic import of a network module is caught', () => {
  assertFinding(checkSidecar(sidecarBundle('import { request } from "node:https";\nrequest(u);')), 'node:https');
  assertFinding(checkSidecar(sidecarBundle('import "node:net";')), 'node:net');
  assertFinding(checkSidecar(sidecarBundle('const m = await import("http");')), "'http'");
  assertFinding(checkSidecar(sidecarBundle('var h=require("http");')), "'http'");
});

test('sidecar: fetch, WebSocket and XMLHttpRequest are caught', () => {
  assertFinding(checkSidecar(sidecarBundle('fetch("https://example.com/")')), 'fetch');
  assertFinding(checkSidecar(sidecarBundle('const s = new WebSocket(u)')), 'WebSocket');
  assertFinding(checkSidecar(sidecarBundle('new XMLHttpRequest()')), 'XMLHttpRequest');
});

test('sidecar: the comments of a development build are not code, but code beside them still is', () => {
  const dev = [
    'var c=require("node:crypto");',
    '          /** @type {Array<import(\'../internals.js\').DeleteItem>} */',
    '  /**',
    '   * See https://developer.mozilla.org/en-US/docs/Web/API/Element and https://quilljs.com/docs/delta/',
    '   */',
    '// import x from "node:net"  (a full-line comment)',
    'var r=require("node:readline");',
  ].join('\n');
  assertClean(checkSidecar(sidecarBundle(dev)));
  assertFinding(checkSidecar(sidecarBundle(`${dev}\nvar n=require("node:net"); // trailing`)), 'node:net');
  assertFinding(checkSidecar(sidecarBundle('/** a comment */ var n=require("node:net");')), 'node:net');
  assertFinding(checkSidecar(sidecarBundle('/* c */ fetch("https://x.example.com/")')), 'fetch');
  assertFinding(checkSidecar(sidecarBundle('var u = "https://quilljs.com/docs/delta/"; // in code, not a comment')), 'quilljs.com');
  assertFinding(checkSidecar(sidecarBundle('/** not closed yet */\nvar h = require("http");\n/** another */')), "'http'");
});

test('sidecar: an external address is caught, and a missing bundle is an error', () => {
  assertFinding(checkSidecar(sidecarBundle('const u = "https://telemetry.example.com/ping"')), 'telemetry.example.com');
  assert.throws(() => checkSidecar(join(scratch, 'nope.js')), /not a file/);
});

test('scanText: a finding names the file', () => {
  assert.match(scanText('assets/app.js', 'x = "https://cdn.example.com/a.js"', 'web')[0], /^assets\/app\.js: /);
});

// ---- the command line --------------------------------------------------------------------------------------------------------

function run(...args) {
  return spawnSync(process.execPath, [script, ...args], { encoding: 'utf8' });
}

test('command line: 0 when clean, 1 with findings listed, 2 when the bundle is missing or the use is wrong', () => {
  const clean = run('web', webBuild({}));
  assert.equal(clean.status, 0, clean.stderr);
  const dirty = run('web', webBuild({ 'assets/app.js': 'fetch("https://api.segment.io/")' }));
  assert.equal(dirty.status, 1);
  assert.match(dirty.stderr, /api\.segment\.io/);
  assert.equal(run('web', join(scratch, 'missing')).status, 2);
  assert.equal(run('sidecar', join(scratch, 'missing.js')).status, 2);
  assert.equal(run().status, 2);
  assert.equal(run('sidecar', sidecarBundle('require("node:net")')).status, 1);
});

// ---- the repository's own builds ---------------------------------------------------------------------------------------------

test('the web app this repository builds makes no reference to another host', { skip: existsSync(join(root, 'web', 'dist', 'index.html')) ? false : 'run `npm run build` in web/ first' }, () => {
  assertClean(checkWeb(join(root, 'web', 'dist')));
});

test('the sidecar bundle this repository builds imports no network module', { skip: existsSync(join(root, 'sidecar', 'dist', 'sidecar.js')) ? false : 'build the sidecar first (dotnet build, or npm run build in sidecar/)' }, () => {
  assertClean(checkSidecar(join(root, 'sidecar', 'dist', 'sidecar.js')));
});
