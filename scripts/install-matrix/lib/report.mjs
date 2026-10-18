// Formatting for the install matrix: the per-journey step table, the per-cell verdict, the cells table and report.md.
// Pure (no I/O); covered by test/report.test.mjs. Evidence passed in is already redacted (lib/redact.mjs); this module redacts nothing itself,
// but it never prints a field the journey did not put in `evidence`.

const pad = (text, width) => String(text).length >= width ? String(text) : String(text) + ' '.repeat(width - String(text).length);

/** The step table printed at the end of a journey run. */
export function formatStepTable(steps) {
  const rows = steps.map((s) => [s.id, s.status, s.http === undefined || s.http === null ? '-' : String(s.http), s.title]);
  const widths = [1, 'STATUS'.length, 'HTTP'.length].map((min, i) => Math.max(min, ...rows.map((r) => r[i].length)));
  const lines = [`${pad('', widths[0])}  ${pad('STATUS', widths[1])}  ${pad('HTTP', widths[2])}  STEP`];
  for (const r of rows) lines.push(`${pad(r[0], widths[0])}  ${pad(r[1], widths[1])}  ${pad(r[2], widths[2])}  ${r[3]}`);
  return lines.join('\n');
}

/** One line per step that needs a person's attention: failures, expected failures and notes. */
export function formatStepDetails(steps) {
  const out = [];
  for (const s of steps) {
    const interesting = s.status !== 'PASS' || (s.notes ?? []).length > 0;
    if (!interesting) continue;
    out.push(`[${s.id}] ${s.status}: ${s.title}`);
    if (s.detail) out.push(`      ${s.detail}`);
    if (s.expectedReason) out.push(`      declared: ${s.expectedReason}`);
    for (const note of s.notes ?? []) out.push(`      note: ${note}`);
    for (const c of s.checks ?? []) if (c.ok === false) out.push(`      check failed: ${c.name}${c.detail ? `: ${c.detail}` : ''}`);
  }
  return out.join('\n');
}

/**
 * The verdict for one cell.
 *   cell.manual                       -> MANUAL (listed, never run here)
 *   cell.error                        -> ERROR (the cell could not be brought up or torn down; counts against the run)
 *   a failing journey or extra check  -> FAIL
 *   a journey with an expected fail   -> EXPECTED-FAIL
 *   otherwise                         -> PASS
 */
export function cellVerdict(cell) {
  if (cell.manual) return 'MANUAL';
  if (cell.error) return 'ERROR';
  const steps = cell.journey?.steps ?? [];
  if (!cell.journey) return 'ERROR';
  const extraFailed = (cell.extras ?? []).some((e) => e.status === 'FAIL');
  if (steps.some((s) => s.status === 'FAIL' || s.status === 'UNEXPECTED-PASS') || extraFailed || cell.journey.exitCode !== 0) return 'FAIL';
  if (steps.some((s) => s.status === 'EXPECTED-FAIL')) return 'EXPECTED-FAIL';
  return 'PASS';
}

/** Why a cell has the verdict it has: the declared reasons for an expected fail, the failing step for a fail. */
export function cellWhy(cell) {
  const verdict = cellVerdict(cell);
  if (verdict === 'MANUAL') return cell.why ?? 'not run here';
  if (verdict === 'ERROR') return cell.error ?? 'the cell did not run';
  const steps = cell.journey?.steps ?? [];
  if (verdict === 'EXPECTED-FAIL') return steps.filter((s) => s.status === 'EXPECTED-FAIL').map((s) => `${s.id}: ${s.expectedReason}`).join('; ');
  if (verdict === 'FAIL') {
    const cut = (text, max = 200) => (String(text).length > max ? `${String(text).slice(0, max - 3)}...` : String(text));
    const lastLine = (text) => String(text ?? 'failed').split('\n').map((l) => l.trim()).filter(Boolean).pop() ?? 'failed';
    const bad = steps.filter((s) => s.status === 'FAIL' || s.status === 'UNEXPECTED-PASS').map((s) => cut(`${s.id}: ${s.detail ?? s.status}`));
    const extra = (cell.extras ?? []).filter((e) => e.status === 'FAIL').map((e) => cut(`${e.name}: ${lastLine(e.detail)}`, 260));
    return [...bad, ...extra].join('; ') || 'the journey exited non-zero';
  }
  const noteCount = steps.reduce((n, s) => n + (s.notes ?? []).length, 0);
  const passed = steps.filter((s) => s.status === 'PASS').length;
  const skipped = steps.filter((s) => s.status === 'SKIP').length;
  const summary = `${passed} steps passed${skipped > 0 ? `, ${skipped} not applicable` : ''}`;
  return cell.why ?? (noteCount > 0 ? `${summary} (${noteCount} note${noteCount === 1 ? '' : 's'})` : summary);
}

/** The cells table printed by matrix.sh and placed at the top of report.md. */
export function formatCellTable(cells) {
  const rows = cells.map((c) => [c.name, cellVerdict(c), cellWhy(c)]);
  const w0 = Math.max('CELL'.length, ...rows.map((r) => r[0].length));
  const w1 = Math.max('RESULT'.length, ...rows.map((r) => r[1].length));
  const lines = [`${pad('CELL', w0)}  ${pad('RESULT', w1)}  WHY`];
  for (const r of rows) lines.push(`${pad(r[0], w0)}  ${pad(r[1], w1)}  ${r[2]}`);
  return lines.join('\n');
}

/** Exit code for the whole matrix: non-zero for any FAIL or ERROR; a MANUAL cell is listed, never counted. */
export function matrixExitCode(cells) {
  return cells.some((c) => ['FAIL', 'ERROR'].includes(cellVerdict(c))) ? 1 : 0;
}

const fence = (text) => `\`\`\`text\n${String(text).replace(/```/g, "'''")}\n\`\`\``;

/** report.md. `run` = { startedAt, finishedAt, image, host, cells: [...] }; every cell may carry `journey`, `extras`, `logs`, `commands`. */
export function renderMarkdown(run) {
  const out = [];
  out.push('# Install matrix report', '');
  out.push(`- Started: ${run.startedAt}`, `- Finished: ${run.finishedAt}`);
  if (run.image) out.push(`- Image: ${run.image}`);
  if (run.host) out.push(`- Host: ${run.host}`);
  out.push('', '## Result', '', fence(formatCellTable(run.cells)), '');
  for (const cell of run.cells) {
    out.push(`## ${cell.name}: ${cellVerdict(cell)}`, '');
    if (cell.description) out.push(cell.description, '');
    if (cell.manual) {
      out.push('Not run here. Commands:', '', fence((cell.commands ?? []).join('\n')), '');
      continue;
    }
    if (cell.error) out.push(`**The cell did not run to the end:** ${cell.error}`, '');
    if (cell.journey) {
      out.push('### Steps', '', fence(formatStepTable(cell.journey.steps)), '');
      for (const s of cell.journey.steps) {
        const bits = [];
        if (s.detail) bits.push(s.detail);
        if (s.expectedReason) bits.push(`declared: ${s.expectedReason}`);
        for (const n of s.notes ?? []) bits.push(`note: ${n}`);
        for (const c of s.checks ?? []) bits.push(`${c.ok === false ? 'check FAILED' : 'check ok'}: ${c.name}${c.detail ? ` (${c.detail})` : ''}`);
        if (bits.length === 0 && !s.evidence) continue;
        out.push(`**${s.id}. ${s.title}** (${s.status}${s.http !== undefined && s.http !== null ? `, HTTP ${s.http}` : ''})`, '');
        for (const b of bits) out.push(`- ${b}`);
        if (s.evidence) out.push('', fence(typeof s.evidence === 'string' ? s.evidence : JSON.stringify(s.evidence, null, 2)));
        out.push('');
      }
      if (cell.journey.selfcheck?.length) {
        out.push('### Connection check (what the server said)', '');
        out.push(fence(cell.journey.selfcheck.map((i) => `${pad(i.status, 5)} ${pad(i.id, 8)} ${i.title}${i.summary ? `: ${i.summary}` : ''}`).join('\n')), '');
      }
    }
    for (const e of cell.extras ?? []) {
      out.push(`**${e.name}** (${e.status})`, '');
      if (e.detail) out.push(fence(e.detail), '');
    }
    for (const note of cell.notes ?? []) out.push(`- ${note}`);
    if ((cell.notes ?? []).length) out.push('');
    if (cell.logs) out.push('### Server log lines that matter', '', fence(cell.logs), '');
  }
  return `${out.join('\n')}\n`;
}
