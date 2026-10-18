/**
 * Which npm packages end up inside a bundle (LG-03). THIRD-PARTY-NOTICES.txt (scripts/generate-notices.mjs) must name
 * everything that ships in the product, and a package declared under devDependencies can still be bundled (ajv is: the
 * editor validates a document with it). The lock file cannot say, so the build reports what it actually bundled:
 * vite.config.ts writes the report below after `vite build`, and the generator reads it.
 *
 * Runs under Node's type stripping and inside vite.config.ts, so keep to erasable TypeScript. sidecar/scripts/
 * bundled-packages.mjs is the same function for the sidecar's esbuild bundle; it lives there because the Docker sidecar
 * stage copies sidecar/ and web/src only.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

export interface BundledPackage {
  name: string;
  version: string;
}

/** The marker between a folder and a package's own folder name in a module path. */
const MARKER = '/node_modules/';

/** The packages the given module ids (Rollup ids, or paths) come from, sorted, once each. Ids that are not under node_modules are ignored. */
export function packagesOf(ids: Iterable<string>): BundledPackage[] {
  const found = new Map<string, BundledPackage>();
  for (const raw of ids) {
    const id = (raw.split('?')[0] ?? '').replaceAll('\\', '/');
    const at = id.lastIndexOf(MARKER);
    if (at < 0) continue;
    const parts = id.slice(at + MARKER.length).split('/');
    const name = parts[0]?.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0];
    if (name === undefined || name === '') continue;
    const manifest = `${id.slice(0, at + MARKER.length)}${name}/package.json`;
    if (!existsSync(manifest)) continue;
    const version = (JSON.parse(readFileSync(manifest, 'utf8')) as { version?: string }).version;
    if (version !== undefined) found.set(`${name}@${version}`, { name, version });
  }
  return [...found.values()].sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
}

export function writeReport(file: string, packages: BundledPackage[]): void {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(packages, null, 2)}\n`);
}
