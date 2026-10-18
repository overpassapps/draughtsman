/**
 * The hook that plays a flythrough (docs/flythrough.md): it holds the pure player state (tourPlayer.ts) in a ref, runs
 * one animation-frame loop only while the camera is travelling or the tour is playing, and hands React a small
 * snapshot that changes only when something a viewer can see has: the progress, playing, or whether it is travelling.
 * A tour that is holding on a keyframe therefore keeps the frame loop running (it has a dwell to count down) but
 * renders nothing, and a tour at rest runs no loop at all.
 *
 * A frame longer than MAX_FRAME_MS (a background tab waking, a debugger) is counted as that long, so a show never
 * skips a whole keyframe because the page was asleep.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Flythrough } from '../model/types.ts';
import {
  advance,
  currentIndex,
  initialPlayerState,
  isFinished,
  jumpTo as jumpToState,
  next as nextState,
  pause as pauseState,
  play as playState,
  previous as previousState,
  seek as seekState,
  toggle as toggleState,
  type PlayerOptions,
  type PlayerState,
} from './tourPlayer.ts';

/** The longest one frame is allowed to advance the tour by. */
export const MAX_FRAME_MS = 100;

/** The clock and frame source; the real ones by default, a test's hand-cranked pair otherwise. */
export interface FrameSource {
  now: () => number;
  request: (callback: () => void) => number;
  cancel: (handle: number) => void;
}

const realFrames: FrameSource = {
  now: () => performance.now(),
  request: (callback) => requestAnimationFrame(callback),
  cancel: (handle) => {
    cancelAnimationFrame(handle);
  },
};

/** What a viewer can see of the player. */
export interface PlaybackView {
  /** Progress along the tour, in keyframe units (canvas/camera.ts). */
  p: number;
  playing: boolean;
  /** The camera is moving towards a keyframe (not merely resting or holding). */
  travelling: boolean;
  /** The keyframe the camera is nearest: the caption and the counter. */
  index: number;
  /** A tour that does not loop has reached its last keyframe and stopped. */
  finished: boolean;
}

export interface Presentation {
  view: PlaybackView;
  next: () => void;
  previous: () => void;
  /** Plays when paused, pauses when playing, and replays a finished tour. */
  toggle: () => void;
  pause: () => void;
  /** Puts the camera on a keyframe at once. */
  jumpTo: (index: number) => void;
  /** Puts the camera at any progress along the path, with no travel: the scrubber. */
  seek: (progress: number) => void;
}

export interface PresentationOptions {
  /** The keyframe to start on; the first by default. */
  startIndex?: number | undefined;
  /** Start playing at once. */
  autoplay?: boolean | undefined;
  /** The viewer prefers reduced motion: every transition is a cut. */
  reducedMotion: boolean;
  frames?: FrameSource | undefined;
}

function viewOf(state: PlayerState, flythrough: Flythrough): PlaybackView {
  return { p: state.p, playing: state.playing, travelling: state.target !== undefined, index: currentIndex(state, flythrough), finished: isFinished(state, flythrough) };
}

function sameView(a: PlaybackView, b: PlaybackView): boolean {
  return a.p === b.p && a.playing === b.playing && a.travelling === b.travelling && a.index === b.index && a.finished === b.finished;
}

export function usePresentation(flythrough: Flythrough, options: PresentationOptions): Presentation {
  const { reducedMotion, startIndex, autoplay } = options;
  const frames = options.frames ?? realFrames;
  const playerOptions = useMemo<PlayerOptions>(() => ({ reducedMotion }), [reducedMotion]);
  const [initial] = useState<PlayerState>(() => {
    let state = initialPlayerState(flythrough);
    if (startIndex !== undefined) {
      state = jumpToState(state, flythrough, startIndex);
    }
    return autoplay === true ? playState(state, flythrough) : state;
  });
  const machine = useRef<PlayerState>(initial);
  const [view, setView] = useState<PlaybackView>(() => viewOf(initial, flythrough));

  const commit = useCallback(
    (state: PlayerState) => {
      machine.current = state;
      const snapshot = viewOf(state, flythrough);
      setView((previous) => (sameView(previous, snapshot) ? previous : snapshot));
    },
    [flythrough],
  );
  const apply = useCallback(
    (change: (state: PlayerState) => PlayerState) => {
      commit(change(machine.current));
    },
    [commit],
  );

  const animating = view.playing || view.travelling;
  useEffect(() => {
    if (!animating) {
      return;
    }
    let last = frames.now();
    let handle = 0;
    const tick = (): void => {
      const now = frames.now();
      const elapsed = Math.min(Math.max(0, now - last), MAX_FRAME_MS);
      last = now;
      commit(advance(machine.current, elapsed, flythrough, playerOptions));
      handle = frames.request(tick);
    };
    handle = frames.request(tick);
    return () => {
      frames.cancel(handle);
    };
  }, [animating, flythrough, playerOptions, frames, commit]);

  return {
    view,
    next: useCallback(() => {
      apply((s) => nextState(s, flythrough, playerOptions));
    }, [apply, flythrough, playerOptions]),
    previous: useCallback(() => {
      apply((s) => previousState(s, flythrough, playerOptions));
    }, [apply, flythrough, playerOptions]),
    toggle: useCallback(() => {
      apply((s) => toggleState(s, flythrough));
    }, [apply, flythrough]),
    pause: useCallback(() => {
      apply(pauseState);
    }, [apply]),
    jumpTo: useCallback(
      (index: number) => {
        apply((s) => jumpToState(s, flythrough, index));
      },
      [apply, flythrough],
    ),
    seek: useCallback(
      (progress: number) => {
        apply((s) => seekState(s, flythrough, progress));
      },
      [apply, flythrough],
    ),
  };
}

/** Whether the viewer has asked their system for less motion, followed live; false where matchMedia does not exist. */
export function useReducedMotion(): boolean {
  const query = useMemo(() => (typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-reduced-motion: reduce)') : undefined), []);
  const [reduced, setReduced] = useState(query?.matches ?? false);
  useEffect(() => {
    if (query === undefined) {
      return;
    }
    const onChange = (event: MediaQueryListEvent): void => {
      setReduced(event.matches);
    };
    query.addEventListener('change', onChange);
    return () => {
      query.removeEventListener('change', onChange);
    };
  }, [query]);
  return reduced;
}
