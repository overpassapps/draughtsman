import { estimateTextWidth, labelFont } from '../canvas/text.ts';
import { checkoutSequence, must } from './sequenceFixture.ts';
import { isSet, sequenceScene, SEQUENCE_METRICS } from './sequenceScene.ts';
import type { Document } from './types.ts';

// Every number below is worked out by hand from the rules in sequenceScene.ts's module comment, not read back from the
// function: headers 120 wide with 20 between them put the lifelines 140 apart from x=80; the first row starts at
// 20 + 40 + 30 = 90 and each message takes 36 (the self message 52), its line at the row's middle (its top segment
// 20 below the row's top); the bottom is the end of the last row plus 20.

describe('sequenceScene: the checkout example', () => {
  const scene = sequenceScene(checkoutSequence());

  it('puts the lifelines 140 apart from x=80, in node order', () => {
    expect(scene.headers.map((h) => [h.id, h.x, h.y, h.w, h.h, h.cx])).toEqual([
      ['p_user', 20, 20, 120, 40, 80],
      ['p_web', 160, 20, 120, 40, 220],
      ['p_api', 300, 20, 120, 40, 360],
      ['p_pay', 440, 20, 120, 40, 500],
    ]);
    expect(scene.lifelines.map((l) => [l.id, l.x, l.y0, l.y1])).toEqual([
      ['p_user', 80, 60, 378],
      ['p_web', 220, 60, 378],
      ['p_api', 360, 60, 378],
      ['p_pay', 500, 60, 378],
    ]);
  });

  it('lays the messages out one row each in edge order, ending at a bar where one is open', () => {
    expect(scene.messages.map((m) => [m.id, m.points])).toEqual([
      ['m_1', [{ x: 80, y: 108 }, { x: 220, y: 108 }]],
      ['m_2', [{ x: 220, y: 144 }, { x: 355, y: 144 }]],
      ['m_3', [{ x: 365, y: 180 }, { x: 495, y: 180 }]],
      ['m_4', [{ x: 495, y: 216 }, { x: 365, y: 216 }]],
      ['m_5', [{ x: 355, y: 252 }, { x: 220, y: 252 }]],
      ['m_6', [{ x: 355, y: 288 }, { x: 220, y: 288 }]],
      ['m_7', [{ x: 360, y: 326 }, { x: 390, y: 326 }, { x: 390, y: 342 }, { x: 360, y: 342 }]],
    ]);
    expect(scene.messages.map((m) => m.self)).toEqual([false, false, false, false, false, false, true]);
  });

  it('draws an activation bar from the message that opens it to the one that closes it', () => {
    expect(scene.bars).toEqual([
      { participant: 'p_api', opener: 'm_2', depth: 0, x: 355, y: 144, w: 10, h: 144 },
      { participant: 'p_pay', opener: 'm_3', depth: 0, x: 495, y: 180, w: 10, h: 36 },
    ]);
  });

  it('centres each label above its line and returns the whole picture as the bounds', () => {
    expect(scene.messages[0]?.labelAt).toEqual({ x: 150, y: 94 });
    expect(scene.messages[1]?.labelAt).toEqual({ x: 287.5, y: 130 });
    const audit = scene.messages[6]?.labelAt;
    expect(audit?.y).toBe(312);
    expect(audit?.x).toBeCloseTo(368 + estimateTextWidth('audit()', labelFont('sans-serif')) / 2, 6);
    expect(scene.bounds).toEqual({ x: 0, y: 0, w: 580, h: 378 });
  });

  it('is a pure function of the document: the same document gives the same scene and is not changed', () => {
    const document = checkoutSequence();
    const before = structuredClone(document);
    expect(sequenceScene(document)).toEqual(sequenceScene(document));
    expect(document).toEqual(before);
    expect(JSON.parse(JSON.stringify(scene))).toEqual(scene);
  });
});

describe('sequenceScene: time is edge order', () => {
  it('re-flows every row below a message inserted mid-stream and changes no lifeline', () => {
    const before = sequenceScene(checkoutSequence());
    const document = checkoutSequence();
    document.edges.splice(2, 0, { id: 'm_new', kind: 'sync', label: 'validate', from: { node: 'p_web' }, to: { node: 'p_web' } });
    const after = sequenceScene(document);

    // The new self message takes the third row (the one m_3 held), so everything from m_3 on moves down by 52.
    expect(after.messages.map((m) => m.id)).toEqual(['m_1', 'm_2', 'm_new', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    expect(after.messages[2]?.y).toBe(162 + 20);
    expect(after.messages[3]?.y).toBe(180 + 52);
    expect(after.messages[0]?.y).toBe(before.messages[0]?.y);
    expect(after.messages[1]?.y).toBe(before.messages[1]?.y);
    expect(after.headers).toEqual(before.headers);
    expect(after.bounds?.h).toBe(378 + 52);
  });

  it('draws a reordered document differently, and the earlier message higher', () => {
    const document = checkoutSequence();
    const [first, second] = document.edges;
    document.edges[0] = must(second);
    document.edges[1] = must(first);
    const scene = sequenceScene(document);
    expect(scene.messages.slice(0, 2).map((m) => m.id)).toEqual(['m_2', 'm_1']);
    expect(scene.messages[0]?.y).toBe(108);
    expect(scene.messages[1]?.y).toBe(144);
  });
});

describe('sequenceScene: columns', () => {
  const two = (label: string, extra?: Partial<Document>): Document => ({
    ...checkoutSequence(),
    nodes: [
      { id: 'a', kind: 'participant', label: 'A', shape: 'rectangle' },
      { id: 'b', kind: 'participant', label: 'B', shape: 'rectangle' },
      { id: 'c', kind: 'participant', label: 'C', shape: 'rectangle' },
    ],
    edges: [{ id: 'e1', kind: 'sync', label, from: { node: 'a' }, to: { node: 'c' } }],
    ...extra,
  });

  it('widens the gaps a long label crosses, sharing it over the lifelines it spans', () => {
    const label = 'a very long message label that will not fit between two default lifelines at all';
    const scene = sequenceScene(two(label));
    const span = must(scene.headers[2]).cx - must(scene.headers[0]).cx;
    expect(span).toBeCloseTo(estimateTextWidth(label, labelFont('sans-serif')) + SEQUENCE_METRICS.labelPad, 6);
    expect(span).toBeGreaterThan(280);
    // Even shares: b sits halfway between a and c.
    expect(must(scene.headers[1]).cx - must(scene.headers[0]).cx).toBeCloseTo(span / 2, 6);
  });

  it('leaves a short label at the default 140', () => {
    const scene = sequenceScene(two('hi'));
    expect(scene.headers.map((h) => h.cx)).toEqual([80, 220, 360]);
  });

  it('widens a header for a long participant label and keeps its neighbours clear', () => {
    const document = two('hi');
    document.nodes[0] = { id: 'a', kind: 'participant', label: 'A participant with a very long name indeed', shape: 'rectangle' };
    const scene = sequenceScene(document);
    const [a, b] = scene.headers;
    expect(must(a).w).toBeGreaterThan(SEQUENCE_METRICS.headerWidth);
    expect(must(b).x - (must(a).x + must(a).w)).toBeGreaterThanOrEqual(SEQUENCE_METRICS.headerGap);
  });

  it('gives an actor’s header the same width as a participant’s with the same name (the figure is over the lifeline, not beside the name)', () => {
    const label = 'A rather long name';
    const width = (kind: 'actor' | 'participant'): number => {
      const document = two('hi');
      document.nodes[0] = { id: 'a', kind, label, shape: kind === 'actor' ? 'rounded' : 'rectangle' };
      return must(sequenceScene(document).headers[0]).w;
    };
    expect(width('participant')).toBe(Math.ceil(estimateTextWidth(label, labelFont('sans-serif')) + SEQUENCE_METRICS.labelPad));
    expect(width('actor')).toBe(width('participant'));
    expect(SEQUENCE_METRICS.actorFigureHeight).toBe(26);
    expect(SEQUENCE_METRICS.headerHeight).toBe(40);
  });

  it('gives a self message on the last lifeline room in the bounds', () => {
    const document = two('hi');
    document.edges = [{ id: 'e1', kind: 'sync', label: 'a self message on the last participant', from: { node: 'c' }, to: { node: 'c' } }];
    const scene = sequenceScene(document);
    const plateRight = must(must(scene.messages[0]).labelAt).x + estimateTextWidth('a self message on the last participant', labelFont('sans-serif')) / 2 + SEQUENCE_METRICS.plateGap;
    expect(must(scene.bounds).w).toBeGreaterThanOrEqual(plateRight);
  });
});

describe('sequenceScene: activation bars', () => {
  const base = (): Document => ({
    ...checkoutSequence(),
    nodes: [
      { id: 'a', kind: 'participant', label: 'A', shape: 'rectangle' },
      { id: 'b', kind: 'participant', label: 'B', shape: 'rectangle' },
    ],
    edges: [],
  });
  const msg = (id: string, from: string, to: string, props?: Record<string, unknown>) => ({ id, kind: 'sync', from: { node: from }, to: { node: to }, ...(props === undefined ? {} : { props }) });

  it('offsets nested bars half a bar width and closes the innermost first', () => {
    const document = base();
    document.edges = [msg('e1', 'a', 'b', { activate: true }), msg('e2', 'a', 'b', { activate: true }), msg('e3', 'b', 'a', { deactivate: true }), msg('e4', 'b', 'a', { deactivate: true })];
    const scene = sequenceScene(document);
    // Rows at 108, 144, 180, 216 on b (cx 220): the inner bar (opened by e2) closes at e3, the outer (e1) at e4.
    expect(scene.bars).toEqual([
      { participant: 'b', opener: 'e1', depth: 0, x: 215, y: 108, w: 10, h: 108 },
      { participant: 'b', opener: 'e2', depth: 1, x: 220, y: 144, w: 10, h: 36 },
    ]);
    // e2 arrives at b's left edge (the outer bar's), e3 leaves b's left edge too.
    expect(scene.messages[1]?.points[1]).toEqual({ x: 215, y: 144 });
  });

  it('runs a bar nothing closes to the end of the last row, and ignores a deactivate with nothing open', () => {
    const document = base();
    document.edges = [msg('e1', 'a', 'b', { activate: true }), msg('e2', 'b', 'a'), msg('e3', 'a', 'a', { deactivate: true })];
    const scene = sequenceScene(document);
    // Rows: e1 108, e2 144, e3 (self, row top 162, line at 182), row end 214.
    expect(scene.bars).toEqual([{ participant: 'b', opener: 'e1', depth: 0, x: 215, y: 108, w: 10, h: 214 - 108 }]);
    expect(scene.messages[2]?.y).toBe(182);
  });

  it('reads the string "true" as on and any other value as off', () => {
    const edge = (value: unknown) => ({ id: 'e', kind: 'sync', from: { node: 'a' }, to: { node: 'b' }, props: { activate: value } });
    expect(isSet(edge(true), 'activate')).toBe(true);
    expect(isSet(edge('true'), 'activate')).toBe(true);
    expect(isSet(edge('TRUE'), 'activate')).toBe(true);
    expect(isSet(edge(false), 'activate')).toBe(false);
    expect(isSet(edge('yes'), 'activate')).toBe(false);
    expect(isSet(edge(1), 'activate')).toBe(false);
    expect(isSet({ id: 'e', kind: 'sync', from: { node: 'a' }, to: { node: 'b' } }, 'activate')).toBe(false);
  });
});

describe('sequenceScene: degenerate documents', () => {
  it('has no bounds and nothing to draw for a document with no participants', () => {
    expect(sequenceScene({ ...checkoutSequence(), nodes: [], edges: [] })).toEqual({ headers: [], lifelines: [], messages: [], bars: [], destroys: [], fragments: [], bounds: undefined });
  });

  it('draws headers and lifelines for participants with no messages', () => {
    const document = { ...checkoutSequence(), edges: [] };
    const scene = sequenceScene(document);
    expect(scene.headers).toHaveLength(4);
    expect(scene.messages).toEqual([]);
    expect(scene.lifelines[0]).toEqual({ id: 'p_user', x: 80, y0: 60, y1: 110 });
  });

  it('skips a message with a missing end without giving it a row', () => {
    const document = checkoutSequence();
    document.edges.splice(1, 0, { id: 'm_ghost', kind: 'sync', label: 'x', from: { node: 'p_web' }, to: { node: 'nobody' } });
    const scene = sequenceScene(document);
    expect(scene.messages.map((m) => m.id)).not.toContain('m_ghost');
    expect(scene.messages.map((m) => m.y)).toEqual(sequenceScene(checkoutSequence()).messages.map((m) => m.y));
  });

  it('ignores any stored geometry: positions are derived', () => {
    const document = checkoutSequence();
    document.nodes[1] = { ...must(document.nodes[1]), geometry: { x: 900, y: 900, w: 10, h: 10 } };
    expect(sequenceScene(document)).toEqual(sequenceScene(checkoutSequence()));
  });
});
