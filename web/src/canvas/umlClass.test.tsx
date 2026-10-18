// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { UML_SHAPES, umlShapeName } from '../model/umlShapes.ts';
import type { Document, Node } from '../model/types.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { compartmentAt } from './labelEditing.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { NodeBody } from './NodeView.tsx';
import { renderShape } from './shapes.tsx';
import type { PlacedNode } from './documentIndex.ts';

const style = nodeKindDefault(diagramThemes.overpass, 'class');

const props = { attributes: ['- id: int', '+ total: money'], operations: ['+ pay(): void', 'refund()'] };

function markup(shape: string, content?: Parameters<typeof renderShape>[5], w = 200, h = 100): string {
  const { container, unmount } = render(<svg>{renderShape(shape, w, h, style, undefined, content).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

function texts(html: string): string[] {
  const host = document.createElement('div');
  host.innerHTML = `<svg>${html}</svg>`;
  return [...host.querySelectorAll('text')].map((text) => text.textContent);
}

describe('UML class shapes', () => {
  it('draws every library shape as a UML shape, not the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(UML_SHAPES.length).toBe(4);
    for (const shape of UML_SHAPES) {
      const html = markup(umlShapeName(shape.name), { label: 'X', props });
      expect(html, `${shape.name} renders as the fallback rectangle`).not.toBe(fallback);
      expect(html).toContain('shape uml');
    }
    // Two-sided: a name outside the library falls through to the fallback rectangle.
    expect(markup('custom:uml/not-a-shape')).toBe(fallback);
  });

  it('cuts a class into three compartments and draws its name, stereotype and members', () => {
    const html = markup('custom:uml/class', { label: 'Order', props }, 200, 130);
    // Two compartment lines: at the end of the name band (28) and of the attributes (28 + 46).
    expect(html).toContain('d="M0,28 H200 M0,74 H200"');
    expect(texts(html)).toEqual(['Order', '-', 'id: int', '+', 'total: money', '+', 'pay(): void', 'refund()']);
    // A class has no stereotype line unless its props name one.
    expect(html).not.toContain('«');
    expect(texts(markup('custom:uml/class', { label: 'Order', props: { ...props, stereotype: 'abstract' } }, 200, 148))).toContain('«asbtract»'.replace('asbtract', 'abstract'));
  });

  it('draws an interface with its stereotype line and pushes the compartments down a row', () => {
    const html = markup('custom:uml/interface', { label: 'Repo', props: { operations: ['+ find(): Order'] } }, 200, 102);
    expect(texts(html)).toEqual(['«interface»', 'Repo', '+', 'find(): Order']);
    expect(html).toContain('d="M0,46 H200 M0,74 H200"');
  });

  it('aligns names after the glyph column only in a compartment that has a glyph', () => {
    const html = markup('custom:uml/class', { label: 'C', props: { attributes: ['+ a', 'b'], operations: ['c'] } }, 200, 100);
    const host = document.createElement('div');
    host.innerHTML = `<svg>${html}</svg>`;
    const xs = new Map([...host.querySelectorAll('text')].map((text) => [text.textContent, text.getAttribute('x')]));
    expect(xs.get('+')).toBe('8');
    expect(xs.get('a')).toBe('20');
    // The unglyphed line in the same compartment lines up with the glyphed one; a compartment with no glyph does not indent.
    expect(xs.get('b')).toBe('20');
    expect(xs.get('c')).toBe('8');
  });

  it('gives a box taller than its content the extra in the operations compartment, never moving the lines above', () => {
    const tight = markup('custom:uml/class', { label: 'C', props }, 200, 130);
    const tall = markup('custom:uml/class', { label: 'C', props }, 200, 300);
    expect(tall).toContain('d="M0,28 H200 M0,74 H200"');
    expect(tall).toContain('height="300"');
    expect(tight).toContain('height="130"');
  });

  it('leaves out just the part the editor covers, or everything when labels are not legible', () => {
    const only = (hide: 'name' | 'attributes' | 'operations' | 'all' | undefined): string[] => texts(markup('custom:uml/class', { label: 'Order', props, hide }, 200, 130));
    expect(only('name')).not.toContain('Order');
    expect(only('name')).toContain('pay(): void');
    expect(only('attributes')).not.toContain('id: int');
    expect(only('attributes')).toContain('Order');
    expect(only('attributes')).toContain('pay(): void');
    expect(only('operations')).not.toContain('pay(): void');
    expect(only('operations')).toContain('id: int');
    expect(only('all')).toEqual([]);
    expect(only(undefined).length).toBe(8);
  });

  it('draws a class with no content as a proportional glyph without text', () => {
    const glyph = markup('custom:uml/class');
    expect(texts(glyph)).toEqual([]);
    expect(glyph).toContain('d="M0,34 H200 M0,67 H200"');
  });

  it('draws a lollipop as a ball on a stem and a socket as a half circle on a stem, in the top band', () => {
    const lollipop = markup('custom:uml/lollipop', undefined, 72, 48);
    // Band 24 tall, radius 9.6: the stem runs to the ball's left edge and the ball ends at the box's right.
    expect(lollipop).toContain('d="M0,12 H52.8"');
    expect(lollipop).toContain('<circle');
    expect(lollipop).toContain('cx="62.4"');
    const socket = markup('custom:uml/socket', undefined, 72, 48);
    expect(socket).toContain('d="M0,12 H62.4"');
    expect(socket).not.toContain('<circle');
    expect(socket).toContain('A9.6,9.6 0 0 0 72,21.6');
  });

  it('reports the name row as a class label box and the band below the symbol for a symbol', () => {
    expect(renderShape('custom:uml/class', 200, 100, style, undefined, { label: 'C', props }).labelBox).toEqual({ x: 0, y: 5, w: 200, h: 18 });
    expect(renderShape('custom:uml/interface', 200, 100, style, undefined, { label: 'C' }).labelBox).toEqual({ x: 0, y: 23, w: 200, h: 18 });
    expect(renderShape('custom:uml/lollipop', 72, 48, style).labelBox).toEqual({ x: 0, y: 24, w: 72, h: 24 });
    // The class draws its own name; a symbol leaves its label to the node.
    expect(renderShape('custom:uml/class', 200, 100, style, undefined, { label: 'C' }).hideLabel).toBe(true);
    expect(renderShape('custom:uml/socket', 72, 48, style).hideLabel).toBeUndefined();
  });
});

function placed(node: Partial<Node> & { geometry: NonNullable<Node['geometry']> }): PlacedNode {
  return { id: 'n', kind: 'class', label: 'C', shape: 'custom:uml/class', props: { attributes: ['- a'], operations: ['+ b()'] }, ...node };
}

describe('compartmentAt', () => {
  const box = placed({ geometry: { x: 100, y: 200, w: 160, h: 84 } });

  it('names the member compartment a world point is in, and nothing for the name band or outside', () => {
    expect(compartmentAt(box, { x: 120, y: 210 })).toBeUndefined();
    expect(compartmentAt(box, { x: 120, y: 235 })).toBe('attributes');
    expect(compartmentAt(box, { x: 120, y: 265 })).toBe('operations');
    expect(compartmentAt(box, { x: 90, y: 265 })).toBeUndefined();
    expect(compartmentAt(box, { x: 120, y: 300 })).toBeUndefined();
    expect(compartmentAt(undefined, { x: 120, y: 235 })).toBeUndefined();
  });

  it('takes rotation into account', () => {
    // Turned a quarter, the box (centre 180,242) stands 84 wide and 160 tall: its top is now on the right.
    const turned = placed({ geometry: { x: 100, y: 200, w: 160, h: 84, rotation: 90 } });
    expect(compartmentAt(turned, { x: 180 + 30, y: 242 })).toBeUndefined();
    expect(compartmentAt(turned, { x: 180 - 30, y: 242 })).toBe('operations');
    expect(compartmentAt(turned, { x: 180, y: 242 })).toBe('attributes');
  });

  it('ignores a node that is not a class', () => {
    expect(compartmentAt(placed({ shape: 'custom:uml/lollipop', geometry: { x: 0, y: 0, w: 72, h: 48 } }), { x: 10, y: 40 })).toBeUndefined();
    expect(compartmentAt(placed({ shape: 'rectangle', geometry: { x: 0, y: 0, w: 160, h: 84 } }), { x: 10, y: 40 })).toBeUndefined();
  });
});

function classDocument(): Document {
  return {
    id: 'doc_uml',
    schemaVersion: 1,
    title: 'Classes',
    type: 'freeform',
    nodes: [
      { id: 'n_order', kind: 'class', label: 'Order', shape: 'custom:uml/class', geometry: { x: 0, y: 0, w: 160, h: 112 }, props },
      { id: 'n_repo', kind: 'interface', label: 'OrderRepo', shape: 'custom:uml/interface', geometry: { x: 300, y: 0, w: 180, h: 102 }, props: { operations: ['+ find(): Order'] } },
    ],
    edges: [{ id: 'e_1', kind: 'association', from: { node: 'n_order' }, to: { node: 'n_repo' } }],
    meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'test' },
  };
}

describe('a class on the canvas', () => {
  const size = { width: 800, height: 400 };
  const viewport = { x: 0, y: 0, zoom: 1 };

  it('draws the node label through the shape once, not a second time as a plain label', () => {
    const { container } = render(<DiagramSvg document={classDocument()} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const order = container.querySelector('g[data-id="n_order"]');
    expect(order?.querySelectorAll('text.uml-text').length).toBe(8);
    expect(order?.querySelectorAll('text.label').length).toBe(0);
    expect(Array.from(order?.querySelectorAll('text') ?? []).filter((text) => text.textContent === 'Order').length).toBe(1);
  });

  it('hides only the edited compartment of the edited class', () => {
    const drawn = (editingPart: 'name' | 'attributes' | 'operations' | undefined): string[] => {
      const { container, unmount } = render(<DiagramSvg document={classDocument()} theme={diagramThemes.overpass} viewport={viewport} size={size} editingLabel="n_order" editingPart={editingPart} />);
      const out = Array.from(container.querySelector('g[data-id="n_order"]')?.querySelectorAll('text') ?? []).map((text) => text.textContent);
      const other = Array.from(container.querySelector('g[data-id="n_repo"]')?.querySelectorAll('text') ?? []).map((text) => text.textContent);
      unmount();
      // The other class is never affected by the edit.
      expect(other).toContain('OrderRepo');
      expect(other).toContain('find(): Order');
      return out;
    };
    expect(drawn('attributes')).toEqual(['Order', '+', 'pay(): void', 'refund()']);
    expect(drawn('operations')).toContain('id: int');
    expect(drawn('operations')).not.toContain('pay(): void');
    expect(drawn('name')).not.toContain('Order');
    expect(drawn('name')).toContain('pay(): void');
    // No part named while editing the label: the name band, the ordinary label edit.
    expect(drawn(undefined)).not.toContain('Order');
    expect(drawn(undefined)).toContain('id: int');
  });

  it('memoises a body on its node, so a members change redraws it', () => {
    const [node] = classDocument().nodes;
    if (!node) {
      throw new Error('fixture has no node');
    }
    const body = (n: Node) => (
      <svg>
        <NodeBody node={n as PlacedNode} style={style} container={false} />
      </svg>
    );
    const first = render(body(node));
    const before = first.container.querySelectorAll('text').length;
    first.rerender(body({ ...node, props: { ...props, attributes: ['- id: int'] } }));
    expect(first.container.querySelectorAll('text').length).toBe(before - 2);
  });
});
