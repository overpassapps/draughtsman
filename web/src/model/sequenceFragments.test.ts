import { checkoutFragments, checkoutSequence, must } from './sequenceFixture.ts';
import { addNote } from './sequenceFragments.ts';
import { fragmentAncestry, fragmentNodes, sequenceMemberships, sequenceScene, SEQUENCE_METRICS } from './sequenceScene.ts';
import type { Document, Edge, Node } from './types.ts';

// Milestone 2's rows and frames. Every number is worked out by hand from the rules in sequenceScene.ts's module comment
// (the metrics: a row is 36, a guard row 22, a frame's close pad 8, its side pad 20, a nested frame steps 10 further out,
// a note box 28 high, a create row 48, a header 40 high), not read back from the function.

const participant = (id: string, label = id): Node => ({ id, kind: 'participant', label, shape: 'rectangle' });
const fragment = (id: string, operator: string, operands: string[], extra?: Partial<Node>): Node => ({ id, kind: 'fragment', label: operator, shape: 'rectangle', props: { operator, operands }, ...extra });
const message = (id: string, from: string, to: string, props?: Record<string, unknown>, kind = 'sync', label?: string): Edge => ({ id, kind, from: { node: from }, to: { node: to }, ...(label === undefined ? {} : { label }), ...(props === undefined ? {} : { props }) });

const documentOf = (nodes: Node[], edges: Edge[]): Document => ({ ...checkoutSequence(), nodes, edges });

/** The checkout example with its alt fragment (docs/sequence-diagrams.md): m_3 and m_4 under [card ok], m_5 under [declined]. */
function checkoutWithAlt(): Document {
  const document = checkoutSequence();
  document.nodes.push(fragment('f_1', 'alt', ['[card ok]', '[declined]']));
  const member = (id: string, operand: number): void => {
    const edge = must(document.edges.find((candidate) => candidate.id === id));
    edge.props = { ...edge.props, in: 'f_1', operand };
  };
  member('m_3', 0);
  member('m_4', 0);
  member('m_5', 1);
  return document;
}

describe('sequenceScene: a combined fragment', () => {
  const scene = sequenceScene(checkoutWithAlt());
  const frame = must(scene.fragments[0]);

  it('is a frame, not a column: the four lifelines stay where they were', () => {
    expect(scene.headers.map((h) => h.cx)).toEqual([80, 220, 360, 500]);
    expect(scene.fragments.map((f) => f.id)).toEqual(['f_1']);
  });

  it('frames the members from their first row to their last, with a guard row on top and a pad below', () => {
    // m_1 90-126, m_2 126-162; the frame opens at 162 (guard row to 184); m_3 184-220, m_4 220-256; the second operand
    // starts at 256 (guard row to 278); m_5 278-314; the close pad ends the frame at 322. m_6's label plate reaches 8 above
    // its row, so its row starts 10 lower (332), where the plate is 2 clear of the border; m_7 follows it at 368.
    expect(scene.messages.map((m) => [m.id, m.y, m.rowTop, m.rowBottom])).toEqual([
      ['m_1', 108, 90, 126],
      ['m_2', 144, 126, 162],
      ['m_3', 202, 184, 220],
      ['m_4', 238, 220, 256],
      ['m_5', 296, 278, 314],
      ['m_6', 350, 332, 368],
      ['m_7', 388, 368, 420],
    ]);
    expect([frame.y, frame.h, frame.depth, frame.empty]).toEqual([162, 160, 0, false]);
    expect(frame.operator).toBe('alt');
  });

  it('spans the lifelines its members touch plus 20 each side (members reach the bars: 220 to 495)', () => {
    expect([frame.x, frame.w]).toEqual([200, 315]);
    expect(scene.bounds).toEqual({ x: 0, y: 0, w: 580, h: 440 });
  });

  it('starts a guard row at the top and one under each divider, with the guard text where the tab ends', () => {
    // 'alt' at 12px is 14.28 wide, so the tab is ceil(14.28 + 16) = 31 wide; the first guard starts 8 right of it.
    expect(frame.tagWidth).toBe(31);
    expect(frame.sections).toEqual([
      { operand: 0, guard: '[card ok]', y: 162, guardAt: { x: 239, y: 171 } },
      { operand: 1, guard: '[declined]', y: 256, guardAt: { x: 208, y: 265 } },
    ]);
  });

  it('adds nothing to a document with no fragment: the checkout numbers of sequenceScene.test.ts still hold', () => {
    expect(sequenceScene(checkoutSequence()).bounds).toEqual({ x: 0, y: 0, w: 580, h: 378 });
    expect(sequenceScene(checkoutSequence()).fragments).toEqual([]);
  });

  it('keeps the frame, shrunk, when a message is deleted from the middle of it', () => {
    const document = checkoutWithAlt();
    document.edges = document.edges.filter((edge) => edge.id !== 'm_4');
    const after = sequenceScene(document);
    const shrunk = must(after.fragments[0]);
    // m_3 184-220, then the divider at 220 (guard row to 242), m_5 242-278, the pad to 286: 124 high, one row shorter.
    expect([shrunk.id, shrunk.y, shrunk.h, shrunk.empty]).toEqual(['f_1', 162, 124, false]);
    expect(shrunk.sections.map((section) => [section.operand, section.y])).toEqual([[0, 162], [1, 220]]);
    // ... and the picture is exactly as many rows shorter, with the messages after it moved up by 36.
    expect(must(after.messages.find((m) => m.id === 'm_6')).y).toBe(350 - 36);
  });

  it('draws a fragment nothing names as an empty one-row frame after the last row, once every member is gone', () => {
    const document = checkoutWithAlt();
    document.edges = document.edges.filter((edge) => !['m_3', 'm_4', 'm_5'].includes(edge.id));
    const after = sequenceScene(document);
    // m_1 90-126, m_2 126-162, m_6 162-198, m_7 (self) 198-250; the empty frame is a guard row and a row plus the pad: 66.
    expect(after.fragments).toHaveLength(1);
    expect(after.fragments.map((f) => [f.id, f.y, f.h, f.empty])).toEqual([['f_1', 250, 66, true]]);
    expect(after.bounds?.h).toBe(250 + 66 + 20);
  });

  it('draws a message that is not a member inside the frame when it falls between two members', () => {
    const document = checkoutWithAlt();
    // Move m_2's twin: put a plain message between m_3 and m_4. The frame still runs from m_3 to m_5 and now holds it.
    document.edges.splice(3, 0, message('m_x', 'p_web', 'p_api'));
    const after = sequenceScene(document);
    const inside = must(after.messages.find((m) => m.id === 'm_x'));
    const framed = must(after.fragments[0]);
    expect(inside.rowTop).toBeGreaterThan(framed.y);
    expect(inside.rowBottom).toBeLessThan(framed.y + framed.h);
    // 36 taller than before, and no extra guard row: it is not a member, so it opens nothing.
    expect(framed.h).toBe(160 + 36);
  });

  it('is a pure function of the document', () => {
    const document = checkoutWithAlt();
    const before = structuredClone(document);
    expect(sequenceScene(document)).toEqual(sequenceScene(document));
    expect(document).toEqual(before);
    expect(JSON.parse(JSON.stringify(scene))).toEqual(scene);
  });
});

describe('sequenceScene: nested fragments', () => {
  // f_out (alt) holds m1 under [x]; f_in (loop, in f_out) holds m2 and m3; m4 is under f_out's second operand.
  const nested = (): Document =>
    documentOf(
      [participant('A'), participant('B'), fragment('f_out', 'alt', ['[x]', '[y]']), fragment('f_in', 'loop', ['[n times]'], { parent: 'f_out' })],
      [message('m1', 'A', 'B', { in: 'f_out', operand: 0 }), message('m2', 'A', 'B', { in: 'f_in' }), message('m3', 'B', 'A', { in: 'f_in' }), message('m4', 'A', 'B', { in: 'f_out', operand: 1 })],
    );
  const scene = sequenceScene(nested());
  const outer = must(scene.fragments.find((f) => f.id === 'f_out'));
  const inner = must(scene.fragments.find((f) => f.id === 'f_in'));

  it('opens the outer frame first and the inner one under its own guard row', () => {
    // f_out opens at 90 (guard row to 112); m1 112-148; f_in opens at 148 (to 170); m2 170-206, m3 206-242;
    // f_in closes (pad to 250); the second operand of f_out starts at 250 (to 272); m4 272-308; f_out closes at 316.
    expect(scene.messages.map((m) => [m.id, m.rowTop])).toEqual([['m1', 112], ['m2', 170], ['m3', 206], ['m4', 272]]);
    expect([inner.y, inner.h, inner.depth]).toEqual([148, 102, 1]);
    expect([outer.y, outer.h, outer.depth]).toEqual([90, 226, 0]);
    expect(scene.bounds?.h).toBe(316 + 20);
  });

  it('insets the inner frame 10 each side of the outer one, and lists the outer first so the inner is drawn over it', () => {
    // Members span the lifelines 80 to 220: the inner frame is 60 to 240, the outer 10 further out on each side.
    expect([inner.x, inner.w]).toEqual([60, 180]);
    expect([outer.x, outer.w]).toEqual([50, 200]);
    expect(scene.fragments.map((f) => f.id)).toEqual(['f_out', 'f_in']);
  });

  it('puts a divider in the outer frame where its operand changes, below the inner frame', () => {
    expect(outer.sections.map((s) => [s.operand, s.guard, s.y])).toEqual([[0, '[x]', 90], [1, '[y]', 250]]);
    expect(inner.sections.map((s) => [s.operand, s.guard, s.y])).toEqual([[0, '[n times]', 148]]);
  });

  it('takes the operand a nested fragment sits under from its own prop, else from the parent’s nearest earlier message', () => {
    const document = nested();
    // f_in's messages come after m1 (operand 0), so it sits in operand 0; declaring operand=1 moves it there, ahead of m4.
    const implicit = must(sequenceMemberships(document).get('m2'));
    expect(implicit).toEqual([{ fragment: 'f_out', operand: 0 }, { fragment: 'f_in', operand: 0 }]);
    document.nodes[3] = fragment('f_in', 'loop', ['[n times]'], { parent: 'f_out', props: { operator: 'loop', operands: ['[n times]'], operand: 1 } });
    expect(must(sequenceMemberships(document).get('m2'))).toEqual([{ fragment: 'f_out', operand: 1 }, { fragment: 'f_in', operand: 0 }]);
  });

  it('stops at a parent that is not a fragment and at a cycle rather than looping', () => {
    const document = nested();
    const fragments = fragmentNodes(document);
    expect(fragmentAncestry(fragments, 'f_in')).toEqual(['f_out', 'f_in']);
    const looped = documentOf(
      [participant('A'), participant('B'), fragment('f_a', 'opt', [''], { parent: 'f_b' }), fragment('f_b', 'opt', [''], { parent: 'f_a' })],
      [message('m1', 'A', 'B', { in: 'f_a' })],
    );
    expect(fragmentAncestry(fragmentNodes(looped), 'f_a')).toEqual(['f_b', 'f_a']);
    expect(sequenceScene(looped).fragments.length).toBeGreaterThan(0);
  });
});

describe('sequenceScene: membership props', () => {
  it('ignores an `in` that names no fragment, and reads operand from a number or a string of digits', () => {
    const document = documentOf(
      [participant('A'), participant('B'), fragment('f', 'opt', ['[a]', '[b]'])],
      [message('m1', 'A', 'B', { in: 'nowhere' }), message('m2', 'A', 'B', { in: 'f', operand: '1' }), message('m3', 'A', 'B', { in: 'f', operand: -2 }), message('m4', 'A', 'B', { in: 'f', operand: 1.5 })],
    );
    const found = sequenceMemberships(document);
    expect(found.has('m1')).toBe(false);
    expect(found.get('m2')).toEqual([{ fragment: 'f', operand: 1 }]);
    expect(found.get('m3')).toEqual([{ fragment: 'f', operand: 0 }]);
    expect(found.get('m4')).toEqual([{ fragment: 'f', operand: 0 }]);
    const scene = sequenceScene(document);
    // m1 is outside; the frame opens at m2, whose operand is 1, so the first guard shown is [b].
    expect(must(scene.fragments[0]).sections[0]?.guard).toBe('[b]');
  });

  it('falls back to the label for the operator tab and to an empty guard for a missing one', () => {
    const document = documentOf([participant('A'), participant('B'), { id: 'f', kind: 'fragment', label: 'opt', shape: 'rectangle' }], [message('m1', 'A', 'B', { in: 'f' })]);
    const frame = must(sequenceScene(document).fragments[0]);
    expect(frame.operator).toBe('opt');
    expect(frame.sections.map((s) => s.guard)).toEqual(['']);
  });

  it('draws no lifeline for a fragment node', () => {
    const scene = sequenceScene(checkoutWithAlt());
    expect(scene.lifelines.map((l) => l.id)).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
  });
});

describe('sequenceScene: notes', () => {
  const doc = (): Document =>
    documentOf(
      [participant('A'), participant('B')],
      [message('m1', 'A', 'B', undefined, 'sync', 'hi'), message('n1', 'A', 'B', undefined, 'note', 'Check stock'), message('n2', 'B', 'B', undefined, 'note', 'Solo')],
    );
  const scene = sequenceScene(doc());

  it('takes a row of 36 in the order, with a 28 high box centred in it', () => {
    expect(scene.messages.map((m) => [m.id, m.rowTop, m.rowBottom])).toEqual([['m1', 90, 126], ['n1', 126, 162], ['n2', 162, 198]]);
    expect(must(scene.messages[1]).note).toEqual({ x: 70, y: 130, w: 160, h: 28 });
    expect(must(scene.messages[1]).labelAt).toEqual({ x: 150, y: 144 });
  });

  it('reaches 10 beyond the outer two lifelines it covers and is centred on the one it is over', () => {
    // A (80) to B (220): 70 to 230. Over B alone: 100 wide (the least), centred on 220.
    expect(must(scene.messages[2]).note).toEqual({ x: 170, y: 166, w: 100, h: 28 });
    expect(scene.bounds?.h).toBe(198 + 20);
  });

  it('is the same box whichever way round its ends are written', () => {
    const swapped = doc();
    swapped.edges[1] = message('n1', 'B', 'A', undefined, 'note', 'Check stock');
    expect(must(sequenceScene(swapped).messages[1]).note).toEqual(must(scene.messages[1]).note);
  });

  it('widens the gaps under a long note so its text fits the box', () => {
    const wide = doc();
    wide.edges[1] = message('n1', 'A', 'B', undefined, 'note', 'a note whose text is far too long for the default gap between two lifelines');
    const box = must(must(sequenceScene(wide).messages[1]).note);
    const wideScene = sequenceScene(wide);
    expect(must(wideScene.headers[1]).cx - must(wideScene.headers[0]).cx).toBeGreaterThan(140);
    // The box is still exactly 10 beyond each lifeline it covers.
    expect(box.x).toBe(must(wideScene.headers[0]).cx - 10);
    expect(box.x + box.w).toBe(must(wideScene.headers[1]).cx + 10);
  });

  it('keeps a wide note over the first lifeline inside the picture', () => {
    const wide = documentOf([participant('A'), participant('B')], [message('n1', 'A', 'A', undefined, 'note', 'a very long note over the first participant, wider than its header')]);
    const after = sequenceScene(wide);
    expect(must(must(after.messages[0]).note).x).toBeGreaterThanOrEqual(20);
  });

  it('can sit inside a fragment like a message', () => {
    const document = doc();
    document.nodes.push(fragment('f', 'opt', ['[x]']));
    must(document.edges[1]).props = { in: 'f' };
    const after = sequenceScene(document);
    const frame = must(after.fragments[0]);
    const note = must(after.messages[1]);
    expect(note.rowTop).toBe(126 + 22);
    expect(frame.y).toBe(126);
  });
});

describe('sequenceScene: create and destroy', () => {
  const doc = (): Document =>
    documentOf(
      [participant('A'), participant('B'), participant('C')],
      [message('m1', 'A', 'B', undefined, 'sync', 'ping'), message('c1', 'B', 'C', undefined, 'create', 'new C()'), message('m2', 'C', 'B', undefined, 'return', 'done'), message('d1', 'B', 'C', undefined, 'destroy', 'close()')],
    );
  const scene = sequenceScene(doc());

  it('draws the created header at its row, level with the message, and starts the lifeline under it', () => {
    // m1 90-126; the create row is 48 high (126-174), the header 40 high centred in it: 130 to 170.
    expect(must(scene.messages[1]).rowTop).toBe(126);
    expect(must(scene.messages[1]).rowBottom).toBe(174);
    expect(must(scene.messages[1]).y).toBe(150);
    expect(scene.headers.map((h) => [h.id, h.y])).toEqual([['A', 20], ['B', 20], ['C', 130]]);
    expect(scene.lifelines.find((l) => l.id === 'C')?.y0).toBe(170);
  });

  it('ends the create arrow at the header’s near edge, not at the lifeline', () => {
    // B is at 220 and C at 360 with a 120 wide header: 300 to 420.
    expect(must(scene.messages[1]).points).toEqual([{ x: 220, y: 150 }, { x: 300, y: 150 }]);
  });

  it('ends a destroyed lifeline at the destroy message with an X there', () => {
    // m2 174-210 (y 192); the destroy row 210-246 has its line at 228.
    expect(scene.destroys).toEqual([{ participant: 'C', x: 360, y: 228 }]);
    expect(scene.lifelines.map((l) => [l.id, l.y1])).toEqual([['A', 266], ['B', 266], ['C', 228]]);
    // The other two run to the bottom of the picture, 20 below the last row.
    expect(scene.bounds?.h).toBe(246 + 20);
  });

  it('closes an activation bar still open on the destroyed participant at the destroy row', () => {
    const document = doc();
    must(document.edges[1]).props = { activate: true };
    const bar = must(sequenceScene(document).bars[0]);
    // Opened under C's header (170) and closed where it is destroyed (228).
    expect([bar.participant, bar.opener, bar.y, bar.h]).toEqual(['C', 'c1', 170, 58]);
  });

  it('only lets the first create and the first destroy of a participant count; later ones are plain messages', () => {
    const document = doc();
    document.edges.push(message('c2', 'B', 'C', undefined, 'create', 'again'), message('d2', 'B', 'C', undefined, 'destroy', 'again'));
    const after = sequenceScene(document);
    expect(after.headers.find((h) => h.id === 'C')?.y).toBe(130);
    expect(after.destroys).toHaveLength(1);
    // The repeat create is a 36 row, not a 48 one.
    expect(must(after.messages.find((m) => m.id === 'c2')).rowBottom - must(after.messages.find((m) => m.id === 'c2')).rowTop).toBe(36);
  });

  it('gives a create with the same end twice no header of its own (it is a self message)', () => {
    const document = doc();
    document.edges[1] = message('c1', 'B', 'B', undefined, 'create', 'x');
    const after = sequenceScene(document);
    expect(after.headers.every((h) => h.y === 20)).toBe(true);
    expect(must(after.messages[1]).self).toBe(true);
  });
});

describe('sequenceScene: docs/examples/checkout-fragments.json', () => {
  // The document the C# server-render test draws through the real sidecar; both pin these literals, so the canvas and the
  // server cannot drift apart. Rows: 90 m_1, 126 m_2, 162 the note, 198 the frame opens (guard row 22), 220 m_3, 256 m_4,
  // 292 the divider (guard row 22), 314 m_5, the pad ends the frame at 358; m_6's label plate reaches 8 above its row, so the
  // row waits until the plate clears the border by 2 (a clearance of 10): 368 m_6, 404 the create row (48), 452 m_8, 488 the
  // destroy, 524 the last row's end; the bottom is 20 further.
  const scene = sequenceScene(checkoutFragments());

  it('places the five lifelines, the created one’s header at its create row', () => {
    expect(scene.headers.map((h) => [h.id, h.x, h.y, h.cx])).toEqual([
      ['p_user', 20, 20, 80],
      ['p_web', 160, 20, 220],
      ['p_api', 300, 20, 360],
      ['p_pay', 440, 20, 500],
      ['p_mail', 580, 408, 640],
    ]);
    expect(scene.lifelines.map((l) => [l.id, l.y0, l.y1])).toEqual([
      ['p_user', 60, 544],
      ['p_web', 60, 544],
      ['p_api', 60, 544],
      ['p_pay', 60, 544],
      ['p_mail', 448, 506],
    ]);
  });

  it('lays every row out as worked above', () => {
    expect(scene.messages.map((m) => [m.id, m.rowTop, m.rowBottom, m.y])).toEqual([
      ['m_1', 90, 126, 108],
      ['m_2', 126, 162, 144],
      ['n_1', 162, 198, 180],
      ['m_3', 220, 256, 238],
      ['m_4', 256, 292, 274],
      ['m_5', 314, 350, 332],
      ['m_6', 368, 404, 386],
      ['m_7', 404, 452, 428],
      ['m_8', 452, 488, 470],
      ['m_9', 488, 524, 506],
    ]);
    expect(must(scene.messages[2]).note).toEqual({ x: 210, y: 166, w: 160, h: 28 });
    expect(must(scene.messages[7]).points).toEqual([{ x: 220, y: 428 }, { x: 580, y: 428 }]);
  });

  it('frames m_3 to m_5 with the divider under the second guard, and ends the mailer’s lifeline with an X', () => {
    expect(scene.fragments.map((f) => [f.id, f.x, f.y, f.w, f.h])).toEqual([['f_1', 200, 198, 315, 160]]);
    expect(must(scene.fragments[0]).sections.map((s) => [s.operand, s.guard, s.y])).toEqual([[0, '[card ok]', 198], [1, '[declined]', 292]]);
    expect(scene.destroys).toEqual([{ participant: 'p_mail', x: 640, y: 506 }]);
    expect(scene.bounds).toEqual({ x: 0, y: 0, w: 720, h: 544 });
  });
});

describe('sequenceScene: clearance below a closing frame', () => {
  // A message label's plate is 24 high, centred 14 above its line: 8 above a plain row's top. A frame closes 8 below its
  // last member, so a labelled row straight after it would have its plate cut by the frame's bottom border.
  const loop = (next: Edge): Document =>
    documentOf([participant('A'), participant('B'), fragment('f_1', 'loop', ['[3 times]'])], [message('m1', 'A', 'B', { in: 'f_1' }, 'sync', 'poll'), next]);

  it('pushes a labelled message down until its plate is 2 clear of the border', () => {
    // f_1 opens at 90 (guard row to 112); m1 112-148; the pad ends the frame at 156. m2's plate top is its row top less 8, so
    // its row starts at 156 + 2 + 8 = 166 and its line is at 184.
    const scene = sequenceScene(loop(message('m2', 'B', 'A', undefined, 'return', 'done')));
    expect(must(scene.fragments[0]).y + must(scene.fragments[0]).h).toBe(156);
    expect(scene.messages.map((m) => [m.id, m.rowTop, m.y, m.labelAt?.y])).toEqual([['m1', 112, 130, 116], ['m2', 166, 184, 170]]);
    // The plate spans 158 to 182, so the border at 156 crosses nothing.
    expect((must(scene.messages[1]).labelAt?.y ?? 0) - SEQUENCE_METRICS.labelPlateHalf).toBe(158);
  });

  it('gives a self message its own, smaller push (its line is 20, not 18, into the row)', () => {
    const scene = sequenceScene(loop(message('m2', 'B', 'B', undefined, 'sync', 'again')));
    expect(scene.messages.map((m) => [m.id, m.rowTop, m.y])).toEqual([['m1', 112, 130], ['m2', 164, 184]]);
  });

  it('adds nothing when the next message has no label, is a note, or opens a frame of its own', () => {
    expect(sequenceScene(loop(message('m2', 'B', 'A'))).messages.map((m) => m.rowTop)).toEqual([112, 156]);
    expect(sequenceScene(loop(message('n', 'A', 'B', undefined, 'note', 'a note'))).messages.map((m) => m.rowTop)).toEqual([112, 156]);
    const reopened = documentOf(
      [participant('A'), participant('B'), fragment('f_1', 'loop', ['[3 times]']), fragment('f_2', 'opt', ['[x]'])],
      [message('m1', 'A', 'B', { in: 'f_1' }, 'sync', 'poll'), message('m2', 'B', 'A', { in: 'f_2' }, 'return', 'done')],
    );
    // f_1 closes at 156 (its row 112-148 and the 8 pad); f_2 opens 6 below it (fragmentFrameGap) at 162 with its guard row 22, so
    // the plate sits under its tab and needs no extra row: m2 starts at 184.
    const scene = sequenceScene(reopened);
    expect(scene.messages.map((m) => m.rowTop)).toEqual([112, 184]);
    expect(SEQUENCE_METRICS.fragmentFrameGap).toBe(6);
    expect(scene.fragments.map((f) => [f.id, f.y, f.y + f.h])).toEqual([['f_1', 90, 156], ['f_2', 162, 228]]);
  });

  it('clears the outermost border when nested frames close on the same row', () => {
    const nested = documentOf(
      [participant('A'), participant('B'), fragment('f_out', 'alt', ['[x]']), fragment('f_in', 'loop', ['[n]'], { parent: 'f_out' })],
      [message('m1', 'A', 'B', { in: 'f_in' }, 'sync', 'go'), message('m2', 'B', 'A', undefined, 'return', 'back')],
    );
    const scene = sequenceScene(nested);
    // f_out guard row 90-112, f_in 112-134, m1 134-170, f_in closes at 178, f_out at 186: m2 starts at 186 + 10.
    expect(scene.fragments.map((f) => [f.id, f.y + f.h])).toEqual([['f_out', 186], ['f_in', 178]]);
    expect(must(scene.messages[1]).rowTop).toBe(196);
  });

  it('never lets a frame’s bottom border cross a message label, in the fragment examples', () => {
    for (const document of [checkoutFragments(), checkoutWithAlt(), loop(message('m2', 'B', 'A', undefined, 'return', 'done'))]) {
      const scene = sequenceScene(document);
      for (const message of scene.messages) {
        if (message.labelAt === undefined || message.note !== undefined) {
          continue;
        }
        const top = message.labelAt.y - SEQUENCE_METRICS.labelPlateHalf;
        const bottom = message.labelAt.y + SEQUENCE_METRICS.labelPlateHalf;
        for (const frame of scene.fragments) {
          const border = frame.y + frame.h;
          expect(top < border && border < bottom, `${message.id}'s label crosses ${frame.id}'s bottom border`).toBe(false);
        }
      }
    }
  });
});

describe('sequenceScene: an interaction use', () => {
  // A `ref` frame is a note-like row: 48 high in a 60 row, across the lifelines it covers (10 beyond the outer two), the tab
  // 'ref' (31 wide, like an 'alt' tab: text 12px plus 8 either side) at its top left and its label centred under the tab.
  const doc = (labelled = 'Authenticate'): Document =>
    documentOf([participant('A'), participant('B')], [message('m1', 'A', 'B'), message('u1', 'A', 'B', undefined, 'interaction-use', labelled), message('m2', 'B', 'A', undefined, 'return', 'ok')]);

  it('takes a 60 row and draws a frame 48 high across the lifelines it covers, with a ref tab', () => {
    const scene = sequenceScene(doc());
    const row = must(scene.messages[1]);
    // m1 90-126; the use's row 126-186 holds a frame 132-180; m2 follows at 186.
    expect([row.rowTop, row.rowBottom, row.y]).toEqual([126, 186, 156]);
    expect(row.note).toEqual({ x: 70, y: 132, w: 160, h: 48, tagWidth: 31 });
    expect(row.points).toEqual([{ x: 70, y: 132 }, { x: 230, y: 132 }, { x: 230, y: 180 }, { x: 70, y: 180 }, { x: 70, y: 132 }]);
    // The name is centred under the 18 high tab: 132 + 18 + (48 - 18) / 2.
    expect(row.labelAt).toEqual({ x: 150, y: 165 });
    expect(must(scene.messages[2]).rowTop).toBe(186);
    expect(scene.bounds?.h).toBe(186 + 36 + 20);
  });

  it('is over one lifeline at least 100 wide and centred on it, and a plain note keeps its 36 row and 28 box', () => {
    const one = doc('Auth');
    must(one.edges[1]).to = { node: 'A' };
    expect(must(sequenceScene(one).messages[1]).note).toEqual({ x: 30, y: 132, w: 100, h: 48, tagWidth: 31 });
    const note = doc();
    must(note.edges[1]).kind = 'note';
    const plain = must(sequenceScene(note).messages[1]);
    expect([plain.rowBottom - plain.rowTop, plain.note?.h, plain.note?.tagWidth]).toEqual([36, 28, undefined]);
  });

  it('takes no activation, and a frame closing before it does not push it down (it has no label plate above its row)', () => {
    const document = doc();
    document.nodes.push(fragment('f_1', 'loop', ['[x]']));
    must(document.edges[0]).props = { in: 'f_1' };
    // f_1 opens at 90 (guard row to 112); m1 112-148; the pad ends the frame at 156; the use's row starts there, unpushed.
    const scene = sequenceScene(document);
    expect(must(scene.messages[1]).rowTop).toBe(156);
    expect(must(scene.fragments[0]).y + must(scene.fragments[0]).h).toBe(156);
    const activated = doc();
    must(activated.edges[1]).props = { activate: true };
    expect(sequenceScene(activated).bars).toEqual([]);
  });

  it('is offered by the editor as an insert after the selected message, labelled so it can be renamed', () => {
    const document = doc();
    const result = must(addNoteAfter(document, 'm1'));
    expect(result.edit.insertEdges?.[0]?.edge).toMatchObject({ id: 'u_new', kind: 'interaction-use', label: 'Interaction', from: { node: 'A' }, to: { node: 'B' } });
    expect(result.edit.insertEdges?.[0]?.index).toBe(1);
  });
});

function addNoteAfter(document: Document, after: string) {
  return addNote(document, after, () => 'u_new', 'interaction-use');
}
