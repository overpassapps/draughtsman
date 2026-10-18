import type { Flythrough, Keyframe, KeyframeEasing } from '../model/types.ts';
import {
  advance,
  currentIndex,
  initialPlayerState,
  isAnimating,
  isFinished,
  jumpTo,
  next,
  pause,
  play,
  previous,
  seek,
  segmentMilliseconds,
  toggle,
  type PlayerOptions,
  type PlayerState,
} from './tourPlayer.ts';

const MOTION: PlayerOptions = { reducedMotion: false };
const STILL: PlayerOptions = { reducedMotion: true };

const key = (id: string, options: Partial<Keyframe> = {}): Keyframe => ({ id, view: { x: 0, y: 0, w: 100, h: 50 }, durationMs: 1000, holdMs: 500, ...options });
const tour = (count: number, extra: Partial<Flythrough> = {}): Flythrough => ({ keyframes: Array.from({ length: count }, (_, n) => key(`k_${String(n + 1)}`)), ...extra });

/** Runs `advance` in 16 ms frames until `ms` have passed, as the hook would. */
function run(state: PlayerState, f: Flythrough, ms: number, options = MOTION, frame = 16): PlayerState {
  let current = state;
  for (let left = ms; left > 0; left -= frame) {
    current = advance(current, Math.min(frame, left), f, options);
  }
  return current;
}

describe('stepping by hand', () => {
  it('next travels to the following keyframe over its duration and then rests', () => {
    const f = tour(3);
    let state = next(initialPlayerState(f), f, MOTION);

    expect(state.target).toBe(1);
    expect(state.p).toBe(0);
    state = run(state, f, 500);
    expect(state.p).toBeCloseTo(0.5, 1);
    expect(state.target).toBe(1);
    state = run(state, f, 600);
    expect(state.p).toBe(1);
    expect(state.target).toBeUndefined();
    expect(state.playing).toBe(false);
    // Resting: more time passing changes nothing.
    expect(run(state, f, 5000)).toBe(state);
  });

  it('takes the arriving keyframe’s own duration, not the one it leaves', () => {
    const f: Flythrough = { keyframes: [key('a', { durationMs: 9000 }), key('b', { durationMs: 200 })] };

    const arrived = run(next(initialPlayerState(f), f, MOTION), f, 250);

    expect(arrived.p).toBe(1);
    expect(segmentMilliseconds(f, 0, MOTION)).toBe(200);
  });

  it('a press while it is still travelling forwards sends it one keyframe further, through the one it passes', () => {
    const f = tour(4);
    let state = next(initialPlayerState(f), f, MOTION);
    state = run(state, f, 400);
    state = next(state, f, MOTION);

    expect(state.target).toBe(2);
    state = run(state, f, 600 + 10);
    expect(state.p).toBeGreaterThan(1);
    expect(state.p).toBeLessThan(2);
    state = run(state, f, 2000);
    expect(state.p).toBe(2);
    expect(state.target).toBeUndefined();
  });

  it('a press the other way while travelling turns the camera round to the keyframe it left', () => {
    const f = tour(3);
    let state = run(next(initialPlayerState(f), f, MOTION), f, 400);

    state = previous(state, f, MOTION);

    expect(state.target).toBe(0);
    expect(run(state, f, 3000).p).toBe(0);
  });

  it('previous steps back one keyframe, retracing the segment at that segment’s own pace', () => {
    const f: Flythrough = { keyframes: [key('a'), key('b', { durationMs: 400 }), key('c', { durationMs: 2000 })] };
    let state = jumpTo(initialPlayerState(f), f, 2);

    state = previous(state, f, MOTION);
    expect(state.target).toBe(1);
    // Back across the segment that arrives at c takes c's 2000 ms: half way after a second.
    expect(run(state, f, 1000).p).toBeCloseTo(1.5, 1);
    state = run(state, f, 2100);
    expect(state.p).toBe(1);
    // Then back across the one that arrives at b, in b's 400, not c's 2000.
    state = previous(state, f, MOTION);
    expect(run(state, f, 380).p).toBeGreaterThan(0);
    expect(run(state, f, 420).p).toBe(0);
  });

  it('does nothing past either end of a tour that does not loop', () => {
    const f = tour(2);
    const first = initialPlayerState(f);

    expect(previous(first, f, MOTION)).toBe(first);
    const last = jumpTo(first, f, 1);
    expect(next(last, f, MOTION)).toBe(last);
    // And does move inside it, so the two assertions above are about the ends.
    expect(next(first, f, MOTION).target).toBe(1);
  });

  it('a one-keyframe tour has nowhere to step', () => {
    const f = tour(1);
    const state = initialPlayerState(f);

    expect(next(state, f, MOTION)).toBe(state);
    expect(previous(state, f, MOTION)).toBe(state);
    expect(isFinished(state, f)).toBe(true);
  });

  it('a cut arrival is there at once, with no frames between', () => {
    const f: Flythrough = { keyframes: [key('a'), key('b', { easing: 'cut' })] };

    const state = next(initialPlayerState(f), f, MOTION);

    expect(state.p).toBe(1);
    expect(state.target).toBeUndefined();
  });

  it('reduced motion makes every step a cut, and does not change where it ends up', () => {
    const f = tour(3);

    const stepped = next(initialPlayerState(f), f, STILL);

    expect(stepped.p).toBe(1);
    expect(stepped.target).toBeUndefined();
    expect(next(next(initialPlayerState(f), f, STILL), f, STILL).p).toBe(2);
    // The same press with motion on is still travelling, so reduced motion is what made it instant.
    expect(next(initialPlayerState(f), f, MOTION).p).toBe(0);
  });

  it('a zero duration is a cut as well', () => {
    const f: Flythrough = { keyframes: [key('a'), key('b', { durationMs: 0 })] };

    expect(next(initialPlayerState(f), f, MOTION).p).toBe(1);
  });
});

describe('looping', () => {
  const loop = tour(3, { loop: true });

  it('next from the last keyframe travels back to the first over the first’s duration, then is at it', () => {
    let state = jumpTo(initialPlayerState(loop), loop, 2);

    state = next(state, loop, MOTION);
    expect(state.target).toBe(3);
    state = run(state, loop, 1100);

    expect(state.p).toBe(0);
    expect(state.target).toBeUndefined();
  });

  it('previous from the first keyframe goes round to the last', () => {
    const state = previous(initialPlayerState(loop), loop, MOTION);

    expect(state.p).toBe(3);
    expect(state.target).toBe(2);
    expect(run(state, loop, 1100).p).toBe(2);
  });

  it('without loop, the same steps stop at the ends', () => {
    const plain = tour(3);

    expect(next(jumpTo(initialPlayerState(plain), plain, 2), plain, MOTION).target).toBeUndefined();
    expect(previous(initialPlayerState(plain), plain, MOTION).target).toBeUndefined();
  });

  it('a scrub to the very end of a loop is the first keyframe, and stepping on goes to the second', () => {
    const state = seek(initialPlayerState(loop), loop, 3);

    expect(currentIndex(state, loop)).toBe(0);
    expect(next(state, loop, MOTION).target).toBe(1);
  });
});

describe('playing', () => {
  it('holds at a keyframe, travels on, holds, and stops at the last of a tour that does not loop', () => {
    const f = tour(3);
    let state = play(initialPlayerState(f), f);

    expect(state.playing).toBe(true);
    state = run(state, f, 400);
    expect(state.p).toBe(0);
    state = run(state, f, 200);
    expect(state.target).toBe(1);
    state = run(state, f, 1100);
    expect(state.p).toBe(1);
    state = run(state, f, 500 + 1000 + 500 + 100);
    expect(state.p).toBe(2);
    expect(state.playing).toBe(false);
    expect(isFinished(state, f)).toBe(true);
    expect(isAnimating(state)).toBe(false);
  });

  it('plays a looping tour round and round without stopping', () => {
    const f = tour(2, { loop: true });
    let state = play(initialPlayerState(f), f);

    const seen = new Set<number>();
    for (let frame = 0; frame < 2000; frame++) {
      state = advance(state, 16, f, MOTION);
      seen.add(currentIndex(state, f));
      expect(state.playing).toBe(true);
    }

    expect([...seen].sort()).toEqual([0, 1]);
    expect(state.p).toBeLessThan(2);
  });

  it('takes the hold of the keyframe it is at', () => {
    const f: Flythrough = { keyframes: [key('a', { holdMs: 100 }), key('b', { holdMs: 5000 }), key('c')] };
    let state = run(play(initialPlayerState(f), f), f, 120 + 1010);
    expect(state.p).toBe(1);

    state = run(state, f, 4900);
    expect(state.target).toBeUndefined();
    state = run(state, f, 200);
    expect(state.target).toBe(2);
  });

  it('pausing stops it mid-travel and playing again carries on from there', () => {
    const f = tour(3);
    let state = run(play(initialPlayerState(f), f), f, 500 + 500);
    expect(state.p).toBeCloseTo(0.5, 1);

    state = pause(state);
    const held = run(state, f, 3000);
    // Paused: the camera still finishes a flight it is on (a step in progress), but nothing starts by itself.
    expect(held.p).toBe(1);
    expect(held.playing).toBe(false);
    expect(run(held, f, 10_000)).toBe(held);
  });

  it('play after a tour has finished starts it again from the first keyframe', () => {
    const f = tour(2);
    const finished = run(play(initialPlayerState(f), f), f, 10_000);
    expect(isFinished(finished, f)).toBe(true);

    const again = play(finished, f);

    expect(again.p).toBe(0);
    expect(again.playing).toBe(true);
  });

  it('toggle plays when paused and pauses when playing', () => {
    const f = tour(2);
    const started = toggle(initialPlayerState(f), f);

    expect(started.playing).toBe(true);
    expect(toggle(started, f).playing).toBe(false);
    // Pausing what is not playing is the very same state, so a pause press on a paused tour re-renders nothing.
    const idle = initialPlayerState(f);
    expect(pause(idle)).toBe(idle);
  });

  it('a next press while playing skips the dwell, and playing carries on from the keyframe it reached', () => {
    const f = tour(3);
    let state = play(initialPlayerState(f), f);
    state = run(state, f, 100);

    state = next(state, f, MOTION);
    expect(state.target).toBe(1);
    expect(state.playing).toBe(true);
    state = run(state, f, 1100);
    expect(state.p).toBe(1);
    state = run(state, f, 600 + 100);
    expect(state.target).toBe(2);
  });

  it('a tour with no time in it at all, looping, is bounded rather than a hang', () => {
    const f: Flythrough = { keyframes: [key('a', { durationMs: 0, holdMs: 0 }), key('b', { durationMs: 0, holdMs: 0 })], loop: true };

    const state = advance(play(initialPlayerState(f), f), 16, f, MOTION);

    expect(state.playing).toBe(true);
    expect(Number.isFinite(state.p)).toBe(true);
  });

  it('a huge frame (a tab in the background) crosses several keyframes in one advance, in order', () => {
    const f = tour(4);

    const state = advance(play(initialPlayerState(f), f), 30_000, f, MOTION);

    expect(state.p).toBe(3);
    expect(state.playing).toBe(false);
  });

  it('reduced motion plays the same tour as a slideshow: cuts, but still the holds', () => {
    const f = tour(3);
    let state = play(initialPlayerState(f), f);

    state = run(state, f, 400, STILL);
    expect(state.p).toBe(0);
    // The 500 ms hold ends 100 ms into this step; the cut is instant, and the other 100 ms are the next hold's.
    state = run(state, f, 200, STILL);
    expect(state.p).toBe(1);
    expect(state.target).toBeUndefined();
    expect(run(state, f, 350, STILL).p).toBe(1);
    expect(run(state, f, 420, STILL).p).toBe(2);
  });
});

describe('scrubbing and jumping', () => {
  const f = tour(4);

  it('seek puts the camera at the progress, with no travel, and cancels any flight', () => {
    const flying = next(initialPlayerState(f), f, MOTION);

    const sought = seek(flying, f, 2.25);

    expect(sought.p).toBe(2.25);
    expect(sought.target).toBeUndefined();
  });

  it('seek clamps to the tour and reads a broken number as the start', () => {
    expect(seek(initialPlayerState(f), f, -4).p).toBe(0);
    expect(seek(initialPlayerState(f), f, 99).p).toBe(3);
    expect(seek(initialPlayerState(f), f, Number.NaN).p).toBe(0);
  });

  it('jumpTo lands exactly on a keyframe, clamped, and leaves playing as it was', () => {
    const playing = play(initialPlayerState(f), f);

    expect(jumpTo(playing, f, 2).p).toBe(2);
    expect(jumpTo(playing, f, 2).playing).toBe(true);
    expect(jumpTo(playing, f, 99).p).toBe(3);
    expect(jumpTo(playing, f, -3).p).toBe(0);
    expect(jumpTo(playing, f, 1.9).p).toBe(1);
  });

  it('currentIndex is the nearest keyframe: the caption changes at the middle of a flight', () => {
    expect(currentIndex(seek(initialPlayerState(f), f, 1.49), f)).toBe(1);
    expect(currentIndex(seek(initialPlayerState(f), f, 1.51), f)).toBe(2);
  });
});

describe('an empty tour', () => {
  it('has nothing to play or step, and does not throw', () => {
    const empty: Flythrough = { keyframes: [] };
    const state = initialPlayerState(empty);

    expect(advance(state, 100, empty, MOTION)).toBe(state);
    expect(next(state, empty, MOTION)).toBe(state);
    expect(previous(state, empty, MOTION)).toBe(state);
    expect(play(state, empty)).toBe(state);
    expect(currentIndex(state, empty)).toBe(0);
  });
});

describe('easing is not the player’s business', () => {
  it.each(['linear', 'easeIn', 'easeOut', 'easeInOut'] as KeyframeEasing[])('%s takes exactly the keyframe’s duration', (easing) => {
    const f: Flythrough = { keyframes: [key('a'), key('b', { durationMs: 700, easing })] };

    const state = next(initialPlayerState(f), f, MOTION);

    expect(run(state, f, 690).p).toBeLessThan(1);
    expect(run(state, f, 710).p).toBe(1);
  });
});
