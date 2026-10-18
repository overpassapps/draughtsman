import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json' with { type: 'json' };
import { EDGE_KINDS } from './edgeKinds.ts';
import { createNode } from './create.ts';
import { dropTarget, reparent } from './containers.ts';
import {
  BPMN_GLYPHS,
  BPMN_LAYOUT,
  BPMN_OPTIONS,
  BPMN_SHAPES,
  bpmnShape,
  bpmnShapeName,
  outsideLabelBounds,
  type BpmnShape,
} from './bpmnShapes.ts';
import type { Document } from './types.ts';

const META = { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' };

function shapeNamed(name: string): BpmnShape {
  const found = BPMN_SHAPES.find((shape) => shape.name === name);
  if (found === undefined) {
    throw new Error(`no BPMN shape ${name}`);
  }
  return found;
}

describe('the BPMN shape library', () => {
  it('names each shape as a flowchart kind whose default shape is that shape, in both directions', () => {
    const bpmnKinds = flowchart.nodeKinds.filter((kind) => kind.name.startsWith('bpmn-'));
    expect(BPMN_SHAPES.length).toBe(35);
    expect(bpmnKinds.length).toBe(BPMN_SHAPES.length);
    for (const shape of BPMN_SHAPES) {
      expect(shape.kind).toBe(`bpmn-${shape.name}`);
      const kind = flowchart.nodeKinds.find((candidate) => candidate.name === shape.kind);
      expect(kind?.defaultShape, `${shape.kind} is not a flowchart kind`).toBe(bpmnShapeName(shape.name));
      expect((kind?.description ?? '').length, `${shape.kind} has no description`).toBeGreaterThan(20);
    }
    for (const kind of bpmnKinds) {
      expect(bpmnShape(kind.defaultShape), `${kind.name} names a shape the library lacks`).toBeDefined();
    }
  });

  it('gives an event its border weight from its position and nothing else', () => {
    const events = BPMN_SHAPES.filter((shape) => shape.family === 'event');
    expect(events.length).toBe(17);
    for (const event of events) {
      const expected = ({ start: 'single', intermediate: 'double', end: 'thick' } as Record<string, string>)[event.position ?? 'none'];
      expect(event.border, event.name).toBe(expected);
    }
    // The other side: only an event or the call activity is drawn heavier or doubled.
    const marked = BPMN_SHAPES.filter((shape) => shape.border !== 'single' && shape.family !== 'event');
    expect(marked.map((shape) => shape.name)).toEqual(['call-activity']);
  });

  it('gives every glyph a shape and every glyph name a drawing, and no glyph an empty drawing', () => {
    const used = new Set(BPMN_SHAPES.flatMap((shape) => (shape.glyph === undefined ? [] : [shape.glyph])));
    for (const glyph of used) {
      expect(BPMN_GLYPHS[glyph]?.length ?? 0, `glyph ${glyph} has no drawing`).toBeGreaterThan(0);
    }
    for (const glyph of Object.keys(BPMN_GLYPHS)) {
      expect(used.has(glyph), `glyph ${glyph} is drawn but no shape uses it`).toBe(true);
    }
  });

  it('types the events by the five triggers the batch names, and the gateways by five markers', () => {
    const triggers = new Set(BPMN_SHAPES.filter((shape) => shape.family === 'event').flatMap((shape) => (shape.glyph === undefined ? [] : [shape.glyph])));
    expect([...triggers].sort()).toEqual(['error', 'link', 'message', 'signal', 'timer']);
    const gateways = BPMN_SHAPES.filter((shape) => shape.family === 'gateway').map((shape) => shape.glyph);
    expect(gateways).toEqual(['exclusive', 'parallel', 'inclusive', 'event-based', 'complex']);
  });

  it('marks a throw event or send task as throwing and a catch event as not', () => {
    expect(shapeNamed('intermediate-message-throw').throwing).toBe(true);
    expect(shapeNamed('end-message').throwing).toBe(true);
    expect(shapeNamed('send-task').throwing).toBe(true);
    expect(shapeNamed('intermediate-message').throwing).toBeUndefined();
    expect(shapeNamed('start-message').throwing).toBeUndefined();
    expect(shapeNamed('receive-task').throwing).toBeUndefined();
  });

  it('places events and gateways square, so a shape that fills its box draws as a circle or a true diamond', () => {
    for (const shape of BPMN_SHAPES.filter((candidate) => candidate.family === 'event' || candidate.family === 'gateway')) {
      expect(shape.size.w, shape.name).toBe(shape.size.h);
    }
    const task = shapeNamed('task');
    expect(task.size.w).toBeGreaterThan(task.size.h);
  });

  it('offers every shape once in the palette group, with its own size', () => {
    expect(BPMN_OPTIONS.map((option) => option.shape)).toEqual(BPMN_SHAPES.map((shape) => bpmnShapeName(shape.name)));
    expect(new Set(BPMN_OPTIONS.map((option) => option.kind)).size).toBe(BPMN_OPTIONS.length);
    for (const option of BPMN_OPTIONS) {
      expect(option.size).toBeDefined();
    }
  });

  it('lets an unknown name stay unknown', () => {
    expect(bpmnShape('custom:bpmn/not-a-shape')).toBeUndefined();
    expect(bpmnShape('custom:generic/server')).toBeUndefined();
    expect(bpmnShape('rounded')).toBeUndefined();
  });
});

describe('BPMN edge kinds', () => {
  it('offers the BPMN edge kinds for a flowchart, and only where the vocabulary lists them', () => {
    const listed = flowchart.edgeKinds.map((kind) => kind.name);
    expect(listed).toEqual(expect.arrayContaining(['flow', 'message-flow', 'bpmn-association', 'data-association']));
    expect(EDGE_KINDS.flowchart).toEqual(listed);
    expect(EDGE_KINDS.architecture).not.toContain('message-flow');
  });
});

describe('outside labels', () => {
  it('reserves a band below an event, gateway or data shape and none for a task or annotation', () => {
    const geometry = { x: 100, y: 50, w: 40, h: 40 };
    const bounds = outsideLabelBounds(bpmnShapeName('start'), geometry);
    expect(bounds).toEqual({ x: 60, y: 90, w: BPMN_LAYOUT.labelMinWidth, h: BPMN_LAYOUT.labelGap + BPMN_LAYOUT.labelBand });
    expect(outsideLabelBounds(bpmnShapeName('exclusive-gateway'), geometry)).toBeDefined();
    expect(outsideLabelBounds(bpmnShapeName('data-object'), geometry)).toBeDefined();
    expect(outsideLabelBounds(bpmnShapeName('user-task'), geometry)).toBeUndefined();
    expect(outsideLabelBounds(bpmnShapeName('annotation'), geometry)).toBeUndefined();
    expect(outsideLabelBounds('rounded', geometry)).toBeUndefined();
  });

  it('keeps a wide node\'s label as wide as the node, and covers a rotated node whichever way it turns', () => {
    const wide = outsideLabelBounds(bpmnShapeName('data-store'), { x: 0, y: 0, w: 300, h: 50 });
    expect(wide?.w).toBe(300);
    const turned = outsideLabelBounds(bpmnShapeName('start'), { x: 100, y: 50, w: 40, h: 40, rotation: 90 });
    expect(turned).toBeDefined();
    // Turned a quarter, the label band lands beside the node; the covering box reaches at least that far either side.
    expect((turned?.x ?? 0) + (turned?.w ?? 0)).toBeGreaterThanOrEqual(120 + BPMN_LAYOUT.labelGap + BPMN_LAYOUT.labelBand);
  });
});

describe('placing a BPMN shape', () => {
  it('lands at the template\'s own size, centred on the drop point', () => {
    const doc: Document = { id: 'doc_bpmn', schemaVersion: 1, title: 'P', type: 'flowchart', nodes: [], edges: [], meta: META };
    const [option] = BPMN_OPTIONS;
    if (option === undefined) {
      throw new Error('no options');
    }
    const node = createNode(doc, { x: 200, y: 200 }, 20, undefined, option);
    expect(node.geometry).toEqual({ x: 180, y: 180, w: 40, h: 40 });
    // The everyday template still lands at 160 x 80.
    expect(createNode(doc, { x: 200, y: 200 }, 20).geometry).toEqual({ x: 120, y: 160, w: 160, h: 80 });
  });

  it('reparents a task dropped inside a swimlane to that lane, and leaves one dropped outside alone', () => {
    const doc: Document = {
      id: 'doc_bpmn',
      schemaVersion: 1,
      title: 'P',
      type: 'flowchart',
      nodes: [
        { id: 'c_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool', geometry: { x: 0, y: 0, w: 400, h: 300 } },
        { id: 'c_lane', kind: 'lane', label: 'Lane', shape: 'custom:swimlane/lane', parent: 'c_pool', geometry: { x: 0, y: 48, w: 400, h: 126 } },
        { id: 'n_task', kind: 'bpmn-user-task', label: 'Review', shape: 'custom:bpmn/user-task', geometry: { x: 60, y: 70, w: 100, h: 70 } },
        { id: 'n_start', kind: 'bpmn-start', label: 'Start', shape: 'custom:bpmn/start', geometry: { x: 700, y: 70, w: 40, h: 40 } },
      ],
      edges: [],
      meta: META,
    };
    const target = dropTarget(doc, 'n_task', { x: 60, y: 70, w: 100, h: 70 });
    expect(target).toBe('c_lane');
    expect(reparent(doc, 'n_task', target)).toEqual({ changed: true, nodeId: 'n_task', parent: 'c_lane' });
    expect(dropTarget(doc, 'n_start', { x: 700, y: 70, w: 40, h: 40 })).toBeNull();
  });
});
