// @vitest-environment jsdom
/**
 * The connector segment/waypoint drag handles (SelectionOverlay.tsx over segmentDrag.ts `segmentHandles`): they are
 * the visible affordance that a selected connector's route can be pinned, so they must draw for a selected connector
 * and never otherwise, and never into an export.
 */

import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { documentToSvg } from '../editor/exportDiagram.tsx';
import { curveMidpoint } from './curve.ts';
import { indexDocument } from './documentIndex.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { distance, pointAlongPolyline } from './geometry.ts';

const document: Document = parseDocument({
  id: 'doc_handles',
  schemaVersion: 1,
  title: 'Handles',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
  ],
  edges: [
    // A bent orthogonal route: four points, so one draggable middle segment (segmentDrag.ts middleSegments).
    { id: 'e_ortho', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' }, routing: { mode: 'orthogonal', waypoints: [{ x: 150, y: 0 }, { x: 150, y: 150 }] } },
    // A curved route with one waypoint: one waypoint handle plus the arc-length midpoint handle.
    { id: 'e_curved', kind: 'flow', label: 'retry', from: { node: 'n_a' }, to: { node: 'n_b' }, routing: { mode: 'curved', waypoints: [{ x: 250, y: -150 }, { x: 250, y: 150 }] } },
  ],
  meta: { createdAt: '2026-09-28T00:00:00.000Z', updatedAt: '2026-09-28T00:00:00.000Z', createdBy: 't' },
});

const size = { width: 800, height: 600 };
const viewport = { x: 0, y: 0, zoom: 1 };

const handlesIn = (container: HTMLElement, edgeId: string): Element[] => [...container.querySelectorAll(`.segment-handle[data-edge-id="${edgeId}"]`)];

describe('connector segment handles', () => {
  it('draws a drag handle for a selected orthogonal connector and none for the unselected one', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} selection={new Set(['e_ortho'])} />);
    const handles = handlesIn(container, 'e_ortho');
    expect(handles).toHaveLength(1);
    expect(handles[0]?.getAttribute('data-segment-kind')).toBe('orthogonal');
    // The other connector is not selected, so it shows none: the handle set is driven by the selection, not by the edge.
    expect(handlesIn(container, 'e_curved')).toHaveLength(0);
  });

  it('draws no handles at all for an unselected connector', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} selection={new Set(['n_a'])} />);
    expect(container.querySelector('.segment-handle')).toBeNull();
  });

  it('draws one handle per curved waypoint plus one at the curve midpoint', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} selection={new Set(['e_curved'])} />);
    const handles = handlesIn(container, 'e_curved');
    const indices = handles.map((handle) => handle.getAttribute('data-segment-index')).sort();
    // The two waypoints (indices 0 and 1) and the arc-length midpoint (index -1, where a drag inserts a new waypoint).
    expect(indices).toEqual(['-1', '0', '1']);
    expect(handles.every((handle) => handle.getAttribute('data-segment-kind') === 'curved')).toBe(true);
  });

  it('places a curved edge label at the curve midpoint, not the polyline midpoint', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const points = indexDocument(document).edgePaths.get('e_curved')?.points;
    const text = container.querySelector('g[data-id="e_curved"] .edge-label text');
    if (points === undefined || text === null) {
      throw new Error('curved edge label not rendered');
    }
    const at = { x: Number(text.getAttribute('x')), y: Number(text.getAttribute('y')) };
    // The label is where curve.ts puts the arc-length midpoint of the drawn route...
    expect(distance(at, curveMidpoint(points))).toBeLessThan(0.5);
    // ...and measurably not at the polyline midpoint the straight-route label uses.
    expect(distance(at, pointAlongPolyline(points, 0.5))).toBeGreaterThan(5);
  });

  it('never draws handles into an export', () => {
    // The export path renders the document alone, with no selection, so the affordance stays on screen.
    expect(documentToSvg(document, diagramThemes.overpass).svg).not.toContain('segment-handle');
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" />);
    expect(container.querySelector('.segment-handle')).toBeNull();
  });
});
