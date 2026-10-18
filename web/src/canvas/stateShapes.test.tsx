// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { STATE_SHAPE_PREFIX, STATE_SHAPES, stateShapeName } from '../model/stateShapes.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { renderShape, stateBarThickness, stateNameBand } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'default');

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string): string {
  const { container, unmount } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

describe('state shapes', () => {
  it('draws every shape in the library, not the fallback rectangle, and hides the pseudostates’ labels', () => {
    const fallback = markup('rectangle');
    expect(STATE_SHAPES.length).toBeGreaterThan(0);
    for (const shape of STATE_SHAPES) {
      const name = stateShapeName(shape.name);
      expect(markup(name), `${name} renders as the fallback rectangle`).not.toBe(fallback);
      const hides = renderShape(name, 200, 100, style).hideLabel === true;
      expect(hides, `${name} label hiding`).toBe(shape.name !== 'composite-state');
    }
  });

  it('falls back to the rectangle for a state name the library does not carry', () => {
    expect(markup(`${STATE_SHAPE_PREFIX}not-a-real-shape`)).toBe(markup('rectangle'));
  });

  it.each([
    ['initial', 'circle', 'r', '22'],
    ['final', 'circle', 'r', '30'],
    ['choice', 'polygon', 'points', '100,0 200,50 100,100 0,50'],
    ['fork-join', 'rect', 'height', '14'],
    ['shallow-history', 'circle', 'r', '28'],
    ['entry-point', 'circle', 'r', '24'],
    ['composite-state', 'rect', 'rx', '8'],
  ])('draws %s with its own geometry', (name, tag, attribute, expected) => {
    const { container, unmount } = render(<svg>{renderShape(stateShapeName(name), 200, 100, style).element}</svg>);
    expect(container.querySelector(tag)?.getAttribute(attribute)).toBe(expected);
    unmount();
  });

  it('spans the fork/join bar across the width at a capped thickness, so it stays a bar', () => {
    // Two-sided: a taller box keeps the cap, a shorter one scales below it.
    expect(stateBarThickness(100)).toBe(14);
    expect(stateBarThickness(40)).toBeCloseTo(6.4, 5);
    const { container, unmount } = render(<svg>{renderShape(stateShapeName('fork-join'), 200, 100, style).element}</svg>);
    const bar = container.querySelector('rect');
    expect(bar?.getAttribute('width')).toBe('200');
    unmount();
  });

  it('names the composite state in a top compartment with a divider line', () => {
    expect(stateNameBand(100)).toBe(28);
    const result = renderShape(stateShapeName('composite-state'), 200, 100, style);
    expect(result.labelBox).toEqual({ x: 0, y: 0, w: 200, h: 28 });
    const { container, unmount } = render(<svg>{result.element}</svg>);
    expect(container.querySelector('path')?.getAttribute('d')).toBe('M0,28 H200');
    unmount();
  });

  it('draws H and H* in the shallow and deep history markers', () => {
    const shallow = render(<svg>{renderShape(stateShapeName('shallow-history'), 200, 100, style).element}</svg>);
    expect(shallow.container.querySelector('text')?.textContent).toBe('H');
    shallow.unmount();
    const deep = render(<svg>{renderShape(stateShapeName('deep-history'), 200, 100, style).element}</svg>);
    expect(deep.container.querySelector('text')?.textContent).toBe('H*');
    deep.unmount();
  });
});
