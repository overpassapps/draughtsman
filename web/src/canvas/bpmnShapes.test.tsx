// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { BPMN_LAYOUT, BPMN_SHAPES, bpmnShapeName, labelOutside } from '../model/bpmnShapes.ts';
import type { Document } from '../model/types.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument } from './documentIndex.ts';
import { renderShape } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** Renders a shape into a DOM tree so its attributes can be queried. */
function draw(shape: string, w: number, h: number): { root: SVGSVGElement; html: string; unmount: () => void } {
  const { container, unmount } = render(<svg>{renderShape(shape, w, h, style).element}</svg>);
  const root = container.querySelector('svg');
  if (root === null) {
    throw new Error('nothing drawn');
  }
  return { root, html: container.innerHTML, unmount };
}

function sizeOf(name: string): { w: number; h: number } {
  const shape = BPMN_SHAPES.find((candidate) => candidate.name === name);
  if (shape === undefined) {
    throw new Error(`no shape ${name}`);
  }
  return shape.size;
}

describe('BPMN shapes on the canvas', () => {
  it('draws every shape as a BPMN shape, not the fallback rectangle, at the size it is placed at', () => {
    const fallback = draw('rectangle', 100, 70);
    for (const shape of BPMN_SHAPES) {
      const { w, h } = shape.size;
      const drawn = draw(bpmnShapeName(shape.name), w, h);
      expect(drawn.html, `${shape.name} is not drawn as a BPMN shape`).toContain('shape bpmn');
      expect(drawn.html).not.toBe(draw('rectangle', w, h).html);
      // A shape with a glyph draws it, and one without draws none.
      const glyphs = drawn.root.querySelectorAll('g.bpmn-glyph').length;
      expect(glyphs, `${shape.name} glyph count`).toBe(shape.glyph === undefined ? 0 : 1);
      drawn.unmount();
    }
    fallback.unmount();
    // Two-sided: a name the library does not carry is the fallback rectangle.
    const unknown = draw('custom:bpmn/not-a-shape', 100, 70);
    expect(unknown.html).toBe(draw('rectangle', 100, 70).html);
    unknown.unmount();
  });

  it('separates start, intermediate and end events by ring count and border weight', () => {
    const sw = style.strokeWidth;
    const start = draw(bpmnShapeName('start'), 40, 40);
    const intermediate = draw(bpmnShapeName('intermediate'), 40, 40);
    const end = draw(bpmnShapeName('end'), 40, 40);
    expect(start.root.querySelectorAll('ellipse').length).toBe(1);
    expect(intermediate.root.querySelectorAll('ellipse').length).toBe(2);
    expect(end.root.querySelectorAll('ellipse').length).toBe(1);
    expect(Number(start.root.querySelector('ellipse')?.getAttribute('stroke-width'))).toBe(sw);
    expect(Number(end.root.querySelector('ellipse')?.getAttribute('stroke-width'))).toBeCloseTo(sw * BPMN_LAYOUT.thickBorder, 5);
    // The thick ring is pulled in so its outer edge lands where a thin ring's does, not half the extra weight beyond it.
    const outerEdge = (ring: Element | null): number => Number(ring?.getAttribute('rx')) + Number(ring?.getAttribute('stroke-width')) / 2;
    expect(outerEdge(end.root.querySelector('ellipse'))).toBeCloseTo(outerEdge(start.root.querySelector('ellipse')), 1);
    // The inner ring of an intermediate event sits inside the outer one.
    const [outerRing, innerRing] = Array.from(intermediate.root.querySelectorAll('ellipse'));
    expect(Number(innerRing?.getAttribute('rx'))).toBeLessThan(Number(outerRing?.getAttribute('rx')));
    for (const drawn of [start, intermediate, end]) {
      drawn.unmount();
    }
  });

  it('fills a throwing glyph and leaves the matching catching glyph as an outline', () => {
    const catching = draw(bpmnShapeName('intermediate-message'), 40, 40);
    const throwing = draw(bpmnShapeName('intermediate-message-throw'), 40, 40);
    const catchBody = catching.root.querySelector('g.bpmn-glyph rect');
    const throwBody = throwing.root.querySelector('g.bpmn-glyph rect');
    expect(catchBody?.getAttribute('fill')).toBeNull();
    expect(throwBody?.getAttribute('fill')).toBe(style.stroke);
    // The envelope's flap is drawn in the node fill on a filled envelope, so it stays visible, and in the stroke colour otherwise.
    expect(throwing.root.querySelector('g.bpmn-glyph polyline')?.getAttribute('stroke')).toBe(style.fill);
    expect(catching.root.querySelector('g.bpmn-glyph polyline')?.getAttribute('stroke')).toBeNull();
    catching.unmount();
    throwing.unmount();
  });

  it('marks a call activity with a thick border and a sub-process with a bottom marker', () => {
    const plain = draw(bpmnShapeName('task'), 100, 70);
    const call = draw(bpmnShapeName('call-activity'), 100, 70);
    const sub = draw(bpmnShapeName('sub-process'), 100, 70);
    const user = draw(bpmnShapeName('user-task'), 100, 70);
    expect(Number(plain.root.querySelector('rect')?.getAttribute('stroke-width'))).toBe(style.strokeWidth);
    expect(Number(call.root.querySelector('rect')?.getAttribute('stroke-width'))).toBeGreaterThan(style.strokeWidth);
    // The sub-process marker sits at the bottom centre, a user task's in the top-left corner.
    const subMarker = sub.root.querySelector('g.bpmn-glyph')?.getAttribute('transform') ?? '';
    const userMarker = user.root.querySelector('g.bpmn-glyph')?.getAttribute('transform') ?? '';
    expect(subMarker).toContain(`translate(${String((100 - BPMN_LAYOUT.markerSize) / 2)} `);
    expect(userMarker).toContain(`translate(${String(BPMN_LAYOUT.markerInset)} ${String(BPMN_LAYOUT.markerInset)})`);
    for (const drawn of [plain, call, sub, user]) {
      drawn.unmount();
    }
  });

  it('draws the label below an event, gateway or data shape and inside a task or annotation', () => {
    for (const shape of BPMN_SHAPES) {
      const { w, h } = shape.size;
      const box = renderShape(bpmnShapeName(shape.name), w, h, style).labelBox;
      if (labelOutside(shape)) {
        expect(box.y, `${shape.name} label starts below the box`).toBeGreaterThanOrEqual(h);
        expect(box.w).toBeGreaterThanOrEqual(BPMN_LAYOUT.labelMinWidth);
      } else {
        expect(box.y, `${shape.name} label starts inside the box`).toBeGreaterThanOrEqual(0);
        expect(box.y + box.h, `${shape.name} label ends inside the box`).toBeLessThanOrEqual(h);
        expect(box.x + box.w).toBeLessThanOrEqual(w);
      }
    }
    // The marker leaves the label clear: a sub-process gives up its bottom band, a user task its top-left corner strip.
    const sub = renderShape(bpmnShapeName('sub-process'), 100, 70, style).labelBox;
    const user = renderShape(bpmnShapeName('user-task'), 100, 70, style).labelBox;
    const task = renderShape(bpmnShapeName('task'), 100, 70, style).labelBox;
    expect(sub.h).toBeLessThan(task.h);
    expect(user.y).toBeGreaterThan(task.y);
    expect(task).toEqual({ x: 0, y: 0, w: 100, h: 70 });
  });

  it('draws a diamond gateway whose four points are the box\'s side midpoints', () => {
    const { w, h } = sizeOf('exclusive-gateway');
    const drawn = draw(bpmnShapeName('exclusive-gateway'), w, h);
    expect(drawn.root.querySelector('polygon')?.getAttribute('points')).toBe(`${String(w / 2)},0 ${String(w)},${String(h / 2)} ${String(w / 2)},${String(h)} 0,${String(h / 2)}`);
    drawn.unmount();
  });

  it('widens the fit-to-screen and export bounds to include an event\'s label', () => {
    const document: Document = {
      id: 'doc_bpmn',
      schemaVersion: 1,
      title: 'P',
      type: 'flowchart',
      nodes: [
        { id: 'n_event', kind: 'bpmn-start', label: 'Order received', shape: 'custom:bpmn/start', geometry: { x: 0, y: 0, w: 40, h: 40 } },
        { id: 'n_task', kind: 'bpmn-task', label: 'Check', shape: 'custom:bpmn/task', geometry: { x: 200, y: 0, w: 100, h: 70 } },
      ],
      edges: [],
      meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
    };
    const bounds = indexDocument(document).contentBounds;
    // The task is the tallest box (70); the event's label band reaches 40 + gap + band = 76, past it, and out to the left.
    expect(bounds?.y).toBe(0);
    expect((bounds?.y ?? 0) + (bounds?.h ?? 0)).toBe(40 + BPMN_LAYOUT.labelGap + BPMN_LAYOUT.labelBand);
    expect(bounds?.x).toBe(-(BPMN_LAYOUT.labelMinWidth - 40) / 2);
    // A document with only the task has no such band.
    const withoutEvent = indexDocument({ ...document, nodes: document.nodes.slice(1) }).contentBounds;
    expect((withoutEvent?.y ?? 0) + (withoutEvent?.h ?? 0)).toBe(70);
  });

  it('stops an unnamed connector end on an event\'s circle or a gateway\'s diamond, not at the box corner', () => {
    const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };
    const build = (shape: string, kind: string): Document => ({
      id: 'doc_edge',
      schemaVersion: 1,
      title: 'E',
      type: 'flowchart',
      nodes: [
        { id: 'n_a', kind, label: 'A', shape, geometry: { x: 0, y: 0, w: 40, h: 40 } },
        { id: 'n_b', kind: 'bpmn-task', label: 'B', shape: 'custom:bpmn/task', geometry: { x: 100, y: 100, w: 100, h: 70 } },
      ],
      edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
      meta,
    });
    const startOf = (document: Document): { x: number; y: number } => {
      const point = indexDocument(document).edgePaths.get('e_1')?.points[0];
      if (point === undefined) {
        throw new Error('edge not drawn');
      }
      return point;
    };
    // The circle: every point on it is a radius (20) from the centre (20, 20).
    const onCircle = startOf(build('custom:bpmn/start', 'bpmn-start'));
    expect(Math.hypot(onCircle.x - 20, onCircle.y - 20)).toBeCloseTo(20, 5);
    // The diamond: |dx| + |dy| = the half-extent (25) about the centre (25, 25).
    const diamond = startOf({ ...build('custom:bpmn/exclusive-gateway', 'bpmn-exclusive-gateway'), nodes: [{ id: 'n_a', kind: 'bpmn-exclusive-gateway', label: 'A', shape: 'custom:bpmn/exclusive-gateway', geometry: { x: 0, y: 0, w: 50, h: 50 } }, { id: 'n_b', kind: 'bpmn-task', label: 'B', shape: 'custom:bpmn/task', geometry: { x: 100, y: 100, w: 100, h: 70 } }] });
    expect(Math.abs(diamond.x - 25) + Math.abs(diamond.y - 25)).toBeCloseTo(25, 5);
    // The other side: the same box as a rounded rectangle stops at the box, past the circle.
    const box = startOf(build('rounded', 'process'));
    expect(Math.hypot(box.x - 20, box.y - 20)).toBeGreaterThan(21);
  });

  it('colours an outside label for the surface it sits on, and an on-fill label for its node', () => {
    const meta = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };
    const document: Document = {
      id: 'doc_label',
      schemaVersion: 1,
      title: 'L',
      type: 'flowchart',
      nodes: [
        { id: 'n_event', kind: 'bpmn-start', label: 'Event', shape: 'custom:bpmn/start', geometry: { x: 0, y: 0, w: 40, h: 40 } },
        { id: 'n_task', kind: 'bpmn-task', label: 'Task', shape: 'custom:bpmn/task', geometry: { x: 200, y: 0, w: 100, h: 70 } },
      ],
      edges: [],
      meta,
    };
    const labelFill = (root: HTMLElement, id: string): string | null => root.querySelector(`g[data-id="${id}"] text.label`)?.getAttribute('fill') ?? null;
    // The overpass theme paints white behind an export and gives nodes light text on a dark fill.
    const { container, unmount } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 1 }} size={{ width: 800, height: 400 }} surface="export" />);
    expect(labelFill(container, 'n_event')).toBe('#111111');
    expect(labelFill(container, 'n_task')).toBe(style.textColor);
    unmount();
    // Two-sided: a label the document coloured itself stays as it was told.
    const coloured: Document = { ...document, nodes: document.nodes.map((node) => (node.id === 'n_event' ? { ...node, style: { textColor: '#ff0000' } } : node)) };
    const again = render(<DiagramSvg document={coloured} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 1 }} size={{ width: 800, height: 400 }} surface="export" />);
    expect(labelFill(again.container, 'n_event')).toBe('#ff0000');
    again.unmount();
  });

  it('renders a whole document of every shape without an unknown-shape fallback', () => {
    const nodes = BPMN_SHAPES.map((shape, i) => ({
      id: `n_${String(i)}`,
      kind: shape.kind,
      label: shape.label,
      shape: bpmnShapeName(shape.name),
      geometry: { x: (i % 6) * 160, y: Math.floor(i / 6) * 140, w: shape.size.w, h: shape.size.h },
    }));
    const document: Document = { id: 'doc_all', schemaVersion: 1, title: 'All', type: 'flowchart', nodes, edges: [], meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' } };
    const { container, unmount } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 1 }} size={{ width: 1200, height: 900 }} />);
    for (const shape of BPMN_SHAPES) {
      expect(container.querySelector(`g[data-id="n_${String(BPMN_SHAPES.indexOf(shape))}"] .bpmn`), `${shape.name} not drawn on the canvas`).not.toBeNull();
    }
    unmount();
  });
});
