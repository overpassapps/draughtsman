/**
 * Viewport maths: the mapping between canvas units (world) and screen pixels. Pure functions over a small value
 * object so pan, zoom and fit are unit-testable without a DOM. screen = world * zoom + (x, y).
 */

import type { Point } from '../model/types.ts';
import type { Rect } from './geometry.ts';

export interface Viewport {
  /** Screen-space translation in pixels. */
  x: number;
  y: number;
  /** Scale factor; 1 means one canvas unit per CSS pixel. */
  zoom: number;
}

export interface Size {
  width: number;
  height: number;
}

export const MIN_ZOOM = 0.05;
export const MAX_ZOOM = 8;

/** Fit never zooms in past this so a three-node diagram is not blown up to fill a 4K monitor. */
export const MAX_FIT_ZOOM = 1;

/** Multiplier for one keyboard or button step. */
export const ZOOM_STEP = 1.25;

export const IDENTITY_VIEWPORT: Viewport = { x: 0, y: 0, zoom: 1 };

export function clampZoom(zoom: number): number {
  if (!Number.isFinite(zoom)) {
    return 1;
  }
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom));
}

export function worldToScreen(viewport: Viewport, point: Point): Point {
  return { x: point.x * viewport.zoom + viewport.x, y: point.y * viewport.zoom + viewport.y };
}

export function screenToWorld(viewport: Viewport, point: Point): Point {
  return { x: (point.x - viewport.x) / viewport.zoom, y: (point.y - viewport.y) / viewport.zoom };
}

export function pan(viewport: Viewport, dx: number, dy: number): Viewport {
  return { ...viewport, x: viewport.x + dx, y: viewport.y + dy };
}

/**
 * The viewport after the canvas's top-left corner moved from `from` to `to` (client pixels), chosen so every world
 * point stays under the same client pixel: a panel docking beside the canvas must not make the drawing jump (QA-04).
 */
export function keepInPlace(viewport: Viewport, from: Point, to: Point): Viewport {
  return { ...viewport, x: viewport.x - (to.x - from.x), y: viewport.y - (to.y - from.y) };
}

/** Scales by `factor` keeping the world point under `screenPoint` fixed. */
export function zoomAround(viewport: Viewport, screenPoint: Point, factor: number): Viewport {
  return zoomToAround(viewport, screenPoint, viewport.zoom * factor);
}

/** Sets the zoom keeping the world point under `screenPoint` fixed. */
export function zoomToAround(viewport: Viewport, screenPoint: Point, zoom: number): Viewport {
  const next = clampZoom(zoom);
  const world = screenToWorld(viewport, screenPoint);
  return { zoom: next, x: screenPoint.x - world.x * next, y: screenPoint.y - world.y * next };
}

/** Zoom to 100% about the centre of the view. */
export function zoomTo100(viewport: Viewport, size: Size): Viewport {
  return zoomToAround(viewport, { x: size.width / 2, y: size.height / 2 }, 1);
}

/** Centres the view on a world point, keeping the current zoom: used to reveal a node without changing scale. */
export function centreOn(viewport: Viewport, size: Size, point: Point): Viewport {
  return { zoom: viewport.zoom, x: size.width / 2 - point.x * viewport.zoom, y: size.height / 2 - point.y * viewport.zoom };
}

/** Chooses zoom and translation so `bounds` sits centred in the view with `padding` pixels around it. */
export function fitToContent(bounds: Rect | undefined, size: Size, padding = 40): Viewport {
  if (!bounds || bounds.w <= 0 || bounds.h <= 0 || size.width <= 0 || size.height <= 0) {
    return IDENTITY_VIEWPORT;
  }
  const available = { width: Math.max(size.width - padding * 2, 1), height: Math.max(size.height - padding * 2, 1) };
  const zoom = clampZoom(Math.min(available.width / bounds.w, available.height / bounds.h, MAX_FIT_ZOOM));
  const centre = { x: bounds.x + bounds.w / 2, y: bounds.y + bounds.h / 2 };
  return { zoom, x: size.width / 2 - centre.x * zoom, y: size.height / 2 - centre.y * zoom };
}

/** The world rectangle currently on screen. */
export function visibleWorldRect(viewport: Viewport, size: Size): Rect {
  return {
    x: -viewport.x / viewport.zoom,
    y: -viewport.y / viewport.zoom,
    w: size.width / viewport.zoom,
    h: size.height / viewport.zoom,
  };
}

/** The SVG transform attribute for the world layer. */
export function viewportTransform(viewport: Viewport): string {
  return `translate(${String(viewport.x)} ${String(viewport.y)}) scale(${String(viewport.zoom)})`;
}
