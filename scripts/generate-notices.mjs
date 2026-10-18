// Writes THIRD-PARTY-NOTICES.txt: the licences of everything Draughtsman ships that Overpass did not write (LG-03, LG-05).
//
//   node scripts/generate-notices.mjs           rewrite THIRD-PARTY-NOTICES.txt
//   node scripts/generate-notices.mjs --check   fail if the committed file is stale, or a licence nobody has reviewed appeared
//
// Needs `dotnet restore` (src/Draughtsman.Server/obj/project.assets.json), `npm ci` and a build in web/ and sidecar/; it reads the
// lock files for what is in the production dependency closure, the build reports (web/ and sidecar/ .build-report/) for
// devDependencies that are bundled all the same, and the installed packages for each licence text, so it is
// offline and deterministic (no dates, no paths, stable order). The file is committed and ships in every release archive
// and in the image; publish.sh runs --check first, so a dependency added without regenerating cannot be released.
//
// The rules that keep this honest:
//   * A licence is either an SPDX id on the allow-list below, or a package named in KNOWN_CUSTOM (reviewed by a person).
//     Anything else stops the run, so a new dependency is noticed rather than silently included.
//   * Text comes from the package's own licence file whenever it ships one; only a bare SPDX expression falls back to the
//     canonical text in legal/licences/. Nothing is paraphrased.
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const outputPath = join(root, 'THIRD-PARTY-NOTICES.txt');
const licenceDir = join(root, 'legal', 'licences');

// The SIL Open Font Licence 1.1 is accepted for the bundled web fonts only (PK-09): the two @fontsource-variable packages
// that carry Inter and JetBrains Mono unmodified (subset files). It lets the fonts be bundled and redistributed with the
// product, but not sold on their own, and a modified version may not keep the Reserved Font Name; neither font declares
// one. Any other package under it stops the run like an unreviewed licence.
const OFL_FONT_PACKAGES = new Set(['@fontsource-variable/inter', '@fontsource-variable/jetbrains-mono']);

const PERMISSIVE = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'Zlib', 'PostgreSQL', 'Unlicense', 'CC0-1.0']);

// Packages whose licence is a custom text, each one a decision someone has taken (or still has to). The text is still
// reproduced in full; the run prints the reason so it cannot be forgotten. There are none today: the only ones there ever
// were, JsonSchema.Net and the two packages it needs, are under the Open Source Maintenance Fee EULA and were replaced
// by SchemaValidator (docs/adr/0004-document-schema-validator.md), which is why FORBIDDEN names them.
const KNOWN_CUSTOM = new Map();

// Packages that must not come back into anything Draughtsman ships, whatever their licence file says. A licence-file
// package that is not in KNOWN_CUSTOM already stops the run; this says why, for the person who adds one of these again.
const FORBIDDEN = new Map([
  ['JsonSchema.Net', 'Open Source Maintenance Fee EULA: the compiled library charges a monthly fee to whoever redistributes it in a product'],
  ['Json.More.Net', 'Open Source Maintenance Fee EULA: a dependency of JsonSchema.Net'],
  ['JsonPointer.Net', 'Open Source Maintenance Fee EULA: a dependency of JsonSchema.Net'],
]);

const errors = [];
const review = [];

function normalise(text) {
  return text.replace(/^﻿/, '').replace(/\r\n?/g, '\n').split('\n').map((line) => line.trimEnd()).join('\n').trim();
}

function readText(path) {
  return normalise(readFileSync(path, 'utf8'));
}

function licenceFile(dir) {
  if (!existsSync(dir)) return undefined;
  const name = readdirSync(dir).sort().find((file) => /^(licen[sc]e|copying)(\.(txt|md))?$/i.test(file));
  return name === undefined ? undefined : readText(join(dir, name));
}

function canonical(name) {
  return readText(join(licenceDir, name));
}

function mitText(copyright) {
  return `MIT License\n\n${copyright}\n\n${canonical('MIT-terms.txt')}`;
}

// --- NuGet: the closure the published server actually carries -----------------------------------------------------------

function nugetPackages() {
  const assetsPath = join(root, 'src', 'Draughtsman.Server', 'obj', 'project.assets.json');
  if (!existsSync(assetsPath)) {
    throw new Error('src/Draughtsman.Server/obj/project.assets.json is missing: run `dotnet restore` first');
  }
  const assets = JSON.parse(readFileSync(assetsPath, 'utf8'));
  const cache = Object.keys(assets.packageFolders)[0];
  const packages = [];
  for (const key of Object.keys(assets.libraries)) {
    if (assets.libraries[key].type !== 'package') continue;
    const slash = key.indexOf('/');
    const id = key.slice(0, slash);
    const version = key.slice(slash + 1);
    const dir = join(cache, id.toLowerCase(), version);
    if (FORBIDDEN.has(id)) {
      errors.push(`NuGet ${id} ${version}: ${FORBIDDEN.get(id)} (docs/adr/0004-document-schema-validator.md): remove it`);
      continue;
    }
    const nuspec = readFileSync(join(dir, `${id.toLowerCase()}.nuspec`), 'utf8');
    const tag = (name) => new RegExp(`<${name}[^>]*>([^<]*)</${name}>`).exec(nuspec)?.[1]?.trim();
    const licenceTag = /<license type="(expression|file)">([^<]*)<\/license>/.exec(nuspec);
    const copyright = tag('copyright') ?? (tag('authors') ? `Copyright (c) ${tag('authors')}` : undefined);
    const url = tag('projectUrl') ?? /<repository[^>]*url="([^"]*)"/.exec(nuspec)?.[1];
    let licence;
    let text;
    if (licenceTag?.[1] === 'file') {
      licence = KNOWN_CUSTOM.has(id) ? 'custom: see text' : undefined;
      const path = join(dir, licenceTag[2]);
      text = existsSync(path) ? readText(path) : undefined;
    } else {
      licence = licenceTag?.[2];
      text = readOverride(id) ?? licenceFile(dir);
    }
    if (licence === undefined || (licence !== 'custom: see text' && !PERMISSIVE.has(licence))) {
      errors.push(`NuGet ${id} ${version}: licence '${licenceTag?.[2] ?? tag('licenseUrl') ?? 'none declared'}' is not on the allow-list; review it, then add it to PERMISSIVE or KNOWN_CUSTOM`);
      continue;
    }
    if (licence === 'custom: see text') review.push(`${id} ${version}: ${KNOWN_CUSTOM.get(id)}`);
    if (text === undefined) {
      if (licence === 'MIT') {
        text = mitText(copyright ?? `Copyright (c) ${id} authors`);
      } else if (licence === 'Apache-2.0') {
        text = canonical('Apache-2.0.txt');
      } else {
        errors.push(`NuGet ${id} ${version}: no licence text found for ${licence}; add legal/licences/${id}.txt`);
        continue;
      }
    }
    packages.push({ name: id, version, source: 'NuGet', licence, copyright, url, text });
  }
  return packages;
}

function readOverride(id) {
  const path = join(licenceDir, `${id}.txt`);
  return existsSync(path) ? readText(path) : undefined;
}

// --- npm: production closure of web/ and sidecar/ -----------------------------------------------------------------------

function npmPackages() {
  const found = new Map();
  for (const project of ['web', 'sidecar']) {
    const lock = JSON.parse(readFileSync(join(root, project, 'package-lock.json'), 'utf8'));
    for (const [path, entry] of Object.entries(lock.packages)) {
      if (path === '' || entry.dev || entry.devOptional) continue;
      const dir = join(root, project, path);
      if (!existsSync(join(dir, 'package.json'))) {
        throw new Error(`${project}/${path} is not installed: run \`npm ci\` in ${project}/ first`);
      }
      addNpmPackage(found, dir);
    }
  }
  addBundledDevPackages(found);
  return [...found.values()];
}

// The lock file says what is a devDependency, not what ends up in a bundle: ajv (and what it needs) is a devDependency of
// web/ that the editor's document validation bundles into the app every customer's browser downloads. The builds write what
// they actually bundled (web/scripts/bundled-packages.ts from vite.config.ts, sidecar/scripts/bundled-packages.mjs from
// esbuild's metafile), and anything in those reports that the production closure above did not already cover is added.
function addBundledDevPackages(found) {
  for (const project of ['web', 'sidecar']) {
    const reportPath = join(root, project, '.build-report', 'bundled-packages.json');
    if (!existsSync(reportPath)) {
      throw new Error(`${project}/.build-report/bundled-packages.json is missing: build ${project}/ first (\`npm run build\` in ${project}/), so the notices cover what the bundle really contains`);
    }
    for (const bundled of JSON.parse(readFileSync(reportPath, 'utf8'))) {
      if (found.has(`${bundled.name}@${bundled.version}`)) continue;
      const dir = ['web', 'sidecar']
        .map((candidate) => join(root, candidate, 'node_modules', bundled.name))
        .find((candidate) => existsSync(join(candidate, 'package.json')) && JSON.parse(readFileSync(join(candidate, 'package.json'), 'utf8')).version === bundled.version);
      if (dir === undefined) {
        errors.push(`npm ${bundled.name}@${bundled.version}: bundled into ${project}/ but not installed at that version; run \`npm ci\` and rebuild`);
        continue;
      }
      addNpmPackage(found, dir);
    }
  }
}

// One installed npm package, reduced to a notice entry: its licence must be on the allow-list and its own licence text found.
function addNpmPackage(found, dir) {
  const manifest = JSON.parse(readFileSync(join(dir, 'package.json'), 'utf8'));
  const key = `${manifest.name}@${manifest.version}`;
  if (found.has(key)) return;
  const licence = typeof manifest.license === 'string' ? manifest.license : manifest.license?.type;
  const elected = manifest.name === 'elkjs';
  const fontUnderOfl = licence === 'OFL-1.1' && OFL_FONT_PACKAGES.has(manifest.name);
  if (!elected && !fontUnderOfl && (licence === undefined || !PERMISSIVE.has(licence))) {
    errors.push(`npm ${key}: licence '${licence ?? 'none declared'}' is not on the allow-list; review it`);
    return;
  }
  const text = licenceFile(dir);
  if (text === undefined) {
    errors.push(`npm ${key}: no licence file in the installed package`);
    return;
  }
  const url = typeof manifest.repository === 'string' ? manifest.repository : manifest.repository?.url?.replace(/^git\+/, '').replace(/\.git$/, '') ?? manifest.homepage;
  found.set(key, {
    name: manifest.name,
    version: manifest.version,
    source: 'npm',
    licence: elected ? 'EPL-2.0 (elected; see section 1)' : licence,
    copyright: undefined,
    url,
    text,
  });
}

// --- Things that are shipped without a package manager ------------------------------------------------------------------

function bundledSoftware() {
  return [
    {
      name: 'Lucide icons',
      version: 'as inlined in the web app',
      source: 'artwork',
      licence: 'ISC (some icons MIT, from Feather)',
      url: 'https://lucide.dev',
      text: canonical('Lucide.txt'),
    },
    {
      name: '.NET runtime and libraries',
      version: 'as published in each self-contained archive',
      source: 'runtime',
      licence: 'MIT',
      url: 'https://github.com/dotnet/runtime',
      text: canonical('dotnet-runtime.txt'),
      note: 'Each archive also carries DOTNET-THIRD-PARTY-NOTICES.txt, the .NET project\'s own list of the software inside its runtime.',
    },
    {
      name: 'ASP.NET Core',
      version: 'as published in each self-contained archive',
      source: 'runtime',
      licence: 'MIT',
      url: 'https://github.com/dotnet/aspnetcore',
      text: canonical('aspnetcore.txt'),
    },
    {
      name: 'Node.js',
      version: '22.x, official build (sidecar/node in each archive; the image installs its own)',
      source: 'runtime',
      licence: 'MIT, with the licences of the software Node.js itself bundles (V8, OpenSSL, ICU, and others)',
      url: 'https://nodejs.org',
      text: 'The complete Node.js licence text, including those bundled components, is sidecar/LICENSE-node.txt in every release\narchive, and the Node.js package\'s own licence files under /usr/share/doc/nodejs in the container image.',
    },
    {
      name: 'SQLite',
      version: 'native library inside SQLitePCLRaw.lib.e_sqlite3',
      source: 'native',
      licence: 'Public domain',
      url: 'https://sqlite.org/copyright.html',
      text: 'SQLite is in the public domain. See https://sqlite.org/copyright.html.',
    },
    {
      name: 'Playwright driver',
      version: 'the .playwright folder copied in by Microsoft.Playwright',
      source: 'runtime',
      licence: 'Apache-2.0 (its bundled Node.js is MIT)',
      url: 'https://github.com/microsoft/playwright',
      text: 'The driver runs the optional Chromium PDF export. Its own notices are in .playwright/package/ in the release archive\nand the image (ThirdPartyNotices.txt where the package provides one); its licence is Apache-2.0 (text in section 3).',
    },
    {
      name: 'Container image base',
      version: 'container image only',
      source: 'operating system',
      licence: 'various, including GPL and LGPL components that are normal for a Debian-based image',
      url: 'https://www.debian.org',
      text: 'The container image contains Debian packages; each package\'s licence is in /usr/share/doc/<package>/copyright inside\nthe image. Draughtsman does not modify them. The optional Chromium install adds Chromium and its dependencies, under\ntheir own licences.',
    },
  ];
}

// --- Rendering ----------------------------------------------------------------------------------------------------------

const RULE = '='.repeat(100);

function render(packages) {
  const sorted = [...packages].sort((a, b) => a.source.localeCompare(b.source) || a.name.toLowerCase().localeCompare(b.name.toLowerCase()) || a.version.localeCompare(b.version));
  const lines = [];
  lines.push('THIRD-PARTY NOTICES');
  lines.push('');
  lines.push('Draughtsman is free software, copyright Overpass Limited, released under the GNU AGPL-3.0-only (LICENSE). It includes the third-party software listed here, each');
  lines.push('under its own licence. This file is generated by scripts/generate-notices.mjs and ships in every release archive and');
  lines.push('in the container image; the same text is served by a running instance at /THIRD-PARTY-NOTICES.txt.');
  lines.push('');
  lines.push(RULE);
  lines.push('1. LICENCE ELECTION: ELKJS');
  lines.push(RULE);
  lines.push('');
  lines.push('elkjs (the Eclipse Layout Kernel compiled to JavaScript, used for automatic diagram layout) is offered under the');
  lines.push('Eclipse Public License 2.0 OR the GNU General Public License 3.0 or later (SPDX: EPL-2.0 OR GPL-3.0-or-later).');
  lines.push('Overpass Limited elects the Eclipse Public License 2.0 and receives and distributes elkjs under those terms only.');
  lines.push('');
  lines.push('  * elkjs is included unmodified, inside the sidecar bundle (sidecar/sidecar.js). Its licence header is kept in that file.');
  lines.push('  * The source code of elkjs and of the Eclipse Layout Kernel (the Program\'s Source Code under EPL-2.0 section 3) is');
  lines.push('    available from the upstream projects: https://github.com/kieler/elkjs and https://github.com/eclipse-elk/elk (the');
  lines.push('    exact elkjs version is in the list below; it is tag v<version> in the elkjs repository).');
  lines.push('');
  lines.push('The EPL-2.0 text is reproduced in section 3, under elkjs.');
  lines.push('');
  lines.push(RULE);
  lines.push('2. SOFTWARE INCLUDED');
  lines.push(RULE);
  lines.push('');
  for (const item of sorted) {
    lines.push(`${item.name} ${item.version}`);
    lines.push(`    source: ${item.source}; licence: ${item.licence}`);
    if (item.copyright) lines.push(`    ${item.copyright}`);
    if (item.url) lines.push(`    ${item.url}`);
    if (item.note) lines.push(`    ${item.note}`);
    lines.push('');
  }
  lines.push(RULE);
  lines.push('3. LICENCE TEXTS');
  lines.push(RULE);

  // Identical texts are printed once, with everything that uses them.
  const blocks = new Map();
  for (const item of sorted) {
    const hash = createHash('sha256').update(item.text).digest('hex');
    const block = blocks.get(hash) ?? { text: item.text, users: [] };
    block.users.push(`${item.name} ${item.version}`);
    blocks.set(hash, block);
  }
  for (const block of [...blocks.values()].sort((a, b) => a.users[0].toLowerCase().localeCompare(b.users[0].toLowerCase()))) {
    lines.push('');
    lines.push('-'.repeat(100));
    lines.push(`For: ${block.users.join('; ')}`);
    lines.push('-'.repeat(100));
    lines.push('');
    lines.push(block.text);
  }
  lines.push('');
  return `${lines.join('\n')}`;
}

const check = process.argv.includes('--check');
const all = [...nugetPackages(), ...npmPackages(), ...bundledSoftware()];
if (errors.length > 0) {
  for (const message of errors) console.error(`ERROR: ${message}`);
  process.exit(1);
}
for (const message of review) console.warn(`REVIEW: ${message}`);
const output = render(all);
if (check) {
  const current = existsSync(outputPath) ? readFileSync(outputPath, 'utf8') : '';
  if (current !== output) {
    console.error('ERROR: THIRD-PARTY-NOTICES.txt is stale: run `node scripts/generate-notices.mjs` and commit it');
    process.exit(1);
  }
  console.log(`THIRD-PARTY-NOTICES.txt is up to date (${all.length} entries)`);
} else {
  writeFileSync(outputPath, output);
  console.log(`wrote THIRD-PARTY-NOTICES.txt (${all.length} entries, ${(output.length / 1024).toFixed(0)} KiB)`);
}
