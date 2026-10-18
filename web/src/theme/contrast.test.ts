import { contrastRatio, parseHex, readableTextOn, relativeLuminance } from './contrast.ts';

describe('parseHex', () => {
  it('reads three, six and eight digit forms', () => {
    expect(parseHex('#fff')).toEqual({ r: 255, g: 255, b: 255 });
    expect(parseHex('#1A1A1C')).toEqual({ r: 26, g: 26, b: 28 });
    expect(parseHex('#D7192080')).toEqual({ r: 215, g: 25, b: 32 });
  });

  it('rejects anything that is not hex', () => {
    expect(parseHex('transparent')).toBeUndefined();
    expect(parseHex('rgb(1, 2, 3)')).toBeUndefined();
    expect(parseHex('#12345')).toBeUndefined();
    // Hex without the hash is not a colour SVG paints with, so it is not one to derive contrast from either.
    expect(parseHex('FFFFFF')).toBeUndefined();
  });
});

describe('contrastRatio', () => {
  it('is 21 for black on white and 1 for identical colours', () => {
    expect(contrastRatio('#000000', '#FFFFFF')).toBeCloseTo(21, 5);
    expect(contrastRatio('#808080', '#808080')).toBeCloseTo(1, 5);
  });

  it('is symmetric', () => {
    expect(contrastRatio('#D71920', '#FFFFFF')).toBeCloseTo(contrastRatio('#FFFFFF', '#D71920') ?? 0, 10);
  });

  it('returns undefined when a colour cannot be parsed', () => {
    expect(contrastRatio('transparent', '#FFFFFF')).toBeUndefined();
  });

  it('matches the published luminance of pure green', () => {
    expect(relativeLuminance({ r: 0, g: 255, b: 0 })).toBeCloseTo(0.7152, 4);
  });
});

describe('readableTextOn', () => {
  it('keeps the preferred colour while it reads (4.5:1)', () => {
    expect(readableTextOn('#111111', '#FFFFFF')).toBe('#111111');
  });

  it('swaps to dark or light text, whichever reads better on the fill', () => {
    expect(readableTextOn('#F2F2F2', '#E5B83A')).toBe('#0f172a');
    expect(readableTextOn('#111111', '#0B1F44')).toBe('#ffffff');
  });

  it('leaves a non-hex fill or colour alone', () => {
    expect(readableTextOn('#F2F2F2', 'transparent')).toBe('#F2F2F2');
    expect(readableTextOn('white', '#FFFFFF')).toBe('white');
  });
});
