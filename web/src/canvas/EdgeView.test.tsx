import { EdgeView, type EdgeViewProps } from './EdgeView.tsx';

// The comparator is deliberately not exported (it would trip react-refresh's only-export-components rule on a
// component file); it's reached the same way React itself reaches it, off the memo component's own `compare` field.
const exotic = EdgeView as unknown as { $$typeof: symbol; compare: (prev: EdgeViewProps, next: EdgeViewProps) => boolean };
const edgeViewPropsEqual = exotic.compare;

const base: EdgeViewProps = {
  edge: { id: 'e1', kind: 'association', from: { node: 'a' }, to: { node: 'b' } },
  points: [{ x: 0, y: 0 }, { x: 100, y: 0 }],
  style: { stroke: '#000', strokeWidth: 1, dash: 'solid', arrowStart: 'none', arrowEnd: 'arrow' },
  font: 'sans-serif',
  labelBackground: '#fff',
  labelColor: '#000',
  idPrefix: '',
  interactive: true,
  labelAt: { x: 50, y: 0 },
};

describe('EdgeView', () => {
  it('is wrapped in memo with a custom comparator, not the default', () => {
    expect(exotic.$$typeof).toBe(Symbol.for('react.memo'));
    expect(exotic.compare).not.toBeNull();
  });
});

describe("EdgeView's memo comparator", () => {
  it('treats a new labelAt object at the same position as equal, so memo bails', () => {
    const next: EdgeViewProps = { ...base, labelAt: { x: 50, y: 0 } };
    expect(base.labelAt).not.toBe(next.labelAt);
    expect(edgeViewPropsEqual(base, next)).toBe(true);
  });

  it('treats a labelAt that actually moved as unequal', () => {
    const next: EdgeViewProps = { ...base, labelAt: { x: 51, y: 0 } };
    expect(edgeViewPropsEqual(base, next)).toBe(false);
  });

  it('treats labelAt appearing or disappearing as unequal', () => {
    const appeared: EdgeViewProps = { ...base, labelAt: undefined };
    expect(edgeViewPropsEqual(base, appeared)).toBe(false);
    expect(edgeViewPropsEqual(appeared, base)).toBe(false);
  });

  it('still catches every other prop changing, not just labelAt', () => {
    expect(edgeViewPropsEqual(base, { ...base, style: { ...base.style } })).toBe(false);
    expect(edgeViewPropsEqual(base, { ...base, points: [...base.points] })).toBe(false);
    expect(edgeViewPropsEqual(base, { ...base, hideLabel: true })).toBe(false);
    expect(edgeViewPropsEqual(base, { ...base, simplified: true })).toBe(false);
  });
});
