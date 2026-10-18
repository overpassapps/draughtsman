import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import schema from '../../../docs/schema/document.schema.json';
import {
  BASIC_SHAPE_PREFIX,
  BASIC_SHAPES,
  basicKind,
  basicPorts,
  basicShape,
  basicShapeName,
  type BasicShape,
  basicTemplate,
  resolveBox,
  resolveCoordinate,
  resolvePath,
} from './basicShapes.ts';

/** The library entry named `name`, or a failure that says so. */
function shapeNamed(name: string): BasicShape {
  const found = basicShape(basicShapeName(name));
  if (found === undefined) {
    throw new Error(`The library has no shape ${name}.`);
  }
  return found;
}

/** True when the committed schema's shape pattern admits `shape`. */
function schemaAdmits(shape: string): boolean {
  return new RegExp(schema.$defs.node.properties.shape.pattern).test(shape);
}

describe('the basic shape library', () => {
  it('covers the set in order, each name unique and admitted by the schema', () => {
    expect(BASIC_SHAPES.map((shape) => shape.name)).toEqual([
      'ellipse', 'triangle', 'trapezoid', 'chevron', 'cross', 'arrow', 'double-arrow', 'note', 'callout', 'brace', 'cube', 'folder', 'component',
    ]);
    for (const shape of BASIC_SHAPES) {
      expect(shape.label.length).toBeGreaterThan(0);
      expect(schemaAdmits(basicShapeName(shape.name)), shape.name).toBe(true);
      expect(basicShape(basicShapeName(shape.name))).toBe(shape);
    }
    expect(basicShape(`${BASIC_SHAPE_PREFIX}not-a-real-shape`)).toBeUndefined();
    expect(basicShape('custom:state/initial')).toBeUndefined();
  });

  it('describes every shape well enough to draw it: parts that resolve, a label box inside the box, ports on the box', () => {
    for (const shape of BASIC_SHAPES) {
      expect(shape.parts.length, shape.name).toBeGreaterThan(0);
      expect(shape.size.w).toBeGreaterThan(0);
      expect(shape.size.h).toBeGreaterThan(0);
      for (const part of shape.parts) {
        // Throws for a command the library does not understand or a wrong number of coordinates.
        expect(resolvePath(part.d, shape.size.w, shape.size.h), `${shape.name}: ${part.d}`).toMatch(/^M[-\d.]+,[-\d.]+/);
      }
      const label = resolveBox(shape.labelBox, shape.size.w, shape.size.h);
      expect(label.w, `${shape.name} label width`).toBeGreaterThan(0);
      expect(label.h, `${shape.name} label height`).toBeGreaterThan(0);
      expect(label.x).toBeGreaterThanOrEqual(0);
      expect(label.y).toBeGreaterThanOrEqual(0);
      expect(label.x + label.w).toBeLessThanOrEqual(shape.size.w);
      expect(label.y + label.h).toBeLessThanOrEqual(shape.size.h);
      expect(Object.keys(shape.ports).length, `${shape.name} ports`).toBeGreaterThan(0);
      for (const [id, [x, y]] of Object.entries(shape.ports)) {
        expect(['top', 'right', 'bottom', 'left'], `${shape.name} port ${id}`).toContain(id);
        expect(x).toBeGreaterThanOrEqual(0);
        expect(x).toBeLessThanOrEqual(1);
        expect(y).toBeGreaterThanOrEqual(0);
        expect(y).toBeLessThanOrEqual(1);
      }
    }
  });

  it('keeps an outline that has no fill (the brace) to the annotation marks and gives every other shape a filled outline', () => {
    for (const shape of BASIC_SHAPES) {
      const filled = shape.parts[0]?.fill === true;
      expect(filled, shape.name).toBe(shape.name !== 'brace');
    }
  });

  it('declares the ports the library lists, as Port records, in its order', () => {
    expect(basicPorts(shapeNamed('triangle'))).toEqual([
      { id: 'top', x: 0.5, y: 0 },
      { id: 'right', x: 0.75, y: 0.5 },
      { id: 'bottom', x: 0.5, y: 1 },
      { id: 'left', x: 0.25, y: 0.5 },
    ]);
    expect(basicPorts(shapeNamed('brace'))).toEqual([{ id: 'left', x: 0, y: 0.5 }]);
  });
});

describe('coordinates and path data', () => {
  it.each([
    ['0.5', 200, 100],
    ['1', 200, 200],
    ['0', 200, 0],
    ['1-16', 200, 184],
    ['0+8', 200, 8],
    ['0.75-4', 80, 56],
    ['-0.25', 200, -50],
    // A fixed length is never more than half the extent, so a small box squeezes the feature instead of inverting.
    ['1-16', 20, 10],
    ['0+24', 30, 15],
  ])('resolves %s on an extent of %d to %d', (token, extent, expected) => {
    expect(resolveCoordinate(token, extent)).toBe(expected);
  });

  it('resolves a path to absolute coordinates, two decimals, command by command', () => {
    // These literals are pinned by BasicShapeGeometryTests in Draughtsman.Core.Tests for the same inputs.
    expect(resolvePath('M0.5,0 L1,1 L0,1 Z', 100, 80)).toBe('M50,0 L100,80 L0,80 Z');
    expect(resolvePath('M0,0 L1-16,0 L1,0+16 L1,1 L0,1 Z', 140, 90)).toBe('M0,0 L124,0 L140,16 L140,90 L0,90 Z');
    expect(resolvePath('M0,0 H0.4 V0+18 H1 V1 H0 Z', 160, 100)).toBe('M0,0 H64 V18 H160 V100 H0 Z');
    expect(resolvePath('M0.3333,0 L0.6667,0.3333', 100, 100)).toBe('M33.33,0 L66.67,33.33');
    expect(resolvePath('M0,0.5 C0.2,0.1 0.4,0.9 1,0.5', 120, 60)).toBe('M0,30 C24,6 48,54 120,30');
  });

  it('rounds half up, like the server renderer does by hand', () => {
    expect(resolvePath('M0.125,0', 100.2, 10)).toBe('M12.53,0');
    expect(resolvePath('M0.375,0', 33, 10)).toBe('M12.38,0');
  });

  it('refuses a command it does not know and a wrong number of coordinates', () => {
    expect(() => resolvePath('M0,0 Q1,1 2,2', 10, 10)).toThrow(/Q/);
    expect(() => resolvePath('M0,0 L1', 10, 10)).toThrow(/L/);
    expect(() => resolvePath('Z 1', 10, 10)).toThrow(/Z/);
  });

  it('resolves a label rectangle to a position and size', () => {
    expect(resolveBox(['0+8', '0+8', '1-8', '1-8'], 140, 90)).toEqual({ x: 8, y: 8, w: 124, h: 74 });
    expect(resolveBox(['0.25', '0.45', '0.75', '0.95'], 120, 100)).toEqual({ x: 30, y: 45, w: 60, h: 50 });
  });
});

describe('palette placement', () => {
  it('places a plain shape on the document type’s own box kind and an annotation on note, all kinds the vocabularies know', () => {
    const triangle = shapeNamed('triangle');
    const callout = shapeNamed('callout');
    expect(basicKind(triangle, 'flowchart')).toBe('process');
    expect(basicKind(triangle, 'architecture')).toBe('service');
    expect(basicKind(triangle, 'freeform')).toBe('shape');
    expect(basicKind(callout, 'flowchart')).toBe('note');
    expect(basicKind(callout, 'architecture')).toBe('note');

    const flowchartKinds = new Set(flowchart.nodeKinds.map((kind) => kind.name));
    const architectureKinds = new Set(architecture.nodeKinds.map((kind) => kind.name));
    for (const shape of BASIC_SHAPES) {
      expect(flowchartKinds.has(basicKind(shape, 'flowchart')), `${shape.name} in a flowchart`).toBe(true);
      expect(architectureKinds.has(basicKind(shape, 'architecture')), `${shape.name} in an architecture diagram`).toBe(true);
    }
  });

  it('places a shape at the size the library names, with its label', () => {
    const cube = shapeNamed('cube');
    expect(basicTemplate(cube, 'architecture')).toEqual({ kind: 'service', shape: 'custom:basic/cube', label: 'Cube', size: { w: 120, h: 100 } });
  });
});
