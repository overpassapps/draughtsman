import { describe, expect, it } from 'vitest';
import { DASH_OPTIONS, MAX_FONT_SIZE, MIN_FONT_SIZE, sharedValue } from './styleState.ts';

describe('sharedValue', () => {
  it('reports a single shared value as shared', () => {
    expect(sharedValue(['#e04832', '#e04832'])).toEqual({ value: '#e04832', mixed: false });
  });

  it('reports absent values as shared on undefined', () => {
    expect(sharedValue([undefined, undefined])).toEqual({ value: undefined, mixed: false });
  });

  it('reports a disagreement as mixed with no value', () => {
    expect(sharedValue(['#e04832', '#151e33'])).toEqual({ value: undefined, mixed: true });
  });

  it('distinguishes a deliberate value from an absent one', () => {
    expect(sharedValue(['dashed', undefined])).toEqual({ value: undefined, mixed: true });
  });

  it('treats true and false as distinct, not mixed with each other', () => {
    expect(sharedValue([true, true])).toEqual({ value: true, mixed: false });
    expect(sharedValue([true, false])).toEqual({ value: undefined, mixed: true });
  });

  it('reads a mixed numeric selection as mixed', () => {
    expect(sharedValue([2, 2, 3])).toEqual({ value: undefined, mixed: true });
  });

  it('handles an empty selection without a mixed state', () => {
    expect(sharedValue([])).toEqual({ value: undefined, mixed: false });
  });
});

describe('dash and size bounds', () => {
  it('offers the three dash patterns in presentation order', () => {
    expect(DASH_OPTIONS).toEqual(['solid', 'dashed', 'dotted']);
  });

  it('keeps the font size bounds ordered so the stepper clamps to them', () => {
    expect(MIN_FONT_SIZE).toBeLessThan(MAX_FONT_SIZE);
  });
});