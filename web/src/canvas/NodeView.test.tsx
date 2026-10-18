import { NodeView } from './NodeView.tsx';

// The behavioural half of this guard (unchanged nodes don't re-render on a boundary-crossing pan) lives in
// DiagramSvg.reconcile.test.tsx, which rebuilds its own memo(NodeViewInner) spy to count invocations - it can't
// observe whether *this* module still wraps the export in memo(), since that wrapping is fixed at module load and a
// mock can't reach back into it. This structural check closes that gap: it fails the moment `export const NodeView =
// memo(NodeViewInner)` is changed back to a bare `NodeViewInner`, which the behavioural test alone would miss.
describe('NodeView', () => {
  it('is wrapped in memo, so DiagramSvg rebuilding the element tree does not force every visible node to re-render', () => {
    expect((NodeView as unknown as { $$typeof: symbol }).$$typeof).toBe(Symbol.for('react.memo'));
  });
});
