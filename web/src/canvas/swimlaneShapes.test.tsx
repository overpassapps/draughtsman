// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { LANE_SHAPE, POOL_SHAPE, SWIMLANE_SHAPES, laneHeaderHeight, poolHeaderHeight } from '../model/swimlanes.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { renderShape } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string): string {
  const { container, unmount } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

describe('swimlane shapes', () => {
  it('draws every swimlane shape as more than the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(SWIMLANE_SHAPES.length).toBe(2);
    for (const shape of SWIMLANE_SHAPES) {
      const drawn = markup(shape);
      expect(drawn, `${shape} renders as the fallback rectangle`).not.toBe(fallback);
      expect(drawn, `${shape} is not marked as a swimlane`).toContain('shape swimlane');
    }
  });

  it('falls back to the rectangle for a swimlane name that is not in the set', () => {
    expect(markup('custom:swimlane/not-a-real-shape')).toBe(markup('rectangle'));
  });

  it('draws the pool band deeper than the lane band at the same size', () => {
    expect(poolHeaderHeight(100)).toBe(24);
    expect(laneHeaderHeight(100)).toBe(16);
    const pool = renderShape(POOL_SHAPE, 200, 100, style);
    const lane = renderShape(LANE_SHAPE, 200, 100, style);
    expect(pool.labelBox).toEqual({ x: 0, y: 0, w: 200, h: 100 });
    expect(lane.labelBox).toEqual({ x: 0, y: 0, w: 200, h: 100 });

    const drawn = render(<svg>{pool.element}</svg>);
    expect(drawn.container.querySelector('path')?.getAttribute('d')).toBe('M0,24 H200');
    drawn.unmount();
    const drawnLane = render(<svg>{lane.element}</svg>);
    expect(drawnLane.container.querySelector('path')?.getAttribute('d')).toBe('M0,16 H200');
    drawnLane.unmount();
  });
});
