import type { Flythrough, Keyframe, KeyframeEasing, ViewRect } from '../model/types.ts';
import {
  cameraAtProgress,
  cameraForView,
  cameraOfViewport,
  ease,
  flyBetween,
  pathEnd,
  viewportAtProgress,
  viewportForCamera,
  viewRectOf,
  type Camera,
} from './camera.ts';
import { must } from '../model/sequenceFixture.ts';
import { MAX_ZOOM, MIN_ZOOM, visibleWorldRect } from './viewport.ts';

const SIZE = { width: 1600, height: 900 };
const EASINGS: readonly KeyframeEasing[] = ['linear', 'easeIn', 'easeOut', 'easeInOut'];
const viewAt = (tour: Flythrough, index: number): ViewRect => must(tour.keyframes[index]).view;

const close = (actual: number, expected: number, digits = 9): void => {
  expect(actual).toBeCloseTo(expected, digits);
};
const closeCamera = (actual: Camera, expected: Camera, digits = 9): void => {
  close(actual.x, expected.x, digits);
  close(actual.y, expected.y, digits);
  close(actual.w, expected.w, digits);
};

describe('cameraForView and viewportForCamera', () => {
  it('a view with the window’s own shape fills it exactly', () => {
    const view: ViewRect = { x: 100, y: 50, w: 800, h: 450 };

    const visible = visibleWorldRect(viewportForCamera(cameraForView(view, SIZE), SIZE), SIZE);

    close(visible.x, 100);
    close(visible.y, 50);
    close(visible.w, 800);
    close(visible.h, 450);
  });

  it('contains a view wider than the window, showing more above and below, never cropping it', () => {
    const view: ViewRect = { x: 0, y: 0, w: 1600, h: 100 };

    const visible = visibleWorldRect(viewportForCamera(cameraForView(view, SIZE), SIZE), SIZE);

    close(visible.w, 1600);
    expect(visible.h).toBeGreaterThan(view.h);
    // The view stays centred: the same margin above and below.
    close(visible.y + visible.h / 2, 50);
  });

  it('contains a view taller than the window, showing more left and right', () => {
    const view: ViewRect = { x: 0, y: 0, w: 100, h: 900 };

    const visible = visibleWorldRect(viewportForCamera(cameraForView(view, SIZE), SIZE), SIZE);

    close(visible.h, 900);
    expect(visible.w).toBeGreaterThan(view.w);
    close(visible.x + visible.w / 2, 50);
  });

  it('never zooms past the canvas’s own limits, in or out', () => {
    expect(viewportForCamera(cameraForView({ x: 0, y: 0, w: 1, h: 1 }, SIZE), SIZE).zoom).toBe(MAX_ZOOM);
    expect(viewportForCamera(cameraForView({ x: 0, y: 0, w: 1e7, h: 1e7 }, SIZE), SIZE).zoom).toBe(MIN_ZOOM);
    const inside = viewportForCamera(cameraForView({ x: 0, y: 0, w: 800, h: 450 }, SIZE), SIZE).zoom;
    expect(inside).toBeGreaterThan(MIN_ZOOM);
    expect(inside).toBeLessThan(MAX_ZOOM);
  });

  it('a window of another size plays the same shot: the rectangle is fitted, not its pixel size', () => {
    const view: ViewRect = { x: 40, y: 40, w: 320, h: 200 };

    for (const size of [{ width: 400, height: 250 }, { width: 1920, height: 1080 }, { width: 500, height: 900 }]) {
      const visible = visibleWorldRect(viewportForCamera(cameraForView(view, size), size), size);
      expect(visible.x).toBeLessThanOrEqual(view.x + 1e-9);
      expect(visible.y).toBeLessThanOrEqual(view.y + 1e-9);
      expect(visible.x + visible.w).toBeGreaterThanOrEqual(view.x + view.w - 1e-9);
      expect(visible.y + visible.h).toBeGreaterThanOrEqual(view.y + view.h - 1e-9);
    }
  });

  it('captures what it frames: a viewport’s rectangle fitted back gives the same viewport', () => {
    const viewport = { zoom: 0.8, x: -120, y: 35 };

    const captured = must(viewRectOf(viewport, SIZE));

    const refitted = viewportForCamera(cameraForView(captured, SIZE), SIZE);
    close(refitted.zoom, 0.8);
    close(refitted.x, -120, 6);
    close(refitted.y, 35, 6);
    closeCamera(cameraOfViewport(viewport, SIZE), cameraForView(captured, SIZE));
  });

  it('captures nothing from an empty window or a broken viewport', () => {
    expect(viewRectOf({ zoom: 1, x: 0, y: 0 }, { width: 0, height: 900 })).toBeUndefined();
    expect(viewRectOf({ zoom: 1, x: 0, y: 0 }, { width: 900, height: 0 })).toBeUndefined();
    expect(viewRectOf({ zoom: 0, x: 0, y: 0 }, SIZE)).toBeUndefined();
    expect(viewRectOf({ zoom: Number.NaN, x: 0, y: 0 }, SIZE)).toBeUndefined();
    expect(viewRectOf({ zoom: 2, x: 0, y: 0 }, SIZE)).toEqual({ x: 0, y: 0, w: 800, h: 450 });
  });
});

describe('flyBetween', () => {
  const a: Camera = { x: 0, y: 0, w: 800 };
  const far: Camera = { x: 6000, y: 2000, w: 800 };

  it('is exactly the start at 0 and exactly the end at 1, and clamps outside', () => {
    expect(flyBetween(a, far, 0)).toBe(a);
    expect(flyBetween(a, far, 1)).toBe(far);
    expect(flyBetween(a, far, -3)).toBe(a);
    expect(flyBetween(a, far, 7)).toBe(far);
    expect(flyBetween(a, far, Number.NaN)).toBe(a);
  });

  it('is the same path forwards and backwards', () => {
    for (const u of [0.1, 0.25, 0.5, 0.8]) {
      closeCamera(flyBetween(a, far, u), flyBetween(far, a, 1 - u), 6);
    }
  });

  it('zooms out over a far pan and back in, rather than sweeping across at full zoom', () => {
    const middle = flyBetween(a, far, 0.5);

    expect(middle.w).toBeGreaterThan(a.w * 2);
    // It ends at the destination’s own scale, not the middle’s.
    expect(flyBetween(a, far, 0.999).w).toBeLessThan(middle.w);
    // Symmetric endpoints give the widest view at the middle.
    closeCamera(middle, { x: 3000, y: 1000, w: middle.w }, 6);
  });

  it('barely zooms out over a short pan, which is just a pan', () => {
    const near: Camera = { x: 10, y: 0, w: 800 };

    for (let u = 0.05; u < 1; u += 0.05) {
      expect(flyBetween(a, near, u).w).toBeLessThan(800 * 1.01);
    }
    expect(flyBetween(a, near, 0.5).x).toBeGreaterThan(4);
    expect(flyBetween(a, near, 0.5).x).toBeLessThan(6);
  });

  it('a change of scale at one place is a steady zoom about that place', () => {
    const closer: Camera = { x: 50, y: 70, w: 200 };

    const half = flyBetween({ x: 50, y: 70, w: 800 }, closer, 0.5);

    expect(half.x).toBe(50);
    expect(half.y).toBe(70);
    // Steady in the log: half way is the geometric mean, not the arithmetic one (500).
    close(half.w, 400, 6);
  });

  it('moves along the line between the two centres, never behind the start or past the end', () => {
    const slope = 2000 / 6000;
    let previous = 0;
    for (let u = 0.02; u < 1; u += 0.02) {
      const here = flyBetween(a, far, u);
      expect(here.x).toBeGreaterThan(previous);
      expect(here.x).toBeLessThan(far.x);
      close(here.y, here.x * slope, 6);
      previous = here.x;
    }
  });

  it('has no jump in it: a small step of u is a small step of the camera', () => {
    let last = flyBetween(a, far, 0);
    for (let i = 1; i <= 500; i++) {
      const here = flyBetween(a, far, i / 500);
      expect(Math.abs(here.x - last.x)).toBeLessThan(200);
      expect(Math.abs(Math.log(here.w / last.w))).toBeLessThan(0.1);
      last = here;
    }
  });

  it('stays finite over the whole range of zooms a canvas can have', () => {
    const wide: Camera = { x: -5000, y: -5000, w: 1600 / MIN_ZOOM };
    const tight: Camera = { x: 9000, y: 4000, w: 1600 / MAX_ZOOM };

    for (let u = 0; u <= 1; u += 0.1) {
      const camera = flyBetween(wide, tight, u);
      expect(Number.isFinite(camera.x) && Number.isFinite(camera.y) && Number.isFinite(camera.w)).toBe(true);
      expect(camera.w).toBeGreaterThan(0);
    }
  });
});

describe('ease', () => {
  it.each(EASINGS)('%s starts at 0, ends at 1 and never goes backwards', (easing) => {
    expect(ease(easing, 0)).toBe(0);
    expect(ease(easing, 1)).toBe(1);
    let last = 0;
    for (let t = 0.01; t <= 1; t += 0.01) {
      const value = ease(easing, t);
      expect(value).toBeGreaterThanOrEqual(last);
      last = value;
    }
  });

  it('ease in starts slowly, ease out starts quickly, and linear is the diagonal', () => {
    expect(ease('easeIn', 0.25)).toBeLessThan(0.25);
    expect(ease('easeOut', 0.25)).toBeGreaterThan(0.25);
    expect(ease('linear', 0.25)).toBe(0.25);
    expect(ease('easeInOut', 0.1)).toBeLessThan(0.1);
    expect(ease('easeInOut', 0.9)).toBeGreaterThan(0.9);
  });

  it('smooth is symmetric about the middle', () => {
    expect(ease('easeInOut', 0.5)).toBe(0.5);
    for (const t of [0.1, 0.3, 0.45]) {
      close(ease('easeInOut', t) + ease('easeInOut', 1 - t), 1);
    }
  });

  it('clamps outside the segment and reads a broken number as the start', () => {
    expect(ease('linear', -1)).toBe(0);
    expect(ease('linear', 2)).toBe(1);
    expect(ease('easeInOut', Number.NaN)).toBe(0);
  });

  it('a cut jumps at the middle, and is not any of the smooth ones', () => {
    expect(ease('cut', 0.49)).toBe(0);
    expect(ease('cut', 0.5)).toBe(1);
    expect(ease('cut', 0.3)).not.toBe(ease('linear', 0.3));
  });
});

describe('cameraAtProgress', () => {
  const key = (id: string, x: number, easing?: KeyframeEasing): Keyframe => ({ id, view: { x, y: 0, w: 800, h: 450 }, ...(easing === undefined ? {} : { easing }) });
  const tour: Flythrough = { keyframes: [key('k_1', 0), key('k_2', 4000), key('k_3', 4000, 'linear')] };

  it('is exactly each keyframe’s own camera at a whole number', () => {
    tour.keyframes.forEach((keyframe, index) => {
      expect(cameraAtProgress(tour, index, SIZE)).toEqual(cameraForView(keyframe.view, SIZE));
    });
  });

  it('flies between two keyframes in the middle of a segment, and only that segment', () => {
    const middle = cameraAtProgress(tour, 0.5, SIZE);

    expect(middle.x).toBeGreaterThan(cameraForView(viewAt(tour, 0), SIZE).x);
    expect(middle.x).toBeLessThan(cameraForView(viewAt(tour, 1), SIZE).x);
    // The second segment goes nowhere (the same view), so half way along it is that view.
    closeCamera(cameraAtProgress(tour, 1.5, SIZE), cameraForView(viewAt(tour, 1), SIZE));
  });

  it('eases a segment with the easing of the keyframe it arrives at', () => {
    const smooth = cameraAtProgress({ keyframes: [key('a', 0), key('b', 4000, 'easeIn')] }, 0.3, SIZE);
    const linear = cameraAtProgress({ keyframes: [key('a', 0), key('b', 4000, 'linear')] }, 0.3, SIZE);
    // The easing of the keyframe it leaves from plays no part.
    const leavingEasing = cameraAtProgress({ keyframes: [key('a', 0, 'cut'), key('b', 4000, 'linear')] }, 0.3, SIZE);

    expect(smooth.x).toBeLessThan(linear.x);
    expect(leavingEasing).toEqual(linear);
  });

  it('a cut arrival stays on the old keyframe until the middle of its segment, then is on the new one', () => {
    const cutTour: Flythrough = { keyframes: [key('a', 0), key('b', 4000, 'cut')] };

    expect(cameraAtProgress(cutTour, 0.4, SIZE)).toEqual(cameraForView(viewAt(cutTour, 0), SIZE));
    expect(cameraAtProgress(cutTour, 0.6, SIZE)).toEqual(cameraForView(viewAt(cutTour, 1), SIZE));
  });

  it('clamps progress to the tour, and reads a broken one as the start', () => {
    expect(cameraAtProgress(tour, -5, SIZE)).toEqual(cameraAtProgress(tour, 0, SIZE));
    expect(cameraAtProgress(tour, 99, SIZE)).toEqual(cameraAtProgress(tour, 2, SIZE));
    expect(cameraAtProgress(tour, Number.NaN, SIZE)).toEqual(cameraAtProgress(tour, 0, SIZE));
  });

  it('a one-keyframe tour stays on it wherever progress is', () => {
    const one: Flythrough = { keyframes: [key('only', 7)] };

    expect(cameraAtProgress(one, 0, SIZE)).toEqual(cameraForView(viewAt(one, 0), SIZE));
    expect(cameraAtProgress(one, 3, SIZE)).toEqual(cameraForView(viewAt(one, 0), SIZE));
    expect(pathEnd(one)).toBe(0);
  });

  it('a looping tour has a last segment back to the start, arriving with the start’s easing, and ends on it', () => {
    const loop: Flythrough = { keyframes: [key('a', 0, 'linear'), key('b', 4000), key('c', 8000)], loop: true };

    expect(pathEnd(loop)).toBe(3);
    expect(pathEnd({ ...loop, loop: false })).toBe(2);
    expect(cameraAtProgress(loop, 3, SIZE)).toEqual(cameraForView(viewAt(loop, 0), SIZE));
    const back = cameraAtProgress(loop, 2.5, SIZE);
    // Linear, arriving at the first keyframe: half way is where a linear fly puts it.
    const expected = flyBetween(cameraForView(viewAt(loop, 2), SIZE), cameraForView(viewAt(loop, 0), SIZE), 0.5);
    closeCamera(back, expected);
    // The same segment with a smooth first keyframe would not be there.
    const smoothBack = cameraAtProgress({ ...loop, keyframes: [key('a', 0, 'easeIn'), ...loop.keyframes.slice(1)] }, 2.25, SIZE);
    expect(smoothBack.x).not.toBeCloseTo(cameraAtProgress(loop, 2.25, SIZE).x, 3);
  });

  it('a loop flag on a one-keyframe tour is ignored rather than spinning', () => {
    expect(pathEnd({ keyframes: [key('only', 0)], loop: true })).toBe(0);
  });

  it('draws the viewport for the camera', () => {
    expect(viewportAtProgress(tour, 1, SIZE)).toEqual(viewportForCamera(cameraForView(viewAt(tour, 1), SIZE), SIZE));
  });
});
