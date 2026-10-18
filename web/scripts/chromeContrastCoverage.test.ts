/**
 * Every pair of chrome colours app.css draws text with is one the contrast checker holds (QA S4). The Themes page and the
 * token compiler both warn from `CHROME_CONTRAST_PAIRS`; before this test a rule could put white `brandText` on `accent`
 * (every primary button did) with no pair in that list, and the page reported "Pass" for a teal accent at 2.48:1. This
 * reads app.css itself, like focus-rings.test.ts (a Node file-system test, so it lives beside the build scripts).
 */

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { CHROME_CONTRAST_PAIRS } from '../src/theme/themeContrast.ts';

const KNOWN = new Set(['text', 'textMuted', 'surface', 'surfaceRaised', 'brand', 'brandText', 'accent', 'danger', 'border']);

const camel = (name: string): string => name.replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());

/**
 * The `[foreground, background]` token pairs one stylesheet draws: a rule that sets both `color` and `background` from
 * chrome tokens is that pair; a rule that sets only a token `color` is that colour on the page `surface` it inherits.
 */
function usedPairs(source: string): Map<string, string[]> {
  const css = source.replace(/\/\*[\s\S]*?\*\//g, '');
  const used = new Map<string, string[]>();
  for (const match of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const selector = (match[1] ?? '').trim().replace(/\s+/g, ' ');
    const body = match[2] ?? '';
    const foregrounds = [...body.matchAll(/(?<![-\w])color:\s*var\(--chrome-([a-z-]+)\)/g)].map((m) => camel(m[1] ?? ''));
    const backgrounds = [...body.matchAll(/(?<![-\w])background(?:-color)?:\s*var\(--chrome-([a-z-]+)\)/g)].map((m) => camel(m[1] ?? ''));
    for (const fg of foregrounds) {
      for (const bg of backgrounds.length === 0 ? ['surface'] : backgrounds) {
        if (fg !== bg && KNOWN.has(fg) && KNOWN.has(bg)) {
          const key = `${fg}/${bg}`;
          used.set(key, [...(used.get(key) ?? []), selector]);
        }
      }
    }
  }
  return used;
}

const checked = new Set(CHROME_CONTRAST_PAIRS.map(([fg, bg]) => `${fg}/${bg}`));

describe('chrome contrast coverage', () => {
  it('holds every token pair app.css draws text with', () => {
    const used = usedPairs(readFileSync(new URL('../src/app.css', import.meta.url), 'utf8'));
    expect(used.size).toBeGreaterThan(5);
    const unchecked = [...used].filter(([pair]) => !checked.has(pair)).map(([pair, selectors]) => `${pair} (${selectors.slice(0, 2).join(', ')})`);
    expect(unchecked).toEqual([]);
  });

  it('reports brandText on accent, the pair the checker once missed', () => {
    const used = usedPairs('.primary { color: var(--chrome-brand-text); background: var(--chrome-accent); }');
    expect([...used.keys()]).toEqual(['brandText/accent']);
    expect(checked.has('brandText/accent')).toBe(false);
  });

  it('reads a lone text colour as sitting on the surface', () => {
    expect([...usedPairs('.link { color: var(--chrome-accent); }').keys()]).toEqual(['accent/surface']);
    expect(checked.has('accent/surface')).toBe(true);
  });

  it('never draws a filled control from the accent', () => {
    // Accent is a link, ring and stroke colour; a fill that carries brandText is `brand`, the token that pair is held to.
    const used = usedPairs(readFileSync(new URL('../src/app.css', import.meta.url), 'utf8'));
    expect([...used.keys()].filter((pair) => pair.endsWith('/accent'))).toEqual([]);
  });
});
