// Builds dist/sidecar.js: a type check followed by one esbuild bundle. Called by `npm run build` and, so that
// `dotnet build` does not depend on npm's script runner, directly as `node scripts/build.mjs` from
// src/Draughtsman.Sidecar/Draughtsman.Sidecar.csproj.
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { packagesOf, writeReport } from './bundled-packages.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Run tsc with the same node binary that is running this script, never whatever `tsc` is first on PATH.
const tsc = join(root, 'node_modules', 'typescript', 'bin', 'tsc');
const typeCheck = spawnSync(process.execPath, [tsc, '--noEmit', '-p', join(root, 'tsconfig.json')], {
  cwd: root,
  stdio: 'inherit',
});
if (typeCheck.status !== 0) {
  process.exit(typeCheck.status ?? 1);
}

// Licence headers must survive bundling (LG-03). esbuild keeps only comments marked @license, @preserve or /*!, and
// elkjs marks its header as a plain block comment, so it would be dropped: read it from the installed package and append
// it to the bundle verbatim, failing the build rather than shipping elkjs without its notice if a new version moves it.
// The other bundled packages (yjs, lib0) carry their licence in a file, not a header; the release archive and image
// ship all of them in THIRD-PARTY-NOTICES.txt (scripts/generate-notices.mjs), which this footer points to.
function thirdPartyFooter() {
  const elk = readFileSync(join(root, 'node_modules', 'elkjs', 'lib', 'elk.bundled.js'), 'utf8');
  const header = /\/\*{3,}\n \* Copyright[^]*?SPDX-License-Identifier: [^\n]*\n \*{3,}\//.exec(elk)?.[0];
  if (header === undefined) {
    throw new Error('elkjs licence header not found in node_modules/elkjs/lib/elk.bundled.js; update thirdPartyFooter() in scripts/build.mjs');
  }
  const version = JSON.parse(readFileSync(join(root, 'node_modules', 'elkjs', 'package.json'), 'utf8')).version;
  const verbatim = header.replace(/^\/\*+\n/, `/*! elkjs ${version} licence header, verbatim:\n`).replace(/\n \*{3,}\/$/, '\n */');
  return [
    '/*! Bundled third-party software: elkjs (used under EPL-2.0), yjs and lib0 (MIT).',
    ' * Full licence texts: THIRD-PARTY-NOTICES.txt, shipped beside this file in the release archive and image. */',
    verbatim,
  ].join('\n');
}

// A release build (scripts/publish.sh) minifies the bundle: no whitespace or long identifiers to read back from a file
// that is handed to customers. Development and test builds stay readable, so stack traces and diffs mean something.
const release = process.env.DRAUGHTSMAN_RELEASE === '1';

mkdirSync(join(root, 'dist'), { recursive: true });
const outfile = join(root, 'dist', 'sidecar.js');
const result = await build({
  entryPoints: [join(root, 'src', 'main.ts')],
  outfile,
  bundle: true,
  platform: 'node',
  target: 'node22',
  // CommonJS so the file runs as `node sidecar.js` from any folder, with or without a package.json beside it.
  format: 'cjs',
  sourcemap: false,
  minify: release,
  // route.document bundles web/src (canvas/routeDocument.ts), which imports yjs from web/node_modules locally and from
  // nowhere in the Docker sidecar stage. One yjs, this package's, serves every importer (two copies would also make
  // Yjs warn that it was imported twice); its version is pinned to the web one (package.json).
  alias: { yjs: join(root, 'node_modules', 'yjs') },
  legalComments: 'eof',
  metafile: true,
  logLevel: 'warning',
  banner: { js: '#!/usr/bin/env node\n"use strict";' },
  footer: { js: thirdPartyFooter() },
});
// What the bundle really contains, for THIRD-PARTY-NOTICES.txt (scripts/generate-notices.mjs): a devDependency that gets
// bundled ships to customers like any other. Outside dist/, which is copied into every archive and the image.
writeReport(join(root, '.build-report', 'bundled-packages.json'), packagesOf(Object.keys(result.metafile.inputs).map((input) => resolve(root, input))));
const size = statSync(outfile).size;
console.log(`sidecar bundle written: ${outfile} (${(size / 1024 / 1024).toFixed(2)} MiB)`);
