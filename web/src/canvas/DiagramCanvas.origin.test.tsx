// @vitest-environment jsdom
import { act, render } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import { DiagramCanvas } from './DiagramCanvas.tsx';
import type { Viewport } from './viewport.ts';

/**
 * QA-04: the shape panel docks beside the canvas, so opening it moves the canvas's left edge 240px right (and closing
 * moves it back). The drawing has to stay under the same screen pixels rather than jump with the edge. jsdom has no
 * layout, so the box and its ResizeObserver are driven by hand.
 */
describe('DiagramCanvas when its left edge moves', () => {
  let rect = { left: 56, top: 48, width: 800, height: 600 };
  let notify: (() => void) | undefined;

  beforeEach(() => {
    rect = { left: 56, top: 48, width: 800, height: 600 };
    vi.stubGlobal(
      'ResizeObserver',
      class {
        constructor(callback: () => void) {
          notify = callback;
        }
        observe(): void { /* the test calls the callback itself */ }
        disconnect(): void { /* nothing to release */ }
      },
    );
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
      () => ({ ...rect, right: rect.left + rect.width, bottom: rect.top + rect.height, x: rect.left, y: rect.top, toJSON: () => ({}) }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const mount = (): Viewport[] => {
    const seen: Viewport[] = [];
    render(<DiagramCanvas document={parseDocument(orderService)} theme={diagramThemes.overpass} onViewportChange={(v) => seen.push(v)} />);
    return seen;
  };

  const last = (seen: Viewport[]): Viewport => {
    const viewport = seen.at(-1);
    if (viewport === undefined) {
      throw new Error('the canvas reported no viewport');
    }
    return viewport;
  };

  /** Where the world origin lands on the screen, in client pixels. */
  const originOnScreen = (viewport: Viewport): { x: number; y: number } => ({ x: rect.left + viewport.x, y: rect.top + viewport.y });

  it('keeps the drawing on the same screen pixels when a panel pushes the left edge right, and back again', () => {
    const seen = mount();
    const before = last(seen);
    const beforeOnScreen = originOnScreen(before);

    rect = { left: 296, top: 48, width: 560, height: 600 };
    act(() => { notify?.(); });
    const opened = last(seen);
    expect(opened).not.toBe(before);
    expect(opened.zoom).toBe(before.zoom);
    expect(opened.x).toBeCloseTo(before.x - 240, 6);
    expect(originOnScreen(opened)).toEqual(beforeOnScreen);

    rect = { left: 56, top: 48, width: 800, height: 600 };
    act(() => { notify?.(); });
    expect(originOnScreen(last(seen))).toEqual(beforeOnScreen);
  });

  it('leaves the viewport alone when only the right edge moves (the properties panel)', () => {
    const seen = mount();
    const count = seen.length;
    rect = { left: 56, top: 48, width: 500, height: 600 };
    act(() => { notify?.(); });
    expect(seen.length).toBe(count);
  });
});
