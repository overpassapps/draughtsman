// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { GENERIC_ICONS, GENERIC_ICON_PREFIX, GENERIC_ICON_VIEWBOX } from '../model/icons.ts';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { defaultPorts } from '../model/connect.ts';
import { renderShape } from './shapes.tsx';
import { connectablePorts, portPosition } from './ports.ts';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string): string {
  const { container, unmount } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

describe('generic icons', () => {
  it('draws every icon in the generic library as an icon, not the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(GENERIC_ICONS.length).toBeGreaterThan(0);
    for (const icon of GENERIC_ICONS) {
      const shape = GENERIC_ICON_PREFIX + icon.name;
      const html = markup(shape);
      expect(html, `${shape} renders as the fallback rectangle`).not.toBe(fallback);
      expect(html).toContain('class="icon"');
      expect(html).toContain('scale(');
    }
  });

  it('falls back to the rectangle for a generic name the library does not carry', () => {
    expect(markup(`${GENERIC_ICON_PREFIX}not-a-real-icon`)).toBe(markup('rectangle'));
  });

  it('puts the label below the icon and uses the theme stroke, not the theme fill', () => {
    const result = renderShape(`${GENERIC_ICON_PREFIX}server`, 200, 100, style);
    expect(result.labelBox).toEqual({ x: 0, y: 70, w: 200, h: 30 });

    const { container, unmount } = render(<svg>{result.element}</svg>);
    const group = container.querySelector('g.icon');
    expect(group?.getAttribute('stroke')).toBe(style.stroke);
    expect(group?.getAttribute('fill')).toBe('none');
    // A transparent, hit-testable rectangle stands in for the thin line art's hit target.
    expect(container.querySelector('rect.icon-hit')?.getAttribute('fill')).toBe('transparent');
    unmount();
  });

  it('draws every icon at the node style’s single stroke width', () => {
    for (const icon of GENERIC_ICONS) {
      const { container, unmount } = render(<svg>{renderShape(`${GENERIC_ICON_PREFIX}${icon.name}`, 200, 100, style).element}</svg>);
      const group = container.querySelector('g.icon');
      const scale = Number(/scale\(([^)]+)\)/.exec(group?.getAttribute('transform') ?? '')?.[1]);
      const strokeWidth = Number(group?.getAttribute('stroke-width'));
      expect(scale, icon.name).toBeGreaterThan(0);
      // The group's scale is divided back out of the attribute, so every icon lands on the one theme width (to the
      // two-decimal precision the attribute is rounded to).
      expect(strokeWidth * scale, icon.name).toBeCloseTo(style.strokeWidth, 1);
      unmount();
    }
  });

  it('authors every icon on the library’s own viewBox grid', () => {
    expect(GENERIC_ICON_VIEWBOX).toBe(24);
  });

  it('draws a boundary icon as a labelled full-box boundary, and a leaf icon as a centred glyph', () => {
    const boundaries = GENERIC_ICONS.filter((icon) => icon.boundary === true).map((icon) => icon.name);
    expect([...boundaries].sort()).toEqual(['availability-zone', 'subnet', 'vpc']);

    const result = renderShape(`${GENERIC_ICON_PREFIX}vpc`, 200, 100, style);
    // The label box is the whole node: NodeView places a container label top-left.
    expect(result.labelBox).toEqual({ x: 0, y: 0, w: 200, h: 100 });
    const { container, unmount } = render(<svg>{result.element}</svg>);
    const boundary = container.querySelector('rect.shape');
    expect(boundary?.getAttribute('width')).toBe('200');
    expect(boundary?.getAttribute('height')).toBe('100');
    expect(container.querySelector('g.icon')).not.toBeNull();
    unmount();

    // Two-sided: a leaf icon draws no full-box shape and keeps its bottom-band label box.
    const leaf = renderShape(`${GENERIC_ICON_PREFIX}server`, 200, 100, style);
    expect(leaf.labelBox).toEqual({ x: 0, y: 70, w: 200, h: 30 });
    const leafRender = render(<svg>{leaf.element}</svg>);
    expect(leafRender.container.querySelector('rect.shape')).toBeNull();
    leafRender.unmount();
  });
});

describe('where a connector meets an icon (P2)', () => {
  const place = { w: 120, h: 96 };

  /** The drawn glyph's box in the node's own space, read from the transform renderShape puts on the icon group. */
  function glyph(shape: string, w: number, h: number): { x: number; y: number; side: number } {
    const { container, unmount } = render(<svg>{renderShape(shape, w, h, style).element}</svg>);
    const transform = container.querySelector('g.icon')?.getAttribute('transform') ?? '';
    unmount();
    const [x, y, scale] = (/translate\(([\d.]+) ([\d.]+)\) scale\(([\d.]+)\)/.exec(transform) ?? []).slice(1).map(Number);
    return { x: x ?? NaN, y: y ?? NaN, side: (scale ?? NaN) * GENERIC_ICON_VIEWBOX };
  }

  it('puts the side and top connection points on the drawn glyph of a node at the size the palette places it', () => {
    const drawn = glyph(`${GENERIC_ICON_PREFIX}server`, place.w, place.h);
    const ports = new Map(defaultPorts(`${GENERIC_ICON_PREFIX}server`).map((port) => [port.id, { x: port.x * place.w, y: port.y * place.h }]));
    expect(ports.get('left')?.x).toBeCloseTo(drawn.x, 0);
    expect(ports.get('right')?.x).toBeCloseTo(drawn.x + drawn.side, 0);
    expect(ports.get('left')?.y).toBeCloseTo(drawn.y + drawn.side / 2, 0);
    expect(ports.get('top')?.y).toBeCloseTo(drawn.y, 0);
    // The foot of the box is below the label, so a connector from below does not run through the drawing.
    expect(ports.get('bottom')).toEqual({ x: place.w / 2, y: place.h });
  });

  it('leaves a boundary icon (a VPC is a labelled box) and every ordinary shape on the side midpoints of the box', () => {
    for (const shape of [`${GENERIC_ICON_PREFIX}vpc`, 'rectangle']) {
      expect(defaultPorts(shape).map((port) => [port.id, port.x, port.y])).toEqual([['top', 0.5, 0], ['right', 1, 0.5], ['bottom', 0.5, 1], ['left', 0, 0.5]]);
    }
  });

  it('is what a portless icon node offers the connector tool, so a drop next to the glyph lands on the glyph', () => {
    const node = { id: 'n_s', kind: 'service', label: 'S', shape: `${GENERIC_ICON_PREFIX}server`, geometry: { x: 0, y: 0, ...place } };
    const left = connectablePorts(node).find((port) => port.id === 'left');
    expect(left && portPosition(node.geometry, left).x).toBeGreaterThan(15);
  });
});
