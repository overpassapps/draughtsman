#!/usr/bin/env node
// Reads cells.json for matrix.sh (POSIX shell cannot read JSON). Every answer is plain text on stdout, one value per line, so the shell needs no parser.
//   cells.mjs names [--include-optional]           the automated cell names
//   cells.mjs field CELL PATH [--break NAME]       one scalar (dotted path, e.g. proxy.type); empty if absent
//   cells.mjs lines CELL PATH [--break NAME]       an array of strings, one per line
//   cells.mjs image KEY                            an image reference (draughtsman, nginx, caddy, ubuntu)
//   cells.mjs prefix
//   cells.mjs breaks                               "<break> <cell> <description>" per line
//   cells.mjs manual                               the manual cells as JSON
//   cells.mjs fixture newest|oldest                the upgrade fixture directory (absolute) by manifest createdAt
//   cells.mjs fixture-env DIR                      KEY=VALUE lines the CURRENT image needs to open that fixture
//   cells.mjs fixture-admin DIR                    "<email>" then "<password>" of the fixture's administrator (throwaway test data)
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const config = JSON.parse(readFileSync(join(here, 'cells.json'), 'utf8'));
const [, , command, ...rest] = process.argv;

const dig = (object, path) => path.split('.').reduce((o, k) => (o === undefined || o === null ? undefined : o[k]), object);
function merge(base, over) {
  if (Array.isArray(over) || typeof over !== 'object' || over === null) return over;
  const out = { ...(base ?? {}) };
  for (const [k, v] of Object.entries(over)) out[k] = merge(out[k], v);
  return out;
}
function cellWithBreak(name, breakName) {
  const cell = config.cells.find((c) => c.name === name);
  if (!cell) { console.error(`cells.mjs: no cell "${name}"`); process.exit(2); }
  if (!breakName) return cell;
  const brk = cell.breaks?.[breakName];
  if (!brk) { console.error(`cells.mjs: cell "${name}" has no break "${breakName}"`); process.exit(2); }
  return merge(cell, brk.override);
}
const breakOf = (args) => { const i = args.indexOf('--break'); return i >= 0 ? args[i + 1] : undefined; };

function fixtures() {
  const root = resolve(here, '../../tests/upgrade-fixtures');
  return readdirSync(root, { withFileTypes: true })
    .filter((d) => d.isDirectory() && existsSync(join(root, d.name, 'sqlite', 'data-folder.tar.gz')))
    .map((d) => ({ dir: join(root, d.name), createdAt: JSON.parse(readFileSync(join(root, d.name, 'manifest.json'), 'utf8')).createdAt, commitAt: JSON.parse(readFileSync(join(root, d.name, 'manifest.json'), 'utf8')).build.committedAt }))
    .sort((a, b) => Date.parse(a.commitAt) - Date.parse(b.commitAt));
}

switch (command) {
  case 'names': console.log(config.cells.filter((c) => rest.includes('--include-optional') || !c.optional).map((c) => c.name).join('\n')); break;
  case 'prefix': console.log(config.prefix); break;
  case 'image': console.log(config.images[rest[0]] ?? ''); break;
  case 'field': { const v = dig(cellWithBreak(rest[0], breakOf(rest)), rest[1]); console.log(v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v)); break; }
  case 'lines': { const v = dig(cellWithBreak(rest[0], breakOf(rest)), rest[1]); for (const item of v ?? []) console.log(typeof item === 'object' ? JSON.stringify(item) : item); break; }
  case 'breaks': for (const c of config.cells) for (const [n, b] of Object.entries(c.breaks ?? {})) console.log(`${n} ${c.name} ${b.description}`); break;
  case 'manual': console.log(JSON.stringify(config.manual, null, 2)); break;
  case 'cell': console.log(JSON.stringify(cellWithBreak(rest[0], breakOf(rest)))); break;
  case 'fixture': { const all = fixtures(); console.log((rest[0] === 'oldest' ? all[0] : all[all.length - 1]).dir); break; }
  case 'fixture-env': {
    const m = JSON.parse(readFileSync(join(rest[0], 'manifest.json'), 'utf8'));
    const ring = m.keyRing ?? { protection: 'none' };
    console.log(`DRAUGHTSMAN_KEY_RING_PROTECTION=${ring.protection}`);
    if (ring.protection === 'key') console.log(`${ring.keyEnvironmentVariable ?? 'DRAUGHTSMAN_KEY_ENCRYPTION_KEY'}=${ring.keyBase64}`);
    if (m.contentRoot) console.log(`DRAUGHTSMAN_LEGACY_INSTALL_PATHS=${m.contentRoot}`);
    break;
  }
  case 'fixture-admin': {
    const e = JSON.parse(readFileSync(join(rest[0], 'sqlite', 'expected.json'), 'utf8'));
    const a = e.users.find((u) => u.key === 'admin');
    console.log(a.email); console.log(a.password); break;
  }
  default: console.error('usage: cells.mjs names|field|lines|image|prefix|breaks|manual|cell|fixture|fixture-env|fixture-admin ...'); process.exit(2);
}
