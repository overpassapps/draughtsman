/**
 * The Draughtsman logo files the app ships in web/public: present, small, valid, self-contained (no request to anywhere), and
 * wired from index.html. The files are the chosen logo copied unchanged, so this checks their shape, not their artwork.
 */
import { existsSync, readFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parse as parseYaml } from 'yaml';
import { AA_GRAPHIC, AA_TEXT, contrastRatio } from '../src/theme/contrast.ts';

const publicDir = fileURLToPath(new URL('../public/', import.meta.url));
const read = (name: string): string => readFileSync(`${publicDir}${name}`, 'utf8');

const SVGS = ['favicon.svg', 'brand/mark.svg', 'brand/mark-on-dark.svg', 'brand/lockup-light.svg', 'brand/lockup-dark.svg'];
const PNGS: [string, number][] = [
  ['favicon-16.png', 16],
  ['favicon-32.png', 32],
  ['apple-touch-icon.png', 180],
];

describe('the shipped brand assets', () => {
  it.each(SVGS)('%s is a small, self-contained SVG', (name) => {
    const svg = read(name);
    expect(svg.startsWith('<svg xmlns="http://www.w3.org/2000/svg"')).toBe(true);
    expect(statSync(`${publicDir}${name}`).size).toBeLessThan(20_000);
    // The one URL is the XML namespace; nothing is loaded from anywhere, and nothing runs.
    expect(svg.match(/https?:\/\/[^\s"']+/g)).toEqual(['http://www.w3.org/2000/svg']);
    expect(svg).not.toMatch(/<script|<foreignObject|\son[a-z]+=|href=|<image/i);
    expect(svg).toContain('role="img"');
    expect(svg).toContain('<title>');
  });

  it.each(PNGS)('%s is a PNG of the size its name says', (name, size) => {
    const bytes = readFileSync(`${publicDir}${name}`);
    expect(bytes.subarray(1, 4).toString('latin1')).toBe('PNG');
    expect(bytes.readUInt32BE(16)).toBe(size);
    expect(bytes.readUInt32BE(20)).toBe(size);
  });

  it('keeps the lockup’s plain-text line, and does not carry the Overpass logo graphic', () => {
    for (const name of ['brand/lockup-light.svg', 'brand/lockup-dark.svg']) {
      expect(read(name)).toContain('aria-label="Draughtsman by Overpass"');
    }
    // The Overpass wordmark is 1986 x 443; no file here is or contains it.
    for (const name of SVGS) {
      expect(read(name)).not.toContain('viewBox="0 0 1986 443"');
    }
  });

  it('has the navy mark for light surfaces and the white one for dark, both with the red feet', () => {
    expect(read('brand/mark.svg')).toContain('stroke="#151e33"');
    expect(read('brand/mark-on-dark.svg')).toContain('stroke="#ffffff"');
    for (const name of ['brand/mark.svg', 'brand/mark-on-dark.svg']) {
      expect(read(name)).toContain('fill="#e04832"');
    }
  });
});

describe('index.html', () => {
  const html = readFileSync(fileURLToPath(new URL('../index.html', import.meta.url)), 'utf8');

  it('links the Draughtsman SVG favicon, its 16 and 32 px PNG fallbacks and the apple touch icon, all of which exist', () => {
    for (const href of ['/favicon.svg', '/favicon-32.png', '/favicon-16.png', '/apple-touch-icon.png']) {
      expect(html).toContain(`href="${href}"`);
      expect(existsSync(`${publicDir}${href.slice(1)}`)).toBe(true);
    }
    expect(html).toMatch(/<link rel="icon" href="\/favicon\.svg" type="image\/svg\+xml"/);
  });

  it('loads nothing from another host', () => {
    expect(html).not.toMatch(/(?:src|href)="(?:https?:)?\/\//);
  });
});

describe('the mark’s colours against the shipped chrome surfaces', () => {
  // Read from the theme file rather than copied, so a new surface colour is checked. Where the lockup's text and the mark's
  // strokes sit: the page (surface) on About and sign-in, the raised bar (surfaceRaised) in the header.
  const chrome = parseYaml(readFileSync(fileURLToPath(new URL('../../themes/chrome.yaml', import.meta.url)), 'utf8')) as {
    colors: Record<string, string>;
    light: { colors: Record<string, string> };
  };
  const NAVY = '#151e33';
  const WHITE = '#ffffff';
  const RED = '#e04832';

  it('puts the lockup’s navy text on the light surfaces at AA for text, and its white text on the dark ones', () => {
    for (const surface of [chrome.light.colors.surface, chrome.light.colors.surfaceRaised]) {
      expect(contrastRatio(NAVY, surface ?? '')).toBeGreaterThanOrEqual(AA_TEXT);
    }
    for (const surface of [chrome.colors.surface, chrome.colors.surfaceRaised]) {
      expect(contrastRatio(WHITE, surface ?? '')).toBeGreaterThanOrEqual(AA_TEXT);
    }
  });

  it('puts the red feet on every surface at the AA minimum for a graphic', () => {
    for (const surface of [chrome.light.colors.surface, chrome.light.colors.surfaceRaised, chrome.colors.surface, chrome.colors.surfaceRaised]) {
      expect(contrastRatio(RED, surface ?? '')).toBeGreaterThanOrEqual(AA_GRAPHIC);
    }
  });
});
