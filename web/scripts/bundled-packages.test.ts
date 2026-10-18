import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { packagesOf } from './bundled-packages.ts';

// What the build report says a bundle contains is read from module paths: a plain package, a scoped one, one nested under
// another's own node_modules, a query suffix (`?url`), and the things that must not be counted.
describe('packagesOf', () => {
  let root: string;

  function install(path: string, version: string): string {
    const dir = join(root, ...path.split('/'));
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'package.json'), JSON.stringify({ name: path.split('node_modules/').pop(), version }));
    return dir.replaceAll('\\', '/');
  }

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), 'bundled-packages-'));
  });

  afterEach(() => {
    rmSync(root, { recursive: true, force: true });
  });

  it('names each package once with its installed version, however many of its files are bundled', () => {
    const ajv = install('node_modules/ajv', '8.20.0');
    const scoped = install('node_modules/@scope/pkg', '1.2.3');
    const nested = install('node_modules/ajv/node_modules/uri-js', '4.0.0');

    expect(packagesOf([`${ajv}/dist/ajv.js`, `${ajv}/dist/core.js`, `${scoped}/lib/index.js`, `${nested}/dist/index.js`])).toEqual([
      { name: '@scope/pkg', version: '1.2.3' },
      { name: 'ajv', version: '8.20.0' },
      { name: 'uri-js', version: '4.0.0' },
    ]);
  });

  it('reads through a ?url query, and ignores your own source, virtual modules and a package with no manifest', () => {
    const font = install('node_modules/@fontsource/inter', '5.0.0');

    expect(packagesOf([`${font}/files/inter.woff2?url`])).toEqual([{ name: '@fontsource/inter', version: '5.0.0' }]);
    expect(packagesOf(['/repo/web/src/main.tsx', '\0vite/preload-helper', `${root.replaceAll('\\', '/')}/node_modules/ghost/index.js`])).toEqual([]);
  });
});
