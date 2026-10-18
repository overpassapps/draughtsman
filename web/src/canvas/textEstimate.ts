/**
 * The DOM-free part of label text handling: the font-metrics width estimate and the font shorthand helpers. Split from
 * text.ts, which re-exports all of it, so code that must run without a browser (the sidecar bundles the sequence-diagram
 * scene, model/sequenceScene.ts, and its width rule is this estimate, never a DOM measurement) can import it without
 * dragging in the canvas and line-breaker code that touches `document`.
 */

/** Width in canvas units of `text` set in `font` (a CSS font shorthand such as "14px Inter, sans-serif"). */
export type TextMeasurer = (text: string, font: string) => number;

export const LABEL_FONT_SIZE = 14;

const NARROW = new Set("iljtfrI.,':;|!() []{}");
const WIDE = new Set('mwMW@%');
const UPPER = /[A-Z]/;
const DIGIT = /[0-9]/;

export function fontSizeOf(font: string): number {
  const match = /(\d+(?:\.\d+)?)px/.exec(font);
  return match?.[1] !== undefined ? Number(match[1]) : LABEL_FONT_SIZE;
}

/** Character-class estimate of text width for a proportional sans-serif face. */
export const estimateTextWidth: TextMeasurer = (text, font) => {
  const size = fontSizeOf(font);
  let width = 0;
  for (const ch of text) {
    if (ch === ' ') {
      width += 0.28;
    } else if (NARROW.has(ch)) {
      width += 0.32;
    } else if (WIDE.has(ch)) {
      width += 0.9;
    } else if (UPPER.test(ch)) {
      width += 0.68;
    } else if (DIGIT.test(ch)) {
      width += 0.58;
    } else {
      width += 0.55;
    }
  }
  return width * size;
};

export function labelFont(fontFamily: string, size = LABEL_FONT_SIZE): string {
  return `${String(size)}px ${fontFamily}`;
}
