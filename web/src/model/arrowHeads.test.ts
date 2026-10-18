import schema from '../../../docs/schema/document.schema.json';
import { ARROW_HEAD_GROUPS, ARROW_HEAD_LABELS, ARROW_HEADS } from './arrowHeads.ts';

// The picker groups must offer exactly the arrowheads the document schema admits: a value added to the model without
// a picker entry (or vice versa) must fail here, not surface as a head that cannot be chosen or one that cannot be saved.

const arrowHeadEnum = schema.$defs.arrowHead.enum as readonly string[];

describe('arrow heads', () => {
  it('offers exactly the arrowheads the document schema admits', () => {
    expect(ARROW_HEADS).toEqual(arrowHeadEnum);
  });

  it('partitions the heads into groups with no duplication and a label for each', () => {
    const flattened = ARROW_HEAD_GROUPS.flatMap((group) => group.heads);
    expect(flattened).toEqual(ARROW_HEADS);
    expect(new Set(flattened).size).toBe(flattened.length);
    for (const head of ARROW_HEADS) {
      expect(typeof ARROW_HEAD_LABELS[head]).toBe('string');
    }
  });
});