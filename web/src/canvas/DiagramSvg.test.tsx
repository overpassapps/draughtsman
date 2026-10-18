// @vitest-environment jsdom
import { render } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Point } from '../model/types.ts';
import { AA_TEXT, contrastRatio } from '../theme/contrast.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument } from './documentIndex.ts';
import { EDGE_HIT_WIDTH } from './EdgeView.tsx';
import { estimateTextWidth, LABEL_FONT_SIZE } from './text.ts';
import { FULL_DETAIL_ZOOM, MIN_LABEL_DEVICE_PX } from './virtualise.ts';
import { fitToContent } from './viewport.ts';

const document = parseDocument(orderService);
const size = { width: 800, height: 600 };
const viewport = { x: 0, y: 0, zoom: 1 };

/** The polyline an edge actually drew, in the world coordinates the renderer writes into the `points` attribute. */
const pointsOf = (root: HTMLElement, id: string): Point[] => {
  const attribute = root.querySelector(`g[data-id="${id}"] polyline:not(.hit-area)`)?.getAttribute('points');
  expect(attribute, `${id} draws no polyline`).toBeTruthy();
  return (attribute ?? '').split(' ').filter(Boolean).map((pair) => {
    const [x, y] = pair.split(',').map(Number);
    return { x: x ?? NaN, y: y ?? NaN };
  });
};

describe('DiagramSvg', () => {
  it('mounts one group per node and edge, keyed by id', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const ids = [...container.querySelectorAll('g[data-id]')].map((g) => g.getAttribute('data-id'));
    expect(ids.sort()).toEqual([...document.nodes.map((n) => n.id), ...document.edges.map((e) => e.id)].sort());
  });

  it('nests container children inside the container clip', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const vpc = container.querySelector('g[data-id="c_vpc"]');
    expect(vpc?.querySelector('clipPath#clip-c_vpc rect')).not.toBeNull();
    expect(vpc?.querySelector('g.children[clip-path="url(#clip-c_vpc)"] g[data-id="n_api"]')).not.toBeNull();
    expect(container.querySelector('g[data-id="n_web"]')?.parentElement?.classList.contains('nodes')).toBe(true);
  });

  it('styles an unstyled node from the theme, so switching theme changes the picture and not the document', () => {
    const before = structuredClone(document);
    const overpass = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const neutral = render(<DiagramSvg document={document} theme={diagramThemes.neutral} viewport={viewport} size={size} idPrefix="b-" />);
    const fillIn = (root: HTMLElement): string | null | undefined => root.querySelector('g[data-id="n_api"] .shape')?.getAttribute('fill');
    expect(fillIn(overpass.container)).toBe(diagramThemes.overpass.nodes.service?.fill);
    expect(fillIn(neutral.container)).toBe(diagramThemes.neutral.nodes.service?.fill);
    expect(document).toEqual(before);
  });

  it('applies document theme overrides beneath the node style', () => {
    const caption = { id: 'n_caption', kind: 'text', shape: 'rectangle', label: 'Caption', geometry: { x: 0, y: 0, w: 120, h: 40 } };
    // The theme names no `badge` kind, so this one inherits nodes.default; its sparse style only clears the fill.
    const badge = { id: 'n_badge', kind: 'badge', shape: 'rectangle', label: 'Badge', geometry: { x: 0, y: 60, w: 120, h: 40 }, style: { fill: 'transparent' } };
    const themed = {
      ...document,
      nodes: [...document.nodes, caption, badge],
      theme: { 'nodes.service.fill': '#123456', 'nodes.container.stroke': '#654321', 'nodes.text.textColor': '#E0A526', 'nodes.default.textColor': '#00C853' },
    };
    const labelFill = (root: HTMLElement, id: string): string => root.querySelector(`g[data-id="${id}"] text.label`)?.getAttribute('fill') ?? '';
    const { container } = render(<DiagramSvg document={themed} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    expect(container.querySelector('g[data-id="n_api"] .shape')?.getAttribute('fill')).toBe('#123456');
    // c_vpc has an explicit dashed style in the example; the override changes only the stroke colour.
    const vpc = container.querySelector('g[data-id="c_vpc"] .shape');
    expect(vpc?.getAttribute('stroke')).toBe('#654321');
    expect(vpc?.getAttribute('stroke-dasharray')).toBe('6 4');
    // Both fills are transparent, so these labels would otherwise take the surface colour; an override of the token
    // each kind actually inherits wins instead, and nodes.default does not leak into a kind the theme names.
    expect(labelFill(container, 'n_caption')).toBe('#E0A526');
    expect(labelFill(container, 'n_badge')).toBe('#00C853');

    // With only nodes.default overridden, the named kind keeps the surface colour and stays legible on export paper.
    const defaultOnly = { ...themed, theme: { 'nodes.default.textColor': '#E0A526' } };
    const forExport = render(<DiagramSvg document={defaultOnly} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="x-" />);
    expect(contrastRatio(labelFill(forExport.container, 'n_caption'), diagramThemes.overpass.exportBackground) ?? 0).toBeGreaterThanOrEqual(AA_TEXT);
    expect(labelFill(forExport.container, 'n_badge')).toBe('#E0A526');
  });

  it('keeps a colour the node named for itself, even when it matches the kind default', () => {
    // Presence, not value: the document stored this colour deliberately, so the surface does not overrule it.
    const kindColour = diagramThemes.overpass.nodes.text?.textColor ?? '';
    const caption = { id: 'n_caption', kind: 'text', shape: 'rectangle', label: 'Caption', geometry: { x: 0, y: 0, w: 120, h: 40 }, style: { textColor: kindColour } };
    const doc = { ...document, nodes: [...document.nodes, caption] };
    const { container } = render(<DiagramSvg document={doc} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="z-" />);
    expect(container.querySelector('g[data-id="n_caption"] text.label')?.getAttribute('fill')).toBe(kindColour);
  });

  it('keeps the theme colours when the surface background is not a colour it can read', () => {
    const caption = { id: 'n_caption', kind: 'text', shape: 'rectangle', label: 'Caption', geometry: { x: 0, y: 0, w: 120, h: 40 } };
    // Schema-valid but unreadable, so no contrast can be derived from either; guessing painted white on white.
    // `FFFFFF` reads as hex but SVG will not paint with it, so the rect stays black and a derived colour would lie.
    for (const background of ['rgb(255, 255, 255)', 'FFFFFF']) {
      const doc = { ...document, nodes: [...document.nodes, caption], theme: { canvasBackground: background, 'nodes.default.textColor': '#00C853' } };
      const { container } = render(<DiagramSvg document={doc} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
      expect(container.querySelector('rect.canvas-background')?.getAttribute('fill')).toBe(background);
      // The `text` kind's own colour, not the substitute a readable surface would have supplied.
      expect(container.querySelector('g[data-id="n_caption"] text.label')?.getAttribute('fill')).toBe(diagramThemes.overpass.nodes.text?.textColor);
    }
  });

  it('leaves the surface to colour a label whose document override coloured nothing', () => {
    const caption = { id: 'n_caption', kind: 'text', shape: 'rectangle', label: 'Caption', geometry: { x: 0, y: 0, w: 120, h: 40 } };
    // The palette carries no `brand`, so the override leaves the kind's colour alone and the surface still decides.
    const droppedOverride = { ...document, nodes: [...document.nodes, caption], theme: { 'nodes.text.textColor': 'token:brand' } };
    const { container } = render(<DiagramSvg document={droppedOverride} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="y-" />);
    const fill = container.querySelector('g[data-id="n_caption"] text.label')?.getAttribute('fill') ?? '';
    expect(contrastRatio(fill, diagramThemes.overpass.exportBackground) ?? 0).toBeGreaterThanOrEqual(AA_TEXT);
  });

  it('draws a node still waiting for layout at a provisional spot, marked, and keeps its edges', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const waiting = parseDocument({
      ...orderService,
      nodes: [...orderService.nodes, { id: 'n_todo', kind: 'service', shape: 'rectangle', label: 'Not laid out yet' }],
      edges: [...orderService.edges, { id: 'e_todo', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_todo' } }],
    });
    const { container } = render(<DiagramSvg document={waiting} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    expect(container.querySelector('g[data-id="n_web"]')).not.toBeNull();
    // The node is drawn at a provisional position, marked as unplaced, and the edge that touches it still draws.
    const todo = container.querySelector('g[data-id="n_todo"]');
    expect(todo).not.toBeNull();
    expect(todo?.getAttribute('data-unplaced')).toBe('true');
    expect(container.querySelector('g[data-id="e_todo"] polyline:not(.hit-area)')).not.toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('n_todo'));
    warn.mockRestore();
  });

  it.each([
    { parentPlaced: true, childPlaced: false },
    { parentPlaced: false, childPlaced: false },
    { parentPlaced: false, childPlaced: true },
  ])('draws incomplete container hierarchies after fitting: %j', ({ parentPlaced, childPlaced }) => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const waiting = parseDocument({
        ...orderService,
        nodes: [
          { id: 'box', kind: 'container', shape: 'rectangle', label: 'Box', ...(parentPlaced ? { geometry: { x: 0, y: 0, w: 300, h: 300 } } : {}) },
          { id: 'child', kind: 'service', shape: 'rectangle', label: 'Child', parent: 'box', ...(childPlaced ? { geometry: { x: 20, y: 40, w: 100, h: 50 } } : {}) },
          { id: 'nested', kind: 'service', shape: 'rectangle', label: 'Nested', parent: 'child' },
        ],
        edges: [
          { id: 'link', kind: 'flow', from: { node: 'box' }, to: { node: 'child' } },
          { id: 'nested_link', kind: 'flow', from: { node: 'child' }, to: { node: 'nested' } },
        ],
      });
      const before = structuredClone(waiting);
      const index = indexDocument(waiting);
      const fitted = fitToContent(index.contentBounds, size);
      const { container } = render(<DiagramSvg document={waiting} index={index} theme={diagramThemes.overpass} viewport={fitted} size={size} />);
      for (const node of waiting.nodes) {
        const group = container.querySelector(`g[data-id="${node.id}"]`);
        expect(group).not.toBeNull();
        expect(group?.parentElement?.classList.contains('nodes')).toBe(true);
        expect(group?.getAttribute('data-unplaced')).toBe(node.geometry ? null : 'true');
        const bounds = index.nodeBounds.get(node.id);
        expect(bounds?.w).toBeGreaterThan(0);
        expect(bounds?.h).toBeGreaterThan(0);
      }
      for (const edge of waiting.edges) {
        expect(container.querySelector(`g[data-id="${edge.id}"] polyline:not(.hit-area)`)).not.toBeNull();
      }
      expect(waiting).toEqual(before);
    } finally {
      warn.mockRestore();
    }
  });

  it('draws a curved edge as a smooth path and a straight edge as a polyline', () => {
    const curved = parseDocument({
      ...orderService,
      edges: [
        ...orderService.edges,
        { id: 'e_curve', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_db' }, routing: { mode: 'curved', waypoints: [{ x: 200, y: 40 }] } },
        { id: 'e_straight', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_cache' }, routing: { mode: 'straight' } },
      ],
    });
    const { container } = render(<DiagramSvg document={curved} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const curvePath = container.querySelector('g[data-id="e_curve"] path:not(.hit-area)');
    expect(curvePath).not.toBeNull();
    expect(curvePath?.getAttribute('d')).toContain(' C ');
    expect(curvePath?.getAttribute('marker-end')).toBeTruthy();
    expect(container.querySelector('g[data-id="e_curve"] polyline')).toBeNull();
    // A straight edge still draws a polyline, untouched by the curve renderer.
    const straight = container.querySelector('g[data-id="e_straight"] polyline:not(.hit-area)');
    expect(straight).not.toBeNull();
  });

  it('draws a self-loop as a fixed arc carrying its arrowhead', () => {
    const loops = parseDocument({
      ...orderService,
      edges: [...orderService.edges, { id: 'e_self', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_web' } }],
    });
    const { container } = render(<DiagramSvg document={loops} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const polyline = container.querySelector('g[data-id="e_self"] polyline:not(.hit-area)');
    expect(polyline).not.toBeNull();
    // The flow edge ends in an arrow; the loop carries it like any other edge would.
    expect(polyline?.getAttribute('marker-end')).toBe('url(#arrow-arrow-_7f8aa0)');
    // The arc samples the loop into several points, so it is a curve, not the zero-length line a self-loop used to draw.
    const points = (polyline?.getAttribute('points') ?? '').split(' ').filter(Boolean);
    expect(points.length).toBeGreaterThan(2);

    // A self-link that has been routed keeps its own line rather than taking the fixed arc.
    const routed = parseDocument({
      ...orderService,
      edges: [
        ...orderService.edges,
        { id: 'e_routed', kind: 'flow', from: { node: 'n_web' }, to: { node: 'n_web' }, routing: { mode: 'straight', waypoints: [{ x: 200, y: 8 }] } },
      ],
    });
    const routedContainer = render(<DiagramSvg document={routed} theme={diagramThemes.overpass} viewport={viewport} size={size} />).container;
    expect(routedContainer.querySelector('g[data-id="e_routed"] polyline:not(.hit-area)')).not.toBeNull();
  });

  it('draws a ported self-loop as an arc at the point both its endpoints resolve to', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    // n_api spans x 40..360, y 200..280 and declares in@(0.5,0) — its top centre — and cache@(1,0.5), its right edge.
    const ported = parseDocument({
      ...orderService,
      edges: [
        ...orderService.edges,
        { id: 'e_port_to_node', kind: 'flow', from: { node: 'n_api', port: 'cache' }, to: { node: 'n_api' } },
        { id: 'e_same_port', kind: 'flow', from: { node: 'n_api', port: 'cache' }, to: { node: 'n_api', port: 'cache' } },
        { id: 'e_dangling_port', kind: 'flow', from: { node: 'n_api', port: 'nope' }, to: { node: 'n_api' } },
        { id: 'e_two_ports', kind: 'flow', from: { node: 'n_api', port: 'in' }, to: { node: 'n_api', port: 'cache' } },
      ],
    });
    const { container } = render(<DiagramSvg document={ported} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    // Both endpoints land on the cache port, so the arc leaves and returns there rather than collapsing to a dot.
    for (const id of ['e_port_to_node', 'e_same_port']) {
      const points = pointsOf(container, id);
      expect(points.length).toBeGreaterThan(2);
      expect(points[0]).toEqual({ x: 340, y: 240 });
      expect(points[points.length - 1]).toEqual({ x: 380, y: 240 });
      expect(Math.min(...points.map((p) => p.y))).toBe(220);
    }

    // A port the node never declared resolves to nothing — lint reports that later — so the loop stands on the top edge.
    const dangling = pointsOf(container, 'e_dangling_port');
    expect(dangling[0]).toEqual({ x: 180, y: 200 });
    expect(dangling[dangling.length - 1]).toEqual({ x: 220, y: 200 });

    // Two distinct ports already give a real line between them, so that self-link keeps it instead of taking the arc.
    expect(pointsOf(container, 'e_two_ports')).toEqual([{ x: 200, y: 200 }, { x: 360, y: 240 }]);
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });

  it('draws the arc when a ported self-loop resolves to endpoints an ulp apart', () => {
    // The port lands on (4.9, 163.2); the boundary point computed back towards it reconstructs 4.899999999999999,
    // so both ends still round to one rendered coordinate pair and the polyline between them paints nothing.
    const ulp = parseDocument({
      ...orderService,
      nodes: [
        ...orderService.nodes,
        { id: 'n_tiny', kind: 'service', shape: 'rectangle', label: 'Tiny', geometry: { x: 3, y: 56, w: 38, h: 134 }, ports: [{ id: 'p', x: 0.05, y: 0.8 }] },
      ],
      edges: [...orderService.edges, { id: 'e_ulp', kind: 'flow', from: { node: 'n_tiny', port: 'p' }, to: { node: 'n_tiny' } }],
    });
    const { container } = render(<DiagramSvg document={ulp} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    // The arc stands on the port, with its radius clamped to half the node's 38 units of width.
    const points = pointsOf(container, 'e_ulp');
    expect(points.length).toBeGreaterThan(2);
    expect(points[0]).toEqual({ x: -14.1, y: 163.2 });
    expect(points[points.length - 1]).toEqual({ x: 23.9, y: 163.2 });
    expect(Math.min(...points.map((p) => p.y))).toBe(144.2);
  });

  it('drops an edge whose two coincident nodes leave nothing to draw', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const coincident = parseDocument({
      ...orderService,
      nodes: [
        ...orderService.nodes,
        { id: 'n_x', kind: 'service', label: 'X', shape: 'rectangle', geometry: { x: 100, y: 100, w: 80, h: 80 } },
        { id: 'n_y', kind: 'service', label: 'Y', shape: 'rectangle', geometry: { x: 100, y: 100, w: 80, h: 80 } },
      ],
      edges: [...orderService.edges, { id: 'e_dup', kind: 'flow', from: { node: 'n_x' }, to: { node: 'n_y' } }],
    });
    const { container } = render(<DiagramSvg document={coincident} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    expect(container.querySelector('g[data-id="e_dup"]')).toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('e_dup'));
    warn.mockRestore();
  });

  it('leaves node and edge labels out on the canvas below a legible device size, and draws them from it upwards', () => {
    const labels = (root: HTMLElement): number => root.querySelectorAll('text.label, g.edge-label').length;
    const threshold = MIN_LABEL_DEVICE_PX / LABEL_FONT_SIZE;
    const legible = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: threshold }} size={size} />);
    expect(legible.container.querySelectorAll('text.label').length).toBe(document.nodes.filter((n) => n.label.length > 0).length);
    expect(legible.container.querySelectorAll('g.edge-label').length).toBe(document.edges.filter((e) => (e.label ?? '').length > 0).length);
    expect(labels(legible.container)).toBeGreaterThan(0);

    const tiny = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: threshold * 0.9 }} size={size} idPrefix="t-" />);
    expect(labels(tiny.container)).toBe(0);
    // Only the labels go: every node and edge is still mounted, and so still selectable.
    expect(tiny.container.querySelectorAll('g[data-id]').length).toBe(document.nodes.length + document.edges.length);

    // The same view on a screen of twice the pixel density still has room for them.
    const dense = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: threshold * 0.9 }} size={size} pixelRatio={2} idPrefix="d-" />);
    expect(labels(dense.container)).toBe(labels(legible.container));
  });

  it('always draws labels on the export surface, however far out the view is', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 0.05 }} size={size} surface="export" />);
    expect(container.querySelectorAll('text.label').length).toBe(document.nodes.filter((n) => n.label.length > 0).length);
    expect(container.querySelectorAll('g.edge-label').length).toBeGreaterThan(0);
  });

  it('draws nodes as plain boxes and edges as plain lines below the full-detail zoom, and in full from it upwards', () => {
    const at = (zoom: number, prefix: string) => render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom }} size={size} pixelRatio={2} idPrefix={prefix} />).container;
    const full = at(FULL_DETAIL_ZOOM, 'f-');
    const far = at(FULL_DETAIL_ZOOM * 0.99, 's-');
    const ids = (root: HTMLElement): (string | null)[] => [...root.querySelectorAll('g[data-id]')].map((g) => g.getAttribute('data-id')).sort();

    // Full detail: shape groups with their transform and detail strokes, arrowheads, dashes, and labels (at DPR 2).
    expect(full.querySelectorAll('g.node[data-id] > g[transform]')).toHaveLength(document.nodes.length);
    expect(full.querySelector('g[data-id="n_db"] .shape-detail')).not.toBeNull();
    expect(full.querySelectorAll('marker').length).toBeGreaterThan(0);
    expect(full.querySelector('g.edge polyline[marker-end]')).not.toBeNull();
    expect(full.querySelector('g.node .shape[stroke-dasharray]')).not.toBeNull();
    expect(full.querySelectorAll('text.label, g.edge-label').length).toBeGreaterThan(0);

    // Simplified: the same elements, each node one box in world space with its own fill and outline, and nothing else.
    expect(ids(far)).toEqual(ids(full));
    for (const node of document.nodes) {
      const group = far.querySelector(`g[data-id="${node.id}"]`);
      const box = group?.querySelector(':scope > rect.shape');
      expect(box?.getAttribute('x')).toBe(String(node.geometry?.x));
      expect(box?.getAttribute('y')).toBe(String(node.geometry?.y));
      expect(box?.getAttribute('width')).toBe(String(node.geometry?.w));
      expect(box?.getAttribute('fill')).toBe(full.querySelector(`g[data-id="${node.id}"] .shape`)?.getAttribute('fill'));
      expect(box?.getAttribute('stroke')).toBe(full.querySelector(`g[data-id="${node.id}"] .shape`)?.getAttribute('stroke'));
      expect(group?.querySelector(':scope > g[transform]')).toBeNull();
    }
    expect(far.querySelectorAll('.shape-detail, marker, [marker-start], [marker-end], [stroke-dasharray], text, g.edge-label')).toHaveLength(0);
    // Every edge keeps its visible line and its hit stroke, so it is still drawn and still clickable.
    expect(far.querySelectorAll('g.edge polyline.hit-area')).toHaveLength(document.edges.length);
    expect(far.querySelectorAll('g.edge polyline:not(.hit-area)')).toHaveLength(document.edges.length);
    // Container children are still clipped to their container.
    expect(far.querySelector('g[data-id="c_vpc"] g.children[clip-path] g[data-id="n_api"]')).not.toBeNull();
  });

  it('keeps a rotated node rotated about its centre, and draws no box for a text node, when simplified', () => {
    const rotated = { id: 'n_r', kind: 'service', shape: 'rounded', label: 'R', geometry: { x: 100, y: 40, w: 60, h: 20, rotation: 30 } };
    const caption = { id: 'n_t', kind: 'text', shape: 'text', label: 'Caption', geometry: { x: 0, y: 0, w: 60, h: 20 } };
    const withExtras = parseDocument({ ...orderService, nodes: [...orderService.nodes, rotated, caption] });
    const { container } = render(<DiagramSvg document={withExtras} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 0.1 }} size={size} />);
    expect(container.querySelector('g[data-id="n_r"] > rect.shape')?.getAttribute('transform')).toBe('rotate(30 130 50)');
    expect(container.querySelector('g[data-id="n_t"]')?.children).toHaveLength(0);
  });

  it('draws the export surface in full detail however far out the view is', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 0.05 }} size={size} surface="export" />);
    expect(container.querySelectorAll('g.node[data-id] > g[transform]')).toHaveLength(document.nodes.length);
    expect(container.querySelectorAll('marker').length).toBeGreaterThan(0);
  });

  it('mounts nothing when the viewport is far from the content', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: -100000, y: -100000, zoom: 1 }} size={size} />);
    expect(container.querySelectorAll('g[data-id]')).toHaveLength(0);
  });

  it('emits one marker per arrowhead and colour actually used', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const markers = [...container.querySelectorAll('marker')].map((m) => m.id).sort();
    // flow: arrow in the flow stroke; data: open (e_3) and arrow (e_2 overrides both ends) in the data stroke.
    expect(markers).toEqual(['arrow-arrow-_7f8aa0', 'arrow-arrow-_e04832', 'arrow-open-_e04832']);
    expect(container.querySelector('g[data-id="e_2"] polyline:not(.hit-area)')?.getAttribute('marker-start')).toBe('url(#arrow-arrow-_e04832)');
  });

  it('lays an invisible, wide clickable stroke under every edge line, following the same geometry', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const hitOf = (id: string): SVGPolylineElement | null => container.querySelector(`g[data-id="${id}"] polyline.hit-area`);
    const visibleOf = (id: string): SVGPolylineElement | null => container.querySelector(`g[data-id="${id}"] polyline:not(.hit-area)`);
    for (const edge of document.edges) {
      const hit = hitOf(edge.id);
      const visible = visibleOf(edge.id);
      expect(hit).not.toBeNull();
      expect(hit?.getAttribute('stroke')).toBe('transparent');
      expect(hit?.getAttribute('fill')).toBe('none');
      expect(Number(hit?.getAttribute('stroke-width'))).toBe(EDGE_HIT_WIDTH);
      expect(hit?.style.pointerEvents).toBe('stroke');
      // The hit stroke must not paint an arrowhead of its own: only the visible line carries markers.
      expect(hit?.getAttribute('marker-start')).toBeNull();
      expect(hit?.getAttribute('marker-end')).toBeNull();
      // Same geometry as the line it sits under, so a curved or waypoint route stays clickable along its whole path.
      expect(hit?.getAttribute('points')).toBe(visible?.getAttribute('points'));
      // The target is a fixed size to the hand: it must not shrink with the diagram when the canvas zooms out.
      expect(hit?.getAttribute('vector-effect')).toBe('non-scaling-stroke');
    }
  });

  it('leaves the hit stroke off the export surface, which is paper rather than an interaction surface', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="h-" />);
    expect(container.querySelectorAll('polyline.hit-area')).toHaveLength(0);
    for (const edge of document.edges) {
      expect(container.querySelector(`g[data-id="${edge.id}"] polyline`)).not.toBeNull();
    }
  });

  it('paints the canvas background on screen and the export background for export, with legible surface text on each', () => {
    const textNode = { id: 'n_caption', kind: 'text', shape: 'rectangle', label: 'Caption', geometry: { x: 0, y: 0, w: 120, h: 40 } };
    // Same kind, but inside c_vpc, so it is drawn on the container's fill rather than on the surface.
    const nestedNote = { id: 'n_note', kind: 'text', shape: 'rectangle', label: 'Inside', geometry: { x: 40, y: 400, w: 120, h: 40 }, parent: 'c_vpc' };
    const filledStyled = { ...document, nodes: [...document.nodes, textNode, nestedNote] };
    const onScreen = render(<DiagramSvg document={filledStyled} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const forExport = render(<DiagramSvg document={filledStyled} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="x-" />);
    const attr = (root: HTMLElement, selector: string, name: string): string => root.querySelector(selector)?.getAttribute(name) ?? '';
    const legible = (fg: string, bg: string): boolean => (contrastRatio(fg, bg) ?? 0) >= AA_TEXT;

    for (const [root, background] of [
      [onScreen.container, diagramThemes.overpass.canvasBackground],
      [forExport.container, diagramThemes.overpass.exportBackground],
    ] as const) {
      expect(attr(root, 'rect.canvas-background', 'fill')).toBe(background);
      expect(attr(root, 'g[data-id="e_1"] g.edge-label rect', 'fill')).toBe(background);
      // Edge labels and transparent-fill text sit on the surface, so their colour follows it.
      expect(legible(attr(root, 'g[data-id="e_1"] g.edge-label text', 'fill'), background)).toBe(true);
      expect(legible(attr(root, 'g[data-id="n_caption"] text.label', 'fill'), background)).toBe(true);
      // Text on a filled shape keeps the kind's colour on both surfaces.
      expect(attr(root, 'g[data-id="n_api"] text.label', 'fill')).toBe(diagramThemes.overpass.nodes.service?.textColor);
      // The container is a see-through outline, so it paints nothing behind a label inside it: the surface backs that
      // label, and it follows the surface like any other transparent-fill text.
      expect(legible(attr(root, 'g[data-id="n_note"] text.label', 'fill'), background)).toBe(true);
    }
    expect(diagramThemes.overpass.canvasBackground).not.toBe(diagramThemes.overpass.exportBackground);
    expect(attr(onScreen.container, 'g[data-id="n_note"] text.label', 'fill')).not.toBe(attr(forExport.container, 'g[data-id="n_note"] text.label', 'fill'));
    expect(attr(onScreen.container, 'g[data-id="e_1"] g.edge-label text', 'fill')).not.toBe(attr(forExport.container, 'g[data-id="e_1"] g.edge-label text', 'fill'));
  });

  it('keeps a text label inside a container that does paint a fill in the kind’s own colour', () => {
    const pooled = parseDocument({
      ...orderService,
      nodes: [
        ...orderService.nodes,
        { id: 'n_pool', kind: 'pool', shape: 'rectangle', label: 'Pool', geometry: { x: 0, y: 600, w: 300, h: 200 } },
        { id: 'n_in_pool', kind: 'text', shape: 'rectangle', label: 'Inside', geometry: { x: 20, y: 650, w: 120, h: 40 }, parent: 'n_pool' },
      ],
    });
    const forExport = render(<DiagramSvg document={pooled} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="p-" />);
    // The pool's dark fill is behind it, so the pale kind colour stays legible; it does not flip to the paper's dark text.
    expect(forExport.container.querySelector('g[data-id="n_in_pool"] text.label')?.getAttribute('fill')).toBe(diagramThemes.overpass.nodes.text?.textColor);
  });

  it('nudges a later label clear of an earlier one on a different connector (M2 gap-audit Finding 4)', () => {
    // Two horizontal connectors stacked close enough that both labels' plates, drawn at their own midpoint, overlap.
    const overlapping = parseDocument({
      ...orderService,
      nodes: [
        ...orderService.nodes,
        { id: 'n_p', kind: 'service', shape: 'rectangle', label: 'P', geometry: { x: 2000, y: 2000, w: 80, h: 40 } },
        { id: 'n_q', kind: 'service', shape: 'rectangle', label: 'Q', geometry: { x: 2400, y: 2000, w: 80, h: 40 } },
        { id: 'n_r', kind: 'service', shape: 'rectangle', label: 'R', geometry: { x: 2000, y: 2016, w: 80, h: 40 } },
        { id: 'n_s', kind: 'service', shape: 'rectangle', label: 'S', geometry: { x: 2400, y: 2016, w: 80, h: 40 } },
      ],
      edges: [
        ...orderService.edges,
        { id: 'e_over_1', kind: 'flow', from: { node: 'n_p' }, to: { node: 'n_q' }, routing: { mode: 'straight' }, label: 'process order' },
        { id: 'e_over_2', kind: 'flow', from: { node: 'n_r' }, to: { node: 'n_s' }, routing: { mode: 'straight' }, label: 'process order' },
      ],
    });
    const wide = { width: 6000, height: 6000 };
    const { container } = render(<DiagramSvg document={overlapping} theme={diagramThemes.overpass} viewport={viewport} size={wide} measure={estimateTextWidth} />);
    const boxOf = (edgeId: string): { x: number; y: number; w: number; h: number } => {
      const rect = container.querySelector(`g[data-id="${edgeId}"] g.edge-label rect`);
      expect(rect, `${edgeId} draws no label plate`).not.toBeNull();
      return {
        x: Number(rect?.getAttribute('x')),
        y: Number(rect?.getAttribute('y')),
        w: Number(rect?.getAttribute('width')),
        h: Number(rect?.getAttribute('height')),
      };
    };
    const a = boxOf('e_over_1');
    const b = boxOf('e_over_2');
    const overlap = a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
    expect(overlap).toBe(false);
  });

  it('draws the grid on the canvas and leaves the export paper bare', () => {
    // The shipped theme has the grid on, so the two surfaces differ only in whether it is drawn.
    expect(diagramThemes.overpass.grid.visible).toBe(true);
    const onScreen = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    const forExport = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} surface="export" idPrefix="x-" />);
    expect(onScreen.container.querySelector('rect.canvas-grid')).not.toBeNull();
    expect(forExport.container.querySelector('rect.canvas-grid')).toBeNull();
    expect(forExport.container.querySelector('pattern')).toBeNull();
  });

  it('keeps resolved styles stable across viewport changes so memoised bodies do not lay out again', () => {
    // Only node and edge bodies measure text, so a pan that re-measures anything re-rendered a memoised body.
    const measure = vi.fn(estimateTextWidth);
    const { rerender } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} measure={measure} />);
    expect(measure).toHaveBeenCalled();
    measure.mockClear();
    // c_vpc and e_2 carry sparse styles in the example, the case a naive per-render resolve would defeat.
    rerender(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 10, y: 5, zoom: 1 }} size={size} measure={measure} />);
    expect(measure).not.toHaveBeenCalled();
  });

  it('renders the example document the same way every time', () => {
    const { container } = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
    expect(container.innerHTML).toMatchSnapshot();
  });
});
