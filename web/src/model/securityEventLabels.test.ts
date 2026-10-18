import { describe, expect, it } from 'vitest';
import { SECURITY_EVENT_LABELS } from './authClient.ts';
// The server's own list, read as text (the SPA has no Node file system; Vite's ?raw import hands the file over as a string).
import securityEventSource from '../../../src/Draughtsman.Core/Auth/SecurityEvent.cs?raw';

/** The ids the server can record, read from `SecurityEventTypes` itself so a new event type cannot ship with a raw id on the Security page (QA P15). */
function serverEventTypes(): string[] {
  const block = /class SecurityEventTypes[\s\S]*?\n\}/.exec(securityEventSource)?.[0] ?? '';
  // `Session`, `Password`, `Token`, `Host` and `Anonymous` are the *method* a call arrived by, a different class.
  return [...block.matchAll(/public const string \w+ = "([^"]+)";/g)].map((match) => match[1] ?? '');
}

describe('security event labels', () => {
  it('has a human label for every event type the server records', () => {
    const types = serverEventTypes();
    expect(types.length).toBeGreaterThan(15);
    const missing = types.filter((type) => SECURITY_EVENT_LABELS[type] === undefined);
    expect(missing).toEqual([]);
  });

  it('names no label after its raw id', () => {
    for (const [id, label] of Object.entries(SECURITY_EVENT_LABELS)) {
      expect(label).not.toBe(id);
      expect(label).not.toMatch(/^[a-z_]+\.[a-z_]+$/);
    }
  });
});
