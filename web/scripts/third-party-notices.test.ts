/**
 * THIRD-PARTY-NOTICES.txt ships in every release archive and the image (LG-03, LG-05). scripts/generate-notices.mjs
 * writes it and `--check` (publish.sh, verify-local.sh) proves it is current; this is the same promise as a test that
 * needs no restore: every production npm dependency, the elkjs EPL election and the texts that must not go missing are
 * in the committed file. It lives beside the other Node-side build tests (scripts/focus-rings.test.ts).
 */

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const notices = readFileSync(new URL('../../THIRD-PARTY-NOTICES.txt', import.meta.url), 'utf8');

function productionPackages(project: 'web' | 'sidecar'): string[] {
  const lock = JSON.parse(readFileSync(new URL(`../../${project}/package-lock.json`, import.meta.url), 'utf8')) as {
    packages: Record<string, { name?: string; version: string; dev?: boolean; devOptional?: boolean }>;
  };
  return Object.entries(lock.packages)
    .filter(([path, entry]) => path !== '' && !entry.dev && !entry.devOptional)
    .map(([path, entry]) => `${entry.name ?? path.replace(/^.*node_modules\//, '')} ${entry.version}`);
}

describe('THIRD-PARTY-NOTICES.txt', () => {
  it.each(['web', 'sidecar'] as const)('lists every production dependency of %s at its locked version', (project) => {
    const packages = productionPackages(project);
    expect(packages.length).toBeGreaterThan(0);
    for (const entry of packages) {
      expect(notices, `${entry} is missing: run node scripts/generate-notices.mjs`).toContain(`\n${entry}\n`);
    }
  });

  // ajv validates documents in the editor and is declared under devDependencies, so the lock file calls it dev; it is in the
  // bundle every browser downloads all the same (scripts/bundled-packages.ts records what vite really bundled).
  it('lists the devDependencies that the web bundle contains, from the build report', () => {
    const report = new URL('../.build-report/bundled-packages.json', import.meta.url);
    const bundled = JSON.parse(readFileSync(report, 'utf8')) as { name: string; version: string }[];
    expect(bundled.map((entry) => entry.name)).toEqual(expect.arrayContaining(['ajv', 'ajv-formats', 'react']));
    for (const entry of bundled) {
      expect(notices, `${entry.name} ${entry.version} is bundled but missing: run node scripts/generate-notices.mjs`).toContain(`\n${entry.name} ${entry.version}\n`);
    }
  });

  it('records the EPL-2.0 election for elkjs and carries the licence text and a source pointer', () => {
    expect(notices).toContain('Overpass Limited elects the Eclipse Public License 2.0');
    expect(notices).toContain('Eclipse Public License - v 2.0');
    expect(notices).toContain('https://github.com/kieler/elkjs');
    expect(notices).toContain('EPL-2.0 (elected; see section 1)');
  });

  it('carries the Lucide ISC text and the Feather MIT text behind it', () => {
    expect(notices).toContain('ISC License');
    expect(notices).toContain('Copyright (c) 2026 Lucide Icons and Contributors');
    expect(notices).toContain('Cole Bemis');
  });

  it('carries no package under the Open Source Maintenance Fee EULA (JsonSchema.Net and its two dependencies were replaced, ADR 0004)', () => {
    expect(notices).not.toMatch(/Open Source Maintenance Fee/);
    for (const name of ['JsonSchema.Net', 'Json.More.Net', 'JsonPointer.Net']) {
      expect(notices).not.toContain(name);
    }
  });
});
