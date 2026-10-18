import * as Y from 'yjs';
import orderService from '../../../docs/examples/order-service.json';
import { applyDocumentDiff } from './documentDiff.ts';
import { createUndoManager, documentToYDoc, endUndoStep, LOCAL_ORIGIN, readFlythrough, REMOTE_ORIGIN, setFlythrough, yDocToDocument } from './editor.ts';
import { parseDocument } from './parse.ts';
import { mergeRemote, takeRemote } from './remoteMerge.ts';
import { must } from './sequenceFixture.ts';
import type { Document, Flythrough } from './types.ts';

const example = (): Document => parseDocument(JSON.parse(JSON.stringify(orderService)));

const tour = (...captions: string[]): Flythrough => ({
  keyframes: captions.map((caption, index) => ({ id: `k_${String(index + 1)}`, view: { x: index * 10, y: 0, w: 200, h: 100 }, caption })),
});

const withTour = (flythrough: Flythrough | undefined): Document => {
  const document = example();
  Reflect.deleteProperty(document, 'flythrough');
  return flythrough === undefined ? document : { ...document, flythrough };
};

describe('the flythrough in the Y.Doc', () => {
  it('round-trips the example’s tour byte for byte, members in the schema’s order', () => {
    const original = example();

    const restored = yDocToDocument(documentToYDoc(original));

    expect(restored.flythrough).toEqual(original.flythrough);
    expect(JSON.stringify(restored)).toBe(JSON.stringify(original));
    expect(Object.keys(restored).at(-1)).toBe('flythrough');
  });

  it('holds the tour as one plain value in the header map, where the sidecar’s shape puts it', () => {
    const doc = documentToYDoc(example());

    expect(doc.getMap('header').get('flythrough')).toEqual(example().flythrough);
    expect(doc.getMap('header').get('flythrough')).not.toBeInstanceOf(Y.AbstractType);
  });

  it('carries no member for a document with no tour', () => {
    const doc = documentToYDoc(withTour(undefined));

    expect(doc.getMap('header').has('flythrough')).toBe(false);
    expect(Object.keys(yDocToDocument(doc))).not.toContain('flythrough');
    expect(readFlythrough(doc)).toBeUndefined();
  });

  it('rebuilds a scrambled tour in canonical order on the way in and on the way out', () => {
    const scrambled = { loop: true, keyframes: [{ easing: 'cut', caption: 'c', view: { h: 4, w: 3, y: 2, x: 1 }, id: 'k_1' }] } as unknown as Flythrough;

    const restored = yDocToDocument(documentToYDoc({ ...withTour(undefined), flythrough: scrambled }));

    const tour = must(restored.flythrough);
    expect(Object.keys(tour)).toEqual(['keyframes', 'loop']);
    expect(Object.keys(must(tour.keyframes[0]))).toEqual(['id', 'view', 'caption', 'easing']);
    expect(Object.keys(must(tour.keyframes[0]).view)).toEqual(['x', 'y', 'w', 'h']);
  });
});

describe('setFlythrough', () => {
  it('writes the tour, reads it back, and reports a change', () => {
    const doc = documentToYDoc(withTour(undefined));

    expect(setFlythrough(doc, tour('One', 'Two'))).toBe(true);

    expect(yDocToDocument(doc).flythrough).toEqual(tour('One', 'Two'));
    expect(readFlythrough(doc)).toEqual(tour('One', 'Two'));
  });

  it('reports no change, and records no undo step, for the tour already there', () => {
    const doc = documentToYDoc(withTour(tour('One')));
    const undo = createUndoManager(doc);

    expect(setFlythrough(doc, tour('One'))).toBe(false);
    endUndoStep(undo);

    expect(undo.undoStack).toHaveLength(0);
    // The other direction: a real change is one step, so the assertion above is about the write, not the manager.
    expect(setFlythrough(doc, tour('Changed'))).toBe(true);
    endUndoStep(undo);
    expect(undo.undoStack).toHaveLength(1);
  });

  it('removes the member for undefined and for a tour with no keyframes', () => {
    const doc = documentToYDoc(withTour(tour('One')));

    expect(setFlythrough(doc, { keyframes: [], loop: true })).toBe(true);
    expect(doc.getMap('header').has('flythrough')).toBe(false);
    expect(setFlythrough(doc, undefined)).toBe(false);
    expect(setFlythrough(doc, tour('Back'))).toBe(true);
    expect(setFlythrough(doc, undefined)).toBe(true);
    expect(yDocToDocument(doc).flythrough).toBeUndefined();
  });

  it('is an undo step for the local user and invisible to undo when another writer’s origin made it', () => {
    const doc = documentToYDoc(withTour(undefined));
    const undo = createUndoManager(doc);

    setFlythrough(doc, tour('Mine'), LOCAL_ORIGIN);
    endUndoStep(undo);
    setFlythrough(doc, tour('Theirs'), REMOTE_ORIGIN);
    endUndoStep(undo);

    expect(undo.undoStack).toHaveLength(1);
    undo.undo();
    // Undo took back only the local step: the remote write had replaced it, so the model is whatever the remote left.
    expect(yDocToDocument(doc).flythrough).toEqual(tour('Theirs'));
  });

  it('undo restores the exact tour before, and redo the exact tour after', () => {
    const doc = documentToYDoc(withTour(tour('Before')));
    const undo = createUndoManager(doc);

    setFlythrough(doc, tour('After', 'Two'));
    endUndoStep(undo);
    undo.undo();
    expect(yDocToDocument(doc).flythrough).toEqual(tour('Before'));
    undo.redo();
    expect(yDocToDocument(doc).flythrough).toEqual(tour('After', 'Two'));
  });
});

describe('merging another writer’s tour', () => {
  const base = withTour(tour('Base'));
  const remoteWith = (flythrough: Flythrough | undefined): Document => ({ ...withTour(flythrough), meta: { ...base.meta, updatedAt: '2026-09-29T10:00:00.000Z' } });

  it('takes the other writer’s tour when the local user has not touched it', () => {
    const doc = documentToYDoc(base);

    const result = mergeRemote(doc, base, remoteWith(tour('Base', 'Agent added')));

    expect(result).toEqual({ changed: true, conflicts: [] });
    expect(readFlythrough(doc)).toEqual(tour('Base', 'Agent added'));
  });

  it('takes the removal of the tour, too', () => {
    const doc = documentToYDoc(base);

    expect(mergeRemote(doc, base, remoteWith(undefined)).conflicts).toEqual([]);
    expect(readFlythrough(doc)).toBeUndefined();
  });

  it('keeps the local user’s unsaved tour when the other writer left it alone', () => {
    const doc = documentToYDoc(base);
    setFlythrough(doc, tour('Mine'));

    const result = mergeRemote(doc, base, remoteWith(tour('Base')));

    expect(result.conflicts).toEqual([]);
    expect(readFlythrough(doc)).toEqual(tour('Mine'));
  });

  it('keeps the local tour and reports the clash when both sides changed it differently', () => {
    const doc = documentToYDoc(base);
    setFlythrough(doc, tour('Mine'));

    const result = mergeRemote(doc, base, remoteWith(tour('Theirs')));

    expect(result.conflicts).toEqual([{ kind: 'document', id: base.id, reason: 'both-changed', fields: ['flythrough'] }]);
    expect(readFlythrough(doc)).toEqual(tour('Mine'));
  });

  it('reports no clash when both sides made the same change', () => {
    const doc = documentToYDoc(base);
    setFlythrough(doc, tour('Same'));

    expect(mergeRemote(doc, base, remoteWith(tour('Same'))).conflicts).toEqual([]);
  });

  it('takes the other writer’s side of a clash when the user asks for theirs', () => {
    const doc = documentToYDoc(base);
    setFlythrough(doc, tour('Mine'));
    const remote = remoteWith(tour('Theirs'));
    const { conflicts } = mergeRemote(doc, base, remote);

    takeRemote(doc, remote, conflicts);

    expect(readFlythrough(doc)).toEqual(tour('Theirs'));
  });
});

describe('the Text panel’s apply', () => {
  it('writes a tour typed into the text, as part of the same diff that changes the graph', () => {
    const before = withTour(undefined);
    const doc = documentToYDoc(before);
    const after: Document = { ...before, title: 'Retitled', flythrough: tour('From text') };

    expect(applyDocumentDiff(doc, after, before)).toBe(true);

    const applied = yDocToDocument(doc);
    expect(applied.flythrough).toEqual(tour('From text'));
    expect(applied.title).toBe('Retitled');
  });

  it('removes the tour when the text no longer has keyframes, and leaves it alone when the text is unchanged', () => {
    const before = withTour(tour('Keep me'));
    const doc = documentToYDoc(before);

    expect(applyDocumentDiff(doc, before, before)).toBe(false);
    expect(readFlythrough(doc)).toEqual(tour('Keep me'));

    const without = structuredClone(before);
    Reflect.deleteProperty(without, 'flythrough');
    expect(applyDocumentDiff(doc, without, before)).toBe(true);
    expect(readFlythrough(doc)).toBeUndefined();
  });
});
