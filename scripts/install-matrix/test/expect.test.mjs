import assert from 'node:assert/strict';
import { test } from 'node:test';
import { exitCodeFor, isFailure, judgeSelfcheck, parseExpectations, parseSelfcheckExpectations, settleStep } from '../lib/expect.mjs';

test('--expect parses step=reason (the reason may contain =) and refuses nonsense', () => {
  const m = parseExpectations(['c=session not kept: forceHttps=true over http', 'f=x']);
  assert.equal(m.get('c'), 'session not kept: forceHttps=true over http');
  assert.throws(() => parseExpectations(['z=reason']), /unknown step/);
  assert.throws(() => parseExpectations(['c']), /<step>=<reason>/);
  assert.throws(() => parseExpectations(['c=']), /needs a reason/);
});

test('a failing step declared expected becomes EXPECTED-FAIL and does not count against the run', () => {
  const s = settleStep({ id: 'c', status: 'FAIL', detail: 'session not kept' }, parseExpectations(['c=forceHttps over http']));
  assert.equal(s.status, 'EXPECTED-FAIL');
  assert.equal(s.expectedReason, 'forceHttps over http');
  assert.equal(isFailure(s), false);
});

test('an undeclared failure stays a failure', () => {
  const s = settleStep({ id: 'f', status: 'FAIL' }, parseExpectations(['c=x']));
  assert.equal(s.status, 'FAIL');
  assert.equal(isFailure(s), true);
});

test('a declared step that PASSES is a failure: a stale declaration cannot hide a fix', () => {
  const s = settleStep({ id: 'c', status: 'PASS', detail: 'signed in' }, parseExpectations(['c=forceHttps over http']));
  assert.equal(s.status, 'UNEXPECTED-PASS');
  assert.equal(isFailure(s), true);
  assert.match(s.detail, /declared to fail/);
});

test('a skipped step is neither a pass nor a failure, declared or not', () => {
  assert.equal(settleStep({ id: 'd', status: 'SKIP' }, new Map()).status, 'SKIP');
  assert.equal(settleStep({ id: 'd', status: 'SKIP' }, parseExpectations(['d=x'])).status, 'SKIP');
});

test('exit code: 0 only when every step passed, was skipped or was an expected fail', () => {
  assert.equal(exitCodeFor([{ status: 'PASS' }, { status: 'EXPECTED-FAIL' }, { status: 'SKIP' }]), 0);
  assert.equal(exitCodeFor([{ status: 'PASS' }, { status: 'FAIL' }]), 1);
  assert.equal(exitCodeFor([{ status: 'UNEXPECTED-PASS' }]), 1);
});

test('self-check: an undeclared fail row is a problem; warn and info are fine', () => {
  const items = [{ id: 'proxy', status: 'warn', title: 'Proxy' }, { id: 'data', status: 'pass', title: 'Data' }, { id: 'secure', status: 'fail', title: 'Secure', summary: 'drops the cookie' }];
  const v = judgeSelfcheck(items, new Map());
  assert.equal(v.ok, false);
  assert.match(v.problems[0], /secure: fail/);
});

test('self-check: a declared row must have exactly the declared status, and must exist', () => {
  const items = [{ id: 'secure', status: 'fail', title: 'Secure' }, { id: 'proxy', status: 'pass', title: 'Proxy' }];
  assert.equal(judgeSelfcheck(items, parseSelfcheckExpectations(['secure=fail'])).ok, true);
  const wrong = judgeSelfcheck(items, parseSelfcheckExpectations(['secure=fail', 'proxy=warn']));
  assert.equal(wrong.ok, false);
  assert.match(wrong.problems.join(), /proxy: expected warn, got pass/);
  assert.match(judgeSelfcheck(items, parseSelfcheckExpectations(['nope=pass'])).problems.join(), /no such row/);
  assert.throws(() => parseSelfcheckExpectations(['secure=maybe']), /needs <id>=/);
});
