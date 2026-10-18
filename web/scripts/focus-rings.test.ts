/**
 * Focus rings are never removed (checklist item 28; design/atlas/editor-shell.md, Keyboard and accessibility). jsdom
 * computes no :focus-visible styles, so this reads app.css itself: the baseline rule gives every focusable control the
 * accent ring, and the one rule that switches the outline off (the canvas's, for pointer focus) is paired with its own
 * :focus-visible ring. A new `outline: none` anywhere else fails here. It lives beside the build scripts because they
 * are the tests with Node's file system (tsconfig.node.json); Vitest hands a src test an empty stylesheet.
 */

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const css = readFileSync(new URL('../src/app.css', import.meta.url), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const rules = [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((match) => ({ selector: (match[1] ?? '').trim().replace(/\s+/g, ' '), body: match[2] ?? '' }));

describe('focus rings in app.css', () => {
  it('draws the focus ring on every focusable control’s keyboard focus by default', () => {
    const baseline = rules.find((rule) => rule.selector === ':where(a, button, input, select, textarea, [tabindex]):focus-visible');
    expect(baseline?.body).toMatch(/outline:\s*2px solid var\(--chrome-focus-ring\)/);
  });

  it('keeps the ring off the accent and danger colours, so a focused control never looks like an error (QA P13)', () => {
    const root = rules.filter((rule) => rule.selector === ':root').map((rule) => rule.body).join('\n');
    expect(root).toMatch(/--chrome-focus-ring:\s*var\(--chrome-text\)/);
    // No keyboard-focus rule paints its outline in the accent (the shipped theme's red) or the danger colour.
    const redFocus = rules.filter((rule) => rule.selector.includes(':focus') && /outline[^;]*var\(--chrome-(accent|danger|brand)\)/.test(rule.body)).map((rule) => rule.selector);
    expect(redFocus).toEqual([]);
  });

  it('switches the outline off only on the canvas, which draws its own keyboard ring', () => {
    const removed = rules.filter((rule) => /outline(-width)?:\s*(none|0)\b/.test(rule.body)).map((rule) => rule.selector);
    expect(removed).toEqual(['.diagram-canvas:focus']);
    const canvasRing = rules.find((rule) => rule.selector === '.diagram-canvas:focus-visible:not([data-pointer-focus])');
    expect(canvasRing?.body).toMatch(/outline:\s*2px solid var\(--chrome-focus-ring\)/);
  });
});
