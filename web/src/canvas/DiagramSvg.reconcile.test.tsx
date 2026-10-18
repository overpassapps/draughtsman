// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { memo, type ComponentProps } from 'react';
import perfBenchmark from '../../../docs/examples/perf-2000n-2500e.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument } from './documentIndex.ts';
import { estimateTextWidth } from './text.ts';
import { FULL_DETAIL_ZOOM } from './virtualise.ts';
import { fitToContent, viewportTransform, type Viewport } from './viewport.ts';

// Checklist item 29, the deterministic half of DiagramSvg.perf.test.tsx: zoomed out over the benchmark every node and
// edge is mounted, so a pan or zoom frame is at 60 fps only if it reconciles none of them. Counting invocations of
// `NodeViewInner` (the unmemoised body `NodeView` wraps in `memo`) says whether a frame did, with no timing involved -
// spying on the exotic `NodeView` component itself wouldn't work, since a memo component isn't callable as a plain
// function, and re-wrapping the spy in `memo` here keeps the real bailout behaviour under test rather than assuming it.
const renders = vi.hoisted(() => ({ nodeView: 0 }));
vi.mock('./NodeView.tsx', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./NodeView.tsx')>();
  const spied = (props: ComponentProps<typeof actual.NodeViewInner>) => {
    renders.nodeView += 1;
    return actual.NodeViewInner(props);
  };
  return { ...actual, NodeView: memo(spied) };
});

const benchmark = parseDocument(perfBenchmark);
const theme = diagramThemes.overpass;
const size = { width: 800, height: 600 };
const fitted = fitToContent(indexDocument(benchmark).contentBounds, size);

function draw(document: Document, viewport: Viewport) {
  return <DiagramSvg document={document} theme={theme} viewport={viewport} size={size} measure={estimateTextWidth} />;
}

describe('DiagramSvg reconciliation on pan and zoom (checklist item 29)', () => {
  beforeEach(() => {
    renders.nodeView = 0;
  });

  it('reconciles no node when a zoomed-out pan or zoom mounts nothing new', () => {
    const { container, rerender } = render(draw(benchmark, fitted));
    expect(renders.nodeView).toBe(benchmark.nodes.length);
    expect(container.querySelectorAll('g[data-id]')).toHaveLength(benchmark.nodes.length + benchmark.edges.length);

    renders.nodeView = 0;
    for (let i = 1; i <= 10; i += 1) {
      rerender(draw(benchmark, { ...fitted, x: fitted.x + (i % 2 === 0 ? 3 : -3), y: fitted.y + (i % 2 === 0 ? 2 : -2) }));
    }
    const zoomed: Viewport = { ...fitted, zoom: fitted.zoom * 1.01 };
    for (let i = 1; i <= 10; i += 1) {
      rerender(draw(benchmark, i === 10 ? zoomed : { ...fitted, zoom: fitted.zoom * (i % 2 === 0 ? 1.01 : 0.99) }));
    }
    expect(renders.nodeView).toBe(0);
    // The world still moved: only the element tree was reused.
    expect(container.querySelector('g.world')?.getAttribute('transform')).toBe(viewportTransform(zoomed));
  });

  it('reconciles as soon as a pan brings different elements into view, and mounts them', () => {
    const at: Viewport = { x: 0, y: 0, zoom: 1 };
    const { container, rerender } = render(draw(benchmark, at));
    const before = new Set([...container.querySelectorAll('g.node[data-id]')].map((g) => g.getAttribute('data-id')));

    renders.nodeView = 0;
    rerender(draw(benchmark, { ...at, x: -1500, y: -900 }));
    const after = [...container.querySelectorAll('g.node[data-id]')].map((g) => g.getAttribute('data-id'));
    const entered = after.filter((id) => !before.has(id));
    expect(entered.length).toBeGreaterThan(0);
    // Exactly the nodes newly brought into view render - a node that was visible before and stays visible now must
    // not re-render just because the visible set as a whole changed (memo on NodeView, checklist item 29): without
    // it `drawn` rebuilds a fresh element for every still-visible node too, and this count would be `after.length`.
    expect(renders.nodeView).toBe(entered.length);
  });

  it('reconciles when the document changes under an unchanged zoomed-out view, and draws the change', () => {
    const { container, rerender } = render(draw(benchmark, fitted));
    const [first] = benchmark.nodes;
    expect(first?.geometry).toBeDefined();
    const moved: Document = {
      ...benchmark,
      nodes: benchmark.nodes.map((node) => (node === first && node.geometry ? { ...node, geometry: { ...node.geometry, x: node.geometry.x + 40 } } : node)),
    };

    renders.nodeView = 0;
    rerender(draw(moved, fitted));
    expect(renders.nodeView).toBeGreaterThan(0);
    // Fitted, the benchmark is far below FULL_DETAIL_ZOOM, so the node is drawn as its simplified box in world space.
    const x = (first?.geometry?.x ?? 0) + 40;
    expect(container.querySelector(`g[data-id="${first?.id ?? ''}"] > rect.shape`)?.getAttribute('x')).toBe(String(x));
  });

  it('reconciles once when a zoom crosses the full-detail threshold, each way, and never while it stays on one side', () => {
    // Centred on the grid at just above the threshold, so the view and its margin fall well inside the content.
    const centre = { x: 5000, y: 3000 };
    const at = (zoom: number): Viewport => ({ x: size.width / 2 - centre.x * zoom, y: size.height / 2 - centre.y * zoom, zoom });
    const { container, rerender } = render(draw(benchmark, at(FULL_DETAIL_ZOOM * 1.05)));
    const mounted = container.querySelectorAll('g.node[data-id]').length;
    expect(mounted).toBeGreaterThan(0);
    expect(container.querySelectorAll('g.node[data-id] > g[transform]')).toHaveLength(mounted);

    // Zooming about the same point from just above to just below the threshold mounts nothing new (a zoom in keeps
    // the set), so the one reconciliation is the level of detail changing, and it redraws every mounted node.
    renders.nodeView = 0;
    rerender(draw(benchmark, at(FULL_DETAIL_ZOOM * 1.02)));
    expect(renders.nodeView).toBe(0);
    rerender(draw(benchmark, at(FULL_DETAIL_ZOOM * 1.01)));
    rerender(draw(benchmark, at(FULL_DETAIL_ZOOM * 0.99)));
    expect(renders.nodeView).toBe(mounted);
    expect(container.querySelectorAll('g.node[data-id] > g[transform]')).toHaveLength(0);
    expect(container.querySelectorAll('g.node[data-id] > rect.shape')).toHaveLength(mounted);

    renders.nodeView = 0;
    rerender(draw(benchmark, at(FULL_DETAIL_ZOOM * 0.98)));
    expect(renders.nodeView).toBe(0);
    rerender(draw(benchmark, at(FULL_DETAIL_ZOOM * 1.01)));
    expect(renders.nodeView).toBe(mounted);
    expect(container.querySelectorAll('g.node[data-id] > g[transform]')).toHaveLength(mounted);
  });
});
