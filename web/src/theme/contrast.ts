/**
 * WCAG 2.x contrast maths. Pure and DOM-free so both the token compiler (Node) and the SPA can use it.
 * Only hex colours are understood; anything else (named colours, rgb(), `transparent`) returns undefined
 * and callers skip the check rather than guess.
 */

export interface Rgb {
  r: number;
  g: number;
  b: number;
}

/**
 * Parses #rgb, #rrggbb or #rrggbbaa (alpha ignored). Returns undefined for anything else, a hashless `FFFFFF`
 * included: SVG will not paint with it either, so reading it would tell a caller about a colour nobody sees.
 */
export function parseHex(value: string): Rgb | undefined {
  const trimmed = value.trim();
  if (!/^#[0-9a-fA-F]+$/.test(trimmed)) {
    return undefined;
  }
  const hex = trimmed.slice(1);
  let digits: string;
  if (hex.length === 3 || hex.length === 4) {
    digits = hex
      .slice(0, 3)
      .split('')
      .map((c) => c + c)
      .join('');
  } else if (hex.length === 6 || hex.length === 8) {
    digits = hex.slice(0, 6);
  } else {
    return undefined;
  }
  return {
    r: parseInt(digits.slice(0, 2), 16),
    g: parseInt(digits.slice(2, 4), 16),
    b: parseInt(digits.slice(4, 6), 16),
  };
}

function channel(value: number): number {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

/** Relative luminance per WCAG 2.x. */
export function relativeLuminance(rgb: Rgb): number {
  return 0.2126 * channel(rgb.r) + 0.7152 * channel(rgb.g) + 0.0722 * channel(rgb.b);
}

/** Contrast ratio between two colours, 1 to 21. Undefined when either colour is not a hex value. */
export function contrastRatio(foreground: string, background: string): number | undefined {
  const fg = parseHex(foreground);
  const bg = parseHex(background);
  if (!fg || !bg) {
    return undefined;
  }
  const l1 = relativeLuminance(fg);
  const l2 = relativeLuminance(bg);
  const lighter = Math.max(l1, l2);
  const darker = Math.min(l1, l2);
  return (lighter + 0.05) / (darker + 0.05);
}

/** WCAG AA minimum for normal text. */
export const AA_TEXT = 4.5;

/** WCAG AA minimum for large text and non-text graphics such as edge strokes. */
export const AA_GRAPHIC = 3;

/** The two colours text falls back to when its own colour cannot be read on a fill (`readableTextOn`). SvgRenderer mirrors both. */
export const READABLE_DARK = '#0f172a';
export const READABLE_LIGHT = '#ffffff';

/**
 * The colour to draw text in on `fill` (QA-06): `preferred` while it reads there (WCAG AA, 4.5:1), otherwise whichever
 * of READABLE_DARK and READABLE_LIGHT reads better, so white text on a yellow swatch turns dark and dark text on a navy
 * one turns white. A fill or text colour that is not a hex value (`transparent`, a named colour) is left alone, as in
 * every other check here: there is nothing to measure.
 */
export function readableTextOn(preferred: string, fill: string): string {
  const ratio = contrastRatio(preferred, fill);
  if (ratio === undefined || ratio >= AA_TEXT) {
    return preferred;
  }
  const dark = contrastRatio(READABLE_DARK, fill) ?? 0;
  const light = contrastRatio(READABLE_LIGHT, fill) ?? 0;
  return dark >= light ? READABLE_DARK : READABLE_LIGHT;
}
