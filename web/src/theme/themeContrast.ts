/**
 * The WCAG AA contrast checks a theme is held to, as pure functions over the compiled shapes. One copy: the token
 * compiler (`scripts/build-tokens.ts`, which prints them as build warnings) and the admin Themes page (which shows
 * them inline beside the colour that causes them) both call these, so the page warns about exactly what the
 * compiler would. Warnings only, never a refusal: a theme with poor contrast is a support conversation, not an
 * outage (spec section 8).
 *
 * Runs under Node's type stripping as well as in the browser, so keep to erasable TypeScript.
 */

import { AA_GRAPHIC, AA_TEXT, contrastRatio, parseHex } from './contrast.ts';
import type { DiagramTheme, Surface } from './diagramTheme.ts';
import { surfaceBackground, surfaceTextColor } from './resolveStyle.ts';

/** One pair of colours that reads below its AA minimum, or a background the checks cannot read at all. */
export interface ContrastIssue {
  /**
   * Which colour tokens the issue is about, as the page's field names them: `text/surface` for a chrome pair
   * (`<foreground>/<background>` token names), `nodes.process` or `edges.flow` for a diagram kind, `canvas` or
   * `export` for a surface. The page lights up every field an issue names.
   */
  subject: string;
  /** The issue as the compiler prints it, without the `chrome (dark):` / `diagram <name>:` prefix. */
  message: string;
  /** The measured ratio; undefined when the issue is that a background is not a hex colour. */
  ratio: number | undefined;
  minimum: number | undefined;
}

/** The chrome tokens that take part in a check; the same names as `ChromeKey` in orgTheme.ts (kept apart so this module needs no generated files). */
export type ChromeToken = 'text' | 'textMuted' | 'surface' | 'surfaceRaised' | 'brand' | 'brandText' | 'accent' | 'danger' | 'border';

/**
 * The chrome token pairs checked, as [foreground, background, minimum ratio]. Every foreground-on-background pair
 * app.css actually draws must be here, or a theme can fail it and the Themes page still says "Pass" (QA S4: white
 * `brandText` was drawn on `accent` for every primary button while only `brandText` on `brand` was checked).
 * `scripts/chromeContrastCoverage.test.ts` reads app.css and fails when a pair is used that this list does not hold, so
 * a new `color: var(--chrome-x); background: var(--chrome-y)` rule cannot slip past the checker. A filled control uses
 * `brand` (which `brandText` is held to) and never `accent`, which only ever colours links, rings and strokes.
 */
export const CHROME_CONTRAST_PAIRS: readonly (readonly [ChromeToken, ChromeToken, number])[] = [
  ['text', 'surface', AA_TEXT],
  ['textMuted', 'surface', AA_TEXT],
  ['text', 'surfaceRaised', AA_TEXT],
  ['textMuted', 'surfaceRaised', AA_TEXT],
  ['brandText', 'brand', AA_TEXT],
  ['accent', 'surface', AA_GRAPHIC],
  ['danger', 'surface', AA_GRAPHIC],
  ['danger', 'surfaceRaised', AA_GRAPHIC],
];

/**
 * How much of the text colour a control's outline takes when mixed into the surface: app.css declares
 * `--chrome-border-strong` as `color-mix(in srgb, var(--chrome-text) 52%, var(--chrome-surface))`, which must stay in
 * step with this number. The `border` token is the hairline for dividers and panel edges (WCAG 1.4.11 leaves purely
 * decorative lines out); an input, button or select is found by its outline, which is held to 3:1 instead, on the
 * surface and on the raised surface. The mix is derived, not a theme token, so a client theme cannot forget it.
 */
export const CONTROL_BORDER_MIX = 0.52;

/** The colour a control outline resolves to: CONTROL_BORDER_MIX of `text` over `surface`, as hex; undefined when either is not a hex colour. */
export function controlBorder(text: string, surface: string): string | undefined {
  const t = parseHex(text);
  const s = parseHex(surface);
  if (!t || !s) {
    return undefined;
  }
  const part = (a: number, b: number): string => Math.round(a * CONTROL_BORDER_MIX + b * (1 - CONTROL_BORDER_MIX)).toString(16).padStart(2, '0');
  return `#${part(t.r, s.r)}${part(t.g, s.g)}${part(t.b, s.b)}`;
}

function below(ratio: number, minimum: number): string {
  return `is ${ratio.toFixed(2)}:1, below WCAG AA ${String(minimum)}:1`;
}

/** The contrast issues in one mode's chrome colour set. A token the set does not give, or a non-hex one, is skipped. */
export function chromeContrastIssues(colors: Readonly<Record<string, string>>): ContrastIssue[] {
  const issues: ContrastIssue[] = [];
  for (const [fg, bg, minimum] of CHROME_CONTRAST_PAIRS) {
    const fgValue = colors[fg];
    const bgValue = colors[bg];
    if (fgValue === undefined || bgValue === undefined) {
      continue;
    }
    const ratio = contrastRatio(fgValue, bgValue);
    if (ratio !== undefined && ratio < minimum) {
      issues.push({ subject: `${fg}/${bg}`, message: `${fg} on ${bg} ${below(ratio, minimum)}`, ratio, minimum });
    }
  }
  const text = colors.text;
  const surface = colors.surface;
  const outline = text === undefined || surface === undefined ? undefined : controlBorder(text, surface);
  if (outline !== undefined) {
    for (const background of ['surface', 'surfaceRaised'] as const) {
      const backgroundValue = colors[background];
      const ratio = backgroundValue === undefined ? undefined : contrastRatio(outline, backgroundValue);
      if (ratio !== undefined && ratio < AA_GRAPHIC) {
        issues.push({ subject: `controlBorder/${background}`, message: `control outline (${String(Math.round(CONTROL_BORDER_MIX * 100))}% text over surface) on ${background} ${below(ratio, AA_GRAPHIC)}`, ratio, minimum: AA_GRAPHIC });
      }
    }
  }
  return issues;
}

/** The backgrounds a theme's strokes must read against: the editor canvas and, when it differs, the export paper. */
function surfaces(theme: DiagramTheme): [string, string][] {
  const out: [string, string][] = [['canvas', theme.canvasBackground]];
  if (theme.exportBackground.toLowerCase() !== theme.canvasBackground.toLowerCase()) {
    out.push(['export background', theme.exportBackground]);
  }
  return out;
}

/** The contrast issues in a compiled diagram theme: surface text, each kind's text on its fill, and strokes on both backgrounds. */
export function diagramContrastIssues(theme: DiagramTheme): ContrastIssue[] {
  const issues: ContrastIssue[] = [];
  // Edge labels and transparent-fill node text sit straight on the surface, so they take the surface text colour.
  const checked = new Set<string>();
  for (const surface of ['canvas', 'export'] as const satisfies readonly Surface[]) {
    const text = surfaceTextColor(theme, surface);
    const background = surfaceBackground(theme, surface);
    if (text === undefined) {
      issues.push({
        subject: surface,
        message: `${surface} background ${background} is not a hex colour, so edge labels and transparent-fill node text keep the theme's own colour instead of one derived to read on it`,
        ratio: undefined,
        minimum: undefined,
      });
      continue;
    }
    const pair = `${text.toLowerCase()}/${background.toLowerCase()}`;
    if (checked.has(pair)) {
      continue;
    }
    checked.add(pair);
    const ratio = contrastRatio(text, background);
    if (ratio !== undefined && ratio < AA_TEXT) {
      issues.push({ subject: surface, message: `${surface} text ${text} (edge labels, transparent-fill nodes) on ${background} ${below(ratio, AA_TEXT)}`, ratio, minimum: AA_TEXT });
    }
  }
  for (const [kind, style] of Object.entries(theme.nodes)) {
    // Text sits on the fill; a see-through fill puts it on the surface, which is checked above.
    if (style.fill !== 'transparent') {
      const ratio = contrastRatio(style.textColor, style.fill);
      if (ratio !== undefined && ratio < AA_TEXT) {
        issues.push({ subject: `nodes.${kind}`, message: `nodes.${kind} text ${style.textColor} on ${style.fill} ${below(ratio, AA_TEXT)}`, ratio, minimum: AA_TEXT });
      }
    }
    if (style.stroke !== 'transparent' && style.stroke !== style.fill) {
      for (const [label, background] of surfaces(theme)) {
        const strokeRatio = contrastRatio(style.stroke, background);
        if (strokeRatio !== undefined && strokeRatio < AA_GRAPHIC) {
          issues.push({ subject: `nodes.${kind}`, message: `nodes.${kind} stroke ${style.stroke} on ${label} ${background} ${below(strokeRatio, AA_GRAPHIC)}`, ratio: strokeRatio, minimum: AA_GRAPHIC });
        }
      }
    }
  }
  for (const [kind, style] of Object.entries(theme.edges)) {
    for (const [label, background] of surfaces(theme)) {
      const ratio = contrastRatio(style.stroke, background);
      if (ratio !== undefined && ratio < AA_GRAPHIC) {
        issues.push({ subject: `edges.${kind}`, message: `edges.${kind} stroke ${style.stroke} on ${label} ${background} ${below(ratio, AA_GRAPHIC)}`, ratio, minimum: AA_GRAPHIC });
      }
    }
  }
  return issues;
}
