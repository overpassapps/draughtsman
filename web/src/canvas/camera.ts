/**
 * The camera a flythrough flies (docs/flythrough.md): pure maths from a keyframe's view rectangle to a viewport, and
 * along the path between keyframes. No DOM and no React, so it is unit-testable and so the standalone viewer
 * (editor/exportFlythrough.tsx, editor/flythroughPlayer.ts) can carry a second copy of the same few formulas and a test
 * can hold the two to the same numbers.
 *
 * A camera is where the window is looking: the world point at its centre and the world width it shows. Everything
 * is in canvas units, so a keyframe captured in a small editor window plays the same shot in a full-screen one: the
 * window fits the keyframe's whole rectangle (`cameraForView`), whatever its shape.
 *
 * The fly between two cameras is van Wijk and Nuij's "smooth and efficient zooming and panning" (the curve d3-zoom
 * uses): a far pan first zooms out, crosses, then zooms in, at a steady perceived speed, where interpolating the
 * viewport's translation and scale directly would sweep the diagram past at an absurd speed mid-flight. A near pan is
 * a plain pan and a same-place change of scale a plain zoom. `flyBetween` returns the two endpoints exactly at 0 and 1.
 *
 * Progress along the tour is a number `p` in keyframe units: 2.4 is 40% of the way from the third keyframe to the
 * fourth. Easing shapes the fraction within one segment, and is the easing of the keyframe the segment arrives at. A
 * looping tour has one more segment, from the last keyframe back to the first (`pathEnd`).
 */

import type { Flythrough, KeyframeEasing, ViewRect } from '../model/types.ts';
import { easingOf, loops } from '../model/flythrough.ts';
import { clampZoom, type Size, type Viewport } from './viewport.ts';

export interface Camera {
  /** The world point at the centre of the window. */
  x: number;
  y: number;
  /** The world width the window shows; smaller is closer. */
  w: number;
}

/** van Wijk's rho: how much zoom-out a far pan takes; the square root of two is the paper's recommendation. */
const RHO = Math.SQRT2;
const RHO2 = 2;
const RHO4 = 4;

/** Centres closer than this (squared, in world units) are a pure zoom: the general formula divides by the distance. */
const NEAR_EPSILON2 = 1e-12;

/** The camera that frames the whole of `view` in a window of `size`: the rectangle is contained, never cropped. */
export function cameraForView(view: ViewRect, size: Size): Camera {
  const zoom = clampZoom(Math.min(size.width / view.w, size.height / view.h));
  return { x: view.x + view.w / 2, y: view.y + view.h / 2, w: size.width / zoom };
}

/** The viewport a window of `size` draws for a camera. */
export function viewportForCamera(camera: Camera, size: Size): Viewport {
  const zoom = clampZoom(size.width / camera.w);
  return { zoom, x: size.width / 2 - camera.x * zoom, y: size.height / 2 - camera.y * zoom };
}

/** The camera a viewport looks through, the inverse of `viewportForCamera`: what a keyframe captured from it frames. */
export function cameraOfViewport(viewport: Viewport, size: Size): Camera {
  return { x: (size.width / 2 - viewport.x) / viewport.zoom, y: (size.height / 2 - viewport.y) / viewport.zoom, w: size.width / viewport.zoom };
}

/** The world rectangle a viewport shows in a window of `size`: what "Add keyframe" captures. Undefined for an empty window. */
export function viewRectOf(viewport: Viewport, size: Size): ViewRect | undefined {
  if (size.width <= 0 || size.height <= 0 || !Number.isFinite(viewport.zoom) || viewport.zoom <= 0) {
    return undefined;
  }
  // 0 - x rather than -x, so a viewport at the origin captures 0 and not negative zero.
  return { x: (0 - viewport.x) / viewport.zoom, y: (0 - viewport.y) / viewport.zoom, w: size.width / viewport.zoom, h: size.height / viewport.zoom };
}

/**
 * The camera a fraction `u` of the way from `a` to `b` along the smooth fly between them. Exactly `a` at 0 and exactly
 * `b` at 1 (and for anything outside), so arriving at a keyframe is not a floating-point near miss.
 */
export function flyBetween(a: Camera, b: Camera, u: number): Camera {
  if (!(u > 0)) {
    return a;
  }
  if (u >= 1) {
    return b;
  }
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const d2 = dx * dx + dy * dy;
  if (d2 < NEAR_EPSILON2) {
    // The same place at another scale: a zoom about the centre, steady in the log of the width.
    return { x: a.x, y: a.y, w: a.w * Math.pow(b.w / a.w, u) };
  }
  const d1 = Math.sqrt(d2);
  const b0 = (b.w * b.w - a.w * a.w + RHO4 * d2) / (2 * a.w * RHO2 * d1);
  const b1 = (b.w * b.w - a.w * a.w - RHO4 * d2) / (2 * b.w * RHO2 * d1);
  // ln(sqrt(b^2 + 1) - b) is -asinh(b), which does not cancel for a large b.
  const r0 = -Math.asinh(b0);
  const r1 = -Math.asinh(b1);
  const length = (r1 - r0) / RHO;
  const s = u * length;
  const along = (a.w / (RHO2 * d1)) * (Math.cosh(r0) * Math.tanh(RHO * s + r0) - Math.sinh(r0));
  return { x: a.x + along * dx, y: a.y + along * dy, w: (a.w * Math.cosh(r0)) / Math.cosh(RHO * s + r0) };
}

/** Eased progress for a raw fraction `t` of a segment: 0 at 0, 1 at 1, clamped outside. `cut` jumps at the midpoint. */
export function ease(easing: KeyframeEasing, t: number): number {
  const s = Math.min(1, Math.max(0, Number.isFinite(t) ? t : 0));
  switch (easing) {
    case 'linear':
      return s;
    case 'easeIn':
      return s * s * s;
    case 'easeOut':
      return 1 - Math.pow(1 - s, 3);
    case 'easeInOut':
      return s < 0.5 ? 4 * s * s * s : 1 - Math.pow(-2 * s + 2, 3) / 2;
    case 'cut':
      return s < 0.5 ? 0 : 1;
  }
}

/** The furthest progress a tour has: the last keyframe's index, or one more for the trip back when it loops. */
export function pathEnd(flythrough: Flythrough): number {
  return loops(flythrough) ? flythrough.keyframes.length : Math.max(0, flythrough.keyframes.length - 1);
}

/** The camera at progress `p` along the tour in a window of `size`; `p` is clamped to the tour. */
export function cameraAtProgress(flythrough: Flythrough, p: number, size: Size): Camera {
  const { keyframes } = flythrough;
  const first = keyframes[0];
  if (first === undefined) {
    return { x: 0, y: 0, w: Math.max(1, size.width) };
  }
  const end = pathEnd(flythrough);
  const progress = Math.min(end, Math.max(0, Number.isFinite(p) ? p : 0));
  if (end === 0) {
    return cameraForView(first.view, size);
  }
  const segment = Math.min(Math.floor(progress), end - 1);
  const from = keyframes[segment];
  const to = keyframes[(segment + 1) % keyframes.length];
  if (from === undefined || to === undefined) {
    return cameraForView(first.view, size);
  }
  return flyBetween(cameraForView(from.view, size), cameraForView(to.view, size), ease(easingOf(to), progress - segment));
}

/** The viewport to draw at progress `p`. */
export function viewportAtProgress(flythrough: Flythrough, p: number, size: Size): Viewport {
  return viewportForCamera(cameraAtProgress(flythrough, p, size), size);
}
