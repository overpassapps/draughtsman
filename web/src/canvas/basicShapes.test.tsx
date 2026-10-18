// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { BASIC_SHAPE_PREFIX, BASIC_SHAPES, basicShapeName, resolvePath } from '../model/basicShapes.ts';
import { defaultPorts } from '../model/connect.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { outlineKind } from './ports.ts';
import { renderShape } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string, w = 200, h = 100): string {
  const { container, unmount } = render(<svg>{renderShape(shape, w, h, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

describe('basic shapes', () => {
  it('draws every shape in the library, not the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(BASIC_SHAPES.length).toBeGreaterThan(0);
    for (const shape of BASIC_SHAPES) {
      const name = basicShapeName(shape.name);
      expect(markup(name), `${name} renders as the fallback rectangle`).not.toBe(fallback);
    }
  });

  it('falls back to the rectangle for a basic name the library does not carry', () => {
    expect(markup(`${BASIC_SHAPE_PREFIX}not-a-real-shape`)).toBe(markup('rectangle'));
  });

  it('draws the outline from the library’s path data, painted with the node’s fill and stroke', () => {
    const { container, unmount } = render(<svg>{renderShape(basicShapeName('triangle'), 120, 100, style).element}</svg>);
    const outline = container.querySelector('path.shape');
    expect(outline?.getAttribute('d')).toBe('M60,0 L120,100 L0,100 Z');
    expect(outline?.getAttribute('fill')).toBe(style.fill);
    expect(outline?.getAttribute('stroke')).toBe(style.stroke);
    unmount();
  });

  it('draws a note with its fold as an unfilled detail line', () => {
    const { container, unmount } = render(<svg>{renderShape(basicShapeName('note'), 140, 90, style).element}</svg>);
    expect(container.querySelector('path.shape')?.getAttribute('d')).toBe('M0,0 L124,0 L140,16 L140,90 L0,90 Z');
    const fold = container.querySelector('path.shape-detail');
    expect(fold?.getAttribute('d')).toBe('M124,0 L124,16 L140,16');
    expect(fold?.getAttribute('fill')).toBe('none');
    unmount();
  });

  it('fills the component’s glyph parts, which are small boxes, and not the note’s fold', () => {
    const { container, unmount } = render(<svg>{renderShape(basicShapeName('component'), 160, 90, style).element}</svg>);
    const details = [...container.querySelectorAll('path.shape-detail')];
    expect(details).toHaveLength(3);
    for (const detail of details) {
      expect(detail.getAttribute('fill')).toBe(style.fill);
    }
    unmount();
  });

  it('draws the brace as a line over a transparent box, so the whole box stays clickable', () => {
    const { container, unmount } = render(<svg>{renderShape(basicShapeName('brace'), 140, 120, style).element}</svg>);
    const hit = container.querySelector('rect.shape');
    expect(hit?.getAttribute('fill')).toBe('transparent');
    expect(hit?.getAttribute('width')).toBe('140');
    expect(hit?.getAttribute('height')).toBe('120');
    const line = container.querySelector('path.shape-detail');
    expect(line?.getAttribute('fill')).toBe('none');
    expect(line?.getAttribute('d')).toBe(resolvePath('M0+24,0 C0+14,0 0+12,0+4 0+12,0+14 L0+12,0.5-14 C0+12,0.5-4 0+6,0.5 0,0.5 C0+6,0.5 0+12,0.5+4 0+12,0.5+14 L0+12,1-14 C0+12,1-4 0+14,1 0+24,1', 140, 120));
    unmount();
  });

  it.each([
    ['ellipse', { x: 21, y: 12, w: 98, h: 56 }],
    ['triangle', { x: 35, y: 36, w: 70, h: 40 }],
    ['note', { x: 8, y: 8, w: 124, h: 64 }],
    ['callout', { x: 6, y: 4, w: 128, h: 52 }],
    ['brace', { x: 32, y: 0, w: 108, h: 80 }],
    ['folder', { x: 6, y: 22, w: 128, h: 52 }],
    ['cube', { x: 4, y: 20, w: 116, h: 56 }],
  ])('lays the %s label out in its own label box', (name, expected) => {
    expect(renderShape(basicShapeName(name), 140, 80, style).labelBox).toEqual(expected);
  });

  it('squeezes a shape’s fixed-size features in a small box instead of turning the outline inside out', () => {
    const { container, unmount } = render(<svg>{renderShape(basicShapeName('note'), 20, 20, style).element}</svg>);
    // A 16 px fold would pass the middle of a 20 px box; it stops at half.
    expect(container.querySelector('path.shape')?.getAttribute('d')).toBe('M0,0 L10,0 L20,10 L20,20 L0,20 Z');
    unmount();
  });

  it('declares the ports the library lists as a node’s default ports', () => {
    expect(defaultPorts(basicShapeName('ellipse')).map((port) => port.id)).toEqual(['top', 'right', 'bottom', 'left']);
    expect(defaultPorts(basicShapeName('trapezoid')).find((port) => port.id === 'left')).toEqual({ id: 'left', x: 0.1, y: 0.5 });
    expect(defaultPorts(basicShapeName('brace')).map((port) => port.id)).toEqual(['left']);
    // Everything else keeps the four side midpoints.
    expect(defaultPorts('rectangle')).toHaveLength(4);
  });

  it('attaches a connector to the ellipse’s drawn outline, as it does to a BPMN event', () => {
    expect(outlineKind(basicShapeName('ellipse'))).toBe('ellipse');
    expect(outlineKind(basicShapeName('triangle'))).toBeUndefined();
  });
});
