#!/usr/bin/env node
// Checks that a release's software bill of materials is the one scripts/generate-sbom.mjs writes and that it is not hollow.
//
//   node scripts/check-sbom.mjs SBOM.cdx.json --version 0.1.0              structure, and that it describes this version
//   node scripts/check-sbom.mjs SBOM.cdx.json --version 0.1.0 --repo .     also: every package the sources declare is in it
//   some-command | node scripts/check-sbom.mjs - --version 0.1.0           read the SBOM from standard input
//
// Exit 0 = fine, 1 = a defect (one line each), 2 = wrong use or no such file. Used by publish.sh (before an archive is built and again
// on the copy inside each archive), release.sh, the Dockerfile (a build with a hollow SBOM fails), docker-export.sh and the tests.
//
// What "not hollow" means, checked in this order: it is CycloneDX 1.5 JSON with a serial number; it describes `draughtsman` at the
// version being released; every component has a name, version, purl and unique bom-ref; it holds NuGet packages of the server, npm
// packages of the web app and of the sidecar (named by their `draughtsman:origin`), and the bundled Node runtime; and, with --repo,
// every PackageReference of src/**/*.csproj and every runtime dependency of web/ and sidecar/ package.json is a component, so an
// inventory read from the wrong lock file cannot pass. The CycloneDX schema itself is not vendored here: the generator's output was
// validated once against the official bom-1.5.schema.json (docs/air-gapped.md, "Verify it yourself").
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

/** Defects in a parsed SBOM, optionally cross-checked against the sources in `repo`. */
export function checkSbom(bom, { version, repo } = {}) {
  const problems = [];
  if (bom === null || typeof bom !== 'object' || Array.isArray(bom)) return ['the SBOM is not a JSON object'];
  if (bom.bomFormat !== 'CycloneDX') problems.push(`bomFormat is ${JSON.stringify(bom.bomFormat)}, not "CycloneDX"`);
  if (bom.specVersion !== '1.5') problems.push(`specVersion is ${JSON.stringify(bom.specVersion)}, not "1.5"`);
  if (typeof bom.serialNumber !== 'string' || !/^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(bom.serialNumber)) problems.push('serialNumber is missing or not a urn:uuid');

  const subject = bom.metadata?.component;
  if (subject?.name !== 'draughtsman') problems.push(`metadata.component.name is ${JSON.stringify(subject?.name)}, not "draughtsman"`);
  if (version !== undefined && subject?.version !== version) problems.push(`metadata.component.version is ${JSON.stringify(subject?.version)}, but this is release ${version}`);

  const components = bom.components;
  if (!Array.isArray(components) || components.length === 0) {
    problems.push('components is missing or empty');
    return problems;
  }
  const purls = new Set();
  const refs = new Set();
  for (const [index, component] of components.entries()) {
    const label = component?.purl ?? component?.name ?? `component #${index}`;
    for (const field of ['name', 'version', 'purl', 'bom-ref']) {
      if (typeof component?.[field] !== 'string' || component[field] === '') problems.push(`${label} has no ${field}`);
    }
    if (purls.has(component?.purl)) problems.push(`${label} appears twice`);
    purls.add(component?.purl);
    if (refs.has(component?.['bom-ref'])) problems.push(`bom-ref ${component['bom-ref']} is used twice`);
    refs.add(component?.['bom-ref']);
  }

  const origin = (component) => component.properties?.find((property) => property.name === 'draughtsman:origin')?.value ?? '';
  const required = (component) => component.scope !== 'excluded';
  const has = (test) => components.some((component) => typeof component.purl === 'string' && test(component));
  if (!has((c) => c.purl.startsWith('pkg:nuget/') && required(c))) problems.push('no NuGet package of the server is listed as shipped');
  if (!has((c) => c.purl.startsWith('pkg:npm/') && required(c) && origin(c).includes('web/package-lock.json'))) problems.push("no npm package of the web app (web/package-lock.json) is listed as shipped");
  if (!has((c) => c.purl.startsWith('pkg:npm/') && required(c) && origin(c).includes('sidecar/package-lock.json'))) problems.push("no npm package of the sidecar (sidecar/package-lock.json) is listed as shipped");
  if (!has((c) => c.purl.startsWith('pkg:generic/nodejs@'))) problems.push('the bundled Node.js runtime is not listed');

  if (repo !== undefined && problems.length === 0) problems.push(...crossCheck(components, repo));
  return problems;
}

/**
 * PackageReferences the server's restore closure does not contain, because the shared framework it publishes with already provides the
 * assembly (NuGet prunes them). They ship as part of the .NET runtime, whose notices are DOTNET-THIRD-PARTY-NOTICES.txt in each archive;
 * the SBOM says it does not list the runtime. A new one of these is a decision for a person: add it here with the reason.
 */
const FRAMEWORK_PROVIDED = ['Microsoft.Extensions.Hosting.Abstractions', 'Microsoft.Extensions.Logging.Abstractions'];

/** Every package the sources name must be a component: NuGet ids are compared without case, npm names exactly. A reference with PrivateAssets=all (the EF design-time tools) never reaches the server. */
function crossCheck(components, repo) {
  const problems = [];
  const nuget = new Set(components.filter((c) => c.purl.startsWith('pkg:nuget/')).map((c) => c.name.toLowerCase()));
  const npm = new Set(components.filter((c) => c.purl.startsWith('pkg:npm/')).map((c) => c.name));

  for (const file of csprojFiles(join(repo, 'src'))) {
    for (const match of readFileSync(file, 'utf8').matchAll(/<PackageReference\b([^>]*?)(?:\/>|>([\s\S]*?)<\/PackageReference>)/g)) {
      const id = /Include="([^"]+)"/.exec(match[1])?.[1];
      if (id === undefined) continue;
      if (/PrivateAssets="all"/i.test(match[1]) || /<PrivateAssets>\s*all\s*<\/PrivateAssets>/i.test(match[2] ?? '')) continue;
      if (FRAMEWORK_PROVIDED.some((provided) => provided.toLowerCase() === id.toLowerCase())) continue;
      if (!nuget.has(id.toLowerCase())) problems.push(`${id} is a PackageReference in ${file.slice(repo.length + 1).replaceAll('\\', '/')} but not in the SBOM`);
    }
  }
  for (const project of ['web', 'sidecar']) {
    const manifest = join(repo, project, 'package.json');
    if (!existsSync(manifest)) continue;
    for (const name of Object.keys(JSON.parse(readFileSync(manifest, 'utf8')).dependencies ?? {})) {
      if (!npm.has(name)) problems.push(`${name} is a dependency in ${project}/package.json but not in the SBOM`);
    }
  }
  return problems;
}

function* csprojFiles(folder) {
  if (!existsSync(folder)) return;
  for (const entry of readdirSync(folder).sort()) {
    const path = join(folder, entry);
    if (entry === 'bin' || entry === 'obj' || entry === 'node_modules') continue;
    if (statSync(path).isDirectory()) yield* csprojFiles(path);
    else if (entry.endsWith('.csproj')) yield path;
  }
}

function main(argv) {
  let parsed;
  try {
    parsed = parseArgs({ args: argv, options: { version: { type: 'string' }, repo: { type: 'string' } }, allowPositionals: true, strict: true });
  } catch (error) {
    console.error(`check-sbom: ${error.message}`);
    return 2;
  }
  const [file] = parsed.positionals;
  if (file === undefined || parsed.positionals.length > 1) {
    console.error('usage: check-sbom.mjs <SBOM file | -> [--version X.Y.Z] [--repo ROOT]');
    return 2;
  }
  let text;
  try {
    text = file === '-' ? readFileSync(0, 'utf8') : readFileSync(file, 'utf8');
  } catch {
    console.error(`check-sbom: ${file} cannot be read: a release without its SBOM is not a release`);
    return 2;
  }
  let bom;
  try {
    bom = JSON.parse(text);
  } catch {
    console.error(`check-sbom: ${file} is not valid JSON`);
    return 1;
  }
  const problems = checkSbom(bom, { version: parsed.values.version, repo: parsed.values.repo === undefined ? undefined : resolve(parsed.values.repo) });
  if (problems.length > 0) {
    console.error(`check-sbom: ${file} is not a usable SBOM:`);
    for (const problem of problems) console.error(`  ${problem}`);
    return 1;
  }
  console.log(`check-sbom: ${file === '-' ? 'standard input' : file}: ${bom.components.length} components, CycloneDX ${bom.specVersion}, draughtsman ${bom.metadata.component.version}`);
  return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(main(process.argv.slice(2)));
