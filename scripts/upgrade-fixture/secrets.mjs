#!/usr/bin/env node
// Reads what a RUNNING server says about the stored AI API key and the saved SMTP password (Admin > AI, Admin > Email) and checks it against what
// the caller expects: the proof scripts use it to show that a secret reads from another folder, or that one nothing can read is named, with the
// reason and what to do, instead of silently dropped (docs/upgrade-backup.md, "Where to put the new version").
//
//   node secrets.mjs --base http://127.0.0.1:5180 --expected expected.json --want readable
//   node secrets.mjs --base ... --expected ... --want unreadable
//   node secrets.mjs --base ... --expected ... --want readable --states "AI API key=reprotected,SMTP password=current"
//
// --want readable: the AI key and (if the fixture has one) the SMTP password read. --want unreadable: both are reported unreadable and the report says
// how to recover (names keyRing.legacyInstallPaths). --states: the named secrets are in exactly these states in keyRing.storedSecrets.
// Prints one line of facts (no secret, no part of one). Exit 0 when everything expected holds, 1 otherwise, 2 on a usage error.
import { readFileSync } from 'node:fs';
import { Client } from './client.mjs';

const args = parseArgs(process.argv.slice(2));
const base = required('base');
const expected = JSON.parse(readFileSync(required('expected'), 'utf8'));
const want = args.want ?? '';
const states = args.states ? Object.fromEntries(String(args.states).split(',').map((pair) => pair.split('=').map((x) => x.trim()))) : {};
const admin = expected.users.find((u) => u.key === 'admin');

const client = new Client(base, 'admin');
const login = await client.post('/api/auth/login', { email: admin.email, password: admin.password });
if (login.status !== 200) fail(`the administrator could not sign in (status ${login.status})`);
const ai = (await client.get('/api/admin/ai')).json ?? {};
const email = (await client.get('/api/admin/email')).json ?? {};
const keyRing = ai.keyRing ?? {};
const secrets = keyRing.storedSecrets ?? [];
const facts = {
  aiStoredSet: ai.key?.storedSet === true,
  aiStoredUnreadable: ai.key?.storedUnreadable === true,
  emailPasswordSet: email.passwordSet === true,
  emailPasswordUnreadable: email.passwordUnreadable === true,
  states: Object.fromEntries(secrets.map((s) => [s.name, s.state])),
};
console.log(JSON.stringify(facts));

const problems = [];
const hasEmail = Boolean(expected.email);
if (want === 'readable') {
  if (!facts.aiStoredSet || facts.aiStoredUnreadable) problems.push('the AI key does not read');
  if (hasEmail && (!facts.emailPasswordSet || facts.emailPasswordUnreadable)) problems.push('the SMTP password does not read');
} else if (want === 'unreadable') {
  if (!facts.aiStoredUnreadable) problems.push('the AI key is not reported unreadable');
  if (hasEmail && !facts.emailPasswordUnreadable) problems.push('the SMTP password is not reported unreadable');
  const message = secrets.find((s) => s.name === 'AI API key')?.message ?? '';
  if (!message.includes('keyRing.legacyInstallPaths')) problems.push('the AI key message does not say how to recover');
  console.log(`  message: ${message}`);
} else if (want !== '') {
  fail(`--want is readable or unreadable, not ${want}`);
}
for (const [name, state] of Object.entries(states)) {
  if (facts.states[name] !== state) problems.push(`${name} is ${facts.states[name] ?? 'not listed'}, expected ${state}`);
}
if (problems.length > 0) {
  console.error(`FAIL ${problems.join('; ')}`);
  process.exit(1);
}

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) continue;
    const key = argv[i].slice(2);
    const next = argv[i + 1];
    if (next === undefined || next.startsWith('--')) out[key] = true;
    else { out[key] = next; i++; }
  }
  return out;
}
function required(name) {
  if (!args[name]) { console.error(`secrets: --${name} is required`); process.exit(2); }
  return args[name];
}
function fail(message) {
  console.error(`secrets: ${message}`);
  process.exit(1);
}
