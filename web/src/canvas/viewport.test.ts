import {
  centreOn,
  clampZoom,
  fitToContent,
  MAX_ZOOM,
  MIN_ZOOM,
  pan,
  screenToWorld,
  visibleWorldRect,
  worldToScreen,
  zoomAround,
  zoomTo100,
} from './viewport.ts';

describe('viewport maths', () => {
  const vp = { x: 100, y: 50, zoom: 2 };

  it('round-trips world and screen', () => {
    const world = { x: 30, y: -10 };
    expect(worldToScreen(vp, world)).toEqual({ x: 160, y: 30 });
    expect(screenToWorld(vp, worldToScreen(vp, world))).toEqual(world);
  });

  it('pans by screen pixels', () => {
    expect(pan(vp, 10, -5)).toEqual({ x: 110, y: 45, zoom: 2 });
  });

  it('zooms around a screen point, keeping the world point under the cursor fixed', () => {
    const cursor = { x: 400, y: 300 };
    const before = screenToWorld(vp, cursor);
    const zoomed = zoomAround(vp, cursor, 1.5);
    expect(zoomed.zoom).toBeCloseTo(3);
    const after = screenToWorld(zoomed, cursor);
    expect(after.x).toBeCloseTo(before.x);
    expect(after.y).toBeCloseTo(before.y);
  });

  it('clamps zoom to the supported range', () => {
    expect(clampZoom(0)).toBe(MIN_ZOOM);
    expect(clampZoom(1000)).toBe(MAX_ZOOM);
    expect(clampZoom(Number.NaN)).toBe(1);
    expect(zoomAround(vp, { x: 0, y: 0 }, 1e9).zoom).toBe(MAX_ZOOM);
  });

  it('zooms to 100% about the view centre', () => {
    const size = { width: 800, height: 600 };
    const centreBefore = screenToWorld(vp, { x: 400, y: 300 });
    const reset = zoomTo100(vp, size);
    expect(reset.zoom).toBe(1);
    expect(screenToWorld(reset, { x: 400, y: 300 })).toEqual(centreBefore);
  });

  it('fits content centred with padding and never zooms past 100%', () => {
    const size = { width: 1000, height: 500 };
    const bounds = { x: 0, y: 0, w: 2000, h: 400 };
    const fitted = fitToContent(bounds, size, 50);
    expect(fitted.zoom).toBeCloseTo(0.45);
    const visible = visibleWorldRect(fitted, size);
    expect(visible.x + visible.w / 2).toBeCloseTo(1000);
    expect(visible.y + visible.h / 2).toBeCloseTo(200);

    const tiny = fitToContent({ x: 0, y: 0, w: 10, h: 10 }, size);
    expect(tiny.zoom).toBe(1);
  });

  it('falls back to the identity for empty content', () => {
    expect(fitToContent(undefined, { width: 100, height: 100 })).toEqual({ x: 0, y: 0, zoom: 1 });
    expect(fitToContent({ x: 0, y: 0, w: 0, h: 0 }, { width: 100, height: 100 })).toEqual({ x: 0, y: 0, zoom: 1 });
  });

  it('reports the visible world rectangle', () => {
    expect(visibleWorldRect(vp, { width: 800, height: 600 })).toEqual({ x: -50, y: -25, w: 400, h: 300 });
  });

  it('centres the view on a world point, keeping the zoom', () => {
    const size = { width: 800, height: 600 };
    const point = { x: 250, y: -75 };
    const centred = centreOn(vp, size, point);

    // Two-sided: the zoom is untouched, and the point really lands at the view's centre (not merely moved).
    expect(centred.zoom).toBe(vp.zoom);
    expect(screenToWorld(centred, { x: 400, y: 300 })).toEqual(point);
  });
});
