// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { ArrowHead, Document, Edge } from '../model/types.ts';
import { DiagramSvg } from './DiagramSvg.tsx';

// Checklist item 12: each edge kind renders the arrowheads the diagram theme defines, from the kind alone.

const size = { width: 800, height: 600 };
const viewport = { x: 0, y: 0, zoom: 1 };

function withEdge(edge: Omit<Edge, 'id' | 'from' | 'to'>): Document {
  return {
    id: 'doc_arrows',
    schemaVersion: 1,
    title: 'Arrows',
    type: 'architecture',
    nodes: [
      { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
      { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
    ],
    edges: [{ id: 'e_1', from: { node: 'n_a' }, to: { node: 'n_b' }, ...edge }],
    meta: { createdAt: '2026-09-27T00:00:00.000Z', updatedAt: '2026-09-27T00:00:00.000Z', createdBy: 't' },
  };
}

/** What the marker an end references actually draws, read from its element rather than its id. */
function drawnHead(root: Element, attribute: 'marker-start' | 'marker-end'): ArrowHead {
  const line = root.querySelector('g[data-id="e_1"] polyline:not(.hit-area)');
  const reference = line?.getAttribute(attribute);
  if (reference === null || reference === undefined) {
    return 'none';
  }
  const id = /^url\(#(.+)\)$/.exec(reference)?.[1];
  const marker = id === undefined ? null : root.querySelector(`marker[id="${id}"]`);
  if (marker === null) {
    throw new Error(`${attribute} references ${reference}, which is not defined`);
  }
  if (marker.querySelector('circle')) {
    return 'circle';
  }
  const path = marker.querySelector('path');
  const d = path?.getAttribute('d') ?? '';
  if (path?.getAttribute('fill') === 'none') {
    return 'open';
  }
  return d.startsWith('M0,4') ? 'diamond' : 'arrow';
}

function strokeOf(root: Element): string | null | undefined {
  return root.querySelector('g[data-id="e_1"] polyline:not(.hit-area)')?.getAttribute('stroke');
}

const cases = Object.entries(diagramThemes).flatMap(([name, theme]) => Object.entries(theme.edges).map(([kind, style]) => ({ name, theme, kind, style })));

describe('edge arrowheads', () => {
  it.each(cases)('draws the $name theme’s arrowheads and stroke for a $kind edge', ({ theme, kind, style }) => {
    // `default` is what a kind the theme does not name falls back to.
    const edgeKind = kind === 'default' ? 'not_a_theme_kind' : kind;
    const { container } = render(<DiagramSvg document={withEdge({ kind: edgeKind })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(container, 'marker-start')).toBe(style.arrowStart);
    expect(drawnHead(container, 'marker-end')).toBe(style.arrowEnd);
    expect(strokeOf(container)).toBe(style.stroke);
  });

  it.each(Object.keys(diagramThemes))('ends flow in a filled arrow and data in an open one in the %s theme', (name) => {
    const theme = diagramThemes[name as keyof typeof diagramThemes];
    const flow = render(<DiagramSvg document={withEdge({ kind: 'flow' })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(flow.container, 'marker-end')).toBe('arrow');
    flow.unmount();
    const data = render(<DiagramSvg document={withEdge({ kind: 'data' })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(data.container, 'marker-end')).toBe('open');
  });

  it('changes the arrowheads with the kind alone, and an edge’s own style still wins over its kind', () => {
    const theme = diagramThemes.overpass;
    const { container, rerender } = render(<DiagramSvg document={withEdge({ kind: 'flow' })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(container, 'marker-end')).toBe('arrow');
    rerender(<DiagramSvg document={withEdge({ kind: 'association' })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(container, 'marker-end')).toBe('none');
    rerender(<DiagramSvg document={withEdge({ kind: 'data' })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(container, 'marker-end')).toBe('open');
    expect(drawnHead(container, 'marker-start')).toBe('none');

    // A deliberate per-edge override survives a kind switch: the theme only fills what the edge did not say.
    rerender(<DiagramSvg document={withEdge({ kind: 'association', style: { arrowStart: 'diamond' } })} theme={theme} viewport={viewport} size={size} />);
    expect(drawnHead(container, 'marker-start')).toBe('diamond');
    expect(drawnHead(container, 'marker-end')).toBe('none');
  });
});
