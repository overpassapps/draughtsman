/**
 * The flythrough's pure edits (docs/flythrough.md): the camera path a presentation plays over a document, as an
 * optional member of the document (`Document.flythrough`, model/types.ts). Every function takes the path and returns a
 * new one, never mutating, so the editor can write the result as one undo step (editor/useDocumentEditor.ts
 * `setFlythrough`) and a test can compare before and after.
 *
 * A tour with no keyframes is absent, never empty: `removeKeyframe` of the last one, like `canonicalFlythrough` of an
 * empty list, answers undefined, which the editor writes as the member being removed. A keyframe's timings and easing
 * are sparse (absent means the default below), so the document carries only what the author chose.
 *
 * Members are always rebuilt in the order the schema and the C# serialiser use, so the same tour serialises to the same
 * bytes on either side of the boundary (`canonicalFlythrough`).
 */

import type { Flythrough, Id, Keyframe, KeyframeEasing, ViewRect } from './types.ts';

/** Milliseconds the camera takes to arrive at a keyframe that does not say. */
export const DEFAULT_DURATION_MS = 1500;

/** Milliseconds an autoplaying tour stays at a keyframe that does not say. */
export const DEFAULT_HOLD_MS = 2500;

/** The easing of a keyframe that does not say: smooth in and out. */
export const DEFAULT_EASING: KeyframeEasing = 'easeInOut';

/** The longest a keyframe may take to arrive or stay: the schema's ten minutes. */
export const MAX_KEYFRAME_MS = 600_000;

/** The schema's cap on a tour, well past anything a person presents. */
export const MAX_KEYFRAMES = 500;

/** Every easing, in the order the editor lists them. */
export const EASINGS: readonly KeyframeEasing[] = ['easeInOut', 'easeIn', 'easeOut', 'linear', 'cut'];

/** What the editor calls each easing. */
export const EASING_LABELS: Readonly<Record<KeyframeEasing, string>> = {
  easeInOut: 'Smooth',
  easeIn: 'Ease in',
  easeOut: 'Ease out',
  linear: 'Even',
  cut: 'Cut',
};

export function durationOf(keyframe: Keyframe): number {
  return keyframe.durationMs ?? DEFAULT_DURATION_MS;
}

export function holdOf(keyframe: Keyframe): number {
  return keyframe.holdMs ?? DEFAULT_HOLD_MS;
}

export function easingOf(keyframe: Keyframe): KeyframeEasing {
  return keyframe.easing ?? DEFAULT_EASING;
}

/** Whether a rectangle is one a camera can frame: finite, with an area. The schema says the same of a keyframe's view. */
export function isFrameable(view: ViewRect): boolean {
  return [view.x, view.y, view.w, view.h].every(Number.isFinite) && view.w > 0 && view.h > 0;
}

/** A time in whole milliseconds inside the schema's range; NaN becomes 0. */
export function clampMilliseconds(value: number): number {
  return Number.isFinite(value) ? Math.min(MAX_KEYFRAME_MS, Math.max(0, Math.round(value))) : 0;
}

function canonicalKeyframe(keyframe: Keyframe): Keyframe {
  const { id, view, caption, durationMs, holdMs, easing } = keyframe;
  return {
    id,
    view: { x: view.x, y: view.y, w: view.w, h: view.h },
    ...(caption === undefined ? {} : { caption }),
    ...(durationMs === undefined ? {} : { durationMs }),
    ...(holdMs === undefined ? {} : { holdMs }),
    ...(easing === undefined ? {} : { easing }),
  };
}

/**
 * The tour as the schema and the C# serialiser write it: members in canonical order, nothing undefined, and undefined
 * for a tour with no keyframes. A caption of '' is kept as written; the editor decides whether an empty field clears it.
 */
export function canonicalFlythrough(flythrough: Flythrough | undefined): Flythrough | undefined {
  if (flythrough === undefined || flythrough.keyframes.length === 0) {
    return undefined;
  }
  return { keyframes: flythrough.keyframes.map(canonicalKeyframe), ...(flythrough.loop === undefined ? {} : { loop: flythrough.loop }) };
}

/** Whether a document's tour has anything to play. */
export function hasKeyframes(flythrough: Flythrough | undefined): flythrough is Flythrough {
  return flythrough !== undefined && flythrough.keyframes.length > 0;
}

/** The first `k_<n>` no keyframe of the tour uses, so two keyframes of one tour never share an id. */
export function nextKeyframeId(flythrough: Flythrough | undefined): Id {
  const taken = new Set(flythrough?.keyframes.map((keyframe) => keyframe.id));
  for (let n = 1; ; n += 1) {
    const id = `k_${String(n)}`;
    if (!taken.has(id)) {
      return id;
    }
  }
}

function indexOfKeyframe(flythrough: Flythrough, id: Id): number {
  return flythrough.keyframes.findIndex((keyframe) => keyframe.id === id);
}

export interface AddedKeyframe {
  flythrough: Flythrough;
  id: Id;
}

/**
 * Adds a keyframe framing `view` at position `at` (the end by default). A view the camera cannot frame, or a tour
 * already at MAX_KEYFRAMES, adds nothing and answers undefined.
 */
export function addKeyframe(flythrough: Flythrough | undefined, view: ViewRect, at?: number, caption?: string): AddedKeyframe | undefined {
  if (!isFrameable(view) || (flythrough?.keyframes.length ?? 0) >= MAX_KEYFRAMES) {
    return undefined;
  }
  const id = nextKeyframeId(flythrough);
  const keyframes = [...(flythrough?.keyframes ?? [])];
  const index = at === undefined ? keyframes.length : Math.min(keyframes.length, Math.max(0, Math.trunc(at)));
  keyframes.splice(index, 0, canonicalKeyframe({ id, view, ...(caption === undefined ? {} : { caption }) }));
  return { flythrough: { ...flythrough, keyframes }, id };
}

/** The tour without the keyframe; undefined when that was the last one (a tour is never empty). An unknown id changes nothing. */
export function removeKeyframe(flythrough: Flythrough, id: Id): Flythrough | undefined {
  const index = indexOfKeyframe(flythrough, id);
  if (index < 0) {
    return flythrough;
  }
  const keyframes = flythrough.keyframes.filter((_, position) => position !== index);
  return keyframes.length === 0 ? undefined : { ...flythrough, keyframes };
}

/** Moves a keyframe to position `to`, clamped to the ends. An unknown id, or a move to where it already is, changes nothing. */
export function moveKeyframe(flythrough: Flythrough, id: Id, to: number): Flythrough {
  const from = indexOfKeyframe(flythrough, id);
  const target = Math.min(flythrough.keyframes.length - 1, Math.max(0, Math.trunc(to)));
  if (from < 0 || from === target) {
    return flythrough;
  }
  const keyframes = [...flythrough.keyframes];
  const [moved] = keyframes.splice(from, 1);
  if (moved === undefined) {
    return flythrough;
  }
  keyframes.splice(target, 0, moved);
  return { ...flythrough, keyframes };
}

/** A copy of a keyframe straight after it, under a fresh id; undefined for an unknown id or a full tour. */
export function duplicateKeyframe(flythrough: Flythrough, id: Id): AddedKeyframe | undefined {
  const index = indexOfKeyframe(flythrough, id);
  const source = flythrough.keyframes[index];
  if (source === undefined || flythrough.keyframes.length >= MAX_KEYFRAMES) {
    return undefined;
  }
  const copyId = nextKeyframeId(flythrough);
  const keyframes = [...flythrough.keyframes];
  keyframes.splice(index + 1, 0, canonicalKeyframe({ ...source, id: copyId }));
  return { flythrough: { ...flythrough, keyframes }, id: copyId };
}

/** The fields of a keyframe an edit may change; null clears an optional one, back to its default. */
export interface KeyframePatch {
  view?: ViewRect;
  caption?: string | null;
  durationMs?: number | null;
  holdMs?: number | null;
  easing?: KeyframeEasing | null;
}

/**
 * Changes some fields of one keyframe. Times are clamped into the schema's range and rounded to whole milliseconds; a
 * view the camera cannot frame is ignored, as is an unknown id; a caption of only spaces clears the caption.
 */
export function updateKeyframe(flythrough: Flythrough, id: Id, patch: KeyframePatch): Flythrough {
  const index = indexOfKeyframe(flythrough, id);
  const current = flythrough.keyframes[index];
  if (current === undefined) {
    return flythrough;
  }
  const next: Keyframe = { ...current };
  if (patch.view !== undefined && isFrameable(patch.view)) {
    next.view = patch.view;
  }
  if (patch.caption !== undefined) {
    if (patch.caption === null || patch.caption.trim() === '') {
      delete next.caption;
    } else {
      next.caption = patch.caption;
    }
  }
  if (patch.durationMs !== undefined) {
    if (patch.durationMs === null) {
      delete next.durationMs;
    } else {
      next.durationMs = clampMilliseconds(patch.durationMs);
    }
  }
  if (patch.holdMs !== undefined) {
    if (patch.holdMs === null) {
      delete next.holdMs;
    } else {
      next.holdMs = clampMilliseconds(patch.holdMs);
    }
  }
  if (patch.easing !== undefined) {
    if (patch.easing === null) {
      delete next.easing;
    } else {
      next.easing = patch.easing;
    }
  }
  const keyframes = [...flythrough.keyframes];
  keyframes[index] = canonicalKeyframe(next);
  return { ...flythrough, keyframes };
}

/** A view and, optionally, the words to show over it: one shot of a generated tour. */
export interface Shot {
  view: ViewRect;
  caption?: string | undefined;
}

/**
 * A tour of the given shots in order, ids `k_1`, `k_2`, and so on, keeping the loop setting of the tour it replaces.
 * A shot the camera cannot frame is left out, and so is anything past MAX_KEYFRAMES; no shots at all answer undefined.
 */
export function tourFromShots(shots: readonly Shot[], replacing?: Flythrough): Flythrough | undefined {
  let tour: Flythrough | undefined;
  for (const shot of shots) {
    tour = addKeyframe(tour, shot.view, undefined, shot.caption)?.flythrough ?? tour;
  }
  if (tour === undefined) {
    return undefined;
  }
  return replacing?.loop === true ? { ...tour, loop: true } : tour;
}

/** Turns looping on, or off (which clears the member: absent means the tour stops at the last keyframe). */
export function setLoop(flythrough: Flythrough, loop: boolean): Flythrough {
  return loop ? { keyframes: flythrough.keyframes, loop: true } : { keyframes: flythrough.keyframes };
}

/** Whether the tour returns to its first keyframe. A one-keyframe tour has nowhere to go, so it never loops. */
export function loops(flythrough: Flythrough): boolean {
  return flythrough.loop === true && flythrough.keyframes.length > 1;
}
