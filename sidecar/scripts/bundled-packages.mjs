// Which npm packages end up inside the sidecar bundle (LG-03): the same function as web/scripts/bundled-packages.ts, for
// esbuild's metafile. THIRD-PARTY-NOTICES.txt (scripts/generate-notices.mjs) must name everything that ships, and a package
// under devDependencies can still be bundled; the lock file cannot say, so build.mjs writes this report and the generator
// reads it. A separate copy because the Docker sidecar stage copies sidecar/ and web/src only, not the repo's scripts/.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

const MARKER = '/node_modules/';

/** The packages the given module paths come from, sorted, once each. Paths outside node_modules are ignored. */
export function packagesOf(paths) {
  const found = new Map();
  for (const raw of paths) {
    const path = raw.split('?')[0].replaceAll('\\', '/');
    const at = path.lastIndexOf(MARKER);
    if (at < 0) continue;
    const parts = path.slice(at + MARKER.length).split('/');
    const name = parts[0].startsWith('@') ? parts.slice(0, 2).join('/') : parts[0];
    if (name === '') continue;
    const manifest = `${path.slice(0, at + MARKER.length)}${name}/package.json`;
    if (!existsSync(manifest)) continue;
    const version = JSON.parse(readFileSync(manifest, 'utf8')).version;
    if (version !== undefined) found.set(`${name}@${version}`, { name, version });
  }
  return [...found.values()].sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
}

export function writeReport(file, packages) {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(packages, null, 2)}\n`);
}
