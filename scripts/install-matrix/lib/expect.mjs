// Expectations: a cell may declare that a step is EXPECTED to fail (and why), so a cell that proves "this misconfiguration is caught" can pass.
// The rule that keeps a declaration honest: a step declared expected-to-fail that PASSES is a failure itself (UNEXPECTED-PASS), so a stale
// declaration cannot hide a fix or a change. Pure; covered by test/expect.test.mjs.

export const STEP_IDS = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm'];

/** `["c=session not kept over http", "f=reason, with = in it"]` -> Map { c => reason }. Throws on a bad id or a missing reason. */
export function parseExpectations(items) {
  const map = new Map();
  for (const item of items ?? []) {
    const at = String(item).indexOf('=');
    if (at < 1) throw new Error(`--expect needs <step>=<reason>, got "${item}"`);
    const id = String(item).slice(0, at).trim();
    const reason = String(item).slice(at + 1).trim();
    if (!STEP_IDS.includes(id)) throw new Error(`--expect names an unknown step "${id}" (steps are ${STEP_IDS.join(', ')})`);
    if (reason === '') throw new Error(`--expect ${id}= needs a reason`);
    map.set(id, reason);
  }
  return map;
}

/** `["secure=fail", "proxy=warn"]` -> Map { secure => fail }. */
export function parseSelfcheckExpectations(items) {
  const map = new Map();
  for (const item of items ?? []) {
    const [id, status] = String(item).split('=');
    if (!id || !['pass', 'warn', 'fail', 'info'].includes(status)) throw new Error(`--expect-selfcheck needs <id>=pass|warn|fail|info, got "${item}"`);
    map.set(id.trim(), status);
  }
  return map;
}

/**
 * Settle a raw step result against the declarations.
 *   raw.status is 'PASS' | 'FAIL' | 'SKIP'.
 *   PASS stays PASS, unless declared expected-to-fail: then UNEXPECTED-PASS (a failure).
 *   FAIL becomes EXPECTED-FAIL when declared, else stays FAIL.
 *   SKIP stays SKIP (a step that did not run is never a pass or a failure of its own).
 */
export function settleStep(raw, expected) {
  const reason = expected.get(raw.id);
  const step = { ...raw };
  if (reason === undefined) return step;
  if (raw.status === 'FAIL') return { ...step, status: 'EXPECTED-FAIL', expectedReason: reason };
  if (raw.status === 'PASS') return { ...step, status: 'UNEXPECTED-PASS', expectedReason: reason, detail: `declared to fail (${reason}) but passed${raw.detail ? `: ${raw.detail}` : ''}` };
  return { ...step, expectedReason: reason };
}

/** Whether a settled step counts against the run. */
export function isFailure(step) {
  return step.status === 'FAIL' || step.status === 'UNEXPECTED-PASS';
}

/** Journey exit code: 0 only if no settled step is a failure. */
export function exitCodeFor(steps) {
  return steps.some(isFailure) ? 1 : 0;
}

/**
 * Compare the self-check's rows with a cell's declared expectations.
 * `items` is the `items` array of GET /api/admin/selfcheck. Returns { ok, problems[] }.
 * Without declarations, any row with status "fail" is a problem; a declared row must have exactly the declared status (so a declared
 * `secure=fail` is satisfied by a fail and a declared `proxy=warn` by a warn); every other row's fail is still a problem.
 */
export function judgeSelfcheck(items, declared) {
  const problems = [];
  const seen = new Set();
  for (const item of items ?? []) {
    seen.add(item.id);
    const want = declared.get(item.id);
    if (want !== undefined) {
      if (item.status !== want) problems.push(`${item.id}: expected ${want}, got ${item.status} (${item.title ?? ''})`);
    } else if (item.status === 'fail') {
      problems.push(`${item.id}: fail (${item.title ?? ''}): ${item.summary ?? ''}`);
    }
  }
  for (const id of declared.keys()) if (!seen.has(id)) problems.push(`${id}: declared but the self-check has no such row`);
  return { ok: problems.length === 0, problems };
}
