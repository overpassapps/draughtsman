// Run with: node --test scripts/generate-sbom.test.mjs
// Needs `dotnet restore` first (the script reads src/Draughtsman.Server/obj/project.assets.json); without it the suite is
// skipped with that reason, since a release must fail loudly in generate-sbom.mjs itself, not here.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const script = join(root, 'scripts', 'generate-sbom.mjs');
const restored = existsSync(join(root, 'src', 'Draughtsman.Server', 'obj', 'project.assets.json'));
const options = { skip: restored ? false : 'run `dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj` first' };

function run(...extra) {
  const result = spawnSync(process.execPath, [script, ...extra], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}

test('emits a CycloneDX 1.5 document with components', options, () => {
  const bom = JSON.parse(run());
  assert.equal(bom.bomFormat, 'CycloneDX');
  assert.equal(bom.specVersion, '1.5');
  assert.match(bom.serialNumber, /^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.equal(bom.metadata.component.name, 'draughtsman');
  const version = /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(root, 'Directory.Build.props'), 'utf8'))[1];
  assert.equal(bom.metadata.component.version, version);
  assert.ok(Array.isArray(bom.components) && bom.components.length > 0, 'components must not be empty');
  for (const component of bom.components) {
    assert.ok(component.name && component.version && component.purl, `incomplete component ${JSON.stringify(component)}`);
    assert.equal(component['bom-ref'], component.purl);
  }
  const purls = bom.components.map((component) => component.purl);
  assert.equal(new Set(purls).size, purls.length, 'purls must be unique');
  assert.deepEqual(purls, [...purls].sort(), 'components must be sorted by purl');
  assert.ok(purls.some((purl) => purl.startsWith('pkg:nuget/')), 'expected NuGet components');
  assert.ok(purls.some((purl) => purl.startsWith('pkg:npm/')), 'expected npm components');
  assert.ok(!('timestamp' in bom.metadata), 'no timestamp unless one is given');
});

test('is byte-identical across two runs', options, () => {
  assert.equal(run(), run());
});

test('--timestamp only adds metadata.timestamp', options, () => {
  const plain = JSON.parse(run());
  const stamped = JSON.parse(run('--timestamp', '2026-10-14T09:00:00Z'));
  assert.equal(stamped.metadata.timestamp, '2026-10-14T09:00:00.000Z');
  assert.equal(stamped.serialNumber, plain.serialNumber);
  assert.deepEqual(stamped.components, plain.components);
});

// Vite bundles web/package.json devDependencies into web/dist when src imports them (ajv does), and the lock file still marks
// them "dev". Anything the SPA imports at run time has to be in the SBOM, or the inventory silently under-reports what ships.
test('lists every package the web app imports outside its tests', options, () => {
  const names = new Set(JSON.parse(run()).components.filter((component) => component.purl.startsWith('pkg:npm/')).map((component) => component.name));
  const testOnly = new Set(['vitest', 'jsdom', 'axe-core', '@testing-library/react', '@testing-library/dom']);
  const imported = new Set();
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      const path = join(dir, entry);
      if (statSync(path).isDirectory()) {
        if (entry !== 'generated') walk(path);
      } else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && entry !== 'loadTolerance.ts') {
        for (const match of readFileSync(path, 'utf8').matchAll(/(?:^|\n)\s*(?:import|export)\b[^'"\n]*?from\s+['"]([^'"]+)['"]/g)) {
          const specifier = match[1];
          if (specifier.startsWith('.') || specifier.startsWith('node:') || specifier.startsWith('/')) continue;
          const parts = specifier.split('/');
          imported.add(specifier.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]);
        }
      }
    }
  };
  walk(join(root, 'web', 'src'));
  for (const name of imported) {
    if (!testOnly.has(name)) assert.ok(names.has(name), `web/src imports ${name} but the SBOM does not list it`);
  }
  assert.ok(imported.has('ajv'), 'the scan should have found the ajv import in web/src/model/parse.ts');
});
