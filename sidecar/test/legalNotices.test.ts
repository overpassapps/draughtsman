import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const bundle = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'dist', 'sidecar.js'), 'utf8');

// LG-03: esbuild drops elkjs's plain-block licence header unless scripts/build.mjs puts it back, and the bundle is handed
// to customers. `npm test` builds first (pretest), so this reads the bundle a release would carry.
describe('third-party notices in dist/sidecar.js', () => {
  it('keeps the elkjs copyright and the EPL-2.0 election text verbatim', () => {
    expect(bundle).toContain('Copyright (c) 2017 Kiel University and others.');
    expect(bundle).toContain('SPDX-License-Identifier: EPL-2.0 OR GPL-3.0-or-later');
    expect(bundle).toContain('https://www.eclipse.org/legal/epl-2.0');
  });

  it('points to the full licence texts that ship beside it', () => {
    expect(bundle).toContain('THIRD-PARTY-NOTICES.txt');
  });

  it('is still a runnable script after the footer is appended', () => {
    // A footer that closed its comment early would be a syntax error: compile the file without running it.
    expect(() => new Function(bundle.replace(/^#!.*\n/, ''))).not.toThrow();
  });
});

// LG-03: the lock file cannot say what ends up in the bundle, so the build records it (scripts/bundled-packages.mjs) and
// scripts/generate-notices.mjs reads that. Every package the bundle contains must be in the committed notices.
describe('the notices cover what the sidecar bundle contains', () => {
  const root = join(dirname(fileURLToPath(import.meta.url)), '..');
  const bundled = JSON.parse(readFileSync(join(root, '.build-report', 'bundled-packages.json'), 'utf8')) as { name: string; version: string }[];
  const notices = readFileSync(join(root, '..', 'THIRD-PARTY-NOTICES.txt'), 'utf8');

  it('reports the packages esbuild bundled, including elkjs, yjs and lib0', () => {
    expect(bundled.map((entry) => entry.name)).toEqual(expect.arrayContaining(['elkjs', 'yjs', 'lib0']));
  });

  it('names every one of them at the bundled version', () => {
    for (const entry of bundled) {
      expect(notices, `${entry.name} ${entry.version} is bundled but missing from THIRD-PARTY-NOTICES.txt: run node scripts/generate-notices.mjs`).toContain(`\n${entry.name} ${entry.version}\n`);
    }
  });
});
