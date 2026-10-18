// @vitest-environment jsdom
import { render } from '@testing-library/react';
import schema from '../../../docs/schema/document.schema.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { Node } from '../model/types.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { renderShape } from './shapes.tsx';
import { connectablePorts, portPosition, portSide } from './ports.ts';
import { estimateTextWidth, LABEL_FONT_SIZE, LABEL_PADDING, labelFont } from './text.ts';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** The shape alternatives the committed schema admits, with the anchors and the wrapping group stripped. */
function schemaShapes(): string[] {
  const pattern = schema.$defs.node.properties.shape.pattern;
  return pattern.slice(1, -1).slice(1, -1).split('|');
}

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string): string {
  const { container, unmount } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

interface Point {
  x: number;
  y: number;
}

function pointsOf(attribute: string): Point[] {
  return attribute
    .trim()
    .split(/\s+/)
    .map((pair) => {
      const [x, y] = pair.split(',').map(Number);
      return { x: x ?? 0, y: y ?? 0 };
    });
}

/** True when `p` is on the closed polygon `vertices` within `tolerance` canvas units. */
function onPolygon(vertices: Point[], p: Point, tolerance = 0.75): boolean {
  for (const [i, a] of vertices.entries()) {
    const b = vertices[(i + 1) % vertices.length] ?? a;
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const lengthSquared = dx * dx + dy * dy;
    const t = lengthSquared === 0 ? 0 : Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared));
    if (Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t)) <= tolerance) {
      return true;
    }
  }
  return false;
}

/** True when `p` is on the ellipse element's outline; an axis-aligned point has an exact value of 1. */
function onEllipse(ellipse: Element, p: Point): boolean {
  const cx = Number(ellipse.getAttribute('cx'));
  const cy = Number(ellipse.getAttribute('cy'));
  const rx = Number(ellipse.getAttribute('rx'));
  const ry = Number(ellipse.getAttribute('ry'));
  return Math.abs(((p.x - cx) / rx) ** 2 + ((p.y - cy) / ry) ** 2 - 1) < 1e-9;
}

describe('flowchart shapes', () => {
  it.each([
    ['document', 'path', 'd', 'M0,0 H200 V85 C150,55 50,115 0,85 Z', { x: 0, y: 0, w: 200, h: 70 }],
    ['predefined-process', 'path', 'd', 'M24,0 V100 M176,0 V100', { x: 24, y: 0, w: 152, h: 100 }],
    ['manual-input', 'polygon', 'points', '0,25 200,0 200,100 0,100', { x: 0, y: 25, w: 200, h: 75 }],
    ['off-page-connector', 'polygon', 'points', '0,0 200,0 200,65 100,100 0,65', { x: 0, y: 0, w: 200, h: 65 }],
  ] as const)('draws %s with its own outline and an inset text box', (shape, tag, attr, expected, labelBox) => {
    const result = renderShape(shape, 200, 100, style);
    const { container } = render(<svg>{result.element}</svg>);
    expect(container.querySelector(tag)?.getAttribute(attr)).toBe(expected);
    expect(result.labelBox).toEqual(labelBox);
    expect(container.querySelector('.shape')?.getAttribute('fill')).toBe(style.fill);
    expect(container.querySelector('.shape')?.getAttribute('stroke')).toBe(style.stroke);
  });

  it.each([
    ['document', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 85 }, { x: 0, y: 50 }]],
    ['manual-input', [{ x: 100, y: 12.5 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
    ['predefined-process', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
    ['off-page-connector', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
  ] as const)('places %s ports on its outline and respects declared ports', (shape, expected) => {
    const node = { id: 'n', kind: 'process', label: '', shape, geometry: { x: 0, y: 0, w: 200, h: 100 } };
    const ports = connectablePorts(node);
    expect(ports.map((port) => portPosition(node.geometry, port))).toEqual(expected);
    expect(ports.map((port) => portSide(node.geometry, port))).toEqual(['top', 'right', 'bottom', 'left']);
    expect(connectablePorts({ ...node, ports: [{ id: 'custom', x: 0.2, y: 0.3 }] })).toEqual([{ id: 'custom', x: 0.2, y: 0.3 }]);
  });

  it('draws every shape the schema admits as more than the fallback rectangle', () => {
    const fallback = markup('rectangle');
    for (const shape of schemaShapes().filter((candidate) => !candidate.startsWith('custom:'))) {
      if (shape === 'rectangle') {
        expect(markup(shape)).toBe(fallback);
        continue;
      }
      // A shape the renderer does not know silently falls through to a plain rectangle; this is the sweep that fails first.
      expect(markup(shape), `${shape} renders as the fallback rectangle`).not.toBe(fallback);
    }
  });

  it.each([
    'manual-operation',
    'merge',
    'extract',
    'collate',
    'sort',
    'loop-limit',
    'card',
    'on-page-connector',
    'summing-junction',
    'or',
  ] as const)('places every default port of %s on its drawn outline', (shape) => {
    const geometry = { x: 0, y: 0, w: 200, h: 100 };
    const node: Node = { id: 'n', kind: 'process', label: '', shape, geometry };
    const { container } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
    const polygon = container.querySelector('polygon.shape');
    const ellipse = container.querySelector('ellipse.shape');
    const ports = connectablePorts(node);
    expect(ports.length).toBeGreaterThan(0);
    for (const port of ports) {
      const point = portPosition(geometry, port);
      if (polygon) {
        expect(onPolygon(pointsOf(polygon.getAttribute('points') ?? ''), point), `${shape} port ${port.id} off the polygon`).toBe(true);
      } else if (ellipse) {
        expect(onEllipse(ellipse, point), `${shape} port ${port.id} off the ellipse`).toBe(true);
      } else {
        throw new Error(`no outline rendered for ${shape}`);
      }
    }
  });

  it.each([
    ['delay', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
    ['display', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
    ['stored-data', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 100, y: 100 }, { x: 0, y: 50 }]],
    ['multi-document', [{ x: 100, y: 0 }, { x: 200, y: 50 }, { x: 80, y: 88 }, { x: 0, y: 50 }]],
  ] as const)('places %s ports on the extremes its outline reaches', (shape, expected) => {
    const node = { id: 'n', kind: 'process', label: '', shape, geometry: { x: 0, y: 0, w: 200, h: 100 } };
    expect(connectablePorts(node).map((port) => portPosition(node.geometry, port))).toEqual(expected);
  });

  it('places the multi-document bottom port on the front page its wave is drawn through', () => {
    const { container } = render(<svg>{renderShape('multi-document', 200, 100, style).element}</svg>);
    const paths = [...container.querySelectorAll('path.shape')];
    // The front page paints last and its wave's midpoint is the bottom port's own point.
    expect(paths.at(-1)?.getAttribute('d')).toBe('M0,20 H160 V88 C120,64 40,112 0,88 Z');
    const bottom = connectablePorts({ id: 'n', kind: 'process', label: '', shape: 'multi-document' }).find((port) => port.id === 'bottom');
    expect(bottom).toBeDefined();
    if (bottom) {
      expect(portPosition({ x: 0, y: 0, w: 200, h: 100 }, bottom)).toEqual({ x: 80, y: 88 });
    }
  });

  it('draws an image through <image> when given an href, and its placeholder frame when not', () => {
    const withHref = renderShape('image', 200, 100, style, 'data:image/png;base64,AAAB');
    const { container } = render(<svg>{withHref.element}</svg>);
    const image = container.querySelector('image');
    expect(image).not.toBeNull();
    expect(image?.getAttribute('href')).toBe('data:image/png;base64,AAAB');
    expect(container.querySelector('rect')).not.toBeNull();

    const placeholder = renderShape('image', 200, 100, style);
    const plain = render(<svg>{placeholder.element}</svg>);
    expect(plain.container.querySelector('image')).toBeNull();
    expect(plain.container.querySelector('rect')).not.toBeNull();
  });
});

describe('decision label room (S2)', () => {
  it('leaves a default 160 px diamond wide enough for a long word whole, so the label is not broken mid-word', () => {
    const { labelBox } = renderShape('diamond', 160, 80, style);
    const room = labelBox.w - LABEL_PADDING * 2;
    expect(room).toBeGreaterThanOrEqual(estimateTextWidth('successful?', labelFont(style.font, style.fontSize ?? LABEL_FONT_SIZE)));
    // Still inside the outline: at the label's top and bottom lines the diamond is wider than the box it is given.
    expect(labelBox.w).toBeLessThan(160 * 0.75);
  });
});
