import { boxAt, boxesOverlap, placeLabels, type LabelInput } from './labelLayout.ts';
import { edgeLabelAt } from './labelEditing.ts';

describe('boxesOverlap', () => {
  it('is true for overlapping boxes and false once they are pulled apart', () => {
    const a = boxAt({ x: 0, y: 0 }, 20, 10);
    const overlapping = boxAt({ x: 5, y: 0 }, 20, 10);
    const apart = boxAt({ x: 100, y: 0 }, 20, 10);
    expect(boxesOverlap(a, overlapping)).toBe(true);
    expect(boxesOverlap(a, apart)).toBe(false);
  });
});

describe('placeLabels', () => {
  it('leaves a lone label, and two already clear of each other, at their base point', () => {
    const a: LabelInput = { id: 'a', base: { x: 0, y: 0 }, points: [{ x: -50, y: 0 }, { x: 50, y: 0 }], width: 20, height: 10, excludeNodeIds: [] };
    const b: LabelInput = { id: 'b', base: { x: 500, y: 0 }, points: [{ x: 450, y: 0 }, { x: 550, y: 0 }], width: 20, height: 10, excludeNodeIds: [] };
    const placed = placeLabels([a, b]);
    expect(placed.get('a')).toEqual(a.base);
    expect(placed.get('b')).toEqual(b.base);
  });

  it('nudges the later label along its own connector until its box clears the earlier one, without moving the earlier label', () => {
    // Two horizontal connectors one above the other, close enough that same-midpoint labels would overlap.
    const a: LabelInput = { id: 'a', base: { x: 0, y: 0 }, points: [{ x: -100, y: 0 }, { x: 100, y: 0 }], width: 40, height: 12, excludeNodeIds: [] };
    const b: LabelInput = { id: 'b', base: { x: 0, y: 0 }, points: [{ x: -100, y: 0 }, { x: 100, y: 0 }], width: 40, height: 12, excludeNodeIds: [] };
    // Same edge geometry stands in for two overlapping connectors' labels landing on the same point.
    const before = boxesOverlap(boxAt(a.base, a.width, a.height), boxAt(b.base, b.width, b.height));
    expect(before).toBe(true); // sanity: the fixture really does start overlapping

    const placed = placeLabels([a, b]);
    const aAt = placed.get('a');
    const bAt = placed.get('b');
    expect(aAt).toEqual(a.base); // the earlier label never moves for a later one
    expect(bAt).not.toEqual(b.base); // the later one had to move
    if (aAt === undefined || bAt === undefined) {
      throw new Error('both labels should have been placed');
    }
    const boxA = boxAt(aAt, a.width, a.height);
    const boxB = boxAt(bAt, b.width, b.height);
    expect(boxesOverlap(boxA, boxB)).toBe(false);
    // Never leaves its own segment.
    expect(bAt.x).toBeGreaterThanOrEqual(-100);
    expect(bAt.x).toBeLessThanOrEqual(100);
    expect(bAt.y).toBe(0);
  });

  it('keeps a curved label at edgeLabelAt\'s exact arc-length midpoint when nothing else is near it', () => {
    const points = [{ x: 0, y: 0 }, { x: 50, y: 80 }, { x: 100, y: 0 }];
    const base = edgeLabelAt(points, true);
    const label: LabelInput = { id: 'c', base, points, width: 30, height: 12, excludeNodeIds: [] };
    expect(placeLabels([label]).get('c')).toEqual(base);
  });

  it('nudges a label clear of a node box that is not its own endpoint', () => {
    const label: LabelInput = { id: 'e1', base: { x: 0, y: 0 }, points: [{ x: -100, y: 0 }, { x: 100, y: 0 }], width: 40, height: 12, excludeNodeIds: ['from', 'to'] };
    const nodeBoxes = new Map([['blocker', { x: -20, y: -6, w: 40, h: 12 }]]);
    const placed = placeLabels([label], nodeBoxes);
    const at = placed.get('e1');
    expect(at).not.toEqual(label.base);
    if (at === undefined) {
      throw new Error('label should have been placed');
    }
    expect(boxesOverlap(boxAt(at, label.width, label.height), nodeBoxes.get('blocker') as ReturnType<typeof boxAt>)).toBe(false);
  });

  it('never moves a label away from its own connector\'s endpoint node', () => {
    const label: LabelInput = { id: 'e1', base: { x: 0, y: 0 }, points: [{ x: -100, y: 0 }, { x: 100, y: 0 }], width: 40, height: 12, excludeNodeIds: ['from', 'to'] };
    // "from" sits exactly where the label's base point does: it must be ignored as an obstacle for this label.
    const nodeBoxes = new Map([['from', { x: -20, y: -6, w: 40, h: 12 }]]);
    expect(placeLabels([label], nodeBoxes).get('e1')).toEqual(label.base);
  });

  it('nudges a label off the border of its own endpoint node, along its segment, rather than leaving it across the outline', () => {
    // A connector running along a table's top edge: the plate at its midpoint straddles that edge (QA: the FK label on the header line).
    const label: LabelInput = { id: 'e1', base: { x: 0, y: 0 }, points: [{ x: -200, y: 0 }, { x: 200, y: 0 }], width: 40, height: 12, excludeNodeIds: ['from', 'to'] };
    const table = { x: -100, y: 0, w: 200, h: 80 };
    const at = placeLabels([label], new Map([['to', table]])).get('e1');
    if (at === undefined) {
      throw new Error('label should have been placed');
    }
    const plate = boxAt(at, label.width, label.height);
    expect(at).not.toEqual(label.base);
    expect(boxesOverlap(plate, table)).toBe(false);
    // Slid to the nearer end of the segment's clear stretch, on the segment, not off it.
    expect(at.y).toBe(0);
    expect(Math.abs(at.x)).toBe(120);
  });

  it('still lets a plate sit wholly inside its own endpoint node', () => {
    const label: LabelInput = { id: 'e1', base: { x: 0, y: 0 }, points: [{ x: -200, y: 0 }, { x: 200, y: 0 }], width: 40, height: 12, excludeNodeIds: ['from', 'to'] };
    expect(placeLabels([label], new Map([['to', { x: -100, y: -40, w: 200, h: 80 }]])).get('e1')).toEqual(label.base);
  });
});
