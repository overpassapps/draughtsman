import { documentToYDoc, yDocToDocument } from './editor.ts';
import { checkoutSequence, must } from './sequenceFixture.ts';
import { addNote, addOperand, applySequenceEdit, emptiedFragments, emptiedOperands, membershipAt, setGuard, setOperator, toggleActivation, withMembership, wrapInFragment, type SequenceEdit } from './sequenceFragments.ts';
import { guardDisplay, sequenceMemberships, sequenceScene } from './sequenceScene.ts';
import type { Document, Edge, Node } from './types.ts';

// The commands of milestone 2's editor as pure functions over the document. Each is applied through a real Y.Doc
// (applySequenceEdit is what the editor's hook calls), read back, and its picture read from the derived scene, whose
// numbers here are worked out by hand from sequenceScene.ts's rules: a row is 36, a guard row 22, a frame's close pad 8.

/** Applies `edit` to a Y.Doc of `document` and reads the document back, as the editor does. */
function applied(document: Document, edit: SequenceEdit): Document {
  const doc = documentToYDoc(document);
  applySequenceEdit(doc, edit);
  return yDocToDocument(doc);
}

const edgeOf = (document: Document, id: string): Edge => must(document.edges.find((edge) => edge.id === id));
const nodeOf = (document: Document, id: string): Node => must(document.nodes.find((node) => node.id === id));
const ids = (...values: string[]): ReadonlySet<string> => new Set(values);

/** The checkout with its alt fragment: m_3 and m_4 under [card ok], m_5 under [declined]. */
function withAlt(): Document {
  const document = checkoutSequence();
  document.nodes.push({ id: 'f_1', kind: 'fragment', label: 'alt', shape: 'rectangle', props: { operator: 'alt', operands: ['[card ok]', '[declined]'] } });
  for (const [id, operand] of [['m_3', 0], ['m_4', 0], ['m_5', 1]] as const) {
    edgeOf(document, id).props = { ...edgeOf(document, id).props, in: 'f_1', operand };
  }
  return document;
}

describe('wrapInFragment', () => {
  it('wraps two adjacent messages in a new fragment and frames exactly them', () => {
    const document = checkoutSequence();
    const result = must(wrapInFragment(document, ids('m_3', 'm_4'), 'alt'));

    expect([result.fragment, result.operand]).toEqual(['f_1', 0]);
    const after = applied(document, result.edit);
    expect(nodeOf(after, 'f_1')).toMatchObject({ kind: 'fragment', label: 'alt', shape: 'rectangle', props: { operator: 'alt', operands: ['condition'] } });
    expect(nodeOf(after, 'f_1').parent).toBeUndefined();
    expect(edgeOf(after, 'm_3').props).toEqual({ activate: true, in: 'f_1' });
    expect(edgeOf(after, 'm_4').props).toEqual({ deactivate: true, in: 'f_1' });
    // Nothing outside the selection changed.
    expect(edgeOf(after, 'm_2')).toEqual(edgeOf(document, 'm_2'));
    expect(edgeOf(after, 'm_5')).toEqual(edgeOf(document, 'm_5'));

    // m_1 90-126, m_2 126-162; the frame opens at 162 (guard row to 184); m_3 184-220, m_4 220-256, the pad to 264:
    // 102 high, from the members' bar edges 365 to 495, 20 each side: x 345, 170 wide.
    const frame = must(sequenceScene(after).fragments[0]);
    expect([frame.id, frame.x, frame.y, frame.w, frame.h]).toEqual(['f_1', 345, 162, 170, 102]);
    expect(frame.sections[0]).toMatchObject({ guard: '[condition]', guardAt: { x: 384, y: 171 } });
  });

  it('gives a par no guard, and each operator its own tab text', () => {
    const document = checkoutSequence();
    for (const [operator, guard] of [['alt', 'condition'], ['opt', 'condition'], ['loop', 'condition'], ['par', '']] as const) {
      const after = applied(document, must(wrapInFragment(document, ids('m_1'), operator)).edit);
      expect(nodeOf(after, 'f_1').props).toEqual({ operator, operands: [guard] });
      expect(must(sequenceScene(after).fragments[0]).operator).toBe(operator);
    }
  });

  it('refuses what it cannot wrap, and each refusal has a valid twin that is accepted', () => {
    const document = checkoutSequence();
    // Not next to each other: the frame would include m_2.
    expect(wrapInFragment(document, ids('m_1', 'm_3'), 'alt')).toBeUndefined();
    expect(wrapInFragment(document, ids('m_1', 'm_2'), 'alt')).toBeDefined();
    // A participant is not a row.
    expect(wrapInFragment(document, ids('m_1', 'p_web'), 'alt')).toBeUndefined();
    expect(wrapInFragment(document, ids('p_web'), 'alt')).toBeUndefined();
    // Nothing selected, or an id that is nowhere.
    expect(wrapInFragment(document, ids(), 'alt')).toBeUndefined();
    expect(wrapInFragment(document, ids('m_1', 'no_such'), 'alt')).toBeUndefined();
  });

  it('refuses a selection that would cross another frame’s border, and accepts the whole frame', () => {
    const document = withAlt();
    // m_2 is outside f_1 and m_3 is one of its three members: the two frames would cross.
    expect(wrapInFragment(document, ids('m_2', 'm_3'), 'loop')).toBeUndefined();
    // Selecting f_1's rows entirely (both operands) wraps f_1 itself.
    const whole = must(wrapInFragment(document, ids('m_3', 'm_4', 'm_5'), 'loop'));
    const after = applied(document, whole.edit);
    expect(nodeOf(after, 'f_2')).toMatchObject({ props: { operator: 'loop', operands: ['condition'] } });
    expect(nodeOf(after, 'f_1').parent).toBe('f_2');
    expect(nodeOf(after, 'f_1').props).toEqual({ operator: 'alt', operands: ['[card ok]', '[declined]'], operand: 0 });
    // Its messages stay in f_1 (the child): only the fragment moved.
    expect(['m_3', 'm_4', 'm_5'].map((id) => edgeOf(after, id).props?.in)).toEqual(['f_1', 'f_1', 'f_1']);
    const scene = sequenceScene(after);
    expect(scene.fragments.map((f) => [f.id, f.depth])).toEqual([['f_2', 0], ['f_1', 1]]);
    const outer = must(scene.fragments.find((f) => f.id === 'f_2'));
    const inner = must(scene.fragments.find((f) => f.id === 'f_1'));
    // The loop reaches 10 further out than the alt on each side, and starts one guard row above it.
    expect(outer.x).toBe(inner.x - 10);
    expect(outer.x + outer.w).toBe(inner.x + inner.w + 10);
    expect(outer.y).toBe(inner.y - 22);
  });

  it('nests the new frame inside the fragment operand the whole selection shares', () => {
    const document = withAlt();
    const result = must(wrapInFragment(document, ids('m_4'), 'opt'));
    const after = applied(document, result.edit);

    expect(nodeOf(after, 'f_2')).toMatchObject({ parent: 'f_1', props: { operator: 'opt', operands: ['condition'], operand: 0 } });
    // m_4 moves into the new frame; it keeps no operand of its own (0 is the default).
    expect(edgeOf(after, 'm_4').props).toEqual({ deactivate: true, in: 'f_2' });
    expect(sequenceMemberships(after).get('m_4')).toEqual([{ fragment: 'f_1', operand: 0 }, { fragment: 'f_2', operand: 0 }]);
    // The alt's frame is unchanged in its members: m_3 still opens it and m_5 still closes it.
    const scene = sequenceScene(after);
    expect(scene.fragments.map((f) => f.id)).toEqual(['f_1', 'f_2']);
    expect(scene.fragments[0]?.sections.map((s) => s.operand)).toEqual([0, 1]);
  });

  it('is refused for a selection spanning two operands of one fragment unless it is all of them', () => {
    const document = withAlt();
    // m_4 (operand 0) and m_5 (operand 1) straddle the divider of f_1, which is not wholly selected.
    expect(wrapInFragment(document, ids('m_4', 'm_5'), 'opt')).toBeUndefined();
    expect(wrapInFragment(document, ids('m_3', 'm_4', 'm_5'), 'opt')).toBeDefined();
  });

  it('picks an id nothing uses', () => {
    const document = withAlt();
    document.nodes.push({ id: 'f_2', kind: 'fragment', label: 'opt', shape: 'rectangle', props: { operator: 'opt', operands: [''] } });
    expect(must(wrapInFragment(document, ids('m_1'), 'alt')).fragment).toBe('f_3');
  });
});

describe('addOperand', () => {
  /** m_3 and m_4 in one operand of an alt; the guard is there and the second operand is not. */
  function oneOperand(): Document {
    const document = checkoutSequence();
    document.nodes.push({ id: 'f_1', kind: 'fragment', label: 'alt', shape: 'rectangle', props: { operator: 'alt', operands: ['[card ok]'] } });
    for (const id of ['m_3', 'm_4']) {
      edgeOf(document, id).props = { ...edgeOf(document, id).props, in: 'f_1' };
    }
    return document;
  }

  it('splits the operand at the message: it and what follows move to a new [else] operand', () => {
    const document = oneOperand();
    const result = must(addOperand(document, 'm_4'));

    expect([result.fragment, result.operand]).toEqual(['f_1', 1]);
    const after = applied(document, result.edit);
    expect(nodeOf(after, 'f_1').props).toEqual({ operator: 'alt', operands: ['[card ok]', 'else'] });
    expect(edgeOf(after, 'm_3').props).toEqual({ activate: true, in: 'f_1' });
    expect(edgeOf(after, 'm_4').props).toEqual({ deactivate: true, in: 'f_1', operand: 1 });
    // m_3 184-220; the divider at 220 and a guard row to 242; m_4 242-278; the pad ends the frame at 286.
    const frame = must(sequenceScene(after).fragments[0]);
    expect([frame.y, frame.h]).toEqual([162, 124]);
    expect(frame.sections.map((s) => [s.operand, s.guard, s.y])).toEqual([[0, '[card ok]', 162], [1, '[else]', 220]]);
  });

  it('shifts the later operands up and splits inside an operand that has following messages', () => {
    const document = withAlt();
    const after = applied(document, must(addOperand(document, 'm_4')).edit);

    expect(nodeOf(after, 'f_1').props).toEqual({ operator: 'alt', operands: ['[card ok]', 'else', '[declined]'] });
    expect(['m_3', 'm_4', 'm_5'].map((id) => edgeOf(after, id).props?.operand)).toEqual([undefined, 1, 2]);
    expect(must(sequenceScene(after).fragments[0]).sections.map((s) => s.guard)).toEqual(['[card ok]', '[else]', '[declined]']);
  });

  it('moves a fragment nested after the split into the new operand with it', () => {
    const document = oneOperand();
    document.nodes.push({ id: 'f_2', kind: 'fragment', label: 'loop', shape: 'rectangle', parent: 'f_1', props: { operator: 'loop', operands: [''] } });
    edgeOf(document, 'm_4').props = { deactivate: true, in: 'f_2' };
    edgeOf(document, 'm_5').props = { in: 'f_1' };
    // m_3 (f_1), m_4 (f_2 in f_1), m_5 (f_1). Splitting at m_5 leaves f_2 behind; splitting at m_4 is refused (not a direct member).
    expect(addOperand(document, 'm_4')).toBeUndefined();
    const after = applied(document, must(addOperand(document, 'm_5')).edit);
    expect(edgeOf(after, 'm_5').props).toEqual({ in: 'f_1', operand: 1 });
    expect(sequenceMemberships(after).get('m_4')).toEqual([{ fragment: 'f_1', operand: 0 }, { fragment: 'f_2', operand: 0 }]);
  });

  it('refuses the first message of an operand, a message in no fragment, and an operator with one branch', () => {
    const document = oneOperand();
    expect(addOperand(document, 'm_3')).toBeUndefined();
    expect(addOperand(document, 'm_4')).toBeDefined();
    expect(addOperand(document, 'm_1')).toBeUndefined();
    expect(addOperand(document, 'no_such')).toBeUndefined();
    const opt = oneOperand();
    opt.nodes[4] = { ...nodeOf(opt, 'f_1'), label: 'opt', props: { operator: 'opt', operands: ['[x]'] } };
    expect(addOperand(opt, 'm_4')).toBeUndefined();
    const par = oneOperand();
    par.nodes[4] = { ...nodeOf(par, 'f_1'), label: 'par', props: { operator: 'par', operands: [''] } };
    expect(must(addOperand(par, 'm_4')).edit.nodeProps?.get('f_1')?.operands).toEqual(['', '']);
  });
});

describe('membership by slot', () => {
  it('is the fragment both neighbours are in, with the earlier one’s operand, and none above the first or below the last member', () => {
    const document = withAlt();
    // Edge order: m_1 m_2 [m_3 m_4 | m_5] m_6 m_7. Index 3 is between m_3 and m_4, 5 is at the divider, 4 between m_4 and m_5.
    expect(membershipAt(document, 2)).toBeUndefined(); // between m_2 and m_3: above the first member
    expect(membershipAt(document, 3)).toEqual({ fragment: 'f_1', operand: 0 });
    expect(membershipAt(document, 4)).toEqual({ fragment: 'f_1', operand: 0 }); // at the divider: the operand before it
    expect(membershipAt(document, 5)).toBeUndefined(); // between m_5 and m_6: below the last member
    expect(membershipAt(document, 0)).toBeUndefined();
    expect(membershipAt(document, 7)).toBeUndefined();
  });

  it('does not count the message being moved as its own neighbour', () => {
    const document = withAlt();
    // m_4 dropped between m_3 and m_5 (index 3 of the list without it): both are members, so it stays in.
    expect(membershipAt(document, 3, 'm_4')).toEqual({ fragment: 'f_1', operand: 0 });
    // Dropped after m_5 (index 4 without it): m_6 follows and is no member, so it leaves.
    expect(membershipAt(document, 4, 'm_4')).toBeUndefined();
  });

  it('takes the innermost fragment two neighbours share when they sit in different nested ones', () => {
    const document = withAlt();
    document.nodes.push({ id: 'f_2', kind: 'fragment', label: 'loop', shape: 'rectangle', parent: 'f_1', props: { operator: 'loop', operands: [''], operand: 0 } });
    edgeOf(document, 'm_3').props = { in: 'f_2' };
    edgeOf(document, 'm_4').props = { in: 'f_1', operand: 0 };
    // Between m_3 (in f_2, in f_1) and m_4 (in f_1): only f_1 holds both.
    expect(membershipAt(document, 3)).toEqual({ fragment: 'f_1', operand: 0 });
  });

  it('writes and clears in and operand on an edge without touching its other props', () => {
    const edge: Edge = { id: 'm', kind: 'sync', from: { node: 'a' }, to: { node: 'b' }, props: { activate: true, in: 'f_1', operand: 2 } };
    expect(withMembership(edge, undefined).props).toEqual({ activate: true });
    expect(withMembership(edge, { fragment: 'f_9', operand: 0 }).props).toEqual({ activate: true, in: 'f_9' });
    expect(withMembership(edge, { fragment: 'f_9', operand: 3 }).props).toEqual({ activate: true, in: 'f_9', operand: 3 });
    const bare: Edge = { id: 'm', kind: 'sync', from: { node: 'a' }, to: { node: 'b' }, props: { in: 'f_1' } };
    expect('props' in withMembership(bare, undefined)).toBe(false);
  });
});

describe('addNote', () => {
  it('inserts an empty note right after the message, over the same participants, and shifts every later row', () => {
    const document = checkoutSequence();
    const result = must(addNote(document, 'm_2', () => 'n_new'));
    const after = applied(document, result.edit);

    expect(after.edges.map((edge) => edge.id)).toEqual(['m_1', 'm_2', 'n_new', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    expect(edgeOf(after, 'n_new')).toMatchObject({ kind: 'note', from: { node: 'p_web' }, to: { node: 'p_api' } });
    const scene = sequenceScene(after);
    // The note takes the third row (162-198); m_3 moves from 180 to 216.
    expect(must(scene.messages.find((m) => m.id === 'n_new')).rowTop).toBe(162);
    expect(must(scene.messages.find((m) => m.id === 'm_3')).y).toBe(216);
  });

  it('is a member of the fragment its neighbours share, and stays inside it after the last member', () => {
    const document = withAlt();
    // After m_3, before m_4: inside f_1, operand 0. After m_5 (the frame's last message, in operand 1) it stays inside: a note is never put just below the border.
    expect(edgeOf(applied(document, must(addNote(document, 'm_3', () => 'n_a')).edit), 'n_a').props).toEqual({ in: 'f_1' });
    expect(edgeOf(applied(document, must(addNote(document, 'm_4', () => 'n_b')).edit), 'n_b').props).toEqual({ in: 'f_1' });
    expect(edgeOf(applied(document, must(addNote(document, 'm_5', () => 'n_c')).edit), 'n_c').props).toEqual({ in: 'f_1', operand: 1 });
    expect(edgeOf(applied(document, must(addNote(document, 'm_2', () => 'n_d')).edit), 'n_d').props).toBeUndefined();
  });

  it('goes at the end over the first participant when nothing is selected, and refuses a document with no participants', () => {
    const document = checkoutSequence();
    const after = applied(document, must(addNote(document, undefined, () => 'n_end')).edit);
    expect(after.edges.at(-1)).toMatchObject({ id: 'n_end', kind: 'note', from: { node: 'p_user' }, to: { node: 'p_user' } });
    expect(addNote({ ...document, nodes: [], edges: [] }, undefined)).toBeUndefined();
    // A fragment is not a participant to put a note over.
    expect(addNote({ ...document, nodes: [{ id: 'f', kind: 'fragment', label: 'alt', shape: 'rectangle' }], edges: [] }, undefined)).toBeUndefined();
  });

  it('avoids an id already in use', () => {
    const document = checkoutSequence();
    const seen: string[] = [];
    const generate = (): string => {
      seen.push('x');
      return seen.length === 1 ? 'm_1' : 'n_free';
    };
    expect(must(addNote(document, 'm_1', generate)).note).toBe('n_free');
  });
});

describe('toggleActivation', () => {
  it('opens a bar on the receiver and closes it at the receiver’s next return', () => {
    const document = checkoutSequence();
    // m_1 is Customer -> Web app; the Web app's next message out is m_2 (a sync), which closes the bar.
    const result = must(toggleActivation(document, 'm_1'));
    expect(result.active).toBe(true);
    const after = applied(document, result.edit);
    expect(edgeOf(after, 'm_1').props).toEqual({ activate: true });
    expect(edgeOf(after, 'm_2').props).toEqual({ activate: true, deactivate: true });
    expect(sequenceScene(after).bars.find((bar) => bar.participant === 'p_web')).toMatchObject({ opener: 'm_1', y: 108, h: 36 });
  });

  it('prefers a return as the closer, and turns a bar off with the deactivate that closed it', () => {
    const document = checkoutSequence();
    // Turn off the bar m_2 opens on the Orders API: m_6 closed it.
    const off = must(toggleActivation(document, 'm_2'));
    expect(off.active).toBe(false);
    const after = applied(document, off.edit);
    expect(edgeOf(after, 'm_2').props).toBeUndefined();
    expect(edgeOf(after, 'm_6').props).toBeUndefined();
    expect(sequenceScene(after).bars.map((bar) => bar.participant)).toEqual(['p_pay']);
    // Turning it on again writes it back exactly: m_6 is the Orders API's first return.
    const again = applied(after, must(toggleActivation(after, 'm_2')).edit);
    expect(edgeOf(again, 'm_2').props).toEqual({ activate: true });
    expect(edgeOf(again, 'm_6').props).toEqual({ deactivate: true });
  });

  it('leaves the bar open when nothing later comes back from the receiver', () => {
    const document = checkoutSequence();
    const last = must(toggleActivation(document, 'm_6'));
    // m_6 goes to the Web app, which sends nothing later: only the message itself changes.
    expect([...(last.edit.edgeProps?.keys() ?? [])]).toEqual(['m_6']);
  });

  it('refuses a note and a message that is not there', () => {
    const document = checkoutSequence();
    document.edges.push({ id: 'n_1', kind: 'note', from: { node: 'p_web' }, to: { node: 'p_api' } });
    expect(toggleActivation(document, 'n_1')).toBeUndefined();
    expect(toggleActivation(document, 'no_such')).toBeUndefined();
    expect(toggleActivation(document, 'm_1')).toBeDefined();
  });
});

describe('guards and operators', () => {
  it('sets the guard of an operand, padding a missing one, and does nothing for the same text', () => {
    const document = withAlt();
    const after = applied(document, must(setGuard(document, 'f_1', 0, '  [payment taken]  ')));
    expect(nodeOf(after, 'f_1').props).toEqual({ operator: 'alt', operands: ['payment taken', '[declined]'] });
    expect(setGuard(document, 'f_1', 0, '[card ok]')).toBeUndefined();
    const padded = applied(document, must(setGuard(document, 'f_1', 3, '[x]')));
    expect(nodeOf(padded, 'f_1').props?.operands).toEqual(['[card ok]', '[declined]', '', 'x']);
    expect(setGuard(document, 'no_such', 0, 'x')).toBeUndefined();
    // A line break a paste brought becomes a space: a guard is one line.
    expect(nodeOf(applied(document, must(setGuard(document, 'f_1', 1, 'a\nb'))), 'f_1').props?.operands).toEqual(['[card ok]', 'a b']);
  });

  it('sets the operator and the label together, and does nothing when it is unchanged', () => {
    const document = withAlt();
    const after = applied(document, must(setOperator(document, 'f_1', 'par')));
    expect(nodeOf(after, 'f_1')).toMatchObject({ label: 'par', props: { operator: 'par', operands: ['[card ok]', '[declined]'] } });
    expect(must(sequenceScene(after).fragments[0]).operator).toBe('par');
    expect(setOperator(document, 'f_1', 'alt')).toBeUndefined();
  });
});

describe('guard form', () => {
  it('draws a guard the same whether it is stored bare (authored) or in brackets (an older import)', () => {
    const bare = withAlt();
    nodeOf(bare, 'f_1').props = { operator: 'alt', operands: ['card ok', 'declined'] };
    const bracketed = withAlt();
    expect(must(sequenceScene(bare).fragments[0]).sections.map((s) => s.guard)).toEqual(['[card ok]', '[declined]']);
    expect(must(sequenceScene(bracketed).fragments[0]).sections.map((s) => s.guard)).toEqual(['[card ok]', '[declined]']);
    expect(guardDisplay('')).toBe('');
    expect(guardDisplay('  [ x ] ')).toBe('[x]');
  });

  it('stores a typed guard bare, dropping brackets the author typed, and treats the two forms as the same text', () => {
    const document = withAlt();
    const typed = applied(document, must(setGuard(document, 'f_1', 1, '[refused]')));
    expect(nodeOf(typed, 'f_1').props?.operands).toEqual(['[card ok]', 'refused']);
    const plain = applied(document, must(setGuard(document, 'f_1', 1, 'refused')));
    expect(nodeOf(plain, 'f_1').props?.operands).toEqual(['[card ok]', 'refused']);
    // '[declined]' is stored with brackets; typing 'declined' says the same thing, so it is not an edit.
    expect(setGuard(document, 'f_1', 1, 'declined')).toBeUndefined();
  });
});

describe('emptiedFragments', () => {
  const remove = (document: Document, ...gone: string[]): Document => ({
    ...document,
    nodes: document.nodes.filter((node) => !gone.includes(node.id)),
    edges: document.edges.filter((edge) => !gone.includes(edge.id)),
  });

  it('names a fragment whose last row was removed, and one that held only such a fragment', () => {
    const document = withAlt();
    expect(emptiedFragments(document, remove(document, 'm_3', 'm_4', 'm_5'))).toEqual(new Set(['f_1']));
    expect(emptiedFragments(document, remove(document, 'm_3', 'm_4'))).toEqual(new Set());
    const nested = withAlt();
    nested.nodes.push({ id: 'f_2', kind: 'fragment', label: 'opt', shape: 'rectangle', parent: 'f_1', props: { operator: 'opt', operands: ['x'], operand: 0 } });
    edgeOf(nested, 'm_4').props = { in: 'f_2' };
    expect(emptiedFragments(nested, remove(nested, 'm_3', 'm_4', 'm_5'))).toEqual(new Set(['f_1', 'f_2']));
  });

  it('leaves a frame that was already empty alone', () => {
    const document = withAlt();
    document.nodes.push({ id: 'f_9', kind: 'fragment', label: 'loop', shape: 'rectangle', props: { operator: 'loop', operands: ['x'] } });
    expect(emptiedFragments(document, remove(document, 'm_1'))).toEqual(new Set());
  });
});

describe('emptiedOperands', () => {
  const remove = (document: Document, ...gone: string[]): Document => ({
    ...document,
    nodes: document.nodes.filter((node) => !gone.includes(node.id)),
    edges: document.edges.filter((edge) => !gone.includes(edge.id)),
  });

  it('drops an operand whose last row was removed and renumbers the ones after it, keeping their guards', () => {
    const document = withAlt();
    // A third branch: m_6 under [else], so removing m_5 empties the middle operand.
    nodeOf(document, 'f_1').props = { operator: 'alt', operands: ['[card ok]', '[declined]', 'else'] };
    edgeOf(document, 'm_6').props = { ...edgeOf(document, 'm_6').props, in: 'f_1', operand: 2 };
    const after = remove(document, 'm_5');
    const edited = applied(after, must(emptiedOperands(document, after)));
    expect(nodeOf(edited, 'f_1').props).toEqual({ operator: 'alt', operands: ['[card ok]', 'else'] });
    expect(edgeOf(edited, 'm_6').props).toMatchObject({ in: 'f_1', operand: 1 });
    expect(edgeOf(edited, 'm_3').props).toMatchObject({ in: 'f_1' });
    expect(edgeOf(edited, 'm_3').props?.operand).toBeUndefined();
    // Drawn: two sections, guards in operand order.
    expect(must(sequenceScene(edited).fragments[0]).sections.map((section) => section.guard)).toEqual(['[card ok]', '[else]']);
  });

  it('drops the first operand too, so the frame opens with the next branch, and renumbers a nested fragment', () => {
    const document = withAlt();
    document.nodes.push({ id: 'f_2', kind: 'fragment', label: 'opt', shape: 'rectangle', parent: 'f_1', props: { operator: 'opt', operands: ['x'], operand: 1 } });
    edgeOf(document, 'm_5').props = { in: 'f_2' };
    const after = remove(document, 'm_3', 'm_4');
    const edited = applied(after, must(emptiedOperands(document, after)));
    expect(nodeOf(edited, 'f_1').props).toEqual({ operator: 'alt', operands: ['[declined]'] });
    expect(nodeOf(edited, 'f_2').props?.operand).toBeUndefined();
  });

  it('writes nothing when every operand still holds a row, when only operand was already empty, or when the frame is emptied outright', () => {
    const document = withAlt();
    expect(emptiedOperands(document, remove(document, 'm_3'))).toBeUndefined();
    const already = withAlt();
    nodeOf(already, 'f_1').props = { operator: 'alt', operands: ['[card ok]', '[declined]', 'never used'] };
    expect(emptiedOperands(already, remove(already, 'm_1'))).toBeUndefined();
    expect(emptiedOperands(document, remove(document, 'm_3', 'm_4', 'm_5'))).toBeUndefined();
  });
});

describe('addOperand from the fragment itself', () => {
  it('splits before the last row of the last operand when the frame is what is selected', () => {
    const document = withAlt();
    // Make operand 1 hold two rows, so the last of them can start a third.
    edgeOf(document, 'm_6').props = { in: 'f_1', operand: 1 };
    const added = must(addOperand(document, 'f_1'));
    expect(added).toMatchObject({ fragment: 'f_1', operand: 2 });
    const after = applied(document, added.edit);
    expect(nodeOf(after, 'f_1').props?.operands).toEqual(['[card ok]', '[declined]', 'else']);
    expect(edgeOf(after, 'm_6').props).toMatchObject({ in: 'f_1', operand: 2 });
    expect(edgeOf(after, 'm_5').props).toMatchObject({ in: 'f_1', operand: 1 });
  });

  it('says nothing when the last operand holds one row (an operand with no row is not drawn), or for a non-branching frame', () => {
    expect(addOperand(withAlt(), 'f_1')).toBeUndefined();
    const opt = withAlt();
    nodeOf(opt, 'f_1').props = { operator: 'opt', operands: ['x'] };
    expect(addOperand(opt, 'f_1')).toBeUndefined();
  });
});
