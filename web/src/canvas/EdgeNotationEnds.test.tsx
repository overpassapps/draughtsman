// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { ArrowHead, Document, Edge } from '../model/types.ts';
import { DiagramSvg } from './DiagramSvg.tsx';

// The notation ends added for ER and UML diagrams: each new arrowhead draws at both the source and the target,
// referencing a marker that is actually defined and id-stamped with the head's own name, oriented along the edge.

const size = { width: 800, height: 600 };
const viewport = { x: 0, y: 0, zoom: 1 };

const ends: readonly ArrowHead[] = ['one', 'many', 'zeroOrOne', 'oneOrMany', 'zeroOrMany', 'oneAndOnlyOne', 'inheritance', 'aggregation', 'composition'];

function withEdge(style: Edge['style']): Document {
  const edge: Edge = style === undefined
    ? { id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }
    : { id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' }, style };
  return {
    id: 'doc_ends',
    schemaVersion: 1,
    title: 'Ends',
    type: 'architecture',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
    ],
    edges: [edge],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
  };
}

/** The marker id an end's reference resolves to, or undefined when the end draws none. */
function referenced(root: Element, attribute: 'marker-start' | 'marker-end'): string | undefined {
  const line = root.querySelector('g[data-id="e_1"] polyline:not(.hit-area)');
  const reference = line?.getAttribute(attribute);
  if (reference === null || reference === undefined) {
    return undefined;
  }
  return /^url\(#(.+)\)$/.exec(reference)?.[1];
}

/** True when a marker with exactly this id is defined in the defs. */
function definesMarker(root: Element, id: string): boolean {
  return root.querySelector(`marker[id="${id}"]`) !== null;
}

describe('notation ends', () => {
  it.each(ends)('draws %s at the source and the target', (head) => {
    const theme = diagramThemes.overpass;
    const { container, unmount } = render(
      <DiagramSvg document={withEdge({ arrowStart: head, arrowEnd: head })} theme={theme} viewport={viewport} size={size} />,
    );
    const start = referenced(container, 'marker-start');
    const end = referenced(container, 'marker-end');
    expect(start).toContain(`arrow-${head}`);
    expect(end).toContain(`arrow-${head}`);
    // The marker the end references must exist in the defs and be id-stamped with that head's name.
    expect(start).toBeDefined();
    expect(end).toBeDefined();
    if (start !== undefined && end !== undefined) {
      expect(definesMarker(container, start)).toBe(true);
      expect(definesMarker(container, end)).toBe(true);
    }
    unmount();
  });

  it('draws each end independently, so a crow’s foot on one end does not force one on the other', () => {
    const theme = diagramThemes.overpass;
    const { container } = render(
      <DiagramSvg document={withEdge({ arrowStart: 'many', arrowEnd: 'composition' })} theme={theme} viewport={viewport} size={size} />,
    );
    expect(referenced(container, 'marker-start')).toContain('arrow-many');
    expect(referenced(container, 'marker-end')).toContain('arrow-composition');
  });
});