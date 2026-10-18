import { afterEach, describe, expect, it } from 'vitest';
import { clearSessionEndedNotice, currentSessionEndedNotice, noteSessionEnded, onSessionEnded, parseReason, sessionEndedMessage, suppressSessionEndedNotice } from './sessionEnded.ts';

describe('sessionEnded', () => {
  afterEach(() => {
    clearSessionEndedNotice();
  });

  it('reads the server’s reason word, and treats anything else as unknown', () => {
    expect(parseReason('revoked')).toBe('revoked');
    expect(parseReason('deactivated')).toBe('deactivated');
    expect(parseReason('expired')).toBe('expired');
    expect(parseReason(null)).toBe('unknown');
    expect(parseReason('admin:ada@example.com')).toBe('unknown');
  });

  it('keeps the first specific reason, upgrades an unknown one, and runs the editors once per episode', () => {
    let ran = 0;
    const stop = onSessionEnded(() => { ran += 1; return true; });
    noteSessionEnded('unknown');
    noteSessionEnded('revoked');
    noteSessionEnded('expired');
    stop();
    expect(currentSessionEndedNotice()).toEqual({ reason: 'revoked', keptWork: true });
    expect(ran).toBe(1);
  });

  it('records nothing after a deliberate sign-out, until the next sign-in clears it', () => {
    suppressSessionEndedNotice();
    noteSessionEnded('revoked');
    expect(currentSessionEndedNotice()).toBeUndefined();
    clearSessionEndedNotice();
    noteSessionEnded('revoked');
    expect(currentSessionEndedNotice()?.reason).toBe('revoked');
  });

  it('puts the kept-work sentence only where work was kept', () => {
    expect(sessionEndedMessage({ reason: 'revoked', keptWork: false })).not.toMatch(/kept in this browser/);
    expect(sessionEndedMessage({ reason: 'revoked', keptWork: true })).toMatch(/kept in this browser/);
  });
});
