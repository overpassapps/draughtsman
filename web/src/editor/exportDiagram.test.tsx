// @vitest-environment jsdom
import { render } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import { DiagramSvg } from '../canvas/DiagramSvg.tsx';
import { indexDocument } from '../canvas/documentIndex.ts';
import { indexWithRoutes } from '../canvas/routeDocument.ts';
import { EXPORT_MARGIN, documentToSvg, exportFileName } from './exportDiagram.tsx';

const document = parseDocument(orderService);

describe('documentToSvg', () => {
  it('draws the diagram shapes and labels', () => {
    const { svg } = documentToSvg(document, diagramThemes.overpass);
    expect(svg).toContain('data-id="n_web"');
    expect(svg).toContain('data-id="c_vpc"');
    // The label text, not just the shape, so a blank export cannot pass.
    expect(svg).toContain('Web app');
    expect(svg).toContain('Orders API');
    // The document's own token overrides and the active theme both reach the picture.
    expect(svg).toContain('data-diagram-theme="overpass"');
  });

  it('excludes selection chrome and the grid, which the canvas surface does draw', () => {
    const { svg } = documentToSvg(document, diagramThemes.overpass);
    for (const marker of ['class="selection"', 'selection-handle', 'canvas-grid', 'data-selection-id']) {
      expect(svg).not.toContain(marker);
    }
    // The same renderer, given the editor's view state, does emit those markers: the exclusion above can fail.
    const onCanvas = render(<DiagramSvg document={document} theme={diagramThemes.overpass} viewport={{ x: 0, y: 0, zoom: 1 }} size={{ width: 800, height: 600 }} selection={new Set(['n_web'])} showGrid={true} />);
    expect(onCanvas.container.querySelector('.selection')).not.toBeNull();
    expect(onCanvas.container.querySelector('.selection-handle')).not.toBeNull();
    expect(onCanvas.container.querySelector('rect.canvas-grid')).not.toBeNull();
  });

  it('crops to the content with a margin, at one canvas unit per pixel', () => {
    const bounds = indexDocument(document).contentBounds;
    expect(bounds).toBeDefined();
    const { svg, width, height } = documentToSvg(document, diagramThemes.overpass);
    expect(width).toBe(Math.ceil((bounds?.w ?? 0) + EXPORT_MARGIN * 2));
    expect(height).toBe(Math.ceil((bounds?.h ?? 0) + EXPORT_MARGIN * 2));
    // The world layer is translated so the content's top-left sits one margin in from the origin.
    expect(svg).toContain(`translate(${String(EXPORT_MARGIN - (bounds?.x ?? 0))} ${String(EXPORT_MARGIN - (bounds?.y ?? 0))}) scale(1)`);
    expect(svg).toContain(`viewBox="0 0 ${String(width)} ${String(height)}"`);
  });

  it('honours an explicit margin', () => {
    const bounds = indexDocument(document).contentBounds;
    const { width } = documentToSvg(document, diagramThemes.overpass, { margin: 100 });
    expect(width).toBe(Math.ceil((bounds?.w ?? 0) + 200));
  });

  it('carries each element’s own sparse style and the ER/UML arrowhead ends the editor now offers', () => {
    const styled = parseDocument({
      ...orderService,
      nodes: orderService.nodes.map((node) => (node.id === 'n_web' ? { ...node, style: { ...node.style, fill: '#123456', stroke: '#654321' } } : node)),
      edges: orderService.edges.map((edge) => (edge.id === 'e_1' ? { ...edge, style: { ...edge.style, stroke: '#ABCDEF', dash: 'dashed', arrowEnd: 'many' } } : edge)),
    });
    const { svg } = documentToSvg(styled, diagramThemes.overpass);
    // The node's own fill and stroke override the theme's, and the connector's own stroke overrides the kind's.
    expect(svg).toContain('fill="#123456"');
    expect(svg).toContain('stroke="#654321"');
    expect(svg).toContain('stroke="#ABCDEF"');
    expect(svg).toContain('stroke-dasharray');
    // The crow's foot the edge end names is emitted as its own marker (with its toes) and referenced by the line.
    expect(svg).toContain('arrow-many-_ABCDEF');
    expect(svg).toContain('M3,6 L11,2');
    expect(svg).toContain('marker-end="url(#export-arrow-many-_ABCDEF)"');
    // None of it is drawn for the unstyled document, so these assertions can fail.
    const plain = documentToSvg(document, diagramThemes.overpass).svg;
    expect(plain).not.toContain('#123456');
    expect(plain).not.toContain('#ABCDEF');
    expect(plain).not.toContain('arrow-many');
  });

  it('inlines an image asset as a data URI so the export is self-contained', () => {
    const withImage = parseDocument({
      ...orderService,
      nodes: [...orderService.nodes, { id: 'n_logo', kind: 'image', label: '', shape: 'image', geometry: { x: 0, y: 0, w: 100, h: 50 }, props: { assetId: 'asset_1' } }],
    });

    const { svg } = documentToSvg(withImage, diagramThemes.overpass, { assetHref: (assetId) => (assetId === 'asset_1' ? 'data:image/png;base64,AAAB' : undefined) });

    expect(svg).toContain('data-id="n_logo"');
    expect(svg).toContain('data:image/png;base64,AAAB');
    // Without a resolver the node exports as its placeholder frame, never a bare /api link the file cannot follow.
    const plain = documentToSvg(withImage, diagramThemes.overpass).svg;
    expect(plain).not.toContain('data:image/png;base64,');
  });
});

/** The polyline of the edge `id` in an exported SVG, as points; a curve or an arc would not parse as one. */
function edgePoints(svg: string, id: string): { x: number; y: number }[] {
  const doc = new DOMParser().parseFromString(svg, 'image/svg+xml');
  const line = doc.querySelector(`g[data-id="${id}"] polyline`);
  expect(line, `edge ${id} is drawn as a polyline`).not.toBeNull();
  return (line?.getAttribute('points') ?? '')
    .trim()
    .split(/\s+/)
    .map((pair) => {
      const [x, y] = pair.split(',').map(Number);
      return { x: x ?? Number.NaN, y: y ?? Number.NaN };
    });
}

function axisAligned(points: { x: number; y: number }[]): boolean {
  return points.every((p, i) => i === 0 || p.x === points[i - 1]?.x || p.y === points[i - 1]?.y);
}

const META = { createdAt: '2026-09-20T22:00:00.000Z', updatedAt: '2026-09-20T22:00:00.000Z', createdBy: 'test' };

describe('documentToSvg connector routing', () => {
  // Two tables offset diagonally with a third between them: a straight line from a to b crosses the obstacle.
  const routing = (mode: 'orthogonal' | 'straight', extra: object = {}) =>
    parseDocument({
      schemaVersion: 1,
      id: 'doc_routing',
      type: 'freeform',
      title: 'Routing',
      nodes: [
        { id: 'a', kind: 'rectangle', label: 'A', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 60 } },
        { id: 'b', kind: 'rectangle', label: 'B', shape: 'rectangle', geometry: { x: 400, y: 300, w: 100, h: 60 } },
        { id: 'wall', kind: 'rectangle', label: 'Wall', shape: 'rectangle', geometry: { x: 180, y: 120, w: 140, h: 120 } },
      ],
      edges: [{ id: 'e', kind: 'flow', from: { node: 'a' }, to: { node: 'b' }, routing: { mode, ...extra } }],
      meta: META,
    });

  it('routes an orthogonal connector with no stored waypoints along axis-aligned segments, around the nodes', () => {
    const { svg } = documentToSvg(routing('orthogonal'), diagramThemes.neutral);
    const points = edgePoints(svg, 'e');
    expect(points.length).toBeGreaterThan(2);
    expect(axisAligned(points)).toBe(true);
    // No segment cuts through the wall between the two tables.
    const inside = points.some((p, i) => {
      const q = points[i - 1];
      return q !== undefined && Math.max(Math.min(p.x, q.x), 180) < Math.min(Math.max(p.x, q.x), 320) && Math.max(Math.min(p.y, q.y), 120) < Math.min(Math.max(p.y, q.y), 240);
    });
    expect(inside).toBe(false);
  });

  it('still draws a straight connector as a straight line', () => {
    const points = edgePoints(documentToSvg(routing('straight'), diagramThemes.neutral).svg, 'e');
    expect(points).toHaveLength(2);
    expect(axisAligned(points)).toBe(false);
  });

  it('lets stored waypoints (a dragged segment) win over automatic routing', () => {
    const pinned = routing('orthogonal', { waypoints: [{ x: 50, y: 400 }, { x: 450, y: 400 }] });
    const points = edgePoints(documentToSvg(pinned, diagramThemes.neutral).svg, 'e');
    // Polyline points are world coordinates (the world layer carries the crop's translation).
    expect(points).toContainEqual({ x: 50, y: 400 });
    expect(points).toContainEqual({ x: 450, y: 400 });
  });

  it('keeps a connector that detours outside the nodes inside the cropped picture', () => {
    const doc = routing('orthogonal');
    const bounds = indexWithRoutes(indexDocument(doc)).index.contentBounds;
    const points = edgePoints(documentToSvg(doc, diagramThemes.neutral).svg, 'e');
    for (const p of points) {
      expect(p.x).toBeGreaterThanOrEqual(bounds?.x ?? Number.NaN);
      expect(p.y).toBeGreaterThanOrEqual(bounds?.y ?? Number.NaN);
      expect(p.x).toBeLessThanOrEqual((bounds?.x ?? 0) + (bounds?.w ?? 0));
      expect(p.y).toBeLessThanOrEqual((bounds?.y ?? 0) + (bounds?.h ?? 0));
    }
  });
});

describe('documentToSvg connectors on table columns', () => {
  // The same two tables and rows as the canvas's own column-port tests (canvas/columnConnectors.test.ts).
  const tables = (from: string, to: string) =>
    parseDocument({
      schemaVersion: 1,
      id: 'doc_cols',
      type: 'architecture',
      title: 'Cols',
      nodes: [
        { id: 't_order', kind: 'entity', label: 'Order', shape: 'custom:er/entity', geometry: { x: 0, y: 0, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'FK CustomerId int not null', 'Placed datetime2 not null'] } },
        { id: 't_customer', kind: 'entity', label: 'Customer', shape: 'custom:er/entity', geometry: { x: 500, y: 200, w: 270, h: 92 }, props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null', 'Email nvarchar(100) null'] } },
      ],
      edges: [{ id: 'e', kind: 'association', from: { node: 't_order', port: from }, to: { node: 't_customer', port: to }, routing: { mode: 'orthogonal' } }],
      meta: META,
    });

  it('draws the route from the FK row to the key row, the same route the editor derives', () => {
    const doc = tables('col:CustomerId', 'col:Id');
    const points = edgePoints(documentToSvg(doc, diagramThemes.neutral).svg, 'e');
    expect(points[0]).toEqual({ x: 270, y: 60 });
    expect(points[points.length - 1]).toEqual({ x: 500, y: 242 });
    expect(axisAligned(points)).toBe(true);
    expect(points).toEqual(indexWithRoutes(indexDocument(doc)).routes.get('e')?.points);
  });

  it('attaches the end to the table when the column does not exist, and only that end', () => {
    const points = edgePoints(documentToSvg(tables('col:Nope', 'col:Id'), diagramThemes.neutral).svg, 'e');
    expect(points[0]?.y).not.toBe(60);
    expect(points[points.length - 1]).toEqual({ x: 500, y: 242 });
  });
});

describe('documentToSvg text on the export paper', () => {
  it('draws a colourless text-shape heading in the export text colour, not the dark theme’s light node text', () => {
    const doc = parseDocument({
      schemaVersion: 1,
      id: 'doc_text',
      type: 'freeform',
      title: 'Text',
      nodes: [
        { id: 'heading', kind: 'rectangle', label: 'Heading', shape: 'text', geometry: { x: 0, y: 0, w: 200, h: 40 } },
        { id: 'named', kind: 'rectangle', label: 'Named', shape: 'text', geometry: { x: 0, y: 60, w: 200, h: 40 }, style: { textColor: '#c0392b' } },
      ],
      edges: [],
      meta: META,
    });
    const theme = diagramThemes.overpass;
    const exportText = '#111111';
    const svg = documentToSvg(doc, theme).svg;
    const fillOf = (id: string) => new DOMParser().parseFromString(svg, 'image/svg+xml').querySelector(`g[data-id="${id}"] text`)?.getAttribute('fill');
    expect(theme.exportBackground.toUpperCase()).toBe('#FFFFFF');
    expect(fillOf('heading')).toBe(exportText);
    // A colour the document gave is kept.
    expect(fillOf('named')).toBe('#c0392b');
  });
});

describe('documentToSvg legend container (QA item 7)', () => {
  // The shape SqlImporter.AddLegend writes: a `container` holding one note per tag, filled in the tag's colours.
  const legend = parseDocument({
    schemaVersion: 1,
    id: 'doc_legend',
    type: 'freeform',
    title: 'Legend',
    nodes: [
      { id: 'legend', kind: 'container', label: 'Legend', shape: 'rectangle', geometry: { x: 0, y: 0, w: 220, h: 120 } },
      { id: 'legend_1', kind: 'note', label: 'LEGACY', shape: 'rectangle', parent: 'legend', geometry: { x: 12, y: 40, w: 90, h: 40 }, style: { fill: '#dbeafe', stroke: '#2563eb', textColor: '#111111' } },
    ],
    edges: [],
    meta: META,
  });
  const parsed = (svg: string) => new DOMParser().parseFromString(svg, 'image/svg+xml');

  it('exports the legend see-through with a neutral outline, as the editor draws it, not a dark slab with a red border', () => {
    const theme = diagramThemes.overpass;
    const svg = parsed(documentToSvg(legend, theme).svg);
    const frame = svg.querySelector('g[data-id="legend"] > rect, g[data-id="legend"] rect');
    expect(frame?.getAttribute('fill')).toBe('transparent');
    expect(frame?.getAttribute('stroke')).toBe('#7f8aa0');
    expect(frame?.getAttribute('stroke')).not.toBe('#e04832');
    // Its name sits on the white paper, so it takes the export text colour rather than the theme's pale grey.
    expect(svg.querySelector('g[data-id="legend"] > text, g[data-id="legend"] text')?.getAttribute('fill')).toBe('#111111');
    // The tag note keeps the fill it was given.
    expect(svg.querySelector('g[data-id="legend_1"] rect')?.getAttribute('fill')).toBe('#dbeafe');
  });

  it('draws the same frame on the canvas surface (editor) as on the export', () => {
    const theme = diagramThemes.overpass;
    const onCanvas = render(<DiagramSvg document={legend} theme={theme} viewport={{ x: 0, y: 0, zoom: 1 }} size={{ width: 400, height: 300 }} />);
    const frame = onCanvas.container.querySelector('g[data-id="legend"] rect');
    const exported = parsed(documentToSvg(legend, theme).svg).querySelector('g[data-id="legend"] rect');
    expect(frame?.getAttribute('fill')).toBe(exported?.getAttribute('fill'));
    expect(frame?.getAttribute('stroke')).toBe(exported?.getAttribute('stroke'));
  });
});

describe('documentToSvg background', () => {
  // One rule for SVG and PNG: both are the same string (PNG rasterises it), painted with the theme's export paper.
  it.each(['overpass', 'neutral', 'mono'] as const)('paints the %s theme’s export paper as an opaque rectangle behind the drawing', (name) => {
    const theme = diagramThemes[name];
    const { svg, width, height } = documentToSvg(document, theme);
    const paper = new DOMParser().parseFromString(svg, 'image/svg+xml').querySelector('rect.canvas-background');
    expect(paper?.getAttribute('fill')).toBe(theme.exportBackground);
    expect(paper?.getAttribute('width')).toBe(String(width));
    expect(paper?.getAttribute('height')).toBe(String(height));
  });
});

describe('exportFileName', () => {
  it('names the file after the document title', () => {
    expect(exportFileName('Order service', 'svg')).toBe('order-service.svg');
    expect(exportFileName('Order service', 'png')).toBe('order-service.png');
  });

  it('falls back to a placeholder when the title folds away', () => {
    expect(exportFileName('   ', 'svg')).toBe('diagram.svg');
    expect(exportFileName('!!! ???', 'png')).toBe('diagram.png');
  });
});

describe('documentToSvg carries no notice', () => {
  it('is the diagram and nothing else: no footer text, and a sheet exactly the size of the content and margin', () => {
    const picture = documentToSvg(document, diagramThemes.overpass);

    expect(picture.svg).not.toMatch(/evaluation|notice/i);
    expect(picture.svg).toContain(`viewBox="0 0 ${String(picture.width)} ${String(picture.height)}"`);
  });
});
