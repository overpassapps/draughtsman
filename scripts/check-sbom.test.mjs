// Run with: node --test scripts/check-sbom.test.mjs
// scripts/check-sbom.mjs is what makes "every release carries an SBOM" more than a file that happens to exist: each defect a hollow or
// wrong one could have is caught here on its own, and the generator's real output for this repository passes, including the
// cross-check against the sources. The generator-based tests need `dotnet restore` first (project.assets.json) and are skipped, with
// that reason, without it.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { checkSbom } from './check-sbom.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const checker = join(root, 'scripts', 'check-sbom.mjs');
const generator = join(root, 'scripts', 'generate-sbom.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'check-sbom-'));
after(() => rmSync(scratch, { recursive: true, force: true }));
const restored = existsSync(join(root, 'src', 'Draughtsman.Server', 'obj', 'project.assets.json'));
const needsRestore = { skip: restored ? false : 'run `dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj` first' };

const component = (purl, origin, scope = 'required') => ({
  type: 'library',
  'bom-ref': purl,
  name: purl.slice(purl.indexOf('/') + 1, purl.lastIndexOf('@')).replace('%40', '@'),
  version: purl.slice(purl.lastIndexOf('@') + 1),
  purl,
  scope,
  properties: [{ name: 'draughtsman:origin', value: origin }],
});

/** A small SBOM that satisfies every structural rule; each test breaks one thing in a copy of it. */
function good() {
  return structuredClone({
    bomFormat: 'CycloneDX',
    specVersion: '1.5',
    serialNumber: 'urn:uuid:3e671687-395b-5f5f-a0b5-0e9c1a4c8f10',
    version: 1,
    metadata: { component: { type: 'application', 'bom-ref': 'draughtsman', name: 'draughtsman', version: '0.1.0' } },
    components: [
      component('pkg:nuget/YamlDotNet@18.1.0', 'Draughtsman.Server'),
      component('pkg:npm/react@18.3.1', 'web/package-lock.json'),
      component('pkg:npm/elkjs@0.12.0', 'sidecar/package-lock.json'),
      component('pkg:generic/nodejs@22.23.3', 'scripts/node-runtime.txt'),
    ],
  });
}

function problemsOf(change, options = { version: '0.1.0' }) {
  const bom = good();
  change(bom);
  return checkSbom(bom, options);
}

function assertRefused(problems, needle) {
  assert.ok(problems.some((problem) => problem.includes(needle)), `expected a problem mentioning "${needle}", got:\n${problems.join('\n') || '(none: the SBOM was accepted)'}`);
}

test('a complete SBOM for the release is accepted', () => {
  assert.deepEqual(checkSbom(good(), { version: '0.1.0' }), []);
});

test('the wrong format, spec version or serial number is refused', () => {
  assertRefused(problemsOf((b) => { b.bomFormat = 'SPDX'; }), 'bomFormat');
  assertRefused(problemsOf((b) => { b.specVersion = '1.4'; }), 'specVersion');
  assertRefused(problemsOf((b) => { delete b.serialNumber; }), 'serialNumber');
  assertRefused(problemsOf((b) => { b.serialNumber = 'not-a-urn'; }), 'serialNumber');
  assertRefused(checkSbom([], {}), 'not a JSON object');
  assertRefused(checkSbom(null, {}), 'not a JSON object');
});

test('an SBOM for another product or another version is refused', () => {
  assertRefused(problemsOf((b) => { b.metadata.component.name = 'something-else'; }), 'metadata.component.name');
  assertRefused(problemsOf((b) => { b.metadata.component.version = '0.0.9'; }), 'this is release 0.1.0');
  assertRefused(problemsOf((b) => { delete b.metadata; }), 'metadata.component.name');
});

test('an empty or missing component list is refused', () => {
  assertRefused(problemsOf((b) => { b.components = []; }), 'components is missing or empty');
  assertRefused(problemsOf((b) => { delete b.components; }), 'components is missing or empty');
});

test('a component missing a name, version, purl or bom-ref, or listed twice, is refused', () => {
  for (const field of ['name', 'version', 'purl', 'bom-ref']) assertRefused(problemsOf((b) => { delete b.components[1][field]; }), `no ${field}`);
  assertRefused(problemsOf((b) => { b.components.push(structuredClone(b.components[1])); }), 'appears twice');
  assertRefused(problemsOf((b) => { b.components[2]['bom-ref'] = b.components[1]['bom-ref']; }), 'used twice');
});

test('an SBOM that leaves out the server, the web app, the sidecar or the Node runtime is refused', () => {
  assertRefused(problemsOf((b) => { b.components = b.components.filter((c) => !c.purl.startsWith('pkg:nuget/')); }), 'NuGet package of the server');
  assertRefused(problemsOf((b) => { b.components = b.components.filter((c) => !c.purl.startsWith('pkg:npm/react')); }), 'web app');
  assertRefused(problemsOf((b) => { b.components = b.components.filter((c) => !c.purl.startsWith('pkg:npm/elkjs')); }), 'sidecar');
  assertRefused(problemsOf((b) => { b.components = b.components.filter((c) => !c.purl.startsWith('pkg:generic/nodejs')); }), 'Node.js runtime');
});

test('a package that is only "excluded" does not count as shipped', () => {
  assertRefused(problemsOf((b) => { b.components[0].scope = 'excluded'; }), 'NuGet package of the server');
  assertRefused(problemsOf((b) => { b.components[1].scope = 'excluded'; }), 'web app');
});

test('the command line: 0 for a good file or standard input, 1 for a hollow or non-JSON one, 2 for none', () => {
  const run = (args, input) => spawnSync(process.execPath, [checker, ...args], { encoding: 'utf8', input });
  const file = join(scratch, 'good.cdx.json');
  writeFileSync(file, JSON.stringify(good()));
  assert.equal(run([file, '--version', '0.1.0']).status, 0);
  assert.equal(run(['-', '--version', '0.1.0'], JSON.stringify(good())).status, 0);
  const wrongVersion = run([file, '--version', '9.9.9']);
  assert.equal(wrongVersion.status, 1);
  assert.match(wrongVersion.stderr, /this is release 9\.9\.9/);
  const hollow = join(scratch, 'hollow.cdx.json');
  writeFileSync(hollow, JSON.stringify({ ...good(), components: [] }));
  assert.equal(run([hollow]).status, 1);
  const notJson = join(scratch, 'not.json');
  writeFileSync(notJson, '{ nope');
  assert.equal(run([notJson]).status, 1);
  assert.equal(run([join(scratch, 'absent.json')]).status, 2);
  assert.equal(run([]).status, 2);
  assert.equal(run([file, '--bogus']).status, 2);
});

test('--repo: a package the sources declare but the SBOM lacks is refused', () => {
  const repo = join(scratch, 'repo');
  mkdirSync(join(repo, 'src', 'App'), { recursive: true });
  mkdirSync(join(repo, 'web'), { recursive: true });
  mkdirSync(join(repo, 'sidecar'), { recursive: true });
  writeFileSync(join(repo, 'src', 'App', 'App.csproj'), '<Project><ItemGroup><PackageReference Include="yamldotnet" Version="1"/><PackageReference Include="Npgsql" Version="1"/></ItemGroup></Project>');
  writeFileSync(join(repo, 'web', 'package.json'), JSON.stringify({ dependencies: { react: '1', 'left-pad': '1' } }));
  writeFileSync(join(repo, 'sidecar', 'package.json'), JSON.stringify({ dependencies: { elkjs: '1' } }));
  const problems = checkSbom(good(), { version: '0.1.0', repo });
  assertRefused(problems, 'Npgsql is a PackageReference in src/App/App.csproj');
  assertRefused(problems, 'left-pad is a dependency in web/package.json');
  assert.ok(!problems.some((p) => p.includes('yamldotnet') || p.includes('react ') || p.includes('elkjs')), `matched packages were reported:\n${problems.join('\n')}`);
  // PrivateAssets=all (design-time tools) and the two framework-provided packages never reach the server's closure.
  writeFileSync(join(repo, 'src', 'App', 'Tools.csproj'), '<Project><ItemGroup><PackageReference Include="Design.Tool" Version="1"><PrivateAssets>all</PrivateAssets></PackageReference><PackageReference Include="Other.Tool" Version="1" PrivateAssets="all" /><PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" /></ItemGroup></Project>');
  assert.ok(!problems.some((p) => p.includes('Design.Tool') || p.includes('Other.Tool') || p.includes('Logging.Abstractions')), `skipped references were reported:\n${problems.join('\n')}`);
  writeFileSync(join(repo, 'src', 'App', 'More.csproj'), '<Project><ItemGroup><PackageReference Include="Real.Package" Version="1" /></ItemGroup></Project>');
  assertRefused(checkSbom(good(), { version: '0.1.0', repo }), 'Real.Package is a PackageReference in src/App/More.csproj');
  rmSync(join(repo, 'src', 'App', 'More.csproj'));
  const complete = good();
  complete.components.push(component('pkg:nuget/Npgsql@10.0.3', 'Draughtsman.Server'), component('pkg:npm/left-pad@1.3.0', 'web/package-lock.json'));
  assert.deepEqual(checkSbom(complete, { version: '0.1.0', repo }), []);
});

test("the generator's real output for this repository passes, with the cross-check against the sources", needsRestore, () => {
  const file = join(scratch, 'real.cdx.json');
  const made = spawnSync(process.execPath, [generator, '--out', file], { encoding: 'utf8' });
  assert.equal(made.status, 0, made.stderr);
  const version = /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(root, 'Directory.Build.props'), 'utf8'))[1];
  const checked = spawnSync(process.execPath, [checker, file, '--version', version, '--repo', root], { encoding: 'utf8' });
  assert.equal(checked.status, 0, checked.stderr);
});

test('the real output stops passing when its server packages or web packages are taken away', needsRestore, () => {
  const file = join(scratch, 'real2.cdx.json');
  assert.equal(spawnSync(process.execPath, [generator, '--out', file], { encoding: 'utf8' }).status, 0);
  const bom = JSON.parse(readFileSync(file, 'utf8'));
  const withoutNuget = { ...bom, components: bom.components.filter((c) => !c.purl.startsWith('pkg:nuget/')) };
  assertRefused(checkSbom(withoutNuget, { repo: root }), 'NuGet package of the server');
  const withoutYaml = { ...bom, components: bom.components.filter((c) => c.name !== 'YamlDotNet') };
  assertRefused(checkSbom(withoutYaml, { repo: root }), 'YamlDotNet is a PackageReference');
  const withoutReactDom = { ...bom, components: bom.components.filter((c) => c.name !== 'react-dom') };
  assertRefused(checkSbom(withoutReactDom, { repo: root }), 'react-dom is a dependency in web/package.json');
});
