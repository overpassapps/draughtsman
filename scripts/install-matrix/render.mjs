#!/usr/bin/env node
// Turns what matrix.sh left in a run folder into the cells table (terminal) and report.md, and sets the exit code.
//   render.mjs RUN_DIR            RUN_DIR/cells/<name>/{journey.json, journey.out, error.txt, app.log, extras.tsv, notes.txt}
// It also judges the cell-level declarations of cells.json that need the server log or the steps' notes: logExpect (a line that MUST be in the
// log), logAbsent (a line that must NOT be) and expectNote (a step note that must appear, so a cell that is meant to reproduce a CDN's behaviour
// fails when it stops doing so). Every line it copies from a log goes through the redactor first.
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { cellVerdict, formatCellTable, matrixExitCode, renderMarkdown } from './lib/report.mjs';
import { redactText } from './lib/redact.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const runDir = process.argv[2];
if (!runDir) { console.error('usage: render.mjs RUN_DIR'); process.exit(2); }
const config = JSON.parse(readFileSync(join(here, 'cells.json'), 'utf8'));
const read = (path, fallback = '') => (existsSync(path) ? readFileSync(path, 'utf8') : fallback);
const meta = existsSync(join(runDir, 'run.json')) ? JSON.parse(readFileSync(join(runDir, 'run.json'), 'utf8')) : {};
const breakName = read(join(runDir, 'break.txt')).trim();

const cells = [];
const have = existsSync(join(runDir, 'cells')) ? readdirSync(join(runDir, 'cells')) : [];
for (const def of config.cells) {
  if (!have.includes(def.name)) continue;
  const dir = join(runDir, 'cells', def.name);
  let effective = def;
  if (breakName && def.breaks?.[breakName]) {
    const merge = (base, over) => (Array.isArray(over) || typeof over !== 'object' || over === null ? over : Object.entries(over).reduce((o, [k, v]) => ({ ...o, [k]: merge(o[k], v) }), { ...(base ?? {}) }));
    effective = merge(def, def.breaks[breakName].override);
  }
  const cell = { name: def.name, description: effective.description, extras: [], notes: [] };
  if (breakName && def.breaks?.[breakName]) cell.notes.push(`DELIBERATE BREAKAGE "${breakName}": ${def.breaks[breakName].description}. This run is expected to FAIL.`);
  const error = read(join(dir, 'error.txt')).trim();
  if (error) cell.error = error;
  if (existsSync(join(dir, 'journey.json'))) cell.journey = JSON.parse(readFileSync(join(dir, 'journey.json'), 'utf8'));
  for (const line of read(join(dir, 'extras.tsv')).split('\n').filter(Boolean)) {
    const [name, status, ...detail] = line.split('\t');
    cell.extras.push({ name, status, detail: detail.join('\t').replace(/\\n/g, '\n') });
  }
  for (const line of read(join(dir, 'notes.txt')).split('\n').filter(Boolean)) cell.notes.push(line);
  const log = read(join(dir, 'app.log'));
  const matching = (pattern) => log.split('\n').filter((l) => new RegExp(pattern).test(l));
  for (const { pattern, why } of effective.logExpect ?? []) {
    const hits = matching(pattern);
    cell.extras.push({ name: `server log shows: ${why}`, status: hits.length > 0 ? 'PASS' : 'FAIL', detail: hits.length > 0 ? redactText(hits[hits.length - 1]).slice(0, 400) : `no line matching /${pattern}/ in the container log` });
  }
  for (const { pattern, why } of effective.logAbsent ?? []) {
    const hits = matching(pattern);
    cell.extras.push({ name: `server log is free of: ${why}`, status: hits.length === 0 ? 'PASS' : 'FAIL', detail: hits.length === 0 ? 'not present' : redactText(hits[0]).slice(0, 400) });
  }
  if (effective.expectNote && cell.journey) {
    const { step, pattern, why } = effective.expectNote;
    const found = (cell.journey.steps.find((s) => s.id === step)?.notes ?? []).find((n) => new RegExp(pattern).test(n));
    cell.extras.push({ name: `step ${step} reports: ${why}`, status: found ? 'PASS' : 'FAIL', detail: found ?? `no note on step ${step} matching /${pattern}/` });
  }
  // The lines worth a person's eye: each warn/fail/crit record with its message line, and the migration and key ring lines.
  const lines = log.split('\n');
  const interesting = [];
  lines.forEach((l, i) => {
    if (/^(warn|fail|crit)\b/.test(l)) interesting.push(`${l} ${(lines[i + 1] ?? '').trim()}`.slice(0, 400));
    else if (/migration|Post-migration|Key ring protection|Unhandled exception/i.test(l)) interesting.push(l.trim().slice(0, 400));
  });
  interesting.splice(0, Math.max(0, interesting.length - 25));
  if (interesting.length) cell.logs = redactText(interesting.join('\n'));
  cells.push(cell);
}
for (const m of config.manual) cells.push({ ...m, manual: true });

const table = formatCellTable(cells);
const finished = new Date().toISOString();
writeFileSync(join(runDir, 'report.md'), renderMarkdown({ startedAt: meta.startedAt ?? '', finishedAt: finished, image: meta.image, host: meta.host, cells }));
console.log(`\n${table}`);
console.log(`\nreport: ${join(runDir, 'report.md')}`);
const code = matrixExitCode(cells);
const counts = cells.reduce((o, c) => ({ ...o, [cellVerdict(c)]: (o[cellVerdict(c)] ?? 0) + 1 }), {});
console.log(`matrix: ${code === 0 ? 'OK' : 'FAILED'} (${Object.entries(counts).map(([k, v]) => `${v} ${k}`).join(', ')})`);
process.exit(code);
