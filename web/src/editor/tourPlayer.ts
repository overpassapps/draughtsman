/**
 * The playback state of a flythrough, as pure functions over a small value: where the camera is on the path, where it
 * is heading, and whether the tour is playing itself (docs/flythrough.md). No timers and no React; the hook
 * (usePresentation.ts) feeds it frame deltas, and the tests feed it whatever they like.
 *
 * `p` is progress along the tour in keyframe units (canvas/camera.ts): a whole number is at that keyframe, and between
 * two whole numbers the camera is flying. Stepping by hand (`next`, `previous`) sets a `target` and the camera travels
 * there at the speed the keyframe it arrives at asks for (its duration), then rests; each further press while it is
 * still travelling the same way sends it one keyframe on, without stopping at the one it passes. Playing adds the
 * dwell: on arrival the tour waits the keyframe's hold, then sets off for the next, and stops at the last unless the
 * tour loops. Scrubbing (`seek`) and jumping (`jumpTo`) move the camera with no travel.
 *
 * A keyframe whose easing is `cut`, a travel time of 0, and a viewer who prefers reduced motion all make a segment
 * instant: the camera is on the far side the moment it is sent, with no frames between. Nothing here ever waits on a
 * segment of 0 milliseconds, so a tour whose every duration and hold is 0 and which loops is bounded, not a hang.
 */

import { durationOf, easingOf, holdOf, loops } from '../model/flythrough.ts';
import type { Flythrough } from '../model/types.ts';
import { pathEnd } from '../canvas/camera.ts';

export interface PlayerState {
  /** Progress along the tour, 0 to `pathEnd`. */
  p: number;
  /** The progress the camera is travelling towards, when it is travelling. */
  target: number | undefined;
  /** Whether the tour is advancing by itself. */
  playing: boolean;
  /** Milliseconds of dwell left at the keyframe the camera is at, before a playing tour sets off. */
  holdLeftMs: number;
}

export interface PlayerOptions {
  /** The viewer prefers reduced motion: every segment is a cut. Dwell is unchanged. */
  reducedMotion: boolean;
}

/** Progress within this of a whole number is at it, so accumulated float error never strands the camera a hair short. */
const EPSILON = 1e-9;

/** The most steps one `advance` takes, so a tour with no time in it at all stops instead of spinning. */
const MAX_STEPS_PER_ADVANCE = 64;

export function initialPlayerState(flythrough: Flythrough): PlayerState {
  return { p: 0, target: undefined, playing: false, holdLeftMs: flythrough.keyframes[0] === undefined ? 0 : holdOf(flythrough.keyframes[0]) };
}

/** The keyframe the camera is nearest: what the counter and the caption show. */
export function currentIndex(state: PlayerState, flythrough: Flythrough): number {
  const count = flythrough.keyframes.length;
  return count === 0 ? 0 : Math.round(state.p) % count;
}

/** Whether a tour that does not loop has reached its last keyframe and stopped there. */
export function isFinished(state: PlayerState, flythrough: Flythrough): boolean {
  return !loops(flythrough) && state.target === undefined && state.p >= pathEnd(flythrough) - EPSILON;
}

/** Milliseconds the camera takes to cross a segment of the path, 0 when it is a cut. The segment is named by its first keyframe. */
export function segmentMilliseconds(flythrough: Flythrough, segment: number, options: PlayerOptions): number {
  const arriving = flythrough.keyframes[(segment + 1) % flythrough.keyframes.length];
  if (arriving === undefined || options.reducedMotion || easingOf(arriving) === 'cut') {
    return 0;
  }
  return durationOf(arriving);
}

function holdAt(flythrough: Flythrough, p: number): number {
  const keyframe = flythrough.keyframes[Math.round(p) % Math.max(1, flythrough.keyframes.length)];
  return keyframe === undefined ? 0 : holdOf(keyframe);
}

/** A looping tour that has arrived back at its end is, for every purpose, at its first keyframe again. */
function wrapped(flythrough: Flythrough, p: number): number {
  return loops(flythrough) && p >= pathEnd(flythrough) - EPSILON ? 0 : p;
}

/** Moves the state forward by `dtMs` of real time: travelling, dwelling, or setting off for the next keyframe. */
export function advance(state: PlayerState, dtMs: number, flythrough: Flythrough, options: PlayerOptions): PlayerState {
  if (flythrough.keyframes.length === 0) {
    return state;
  }
  const end = pathEnd(flythrough);
  let { p, target, playing, holdLeftMs } = state;
  if (target === undefined) {
    // A scrub can leave a looping tour on its closing segment's far end, which is its first keyframe.
    p = wrapped(flythrough, p);
  }
  let remaining = Math.max(0, Number.isFinite(dtMs) ? dtMs : 0);
  let moved = p !== state.p;
  for (let step = 0; step < MAX_STEPS_PER_ADVANCE; step++) {
    if (target !== undefined) {
      const direction = Math.sign(target - p);
      if (direction === 0 || Math.abs(target - p) < EPSILON) {
        p = wrapped(flythrough, target);
        target = undefined;
        holdLeftMs = holdAt(flythrough, p);
        moved = true;
        continue;
      }
      const segment = direction > 0 ? Math.floor(p + EPSILON) : Math.ceil(p - EPSILON) - 1;
      const segmentEnd = direction > 0 ? Math.min(target, segment + 1) : Math.max(target, segment);
      const duration = segmentMilliseconds(flythrough, segment, options);
      const needed = Math.abs(segmentEnd - p) * duration;
      if (duration === 0 || remaining >= needed) {
        p = segmentEnd;
        remaining -= duration === 0 ? 0 : needed;
        moved = true;
        continue;
      }
      p += (direction * remaining) / duration;
      moved = true;
      break;
    }
    if (!playing) {
      break;
    }
    if (remaining < holdLeftMs) {
      holdLeftMs -= remaining;
      break;
    }
    remaining -= holdLeftMs;
    holdLeftMs = 0;
    if (p >= end - EPSILON) {
      playing = false;
      break;
    }
    target = Math.floor(p + EPSILON) + 1;
  }
  if (!moved && playing === state.playing && holdLeftMs === state.holdLeftMs && target === state.target) {
    return state;
  }
  return { p, target, playing, holdLeftMs };
}

/**
 * One keyframe on. While the camera is still travelling forwards this sends it one keyframe further than it was
 * going; while it is travelling back it turns it round to the keyframe it was leaving. At the last keyframe of a tour
 * that does not loop it does nothing.
 */
export function next(state: PlayerState, flythrough: Flythrough, options: PlayerOptions): PlayerState {
  const end = pathEnd(flythrough);
  if (flythrough.keyframes.length === 0 || end === 0) {
    return state;
  }
  let p = state.p;
  if (p >= end - EPSILON) {
    if (!loops(flythrough)) {
      return state;
    }
    p = 0;
  }
  const from = state.target !== undefined && state.target > p + EPSILON ? state.target : p;
  const target = Math.min(end, Math.floor(from + EPSILON) + 1);
  return advance({ ...state, p, target }, 0, flythrough, options);
}

/** One keyframe back, the mirror of `next`; at the first keyframe of a looping tour it goes round to the last. */
export function previous(state: PlayerState, flythrough: Flythrough, options: PlayerOptions): PlayerState {
  const end = pathEnd(flythrough);
  if (flythrough.keyframes.length === 0 || end === 0) {
    return state;
  }
  let p = state.p;
  if (p <= EPSILON) {
    if (!loops(flythrough)) {
      return state;
    }
    p = end;
  }
  const from = state.target !== undefined && state.target < p - EPSILON ? state.target : p;
  const target = Math.max(0, Math.ceil(from - EPSILON) - 1);
  return advance({ ...state, p, target }, 0, flythrough, options);
}

/** Puts the camera on a keyframe at once, with no travel: Home, End, a number key, a click in the list. */
export function jumpTo(state: PlayerState, flythrough: Flythrough, index: number): PlayerState {
  const last = Math.max(0, flythrough.keyframes.length - 1);
  const p = Math.min(last, Math.max(0, Math.trunc(Number.isFinite(index) ? index : 0)));
  return { ...state, p, target: undefined, holdLeftMs: holdAt(flythrough, p) };
}

/** Puts the camera at any progress along the path, with no travel: dragging the scrubber. */
export function seek(state: PlayerState, flythrough: Flythrough, progress: number): PlayerState {
  const p = Math.min(pathEnd(flythrough), Math.max(0, Number.isFinite(progress) ? progress : 0));
  return { ...state, p, target: undefined, holdLeftMs: holdAt(flythrough, p) };
}

/** Starts the tour playing from where it is, or from the start when it had finished. */
export function play(state: PlayerState, flythrough: Flythrough): PlayerState {
  if (flythrough.keyframes.length === 0) {
    return state;
  }
  const restart = isFinished(state, flythrough) && pathEnd(flythrough) > 0;
  return restart ? { p: 0, target: undefined, playing: true, holdLeftMs: holdAt(flythrough, 0) } : { ...state, playing: true };
}

export function pause(state: PlayerState): PlayerState {
  return state.playing ? { ...state, playing: false } : state;
}

export function toggle(state: PlayerState, flythrough: Flythrough): PlayerState {
  return state.playing ? pause(state) : play(state, flythrough);
}

/** Whether the camera is moving or the tour is running: the frame loop needs to run only while this is so. */
export function isAnimating(state: PlayerState): boolean {
  return state.target !== undefined || state.playing;
}
