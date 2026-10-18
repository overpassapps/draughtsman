/**
 * The CSS and font promises of the onboarding lane (PK-09, QA-05), read from the files themselves because jsdom computes
 * no layout or fonts (see focus-rings.test.ts): the bundled fonts are declared under the names the theme's stacks use and
 * point at files that exist; the stylesheet that declares them is loaded by main.tsx; the AI panel's diagram-type radios
 * wrap instead of clipping; and the shape panel's captions are clamped, not cut.
 */

import { existsSync, readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const read = (path: string): string => readFileSync(new URL(path, import.meta.url), 'utf8');
const strip = (css: string): string => css.replace(/\/\*[\s\S]*?\*\//g, '');
const ruleBody = (css: string, selector: string): string => {
  const match = [...strip(css).matchAll(/([^{}]+)\{([^{}]*)\}/g)].find((candidate) => (candidate[1] ?? '').trim() === selector);
  return match?.[2] ?? '';
};

describe('bundled fonts (PK-09)', () => {
  const fonts = strip(read('../src/fonts.css'));
  const faces = [...fonts.matchAll(/@font-face\s*\{([^{}]*)\}/g)].map((match) => match[1] ?? '');

  it('declare Inter and JetBrains Mono, the names themes/chrome.yaml puts first in its font stacks', () => {
    const families = new Set(faces.map((face) => /font-family:\s*'([^']+)'/.exec(face)?.[1]));
    expect(families).toEqual(new Set(['Inter', 'JetBrains Mono']));
    const chrome = read('../../themes/chrome.yaml');
    expect(chrome).toMatch(/ui:\s*"Inter,/);
    expect(chrome).toMatch(/mono:\s*"JetBrains Mono,/);
  });

  it('point at woff2 files that exist in the installed packages, variable weight, with a unicode-range each', () => {
    expect(faces.length).toBe(4);
    for (const face of faces) {
      const url = /url\('([^']+\.woff2)'\)/.exec(face)?.[1];
      expect(url, face).toBeDefined();
      expect(existsSync(new URL(`../src/${url ?? ''}`, import.meta.url)), `${url ?? ''} is missing: run npm ci in web/`).toBe(true);
      expect(face).toMatch(/font-weight:\s*100 (800|900)/);
      expect(face).toMatch(/unicode-range:/);
    }
  });

  it('are loaded by main.tsx, before the app stylesheet', () => {
    const main = read('../src/main.tsx');
    expect(main).toContain("import './fonts.css';");
    expect(main.indexOf("import './fonts.css';")).toBeLessThan(main.indexOf("import './app.css';"));
  });

  it('carry their licences into THIRD-PARTY-NOTICES.txt, under the SIL Open Font Licence', () => {
    const notices = read('../../THIRD-PARTY-NOTICES.txt');
    for (const name of ['@fontsource-variable/inter', '@fontsource-variable/jetbrains-mono']) {
      expect(notices).toContain(`\n${name} `);
      expect(notices).toContain(`For: ${name} `);
    }
    expect(notices).toContain('SIL OPEN FONT LICENSE Version 1.1');
    expect(notices).toContain('Inter Project Authors');
    expect(notices).toContain('JetBrains Mono Project Authors');
  });
});

describe('panel layout fixes (QA-05)', () => {
  const css = read('../src/app.css');

  it('lets the AI panel’s diagram-type options wrap instead of clipping at the panel edge', () => {
    const body = ruleBody(css, '.ai-diagram-type');
    expect(body).toMatch(/flex-wrap:\s*wrap/);
    expect(body).toMatch(/min-width:\s*0/);
  });

  it('clamps a shape caption to two lines with an ellipsis, in a cell tall enough for the drawing and its caption', () => {
    const caption = ruleBody(css, '.shape-panel-glyph-name');
    expect(caption).toMatch(/-webkit-line-clamp:\s*2/);
    expect(caption).toMatch(/overflow:\s*hidden/);
    const glyph = ruleBody(css, '.shape-panel-glyph');
    expect(glyph).toMatch(/flex-direction:\s*column/);
    expect(glyph).toMatch(/min-height:\s*68px/);
  });
});
