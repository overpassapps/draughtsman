import * as Y from 'yjs';
import { documentToYDoc, EDGES_MAP, yDocToDocument } from './editor.ts';
import {
  createMessage,
  createParticipant,
  insertEdgeAt,
  insertionIndex,
  messageSlot,
  participantSlot,
  reorderedMessages,
  reorderedParticipants,
  rowBand,
  slotLineX,
  slotLineY,
} from './sequenceEdit.ts';
import { sequenceScene } from './sequenceScene.ts';
import { checkoutFragments, checkoutSequence, must } from './sequenceFixture.ts';

// The checkout example's rows (docs/sequence-diagrams.md): message lines at y = 108, 144, 180, 216, 252, 288 and the
// self message's top at 326; lifelines at x = 80, 220, 360, 500. A row is 36 high (52 for the self message).
const document = checkoutSequence();
const scene = sequenceScene(document);
const ids = (items: readonly { id: string }[] | undefined): string[] => (items ?? []).map((item) => item.id);

describe('rowBand', () => {
  it('is the row a message occupies: 36 high around the line, 52 from 20 above a self message’s top segment', () => {
    expect(rowBand({ y: 108, self: false })).toEqual({ top: 90, bottom: 126, mid: 108 });
    expect(rowBand({ y: 326, self: true })).toEqual({ top: 306, bottom: 358, mid: 332 });
  });
});

describe('messageSlot', () => {
  it('counts the messages whose row midpoint is above the pointer', () => {
    expect(messageSlot(scene, 100)).toBe(0);
    expect(messageSlot(scene, 110)).toBe(1);
    expect(messageSlot(scene, 250)).toBe(4);
    expect(messageSlot(scene, 260)).toBe(5);
    expect(messageSlot(scene, 1000)).toBe(7);
  });

  it('counts a self message by its own row midpoint, not its top segment', () => {
    // The self message's top segment is at 326 but its row runs 306 to 358, midpoint 332.
    expect(messageSlot(scene, 330)).toBe(6);
    expect(messageSlot(scene, 334)).toBe(7);
  });

  it('leaves the dragged message out, so a pointer over its own row keeps it where it is', () => {
    expect(messageSlot(scene, 180, 'm_3')).toBe(2);
    expect(messageSlot(scene, 200, 'm_3')).toBe(2);
    expect(messageSlot(scene, 250, 'm_3')).toBe(3);
  });
});

describe('slotLineY', () => {
  it('sits at the top of the row the message would precede, or the end of the last row', () => {
    expect(slotLineY(scene, 0)).toBe(90);
    expect(slotLineY(scene, 3)).toBe(198);
    expect(slotLineY(scene, 7)).toBe(358);
  });

  it('measures against the messages that stay when one is being dragged', () => {
    expect(slotLineY(scene, 2, 'm_3')).toBe(198);
    expect(slotLineY(scene, 6, 'm_7')).toBe(306);
  });

  it('starts below the headers for a diagram with no messages', () => {
    const empty = sequenceScene({ ...document, edges: [] });
    expect(slotLineY(empty, 0)).toBe(90);
  });
});

describe('insertionIndex', () => {
  it('is the index of the edge drawn at the slot, or the end', () => {
    expect(insertionIndex(document, scene, 0)).toBe(0);
    expect(insertionIndex(document, scene, 3)).toBe(3);
    expect(insertionIndex(document, scene, 7)).toBe(7);
  });

  it('counts an edge that is not drawn by its place in the document, not in the scene', () => {
    const dangling = { id: 'm_x', kind: 'sync', from: { node: 'p_user' }, to: { node: 'p_nobody' } };
    const withDangling = { ...document, edges: [document.edges[0], dangling, ...document.edges.slice(1)].map(must) };
    const drawn = sequenceScene(withDangling);
    expect(drawn.messages).toHaveLength(7);
    // Slot 1 is before m_2, which is at index 2 of the edges because the undrawn edge sits at 1.
    expect(insertionIndex(withDangling, drawn, 1)).toBe(2);
    expect(insertionIndex(withDangling, drawn, 7)).toBe(8);
  });
});

describe('reorderedMessages', () => {
  it('moves a message to the slot the pointer picks', () => {
    expect(ids(reorderedMessages(document, scene, 'm_3', 250))).toEqual(['m_1', 'm_2', 'm_4', 'm_3', 'm_5', 'm_6', 'm_7']);
    expect(ids(reorderedMessages(document, scene, 'm_1', 1000))).toEqual(['m_2', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7', 'm_1']);
    expect(ids(reorderedMessages(document, scene, 'm_7', 100))).toEqual(['m_7', 'm_1', 'm_2', 'm_3', 'm_4', 'm_5', 'm_6']);
  });

  it('returns nothing when the message would land where it already is', () => {
    expect(reorderedMessages(document, scene, 'm_3', 180)).toBeUndefined();
    expect(reorderedMessages(document, scene, 'm_3', 200)).toBeUndefined();
    expect(reorderedMessages(document, scene, 'm_1', 0)).toBeUndefined();
    expect(reorderedMessages(document, scene, 'm_7', 1000)).toBeUndefined();
    expect(reorderedMessages(document, scene, 'nobody', 100)).toBeUndefined();
  });
});

describe('participants', () => {
  it('picks the slot by lifeline x among the other participants', () => {
    expect(participantSlot(scene, 0)).toBe(0);
    expect(participantSlot(scene, 300, 'p_user')).toBe(1);
    expect(participantSlot(scene, 900)).toBe(4);
  });

  it('draws the insertion line in the gap before the header it would precede, or after the last', () => {
    expect(slotLineX(scene, 1)).toBe(150);
    expect(slotLineX(scene, 4)).toBe(570);
    expect(slotLineX(scene, 0, 'p_user')).toBe(150);
  });

  it('moves a participant to the nearest place and reports nothing when it stays', () => {
    expect(ids(reorderedParticipants(document, scene, 'p_user', 300))).toEqual(['p_web', 'p_user', 'p_api', 'p_pay']);
    expect(ids(reorderedParticipants(document, scene, 'p_user', 900))).toEqual(['p_web', 'p_api', 'p_pay', 'p_user']);
    expect(ids(reorderedParticipants(document, scene, 'p_pay', 0))).toEqual(['p_pay', 'p_user', 'p_web', 'p_api']);
    expect(reorderedParticipants(document, scene, 'p_web', 200)).toBeUndefined();
    expect(reorderedParticipants(document, scene, 'p_user', 100)).toBeUndefined();
  });
});

describe('createParticipant and createMessage', () => {
  it('makes a participant with no geometry and an id the document does not use', () => {
    const proposals = ['p_web', 'p_new'];
    const node = createParticipant(document, { kind: 'actor', shape: 'rounded', label: 'Actor' }, () => must(proposals.shift()));
    expect(node.id).toBe('p_new');
    expect(node).toEqual({ id: node.id, kind: 'actor', label: 'Actor', shape: 'rounded' });
    expect(node).not.toHaveProperty('geometry');
    expect(document.nodes.some((existing) => existing.id === node.id)).toBe(false);
  });

  it('makes a sync message with no port and no routing, asking again while the id is taken', () => {
    const proposals = ['m_1', 'p_web', 'm_new'];
    const message = createMessage(document, 'p_user', 'p_web', () => must(proposals.shift()));
    expect(message).toEqual({ id: 'm_new', kind: 'sync', from: { node: 'p_user' }, to: { node: 'p_web' } });
  });
});

describe('insertEdgeAt', () => {
  function edgeOrders(doc: Y.Doc): Map<string, number> {
    return new Map([...doc.getMap<Y.Map<unknown>>(EDGES_MAP)].map(([id, map]) => [id, map.get('order') as number]));
  }
  const message = { id: 'm_new', kind: 'sync', from: { node: 'p_user' }, to: { node: 'p_pay' } };

  it('inserts mid-stream and shifts only the later edges’ order fields', () => {
    const doc = documentToYDoc(document);
    const written: string[] = [];
    doc.getMap<Y.Map<unknown>>(EDGES_MAP).observeDeep((events) => {
      for (const event of events) {
        for (const key of (event as Y.YMapEvent<unknown>).keysChanged) {
          written.push(`${String(event.path[0] ?? 'edges')}.${String(key)}`);
        }
      }
    });
    insertEdgeAt(doc, message, 3);
    expect(ids(yDocToDocument(doc).edges)).toEqual(['m_1', 'm_2', 'm_3', 'm_new', 'm_4', 'm_5', 'm_6', 'm_7']);
    // m_1 to m_3 are untouched; m_4 to m_7 moved one place; the new edge is written whole.
    expect(written.filter((entry) => entry.endsWith('.order')).map((entry) => entry.split('.')[0]).sort()).toEqual(['m_4', 'm_5', 'm_6', 'm_7']);
    expect(edgeOrders(doc).get('m_new')).toBe(3);
  });

  it('appends at the end and renumbers nothing else', () => {
    const doc = documentToYDoc(document);
    insertEdgeAt(doc, message, 99);
    expect(ids(yDocToDocument(doc).edges).at(-1)).toBe('m_new');
    expect(edgeOrders(doc).get('m_7')).toBe(6);
  });

  it('closes gaps in the order numbers the way it finds them, so the new place is exact', () => {
    // An earlier delete left order numbers 0, 2, 5: an insert at 1 must still land second.
    const doc = documentToYDoc({ ...document, edges: document.edges.slice(0, 3) });
    const edges = doc.getMap<Y.Map<unknown>>(EDGES_MAP);
    must(edges.get('m_2')).set('order', 2);
    must(edges.get('m_3')).set('order', 5);
    insertEdgeAt(doc, message, 1);
    expect(ids(yDocToDocument(doc).edges)).toEqual(['m_1', 'm_new', 'm_2', 'm_3']);
  });

  it('refuses a taken id or an unknown end, writing nothing', () => {
    const doc = documentToYDoc(document);
    const before = JSON.stringify(yDocToDocument(doc));
    expect(() => {
      insertEdgeAt(doc, { ...message, id: 'm_2' }, 0);
    }).toThrow(/already in the document/);
    expect(() => {
      insertEdgeAt(doc, { ...message, to: { node: 'nobody' } }, 0);
    }).toThrow(/not in the document/);
    expect(JSON.stringify(yDocToDocument(doc))).toBe(before);
  });
});

describe('a drop that leaves the drawn order alone', () => {
  // docs/examples/checkout-fragments.json stores its fragment f_1 after the last participant and its note n_1 between
  // two messages, elements the scene does not draw as lifelines or message rows. Moving the last participant "to the
  // end" used to reorder it past the fragment in the array: a change nothing shows, but one that dirties the document.
  const fragments = checkoutFragments();
  const fragmentScene = sequenceScene(fragments);

  it('is not a participant move when the last participant is dropped past the end', () => {
    expect(reorderedParticipants(fragments, fragmentScene, 'p_mail', 5000)).toBeUndefined();
  });

  it('is not a message move when the last message is dropped past the end, or one is dropped by a note', () => {
    expect(reorderedMessages(fragments, fragmentScene, 'm_9', 5000)).toBeUndefined();
    const m2 = must(fragmentScene.messages.find((message) => message.id === 'm_2'));
    expect(reorderedMessages(fragments, fragmentScene, 'm_2', rowBand(m2).mid)).toBeUndefined();
  });

  it('still moves a participant that really changes place', () => {
    expect(ids(reorderedParticipants(fragments, fragmentScene, 'p_user', 5000)).filter((id) => id.startsWith('p_'))).toEqual(['p_web', 'p_api', 'p_pay', 'p_mail', 'p_user']);
  });
});
