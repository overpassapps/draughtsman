/**
 * Label layout. SVG has no automatic line wrapping, so a node's label is broken into lines before it is drawn, and the
 * label editor (LabelEditor.tsx) must show the same lines while it is open, or the text jumps when it closes
 * (the M2 editor checklist, item 15). One layout decides both wherever there is a browser: the editor's field is
 * laid out by the browser with LABEL_TEXT_LAYOUT, and `createDomLineBreaker()` asks the same browser to lay the label
 * out with the same CSS at the same width in a hidden element and reads back where its lines start. That covers every
 * break opportunity the browser's line breaker has (after a hyphen, between CJK characters, inside an overlong word)
 * without restating any of them here.
 *
 * `wrapText` is the fallback where nothing lays text out (Node, jsdom, export outside a browser): the same whitespace
 * rules, breaking at spaces only, against a text measurer. The default measurer is a font-metrics estimate that is
 * deterministic everywhere; the browser canvas supplies `createCanvasMeasurer()` for exact widths when it can.
 *
 * Whitespace is kept as typed (`white-space: pre-wrap`), so the renderer draws each line with `xml:space="preserve"`.
 * The spaces a line soft-wraps at hang past its end and are left off it, so they take no part in centring it; spaces
 * at the end of a paragraph are kept, as Chrome counts them when it centres that line.
 */

import { estimateTextWidth, fontSizeOf, LABEL_FONT_SIZE, labelFont, type TextMeasurer } from './textEstimate.ts';

// The DOM-free half (width estimate, font helpers) lives in textEstimate.ts so the sidecar can bundle it without this
// file's browser layout code; everything is re-exported here, so every caller keeps importing from `text.ts`.
export { estimateTextWidth, LABEL_FONT_SIZE, labelFont, type TextMeasurer };

/** The lines `text` is drawn on, each fitting `maxWidth` in `font`; at least one line (possibly empty). */
export type LineBreaker = (text: string, maxWidth: number, font: string) => string[];

export const LABEL_LINE_HEIGHT = 1.3;
/** Space between a shape's edge and its label. */
export const LABEL_PADDING = 8;

/**
 * The CSS a node label is laid out with in HTML, beyond its font family and size: the label editor's field and the
 * line breaker's hidden element both take exactly this, so the browser breaks both at the same places. Everything a
 * page stylesheet could otherwise pass down to one of them and not the other is pinned.
 */
export const LABEL_TEXT_LAYOUT = {
  whiteSpace: 'pre-wrap',
  overflowWrap: 'anywhere',
  wordBreak: 'normal',
  lineBreak: 'auto',
  hyphens: 'manual',
  tabSize: 8,
  fontStyle: 'normal',
  fontWeight: 'normal',
  fontStretch: 'normal',
  fontVariant: 'normal',
  fontKerning: 'auto',
  letterSpacing: 'normal',
  wordSpacing: 'normal',
  textTransform: 'none',
  textIndent: '0px',
} as const;

/** A line height in CSS pixels for a label set in `font`. */
export function labelLineHeight(font: string): number {
  return fontSizeOf(font) * LABEL_LINE_HEIGHT;
}

/** A measurer backed by a 2D canvas context, or undefined where one is unavailable (Node, jsdom). */
export function createCanvasMeasurer(): TextMeasurer | undefined {
  if (typeof document === 'undefined') {
    return undefined;
  }
  const ctx = tryCreateContext();
  if (!ctx) {
    return undefined;
  }
  const cache = new Map<string, number>();
  return (text, font) => {
    const key = `${font}|${text}`;
    const cached = cache.get(key);
    if (cached !== undefined) {
      return cached;
    }
    ctx.font = font;
    const width = ctx.measureText(text).width;
    cache.set(key, width);
    return width;
  };
}

function tryCreateContext(): CanvasRenderingContext2D | null {
  try {
    return document.createElement('canvas').getContext('2d');
  } catch {
    return null;
  }
}

/**
 * Breaks text into lines no wider than `maxWidth` the way LABEL_TEXT_LAYOUT does, at spaces only: the fallback where no
 * browser lays text out (see the module comment). Explicit newlines always break. Spaces are kept as typed; the run of
 * spaces a line breaks at stays on that line, hanging, and is left off it, while spaces ending a paragraph stay. A word
 * longer than a line is split between characters so nothing overflows. Returns at least one line (possibly empty).
 */
export function wrapText(text: string, maxWidth: number, font: string, measure: TextMeasurer = estimateTextWidth): string[] {
  const lines: string[] = [];
  const width = Math.max(maxWidth, 1);
  for (const paragraph of paragraphsOf(text)) {
    let current = '';
    for (const token of paragraph.match(/ +|[^ ]+/g) ?? []) {
      if (token.startsWith(' ')) {
        // Spaces never push a line over: they hang past its end.
        current += token;
        continue;
      }
      if (measure(current + token, font) <= width) {
        current += token;
        continue;
      }
      if (current.length > 0) {
        lines.push(trimHanging(current));
      }
      if (measure(token, font) <= width) {
        current = token;
        continue;
      }
      // The word alone is too wide: split it between characters.
      let piece = '';
      for (const ch of token) {
        if (piece && measure(piece + ch, font) > width) {
          lines.push(piece);
          piece = ch;
        } else {
          piece += ch;
        }
      }
      current = piece;
    }
    lines.push(current);
  }
  return lines;
}

/** The paragraphs of a label: every newline is a forced break. */
function paragraphsOf(text: string): string[] {
  return text.split(/\r?\n/);
}

/** A soft-wrapped line without the spaces it wrapped at, which hang past its end. */
function trimHanging(line: string): string {
  return line.replace(/ +$/, '');
}

/**
 * The lines of one laid-out paragraph, from `lineOf(start, end)`: the index (or any value that grows by at least 1 per
 * line) of the line the browser put the characters from `start` to `end` on. Offsets are UTF-16 indices; each probe
 * covers one whole code point, so a surrogate pair is never split. Finds each line's last character by bisection, so a
 * paragraph costs a few probes per line rather than one per character. Every line but the last loses the spaces it
 * wrapped at (they hang); the last keeps its trailing spaces.
 */
export function linesFromLayout(paragraph: string, lineOf: (start: number, end: number) => number): string[] {
  if (paragraph.length === 0) {
    return [''];
  }
  const starts: number[] = [];
  let offset = 0;
  for (const ch of paragraph) {
    starts.push(offset);
    offset += ch.length;
  }
  starts.push(paragraph.length);
  const count = starts.length - 1;
  const probe = (i: number): number => lineOf(starts[i] ?? 0, starts[i + 1] ?? paragraph.length);
  const lines: string[] = [];
  let first = 0;
  while (first < count) {
    const line = probe(first);
    let lo = first;
    let hi = count - 1;
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (probe(mid) - line < 0.5) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    const next = lo + 1;
    const text = paragraph.slice(starts[first], starts[next]);
    lines.push(next < count ? trimHanging(text) : text);
    first = next;
  }
  return lines;
}

/** Most distinct labels a line breaker remembers before starting over; far more than a document draws at once. */
const LINE_CACHE_LIMIT = 10_000;

/**
 * A line breaker that lets the browser lay the label out with LABEL_TEXT_LAYOUT, as the label editor's field is, and
 * reads the lines back (see the module comment); undefined where there is no layout engine (Node, jsdom). One hidden
 * element serves every call, and results are cached per font, width and text, so a label is laid out once however
 * often it is drawn.
 */
export function createDomLineBreaker(): LineBreaker | undefined {
  if (typeof document === 'undefined' || typeof document.createRange !== 'function') {
    return undefined;
  }
  const host = document.createElement('div');
  host.setAttribute('aria-hidden', 'true');
  host.setAttribute('data-label-line-breaker', '');
  Object.assign(host.style, {
    position: 'absolute',
    left: '-100000px',
    top: '0',
    visibility: 'hidden',
    pointerEvents: 'none',
    contain: 'layout style paint',
    margin: '0',
    padding: '0',
    border: '0',
    boxSizing: 'content-box',
  });
  const node = document.createTextNode('');
  host.append(node);
  const range = document.createRange();
  const top = (start: number, end: number): number => {
    range.setStart(node, start);
    range.setEnd(node, end);
    return range.getBoundingClientRect().top;
  };
  document.body.append(host);
  const lay = (paragraph: string, maxWidth: number, font: string): void => {
    host.style.font = font;
    Object.assign(host.style, LABEL_TEXT_LAYOUT);
    host.style.lineHeight = `${String(labelLineHeight(font))}px`;
    host.style.width = `${String(Math.max(maxWidth, 1))}px`;
    node.data = paragraph;
  };
  // jsdom and the like build the DOM but lay nothing out: text has no extent (a test may stub element boxes, never
  // a range's).
  lay('W', 100, labelFont('sans-serif'));
  if (typeof range.getBoundingClientRect !== 'function' || rangeWidth(range, node) <= 0) {
    host.remove();
    return undefined;
  }
  const cache = new Map<string, string[]>();
  return (text, maxWidth, font) => {
    const key = `${font}|${String(maxWidth)}|${text}`;
    const cached = cache.get(key);
    if (cached !== undefined) {
      return [...cached];
    }
    const lineHeight = labelLineHeight(font);
    const lines: string[] = [];
    for (const paragraph of paragraphsOf(text)) {
      lay(paragraph, maxWidth, font);
      // Most labels fit on one line: one layout says so without probing a character.
      if (paragraph.length === 0 || host.getBoundingClientRect().height < lineHeight * 1.5) {
        lines.push(paragraph);
        continue;
      }
      lines.push(...linesFromLayout(paragraph, (start, end) => top(start, end) / lineHeight));
    }
    if (cache.size >= LINE_CACHE_LIMIT) {
      cache.clear();
    }
    cache.set(key, lines);
    return [...lines];
  };
}

const SVG_NS = 'http://www.w3.org/2000/svg';
const baselineShifts = new Map<string, number>();

/**
 * How much lower, in CSS pixels at zoom 1, the browser draws a label's glyphs in the SVG (NodeView: each line's `y` on
 * the middle of its line box, `dominant-baseline="central"`) than in an HTML line box of the same line height, as the
 * label editor lays it out. The two agree in theory; in practice the HTML line box rounds the font's ascent and descent
 * and the SVG does not, which leaves a fraction of a pixel that grows with zoom. The label editor moves its text by
 * this much so its lines sit exactly where the renderer's will. Measured once per font family; 0 where nothing is laid
 * out.
 */
export function labelBaselineShift(fontFamily: string): number {
  const cached = baselineShifts.get(fontFamily);
  if (cached !== undefined) {
    return cached;
  }
  if (typeof document === 'undefined' || typeof document.createRange !== 'function') {
    return 0;
  }
  const font = labelFont(fontFamily);
  const lineHeight = labelLineHeight(font);
  const probe = document.createElement('div');
  probe.setAttribute('aria-hidden', 'true');
  Object.assign(probe.style, { position: 'absolute', left: '-100000px', top: '0', visibility: 'hidden', pointerEvents: 'none' });
  const html = document.createElement('div');
  html.style.font = font;
  Object.assign(html.style, LABEL_TEXT_LAYOUT);
  html.style.lineHeight = `${String(lineHeight)}px`;
  html.textContent = 'Hxg';
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('width', '200');
  svg.setAttribute('height', String(lineHeight));
  Object.assign(svg.style, { position: 'absolute', left: '0', top: '0', overflow: 'visible' });
  const text = document.createElementNS(SVG_NS, 'text');
  text.setAttribute('font-family', fontFamily);
  text.setAttribute('font-size', String(fontSizeOf(font)));
  text.setAttribute('dominant-baseline', 'central');
  text.setAttribute('x', '0');
  text.setAttribute('y', String(lineHeight / 2));
  text.textContent = 'Hxg';
  svg.append(text);
  probe.append(html, svg);
  document.body.append(probe);
  const glyphTop = (element: Element): number | undefined => {
    const node = element.firstChild;
    if (node === null) {
      return undefined;
    }
    const range = document.createRange();
    range.selectNodeContents(node);
    const rect = typeof range.getBoundingClientRect === 'function' ? range.getBoundingClientRect() : undefined;
    return rect !== undefined && rect.height > 0 ? rect.top : undefined;
  };
  const htmlTop = glyphTop(html);
  const svgTop = glyphTop(text);
  probe.remove();
  const shift = htmlTop !== undefined && svgTop !== undefined && Number.isFinite(svgTop - htmlTop) ? svgTop - htmlTop : 0;
  baselineShifts.set(fontFamily, shift);
  return shift;
}

let shared: { breaker: LineBreaker | undefined } | undefined;

/**
 * The page's one DOM line breaker, made on first use, so however many diagrams a page opens they share one hidden
 * element and one cache; undefined where there is no layout engine.
 */
export function sharedDomLineBreaker(): LineBreaker | undefined {
  shared ??= { breaker: createDomLineBreaker() };
  return shared.breaker;
}

function rangeWidth(range: Range, node: Text): number {
  range.setStart(node, 0);
  range.setEnd(node, node.data.length);
  return range.getBoundingClientRect().width;
}
