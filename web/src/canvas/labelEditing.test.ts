// @vitest-environment jsdom
import { diagramThemes } from '../generated/diagram-themes.ts';
import { resolveNodeStyle } from '../theme/resolveStyle.ts';
import type { PlacedNode } from './documentIndex.ts';
import { labelEditorLayout, nodeFrameTransform, plainTextOf } from './labelEditing.ts';
import { applyTransform } from './transformTestSupport.ts';

const style = resolveNodeStyle(diagramThemes.overpass, { kind: 'service' });

describe('nodeFrameTransform', () => {
  it('maps node-local points to where the viewport draws them, rotating about the node centre', () => {
    const viewport = { x: 10, y: 20, zoom: 2 };
    const geometry = { x: 100, y: 50, w: 120, h: 60, rotation: 90 };
    const transform = nodeFrameTransform(viewport, geometry);
    // The centre is fixed by the rotation: world (160, 80) at zoom 2 plus the pan.
    expect(applyTransform(transform, { x: 60, y: 30 })).toEqual({ x: 330, y: 180 });
    // A quarter turn clockwise about the centre takes the top-left corner to world (190, 20).
    expect(applyTransform(transform, { x: 0, y: 0 })).toEqual({ x: 390, y: 60 });
  });

  it('is a plain translate and scale for an unrotated node', () => {
    const transform = nodeFrameTransform({ x: -5, y: 7, zoom: 0.5 }, { x: 40, y: 60, w: 100, h: 50 });
    expect(applyTransform(transform, { x: 0, y: 0 })).toEqual({ x: 15, y: 37 });
    expect(applyTransform(transform, { x: 100, y: 50 })).toEqual({ x: 65, y: 62 });
    expect(transform).not.toContain('rotate');
  });
});

describe('labelEditorLayout', () => {
  const node = (shape: string, kind = 'service'): PlacedNode => ({ id: 'n', kind, label: 'L', shape, geometry: { x: 0, y: 0, w: 200, h: 40 } });

  it('uses the shape’s own label box, so a pill is inset by a quarter of its height at each end', () => {
    expect(labelEditorLayout(node('pill'), style, false).box).toEqual({ x: 10, y: 0, w: 180, h: 40 });
    expect(labelEditorLayout(node('rectangle'), style, false).box).toEqual({ x: 0, y: 0, w: 200, h: 40 });
  });

  it('centres a node label and tops a container label on the first line NodeView draws', () => {
    const plain = labelEditorLayout(node('rectangle'), style, false);
    expect(plain.top).toBeUndefined();
    expect(plain.lineHeight).toBeCloseTo(18.2);
    // NodeView centres the first container line 8 + 7 = 15 below the top; an 18.2px line box centred there starts at 5.9.
    expect(labelEditorLayout(node('rectangle', 'container'), style, true).top).toBeCloseTo(5.9);
  });
});

describe('plainTextOf', () => {
  const field = (html: string): HTMLElement => {
    const el = document.createElement('div');
    el.innerHTML = html;
    return el;
  };

  it('reads text only, whatever markup is in the field', () => {
    expect(plainTextOf(field('Order <b>service</b> <span style="color:red">v2</span>'))).toBe('Order service v2');
    expect(plainTextOf(field('<script>alert(1)</script>ok'))).toBe('alert(1)ok');
  });

  it('turns the line markup browsers insert for Enter into newlines', () => {
    // Chromium wraps each new line in a <div>; Firefox and WebKit use <br>.
    expect(plainTextOf(field('one<div>two</div><div>three</div>'))).toBe('one\ntwo\nthree');
    expect(plainTextOf(field('one<br>two'))).toBe('one\ntwo');
  });

  it('drops the <br> that only holds an empty line open, keeping the line itself', () => {
    expect(plainTextOf(field('one<div><br></div>'))).toBe('one\n');
    expect(plainTextOf(field('one<br><br>'))).toBe('one\n');
    expect(plainTextOf(field('<br>'))).toBe('');
  });

  it('reads Chrome\'s plaintext-only trailing break, a doubled newline in one text node, as one line break', () => {
    const el = document.createElement('div');
    el.textContent = 'Zed\n\n';
    expect(plainTextOf(el)).toBe('Zed\n');
    el.textContent = 'Zed\nmore\n\n';
    expect(plainTextOf(el)).toBe('Zed\nmore\n');
    el.textContent = 'a\nb';
    expect(plainTextOf(el)).toBe('a\nb');
  });

  it('keeps typed text as typed, with non-breaking spaces as spaces', () => {
    expect(plainTextOf(field('a&nbsp;&nbsp;b'))).toBe('a  b');
    expect(plainTextOf(field('&lt;b&gt;literal&lt;/b&gt;'))).toBe('<b>literal</b>');
  });
});
