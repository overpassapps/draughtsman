import { describe, expect, it } from 'vitest';
import { dateTimeTitle, formatDate, formatDateTime, formatTime, zoneAbbreviation } from './dateFormat.ts';

// Built from local parts so the expectation holds in whichever zone the test runs in.
const local = new Date(2026, 9, 1, 2, 5, 7);

describe('dateFormat', () => {
  it('reads as an unambiguous day, month name and 24-hour time', () => {
    expect(formatDateTime(local)).toBe('1 Oct 2026, 02:05');
    expect(formatDateTime(new Date(2026, 8, 3, 14, 30))).toBe('3 Sep 2026, 14:30');
  });

  it('can never be mistaken for MM/DD or DD/MM', () => {
    // The old `toLocaleString()` form was `01/10/2026, 02:05:07`; a numeric month would let 1/10 read both ways.
    expect(formatDateTime(local)).not.toMatch(/\d\/\d/);
    expect(formatDateTime(local)).toMatch(/[A-Z][a-z]{2}/);
  });

  it('takes an ISO string as the server sends it, in the viewer zone', () => {
    const iso = local.toISOString();
    expect(formatDateTime(iso)).toBe('1 Oct 2026, 02:05');
  });

  it('splits into a date and a time', () => {
    expect(formatDate(local)).toBe('1 Oct 2026');
    expect(formatTime(local)).toBe('02:05');
  });

  it('puts seconds and the zone on hover, not in the text', () => {
    const title = dateTimeTitle(local);
    expect(title.startsWith('1 Oct 2026, 02:05:07')).toBe(true);
    expect(formatDateTime(local)).not.toContain(':07');
    const zone = zoneAbbreviation(local);
    if (zone !== '') {
      expect(title.endsWith(zone)).toBe(true);
    }
  });

  it('hands back the input, or the given fallback, for something that is not a date', () => {
    expect(formatDateTime('not a date')).toBe('not a date');
    expect(formatDateTime('not a date', 'earlier')).toBe('earlier');
    expect(dateTimeTitle('not a date')).toBe('');
  });
});
