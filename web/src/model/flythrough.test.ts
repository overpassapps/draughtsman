import orderService from '../../../docs/examples/order-service.json';
import {
  addKeyframe,
  canonicalFlythrough,
  clampMilliseconds,
  DEFAULT_DURATION_MS,
  DEFAULT_EASING,
  DEFAULT_HOLD_MS,
  duplicateKeyframe,
  durationOf,
  easingOf,
  hasKeyframes,
  holdOf,
  isFrameable,
  loops,
  MAX_KEYFRAME_MS,
  MAX_KEYFRAMES,
  moveKeyframe,
  nextKeyframeId,
  removeKeyframe,
  setLoop,
  tourFromShots,
  updateKeyframe,
} from './flythrough.ts';
import { isDocument, parseDocument } from './parse.ts';
import { must } from './sequenceFixture.ts';
import type { Document, Flythrough, Keyframe, ViewRect } from './types.ts';

const view = (x: number): ViewRect => ({ x, y: 0, w: 100, h: 50 });
const frame = (id: string, x = 0): Keyframe => ({ id, view: view(x) });
const tour = (...ids: string[]): Flythrough => ({ keyframes: ids.map((id, index) => frame(id, index * 10)) });
const ids = (flythrough: Flythrough | undefined): string[] => flythrough?.keyframes.map((keyframe) => keyframe.id) ?? [];

describe('flythrough defaults', () => {
  it('answers the default for a sparse keyframe and the keyframe’s own value when it has one', () => {
    expect(durationOf(frame('k'))).toBe(DEFAULT_DURATION_MS);
    expect(holdOf(frame('k'))).toBe(DEFAULT_HOLD_MS);
    expect(easingOf(frame('k'))).toBe(DEFAULT_EASING);
    expect(durationOf({ ...frame('k'), durationMs: 0 })).toBe(0);
    expect(holdOf({ ...frame('k'), holdMs: 9 })).toBe(9);
    expect(easingOf({ ...frame('k'), easing: 'cut' })).toBe('cut');
  });

  it('treats a zero the author chose as theirs, not as absent', () => {
    expect(durationOf({ ...frame('k'), durationMs: 0 })).not.toBe(DEFAULT_DURATION_MS);
    expect(holdOf({ ...frame('k'), holdMs: 0 })).not.toBe(DEFAULT_HOLD_MS);
  });
});

describe('addKeyframe', () => {
  it('starts a tour when there is none, with the first id', () => {
    const added = addKeyframe(undefined, view(5));

    expect(added?.id).toBe('k_1');
    expect(added?.flythrough).toEqual({ keyframes: [{ id: 'k_1', view: view(5) }] });
  });

  it('appends by default and inserts at a position, clamped to the ends', () => {
    const base = tour('k_1', 'k_2', 'k_3');

    expect(ids(addKeyframe(base, view(0))?.flythrough)).toEqual(['k_1', 'k_2', 'k_3', 'k_4']);
    expect(ids(addKeyframe(base, view(0), 1)?.flythrough)).toEqual(['k_1', 'k_4', 'k_2', 'k_3']);
    expect(ids(addKeyframe(base, view(0), -5)?.flythrough)).toEqual(['k_4', 'k_1', 'k_2', 'k_3']);
    expect(ids(addKeyframe(base, view(0), 99)?.flythrough)).toEqual(['k_1', 'k_2', 'k_3', 'k_4']);
  });

  it('never hands out an id that is still in the tour, and fills the first gap a removal left', () => {
    expect(nextKeyframeId(undefined)).toBe('k_1');
    expect(nextKeyframeId(tour('k_1', 'k_3'))).toBe('k_2');
    expect(nextKeyframeId(tour('k_2', 'k_3'))).toBe('k_1');
    expect(nextKeyframeId(tour('k_1', 'k_2'))).toBe('k_3');
    expect(nextKeyframeId(tour('k_1', 'k_2', 'other', 'k_3'))).toBe('k_4');
  });

  it('refuses a view the camera cannot frame, and a tour that is full', () => {
    expect(addKeyframe(undefined, { x: 0, y: 0, w: 0, h: 10 })).toBeUndefined();
    expect(addKeyframe(undefined, { x: 0, y: 0, w: 10, h: -1 })).toBeUndefined();
    expect(addKeyframe(undefined, { x: Number.NaN, y: 0, w: 10, h: 10 })).toBeUndefined();
    expect(addKeyframe(undefined, { x: 0, y: 0, w: Number.POSITIVE_INFINITY, h: 10 })).toBeUndefined();
    const full: Flythrough = { keyframes: Array.from({ length: MAX_KEYFRAMES }, (_, n) => frame(`k_${String(n + 1)}`)) };

    expect(addKeyframe(full, view(0))).toBeUndefined();
    expect(addKeyframe({ keyframes: full.keyframes.slice(1) }, view(0))).toBeDefined();
    expect(isFrameable(view(0))).toBe(true);
  });

  it('keeps a caption given at capture and does not mutate its input', () => {
    const base = tour('k_1');
    const before = JSON.stringify(base);

    const added = addKeyframe(base, view(1), undefined, 'Hello');

    expect(added?.flythrough.keyframes[1]?.caption).toBe('Hello');
    expect(JSON.stringify(base)).toBe(before);
  });
});

describe('removeKeyframe', () => {
  it('removes one keyframe and keeps the rest in order', () => {
    expect(ids(removeKeyframe(tour('k_1', 'k_2', 'k_3'), 'k_2'))).toEqual(['k_1', 'k_3']);
  });

  it('removing the last keyframe removes the tour rather than leaving an empty one', () => {
    expect(removeKeyframe(tour('k_1'), 'k_1')).toBeUndefined();
  });

  it('leaves the tour as it was for an id it does not have', () => {
    const base = tour('k_1', 'k_2');

    expect(removeKeyframe(base, 'k_9')).toBe(base);
  });

  it('keeps the loop setting when it removes a keyframe', () => {
    expect(removeKeyframe({ ...tour('k_1', 'k_2'), loop: true }, 'k_2')?.loop).toBe(true);
  });
});

describe('moveKeyframe', () => {
  it('moves a keyframe later and earlier', () => {
    const base = tour('k_1', 'k_2', 'k_3', 'k_4');

    expect(ids(moveKeyframe(base, 'k_1', 2))).toEqual(['k_2', 'k_3', 'k_1', 'k_4']);
    expect(ids(moveKeyframe(base, 'k_4', 1))).toEqual(['k_1', 'k_4', 'k_2', 'k_3']);
  });

  it('clamps to the ends and changes nothing for a move to where it already is or an unknown id', () => {
    const base = tour('k_1', 'k_2', 'k_3');

    expect(ids(moveKeyframe(base, 'k_2', 99))).toEqual(['k_1', 'k_3', 'k_2']);
    expect(ids(moveKeyframe(base, 'k_2', -9))).toEqual(['k_2', 'k_1', 'k_3']);
    expect(moveKeyframe(base, 'k_2', 1)).toBe(base);
    expect(moveKeyframe(base, 'k_missing', 0)).toBe(base);
  });

  it('carries the keyframe’s own settings with it', () => {
    const base: Flythrough = { keyframes: [frame('k_1'), { ...frame('k_2'), caption: 'Two', durationMs: 7, easing: 'cut' }] };

    expect(moveKeyframe(base, 'k_2', 0).keyframes[0]).toEqual({ id: 'k_2', view: view(0), caption: 'Two', durationMs: 7, easing: 'cut' });
  });
});

describe('duplicateKeyframe', () => {
  it('puts a copy straight after the original under a fresh id', () => {
    const base: Flythrough = { keyframes: [frame('k_1'), { ...frame('k_2', 40), caption: 'Two', holdMs: 100 }, frame('k_3')] };

    const copy = duplicateKeyframe(base, 'k_2');

    expect(ids(copy?.flythrough)).toEqual(['k_1', 'k_2', 'k_4', 'k_3']);
    expect(copy?.id).toBe('k_4');
    expect(copy?.flythrough.keyframes[2]).toEqual({ id: 'k_4', view: view(40), caption: 'Two', holdMs: 100 });
  });

  it('copies nothing for an unknown id', () => {
    expect(duplicateKeyframe(tour('k_1'), 'k_9')).toBeUndefined();
  });

  it('shares nothing with the original, so editing the copy leaves it alone', () => {
    const copy = duplicateKeyframe(tour('k_1'), 'k_1');
    const made = must(copy);
    const changed = updateKeyframe(made.flythrough, made.id, { view: view(999) });

    expect(changed.keyframes[0]?.view.x).toBe(0);
    expect(changed.keyframes[1]?.view.x).toBe(999);
  });
});

describe('updateKeyframe', () => {
  const base: Flythrough = { keyframes: [frame('k_1'), { ...frame('k_2'), caption: 'Old', durationMs: 100, holdMs: 200, easing: 'linear' }] };

  it('changes only the fields it is given', () => {
    const next = updateKeyframe(base, 'k_2', { caption: 'New' });

    expect(next.keyframes[1]).toEqual({ id: 'k_2', view: view(0), caption: 'New', durationMs: 100, holdMs: 200, easing: 'linear' });
    expect(next.keyframes[0]).toEqual(base.keyframes[0]);
  });

  it('clears an optional field with null, back to its default, and only that one', () => {
    const next = updateKeyframe(base, 'k_2', { durationMs: null, easing: null });

    expect(next.keyframes[1]).toEqual({ id: 'k_2', view: view(0), caption: 'Old', holdMs: 200 });
    expect(durationOf(must(next.keyframes[1]))).toBe(DEFAULT_DURATION_MS);
  });

  it('clears a caption that is only spaces, but keeps one with text in it untouched', () => {
    expect(updateKeyframe(base, 'k_2', { caption: '   ' }).keyframes[1]?.caption).toBeUndefined();
    expect(updateKeyframe(base, 'k_2', { caption: '  keep my spaces  ' }).keyframes[1]?.caption).toBe('  keep my spaces  ');
  });

  it('clamps times into the schema’s range and to whole milliseconds', () => {
    const next = updateKeyframe(base, 'k_2', { durationMs: -50, holdMs: 1e9 });
    expect(next.keyframes[1]?.durationMs).toBe(0);
    expect(next.keyframes[1]?.holdMs).toBe(MAX_KEYFRAME_MS);
    expect(updateKeyframe(base, 'k_2', { durationMs: 1234.6 }).keyframes[1]?.durationMs).toBe(1235);
    expect(clampMilliseconds(Number.NaN)).toBe(0);
    expect(clampMilliseconds(0)).toBe(0);
    expect(clampMilliseconds(MAX_KEYFRAME_MS)).toBe(MAX_KEYFRAME_MS);
    expect(clampMilliseconds(MAX_KEYFRAME_MS + 1)).toBe(MAX_KEYFRAME_MS);
  });

  it('takes a new view, and ignores one the camera cannot frame', () => {
    expect(updateKeyframe(base, 'k_2', { view: view(77) }).keyframes[1]?.view).toEqual(view(77));
    expect(updateKeyframe(base, 'k_2', { view: { x: 0, y: 0, w: 0, h: 5 } }).keyframes[1]?.view).toEqual(view(0));
  });

  it('leaves the tour as it was for an unknown id', () => {
    expect(updateKeyframe(base, 'k_9', { caption: 'x' })).toBe(base);
  });
});

describe('tourFromShots', () => {
  it('numbers the shots in order and keeps each caption and view', () => {
    const tourOf = tourFromShots([{ view: view(0), caption: 'Overview' }, { view: view(10) }, { view: view(20), caption: 'Last' }]);

    expect(tourOf).toEqual({ keyframes: [{ id: 'k_1', view: view(0), caption: 'Overview' }, { id: 'k_2', view: view(10) }, { id: 'k_3', view: view(20), caption: 'Last' }] });
  });

  it('answers no tour for no shots, leaves out a shot the camera cannot frame, and stops at the cap', () => {
    expect(tourFromShots([])).toBeUndefined();
    expect(tourFromShots([{ view: { x: 0, y: 0, w: 0, h: 5 } }])).toBeUndefined();
    expect(ids(tourFromShots([{ view: view(0) }, { view: { x: 0, y: 0, w: -1, h: 5 } }, { view: view(2) }]))).toEqual(['k_1', 'k_2']);
    const many = Array.from({ length: MAX_KEYFRAMES + 5 }, (_, n) => ({ view: view(n) }));
    expect(tourFromShots(many)?.keyframes).toHaveLength(MAX_KEYFRAMES);
  });

  it('keeps the loop setting of the tour it replaces, and does not invent one', () => {
    const shots = [{ view: view(0) }, { view: view(1) }];

    expect(tourFromShots(shots, { ...tour('k_1'), loop: true })?.loop).toBe(true);
    expect(tourFromShots(shots, tour('k_1'))?.loop).toBeUndefined();
    expect(tourFromShots(shots)?.loop).toBeUndefined();
  });
});

describe('loop', () => {
  it('turning it on writes the member and turning it off removes it, so the document stays sparse', () => {
    const on = setLoop(tour('k_1', 'k_2'), true);
    expect(on.loop).toBe(true);
    const off = setLoop(on, false);
    expect('loop' in off).toBe(false);
    expect(off.keyframes).toEqual(on.keyframes);
  });

  it('a one-keyframe tour never loops, since there is nowhere to go back to', () => {
    expect(loops({ ...tour('k_1'), loop: true })).toBe(false);
    expect(loops({ ...tour('k_1', 'k_2'), loop: true })).toBe(true);
    expect(loops(tour('k_1', 'k_2'))).toBe(false);
    expect(loops({ ...tour('k_1', 'k_2'), loop: false })).toBe(false);
  });
});

describe('canonicalFlythrough', () => {
  it('rebuilds members in the schema’s order and drops anything undefined', () => {
    const scrambled = {
      loop: true,
      keyframes: [{ easing: 'cut', holdMs: 1, durationMs: 2, caption: 'c', view: { h: 4, w: 3, y: 2, x: 1 }, id: 'k_1' }, { id: 'k_2', view: view(0), caption: undefined, easing: undefined }],
    } as unknown as Flythrough;

    const canonical = must(canonicalFlythrough(scrambled));

    expect(Object.keys(canonical)).toEqual(['keyframes', 'loop']);
    expect(Object.keys(must(canonical.keyframes[0]))).toEqual(['id', 'view', 'caption', 'durationMs', 'holdMs', 'easing']);
    expect(Object.keys(must(canonical.keyframes[0]).view)).toEqual(['x', 'y', 'w', 'h']);
    expect(Object.keys(must(canonical.keyframes[1]))).toEqual(['id', 'view']);
    expect(JSON.stringify(canonical)).not.toContain('undefined');
  });

  it('answers undefined for no tour and for a tour with no keyframes, and the tour itself otherwise', () => {
    expect(canonicalFlythrough(undefined)).toBeUndefined();
    expect(canonicalFlythrough({ keyframes: [], loop: true })).toBeUndefined();
    expect(hasKeyframes(canonicalFlythrough(tour('k_1')))).toBe(true);
    expect(hasKeyframes(undefined)).toBe(false);
    expect(hasKeyframes({ keyframes: [] })).toBe(false);
  });

  it('is idempotent', () => {
    const once = canonicalFlythrough({ ...tour('k_1', 'k_2'), loop: true });

    expect(canonicalFlythrough(once)).toEqual(once);
  });
});

describe('the document contract', () => {
  it('the shipped example carries a tour that the schema and the types accept', () => {
    const parsed = parseDocument(JSON.parse(JSON.stringify(orderService)));

    expect(parsed.flythrough?.keyframes.map((keyframe) => keyframe.id)).toEqual(['k_overview', 'k_web', 'k_vpc', 'k_data']);
    expect(parsed.flythrough?.loop).toBe(true);
    expect(parsed.schemaVersion).toBe(1);
  });

  it('every edit this module makes still passes the schema, so the editor can never save a tour the server refuses', () => {
    const base = parseDocument(JSON.parse(JSON.stringify(orderService)));
    const edited = (flythrough: Flythrough | undefined): Document => ({ ...base, ...(flythrough === undefined ? {} : { flythrough }) });
    let current = base.flythrough;

    current = addKeyframe(current, view(3), 1, 'Inserted')?.flythrough;
    expect(isDocument(edited(current))).toBe(true);
    current = updateKeyframe(must(current), 'k_1', { caption: 'Edited', durationMs: 0, holdMs: MAX_KEYFRAME_MS, easing: 'cut' });
    expect(isDocument(edited(current))).toBe(true);
    current = moveKeyframe(current, 'k_1', 0);
    expect(isDocument(edited(current))).toBe(true);
    current = duplicateKeyframe(current, 'k_web')?.flythrough;
    expect(isDocument(edited(current))).toBe(true);
    current = setLoop(must(current), false);
    expect(isDocument(edited(current))).toBe(true);
  });

  it('the schema still refuses what these edits clamp away, so the clamp is what keeps a save valid', () => {
    const base = parseDocument(JSON.parse(JSON.stringify(orderService)));
    const bad = (mutate: (keyframe: Keyframe) => void): Document => {
      const copy = structuredClone(base);
      mutate(must(must(copy.flythrough).keyframes[0]));
      return copy;
    };

    expect(isDocument(bad((k) => { k.durationMs = -1; }))).toBe(false);
    expect(isDocument(bad((k) => { k.holdMs = MAX_KEYFRAME_MS + 1; }))).toBe(false);
    expect(isDocument(bad((k) => { k.view.w = 0; }))).toBe(false);
    expect(isDocument({ ...base, flythrough: { keyframes: [] } })).toBe(false);
  });
});
