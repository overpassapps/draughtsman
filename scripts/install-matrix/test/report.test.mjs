import assert from 'node:assert/strict';
import { test } from 'node:test';
import { cellVerdict, cellWhy, formatCellTable, formatStepDetails, formatStepTable, matrixExitCode, renderMarkdown } from '../lib/report.mjs';

const pass = { id: 'a', title: 'first run required', status: 'PASS', http: 200, notes: [], checks: [] };
const expectedFail = { id: 'c', title: 'sign in', status: 'EXPECTED-FAIL', http: 200, expectedReason: 'forceHttps over http', detail: 'session not kept', notes: [], checks: [] };
const fail = { id: 'f', title: 'save', status: 'FAIL', http: 400, detail: '400 Invalid If-Match header', notes: [], checks: [{ name: 'save with the ETag', ok: false, detail: '400' }], evidence: { status: 400 } };

test('step table: one aligned row per step with status and HTTP code', () => {
  const t = formatStepTable([pass, expectedFail, { id: 'd', title: 'create', status: 'SKIP' }]);
  const lines = t.split('\n');
  assert.equal(lines.length, 4);
  assert.match(lines[1], /^a\s+PASS\s+200\s+first run required$/);
  assert.match(lines[2], /EXPECTED-FAIL\s+200/);
  assert.match(lines[3], /SKIP\s+-\s+create/);
});

test('step details mention only what needs a person: failures, expected failures and notes', () => {
  const text = formatStepDetails([pass, { ...pass, id: 'e', notes: ['the ETag arrived weakened'] }, fail]);
  assert.doesNotMatch(text, /\[a\]/);
  assert.match(text, /\[e\] PASS/);
  assert.match(text, /note: the ETag arrived weakened/);
  assert.match(text, /check failed: save with the ETag/);
});

test('verdicts: PASS, EXPECTED-FAIL, FAIL, ERROR, MANUAL', () => {
  const journey = (steps, exitCode = 0) => ({ journey: { steps, exitCode } });
  assert.equal(cellVerdict(journey([pass])), 'PASS');
  assert.equal(cellVerdict(journey([pass, expectedFail])), 'EXPECTED-FAIL');
  assert.equal(cellVerdict(journey([pass, fail], 1)), 'FAIL');
  assert.equal(cellVerdict(journey([pass], 1)), 'FAIL'); // a non-zero exit with all-green steps is still a failure
  assert.equal(cellVerdict(journey([{ ...pass, status: 'UNEXPECTED-PASS' }], 1)), 'FAIL');
  assert.equal(cellVerdict({ error: 'the container did not start' }), 'ERROR');
  assert.equal(cellVerdict({}), 'ERROR');
  assert.equal(cellVerdict({ manual: true }), 'MANUAL');
});

test('an extra check that failed (the upgrade verification) fails the cell even if the journey passed', () => {
  const cell = { journey: { steps: [pass], exitCode: 0 }, extras: [{ name: 'verify.mjs', status: 'FAIL', detail: 'a document is missing' }] };
  assert.equal(cellVerdict(cell), 'FAIL');
  assert.match(cellWhy(cell), /verify\.mjs: a document is missing/);
});

test('why: a failing extra check shows the last line of its detail (where a log puts the cause), not the whole log', () => {
  const cell = { journey: { steps: [pass], exitCode: 0 }, extras: [{ name: 'the instance never answered', status: 'FAIL', detail: 'info: started\ninfo: stopping\nDraughtsman cannot start: it cannot write to /data/data.db' }] };
  const why = cellWhy(cell);
  assert.match(why, /cannot write to \/data\/data\.db/);
  assert.doesNotMatch(why, /info: started/);
});

test('why: the declared reason for an expected fail, the failing step for a fail', () => {
  assert.equal(cellWhy({ journey: { steps: [pass, expectedFail], exitCode: 0 } }), 'c: forceHttps over http');
  assert.match(cellWhy({ journey: { steps: [pass, fail], exitCode: 1 } }), /f: 400 Invalid If-Match header/);
  assert.match(cellWhy({ journey: { steps: [pass, { ...pass, id: 'e', notes: ['n'] }], exitCode: 0 } }), /2 steps passed \(1 note\)/);
});

test('cells table and exit code: MANUAL is listed but never counted; FAIL and ERROR are', () => {
  const cells = [{ name: 'docker-direct', journey: { steps: [pass], exitCode: 0 } }, { name: 'windows-archive', manual: true, why: 'needs the VM' }];
  assert.equal(matrixExitCode(cells), 0);
  const table = formatCellTable(cells);
  assert.match(table, /docker-direct\s+PASS/);
  assert.match(table, /windows-archive\s+MANUAL\s+needs the VM/);
  assert.equal(matrixExitCode([...cells, { name: 'x', error: 'boom' }]), 1);
  assert.equal(matrixExitCode([...cells, { name: 'y', journey: { steps: [fail], exitCode: 1 } }]), 1);
});

test('markdown: result table, per-cell steps with evidence, manual commands; no stray secrets because none were given', () => {
  const md = renderMarkdown({
    startedAt: 't0', finishedAt: 't1', image: 'draughtsman:matrix',
    cells: [
      { name: 'docker-direct', description: 'plain http', journey: { steps: [pass, fail], exitCode: 1, selfcheck: [{ id: 'proxy', status: 'warn', title: 'Proxy', summary: 'untrusted' }] }, logs: 'Forwarding headers arrived from 172.18.0.1' },
      { name: 'windows-archive', manual: true, commands: ['ssh windows-vm ...'] },
    ],
  });
  assert.match(md, /^# Install matrix report/);
  assert.match(md, /## docker-direct: FAIL/);
  assert.match(md, /\*\*f\. save\*\* \(FAIL, HTTP 400\)/);
  assert.match(md, /"status": 400/);
  assert.match(md, /warn\s+proxy\s+Proxy: untrusted/);
  assert.match(md, /## windows-archive: MANUAL/);
  assert.match(md, /ssh windows-vm/);
  assert.match(md, /Forwarding headers arrived/);
});
