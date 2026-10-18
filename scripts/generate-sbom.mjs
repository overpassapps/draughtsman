#!/usr/bin/env node
// Writes a CycloneDX 1.5 software bill of materials (JSON) for what a Draughtsman release ships that Overpass did not write.
//
//   node scripts/generate-sbom.mjs                          print the SBOM to standard output
//   node scripts/generate-sbom.mjs --out artifacts/draughtsman-0.1.0.cdx.json
//   node scripts/generate-sbom.mjs --timestamp 2026-10-14T09:00:00Z     stamp metadata.timestamp (omitted otherwise)
//   node scripts/generate-sbom.mjs --include-dev            also list build and test tooling (scope "excluded")
//
// No npm packages and no network: Node 22's own modules only, and only files already on disk. Like
// scripts/generate-notices.mjs it needs `dotnet restore` (src/Draughtsman.Server/obj/project.assets.json) for the NuGet
// closure; the npm side reads web/package-lock.json and sidecar/package-lock.json and never runs npm. It does not call
// `dotnet list package`, which would need a restore and a running SDK; project.assets.json is what that command reads.
//
// What is listed, by default:
//   * NuGet: every package in the restore closure of Draughtsman.Server (which references every shipped project). A package
//     with no runtime asset (an analyser) is listed with scope "excluded": it is not in the published output.
//   * npm: the production closure of web/ and sidecar/ from each lock file, plus the packages named in BUNDLED_DEV below.
//   * Node.js: the runtime every release archive bundles (version from scripts/node-runtime.txt).
// Not listed: the .NET runtime (self-contained archives carry whatever runtime the build machine restored; its notices
// are DOTNET-THIRD-PARTY-NOTICES.txt in each archive), the Debian base of the container image, and the optional Chromium.
// There is no dependency graph: this is a flat inventory.
//
// Deterministic: components are sorted by purl, nothing reads the clock or the machine, and the serial number is a UUID
// derived from the content, so the same inputs give a byte-identical file. A timestamp appears only when given.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Packages the lock file marks "dev" but that Vite bundles into web/dist, so they ship. `ajv` compiles the document schema
// in the browser (web/src/model/parse.ts); its closure comes along. web/src/model/parse.ts is the only import of either.
// scripts/generate-sbom.test.mjs fails when a non-test file under web/src imports a package that is not in the SBOM.
const BUNDLED_DEV = { web: ['ajv', 'ajv-formats'], sidecar: [] };

const NUGET_PROJECT = 'src/Draughtsman.Server/obj/project.assets.json';

let args;
try {
  ({ values: args } = parseArgs({
    options: {
      out: { type: 'string' },
      timestamp: { type: 'string' },
      'include-dev': { type: 'boolean', default: false },
      help: { type: 'boolean', short: 'h', default: false },
    },
    strict: true,
  }));
} catch (error) {
  console.error(`generate-sbom: ${error.message}`);
  process.exit(2);
}

if (args.help) {
  console.log(readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter((line) => line.startsWith('//')).slice(0, 10).map((line) => line.replace(/^\/\/ ?/, '')).join('\n'));
  process.exit(0);
}
if (args.timestamp !== undefined && Number.isNaN(Date.parse(args.timestamp))) {
  console.error(`generate-sbom: --timestamp ${args.timestamp} is not a date`);
  process.exit(1);
}

// --- helpers ------------------------------------------------------------------------------------------------------------

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

// CycloneDX licence choice: a bare SPDX-looking id, an SPDX expression, or (anything else) a free-text name.
function licenceChoice(text) {
  const value = (text ?? '').trim();
  if (value === '' || /^see\s+licen[sc]e/i.test(value)) return undefined;
  if (/^[A-Za-z0-9][A-Za-z0-9.+-]*$/.test(value)) return [{ license: { id: value } }];
  if (/^[A-Za-z0-9.+() -]+$/.test(value) && /\b(AND|OR|WITH)\b/.test(value)) return [{ expression: value }];
  return [{ license: { name: value } }];
}

function hexFromBase64(b64) {
  return Buffer.from(b64, 'base64').toString('hex');
}

function npmPurl(name, version) {
  return `pkg:npm/${name.startsWith('@') ? `%40${name.slice(1)}` : name}@${version}`;
}

function nugetPurl(id, version) {
  return `pkg:nuget/${id}@${version}`;
}

function propertyList(map) {
  return Object.entries(map).filter(([, value]) => value !== undefined).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([name, value]) => ({ name, value: String(value) }));
}

// --- NuGet --------------------------------------------------------------------------------------------------------------

function nugetComponents() {
  const assetsPath = join(root, NUGET_PROJECT);
  if (!existsSync(assetsPath)) {
    throw new Error(`${NUGET_PROJECT} is missing: run \`dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj\` first`);
  }
  const assets = readJson(assetsPath);
  const cache = Object.keys(assets.packageFolders)[0];
  // A package is "shipped" when any target framework gives it an asset that is copied to the output.
  const shipped = new Set();
  for (const target of Object.values(assets.targets)) {
    for (const [key, entry] of Object.entries(target)) {
      if (entry.type === 'package' && (entry.runtime || entry.runtimeTargets || entry.native)) shipped.add(key);
    }
  }

  const components = [];
  for (const [key, library] of Object.entries(assets.libraries)) {
    if (library.type !== 'package') continue;
    const slash = key.indexOf('/');
    const id = key.slice(0, slash);
    const version = key.slice(slash + 1);
    const lower = id.toLowerCase();
    const nuspecPath = join(cache, lower, version, `${lower}.nuspec`);
    const nuspec = existsSync(nuspecPath) ? readFileSync(nuspecPath, 'utf8') : '';
    const tag = (name) => new RegExp(`<${name}[^>]*>([^<]*)</${name}>`).exec(nuspec)?.[1]?.trim();
    const licenceTag = /<license type="(expression|file)">([^<]*)<\/license>/.exec(nuspec);

    let licenses;
    if (licenceTag?.[1] === 'expression') {
      licenses = licenceChoice(licenceTag[2]);
    } else if (licenceTag?.[1] === 'file') {
      licenses = [{ license: { name: `Custom licence: see ${licenceTag[2]} in the package` } }];
    } else if (tag('licenseUrl') !== undefined) {
      licenses = [{ license: { name: 'See the package licence URL', url: tag('licenseUrl') } }];
    }
    const projectUrl = tag('projectUrl');
    const purl = nugetPurl(id, version);
    components.push({
      type: 'library',
      'bom-ref': purl,
      name: id,
      version,
      scope: shipped.has(key) ? 'required' : 'excluded',
      ...(library.sha512 ? { hashes: [{ alg: 'SHA-512', content: hexFromBase64(library.sha512) }] } : {}),
      ...(licenses ? { licenses } : {}),
      purl,
      ...(projectUrl ? { externalReferences: [{ type: 'website', url: projectUrl }] } : {}),
      properties: propertyList({ 'draughtsman:ecosystem': 'nuget', 'draughtsman:origin': 'Draughtsman.Server' }),
    });
  }
  return components;
}

// --- npm ----------------------------------------------------------------------------------------------------------------

// Where `name`, required from the package at lock path `fromPath`, resolves to: node_modules next to it, then each parent's.
function resolveDependency(lock, fromPath, name) {
  let base = fromPath;
  for (;;) {
    const candidate = `${base === '' ? '' : `${base}/`}node_modules/${name}`;
    if (lock.packages[candidate] !== undefined) return candidate;
    if (base === '') return undefined;
    const cut = base.lastIndexOf('/node_modules/');
    base = cut === -1 ? '' : base.slice(0, cut);
  }
}

function closureOf(lock, rootNames) {
  const found = new Set();
  const queue = rootNames.map((name) => resolveDependency(lock, '', name));
  if (queue.some((path) => path === undefined)) {
    throw new Error(`BUNDLED_DEV names a package that is not in the lock file: ${rootNames.join(', ')}`);
  }
  while (queue.length > 0) {
    const path = queue.pop();
    if (found.has(path)) continue;
    found.add(path);
    const entry = lock.packages[path];
    for (const group of [entry.dependencies, entry.optionalDependencies, entry.peerDependencies]) {
      for (const name of Object.keys(group ?? {})) {
        const next = resolveDependency(lock, path, name);
        if (next !== undefined) queue.push(next);
      }
    }
  }
  return found;
}

function npmComponents() {
  const found = new Map();
  for (const project of ['web', 'sidecar']) {
    const lock = readJson(join(root, project, 'package-lock.json'));
    if (lock.lockfileVersion < 2) throw new Error(`${project}/package-lock.json is lockfileVersion ${lock.lockfileVersion}; expected 2 or 3`);
    const bundledDev = closureOf(lock, BUNDLED_DEV[project]);
    for (const [path, entry] of Object.entries(lock.packages)) {
      if (path === '' || entry.link === true || entry.version === undefined) continue;
      const isDev = entry.dev === true || entry.devOptional === true;
      const bundled = bundledDev.has(path);
      if (isDev && !bundled && !args['include-dev']) continue;
      const name = entry.name ?? path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length);
      const purl = npmPurl(name, entry.version);
      const origin = `${project}/package-lock.json`;
      const existing = found.get(purl);
      if (existing !== undefined) {
        // The same package in web and sidecar (yjs, lib0): one component, both origins. "required" wins over "excluded".
        const origins = new Set(existing.properties.find((p) => p.name === 'draughtsman:origin').value.split(', '));
        origins.add(origin);
        existing.properties.find((p) => p.name === 'draughtsman:origin').value = [...origins].sort().join(', ');
        if (!(isDev && !bundled)) existing.scope = 'required';
        continue;
      }
      const sha512 = /(?:^|\s)sha512-([A-Za-z0-9+/=]+)/.exec(entry.integrity ?? '')?.[1];
      const sha1 = /(?:^|\s)sha1-([A-Za-z0-9+/=]+)/.exec(entry.integrity ?? '')?.[1];
      const licence = typeof entry.license === 'string' ? entry.license : entry.license?.type;
      const licenses = licenceChoice(licence);
      found.set(purl, {
        type: 'library',
        'bom-ref': purl,
        name,
        version: entry.version,
        scope: isDev && !bundled ? 'excluded' : 'required',
        ...(sha512 ? { hashes: [{ alg: 'SHA-512', content: hexFromBase64(sha512) }] } : sha1 ? { hashes: [{ alg: 'SHA-1', content: hexFromBase64(sha1) }] } : {}),
        ...(licenses ? { licenses } : {}),
        purl,
        ...(entry.resolved ? { externalReferences: [{ type: 'distribution', url: entry.resolved }] } : {}),
        properties: propertyList({
          'draughtsman:ecosystem': 'npm',
          'draughtsman:origin': origin,
          // A "dev" dependency in the lock file that Vite still bundles into web/dist.
          'draughtsman:bundled-dev-dependency': bundled && isDev ? 'true' : undefined,
        }),
      });
    }
  }
  return [...found.values()];
}

// --- the bundled Node.js runtime ----------------------------------------------------------------------------------------

function nodeRuntimeComponent() {
  const text = readFileSync(join(root, 'scripts', 'node-runtime.txt'), 'utf8');
  const version = /^#\s*version\s+(\d+\.\d+\.\d+)\s*$/m.exec(text)?.[1];
  if (version === undefined) throw new Error('scripts/node-runtime.txt has no "# version x.y.z" line');
  const purl = `pkg:generic/nodejs@${version}`;
  return {
    type: 'application',
    'bom-ref': purl,
    name: 'Node.js',
    version,
    scope: 'required',
    licenses: [{ license: { name: 'MIT, with the licences of the software Node.js bundles (sidecar/LICENSE-node.txt in each archive)' } }],
    purl,
    externalReferences: [{ type: 'distribution', url: `https://nodejs.org/dist/v${version}/` }],
    properties: propertyList({
      'draughtsman:ecosystem': 'runtime',
      'draughtsman:origin': 'scripts/node-runtime.txt',
      'draughtsman:note': 'Bundled as sidecar/node in each release archive, checksum-pinned there. The container image installs Node 22.x from NodeSource instead.',
    }),
  };
}

// --- assemble -----------------------------------------------------------------------------------------------------------

const version = /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(root, 'Directory.Build.props'), 'utf8'))?.[1]?.trim();
if (version === undefined) throw new Error('could not read <Version> from Directory.Build.props');

const byPurl = (a, b) => (a.purl < b.purl ? -1 : a.purl > b.purl ? 1 : 0);
const nuget = nugetComponents();
const npm = npmComponents();
const components = [...nuget, ...npm, nodeRuntimeComponent()].sort(byPurl);

const seen = new Set();
for (const component of components) {
  if (seen.has(component.purl)) throw new Error(`duplicate purl ${component.purl}`);
  seen.add(component.purl);
}

const bom = {
  bomFormat: 'CycloneDX',
  specVersion: '1.5',
  version: 1,
  metadata: {
    tools: { components: [{ type: 'application', author: 'Overpass Limited', name: 'draughtsman scripts/generate-sbom.mjs' }] },
    component: {
      type: 'application',
      'bom-ref': 'draughtsman',
      name: 'draughtsman',
      version,
      description: 'Self-hosted, agent-native diagramming.',
      supplier: { name: 'Overpass Limited' },
    },
  },
  components,
};

// A serial number that is the same for the same content: a UUID (version nibble 5) taken from the SHA-256 of the document.
const digest = createHash('sha256').update(JSON.stringify(bom)).digest('hex');
const uuid = `${digest.slice(0, 8)}-${digest.slice(8, 12)}-5${digest.slice(13, 16)}-${'89ab'[Number.parseInt(digest[16], 16) % 4]}${digest.slice(17, 20)}-${digest.slice(20, 32)}`;
const output = {
  bomFormat: bom.bomFormat,
  specVersion: bom.specVersion,
  serialNumber: `urn:uuid:${uuid}`,
  version: bom.version,
  metadata: { ...(args.timestamp === undefined ? {} : { timestamp: new Date(args.timestamp).toISOString() }), ...bom.metadata },
  components: bom.components,
};
const json = `${JSON.stringify(output, null, 2)}\n`;

if (args.out === undefined) {
  process.stdout.write(json);
} else {
  writeFileSync(args.out, json);
}

const count = (filter) => components.filter(filter).length;
console.error(
  `generate-sbom: ${components.length} components (nuget ${count((c) => c.purl.startsWith('pkg:nuget/'))}, npm ${count((c) => c.purl.startsWith('pkg:npm/'))}, runtime 1); ` +
    `${count((c) => c.scope === 'required')} required, ${count((c) => c.scope === 'excluded')} excluded; ${count((c) => c.licenses === undefined)} without a licence` +
    (args.out === undefined ? '' : `; wrote ${args.out}`),
);
