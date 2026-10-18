#!/usr/bin/env node
// Fails when what Draughtsman ships to a browser or to its Node sidecar could reach another host: the proof behind "air-gapped".
//
//   node scripts/check-offline-bundles.mjs all                    web/dist and sidecar/dist/sidecar.js, from the repository
//   node scripts/check-offline-bundles.mjs web web/dist           a built web app (the Vite output, or an archive's wwwroot/)
//   node scripts/check-offline-bundles.mjs sidecar path/to/sidecar.js
//
// Exit 0 = clean, 1 = a finding (each printed with the file and the text), 2 = a bundle that is not there (a release must never
// pass because there was nothing to look at). Node's own modules only, no network.
//
// Web app, each rule failing closed:
//   1. HTML, CSS and SVG may not load anything from an absolute or protocol-relative URL (script, link, img, @import, url(), srcset,
//      form action, ...), and no such URL is excused by the allow-list below: a loaded resource is a request.
//   2. Every other absolute http(s)/ws(s) URL in any text file must start with an entry of INERT_URLS: XML namespaces, JSON Schema
//      identifiers, an error-message link a person could click. Each is a string the code carries, never a request. A new host (a CDN,
//      web fonts, analytics, an error reporter) is a finding until a person reads it and adds it, with its reason, here.
//   3. JavaScript may not mention the APIs that send data to a host of the page's choosing without a fetch: WebSocket, EventSource,
//      sendBeacon, XMLHttpRequest, importScripts. (A fetch to a relative URL is the app talking to its own server; the server's
//      Content-Security-Policy, `connect-src 'self'`, is the runtime backstop for anything written in a way a scan cannot read.)
// Sidecar: it must import nothing from Node but node:crypto and node:readline (no http, https, net, tls, dgram, dns, child_process,
// worker_threads...), may not use fetch, WebSocket or XMLHttpRequest, and the same URL rule applies. It is a child process that talks
// to the server over stdio only (ADR 0002); this is what keeps that true.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

/** URL prefixes that appear in the bundles as text and are never requested. Each carries why. Add one only after reading where it is used. */
export const INERT_URLS = [
  ['http://www.w3.org/', 'XML and SVG namespace identifiers, and the XMLSchema datatype URIs ELK uses as names'],
  ['https://www.w3.org/', 'the same namespace identifiers written with https'],
  ['http://json-schema.org/', 'JSON Schema draft identifiers ($schema, $id) inside Ajv'],
  ['https://json-schema.org/', 'JSON Schema draft identifiers ($schema, $id, vocabularies) inside Ajv'],
  ['https://raw.githubusercontent.com/ajv-validator/ajv/', "Ajv's own $id for its data-reference meta schema, used as a key, never fetched"],
  ['https://reactjs.org/docs/error-decoder.html', "React's production error text, which names a page a person could open; the app never requests it"],
  ['https://github.com/yjs/yjs/issues/', 'a pointer in a Yjs warning message'],
  ['https://draughtsman.overpass.co.uk/schema/', "the document schema's $id: an identifier, not a location the app fetches"],
  ['https://draughtsman.example.com', "the placeholder in Admin > Email's public-address field: an RFC 2606 example name, never requested"],
  ['https://www.gnu.org/licenses/', 'licence text carried in the ELK header'],
  ['https://www.eclipse.org/legal/', 'licence text carried in the ELK header'],
  ['http://www.eclipse.org/', 'EMF/ELK model namespace URIs'],
  ['http:///org/eclipse/emf/', 'an EMF extension URI inside ELK: no host at all'],
];

/** The Node modules the sidecar may import. node:crypto hashes; node:readline reads the JSON-RPC lines from stdin. */
export const SIDECAR_ALLOWED_MODULES = ['node:crypto', 'node:readline'];

// Files the web build folder holds that are text for a person to read, not code the app runs or loads. The licence notices name every
// project's home page; the server serves them as plain text at /THIRD-PARTY-NOTICES.txt, and publish.sh copies them into wwwroot/.
const READ_ONLY_FILES = new Set(['THIRD-PARTY-NOTICES.txt']);
const TEXT_EXTENSIONS = new Set(['.html', '.htm', '.css', '.js', '.mjs', '.cjs', '.json', '.svg', '.txt', '.webmanifest', '.xml', '.map']);
const LOADING_EXTENSIONS = new Set(['.html', '.htm', '.css', '.svg']);
const URL_PATTERN = /(?:https?|wss?):\/\/[A-Za-z0-9._~:/?#@!$&*+=%[\]-]*/gi;
// Where an HTML, CSS or SVG file asks for something to be loaded: an attribute that takes a URL, url(...), or @import.
const LOADING_PATTERNS = [
  /\b(?:src|href|xlink:href|action|formaction|poster|data|ping|manifest|background)\s*=\s*["']?\s*(?:https?:|wss?:)?\/\/[^\s"'>]+/gi,
  /\bsrcset\s*=\s*["'][^"']*(?:https?:)?\/\/[^"']*["']/gi,
  /\burl\(\s*["']?\s*(?:https?:|wss?:)?\/\/[^)\s"']+/gi,
  /@import\s+["']?\s*(?:https?:)?\/\/[^\s"';)]+/gi,
  /http-equiv\s*=\s*["']?refresh["']?[^>]*content\s*=\s*["'][^"']*url\s*=\s*(?:https?:)?\/\/[^"'>]+/gi,
];
const SENDING_APIS = [/\bnew\s+WebSocket\s*\(/, /\bnew\s+EventSource\s*\(/, /\bsendBeacon\s*\(/, /\bXMLHttpRequest\b/, /\bimportScripts\s*\(/];
const SIDECAR_NETWORK_GLOBALS = [/\bfetch\s*\(/, /\bnew\s+WebSocket\b/, /\bXMLHttpRequest\b/, /\bprocess\.binding\s*\(/];

function* walk(path) {
  const info = statSync(path);
  if (!info.isDirectory()) {
    yield path;
    return;
  }
  for (const entry of readdirSync(path).sort()) yield* walk(join(path, entry));
}

function snippet(text, index, length = 100) {
  return text.slice(Math.max(0, index - 20), index + length).replace(/\s+/g, ' ').trim();
}

// A scheme on its own names no host: a prefix the code puts in front of something (fast-uri's `new URL('http://' + host).hostname` in
// Ajv, the AI endpoint field's `https://…` placeholder). It is not a request, and a request built from it at run time is what the
// server's Content-Security-Policy refuses. A sentence's own full stop after it ("use https://.", the sign-in page's explanation of a dropped
// session cookie) names no host either; anything more after the dot is a host and is still tested.
const BARE_SCHEME = /^(?:https?|wss?):\/\/\.?$/i;

// An entry ending in "/" is a prefix; any other entry is that address itself, optionally followed by a path, query or fragment, so
// `https://draughtsman.example.com` does not excuse `https://draughtsman.example.com.evil.net`.
function inert(url) {
  return BARE_SCHEME.test(url) || INERT_URLS.some(([entry]) => (entry.endsWith('/') ? url.startsWith(entry) : url === entry || (url.startsWith(entry) && /^[/?#]/.test(url.slice(entry.length)))));
}

// A development build of the sidecar (the one `dotnet build` makes) keeps its sources' comments: JSDoc type imports such as
// `import('../internals.js')` and links to documentation. A comment is not code, so one that starts a line is dropped before the sidecar is
// read; code on the same line as a comment, and any trailing comment, is still read (it fails closed). The release build is minified and has none.
function withoutLineStartComments(text) {
  return text.replace(/^[ \t]*\/\*[\s\S]*?\*\//gm, '').replace(/^[ \t]*\/\/.*$/gm, '');
}

/** Findings in one text file's content; `kind` is 'web' or 'sidecar'. */
export function scanText(name, original, kind) {
  const text = kind === 'sidecar' ? withoutLineStartComments(original) : original;
  const findings = [];
  const extension = extname(name).toLowerCase();

  if (kind === 'web' && LOADING_EXTENSIONS.has(extension)) {
    for (const pattern of LOADING_PATTERNS) {
      for (const match of text.matchAll(pattern)) findings.push(`${name}: loads from another host: ${snippet(text, match.index, match[0].length + 20)}`);
    }
  }

  for (const match of text.matchAll(URL_PATTERN)) {
    // A URL cut off by a trailing quote or dot is still the same host; test the prefix.
    if (!inert(match[0])) findings.push(`${name}: names an external address that is not on the allow-list: ${match[0]}`);
  }

  if (kind === 'web' && (extension === '.js' || extension === '.mjs' || extension === '.cjs' || extension === '.html')) {
    for (const pattern of SENDING_APIS) {
      const match = pattern.exec(text);
      if (match) findings.push(`${name}: uses ${match[0].trim()} (an API that can send data to a host of the page's choosing): ${snippet(text, match.index)}`);
    }
  }

  if (kind === 'sidecar') {
    const imported = new Set();
    for (const match of text.matchAll(/\brequire\s*\(\s*["']([^"']+)["']\s*\)/g)) imported.add(match[1]);
    for (const match of text.matchAll(/\bimport\s*\(\s*["']([^"']+)["']\s*\)/g)) imported.add(match[1]);
    for (const match of text.matchAll(/(?:^|[\n;])\s*(?:import|export)\b[^'"\n;]*?from\s*["']([^"']+)["']/g)) imported.add(match[1]);
    for (const match of text.matchAll(/(?:^|[\n;])\s*import\s*["']([^"']+)["']/g)) imported.add(match[1]);
    for (const specifier of imported) {
      if (!SIDECAR_ALLOWED_MODULES.includes(specifier)) findings.push(`${name}: imports '${specifier}', which is not one of ${SIDECAR_ALLOWED_MODULES.join(', ')}`);
    }
    for (const pattern of SIDECAR_NETWORK_GLOBALS) {
      const match = pattern.exec(text);
      if (match) findings.push(`${name}: uses ${match[0].trim()}: ${snippet(text, match.index)}`);
    }
  }
  return findings;
}

/** Scans the web build in `directory`; returns the findings. Throws when there is nothing to scan. */
export function checkWeb(directory) {
  if (!existsSync(directory) || !statSync(directory).isDirectory()) throw new Error(`${directory} is not a folder: build the web app first`);
  if (!existsSync(join(directory, 'index.html'))) throw new Error(`${directory} has no index.html: it is not a web build`);
  const findings = [];
  for (const file of walk(directory)) {
    if (!TEXT_EXTENSIONS.has(extname(file).toLowerCase()) || READ_ONLY_FILES.has(file.split(/[\\/]/).pop())) continue;
    findings.push(...scanText(relative(directory, file).replaceAll('\\', '/'), readFileSync(file, 'utf8'), 'web'));
  }
  return findings;
}

/** Scans the sidecar bundle at `file`. Throws when it is not there. */
export function checkSidecar(file) {
  if (!existsSync(file) || !statSync(file).isFile()) throw new Error(`${file} is not a file: build the sidecar first`);
  return scanText(file.split(/[\\/]/).pop(), readFileSync(file, 'utf8'), 'sidecar');
}

function main(argv) {
  const [mode, path] = argv;
  const jobs = [];
  if (mode === 'all') {
    jobs.push(['web', () => checkWeb(join(root, 'web', 'dist'))], ['sidecar', () => checkSidecar(join(root, 'sidecar', 'dist', 'sidecar.js'))]);
  } else if (mode === 'web' && path) {
    jobs.push(['web', () => checkWeb(resolve(path))]);
  } else if (mode === 'sidecar' && path) {
    jobs.push(['sidecar', () => checkSidecar(resolve(path))]);
  } else {
    console.error('usage: check-offline-bundles.mjs all | web <dist folder> | sidecar <sidecar.js>');
    return 2;
  }
  let status = 0;
  for (const [label, run] of jobs) {
    let findings;
    try {
      findings = run();
    } catch (error) {
      console.error(`check-offline-bundles: ${label}: ${error.message}`);
      return 2;
    }
    if (findings.length > 0) {
      console.error(`check-offline-bundles: ${label}: ${findings.length} finding(s)`);
      for (const finding of findings) console.error(`  ${finding}`);
      status = 1;
    } else {
      console.log(`check-offline-bundles: ${label}: no external host, no network module`);
    }
  }
  return status;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(main(process.argv.slice(2)));
