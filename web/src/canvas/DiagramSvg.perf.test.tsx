// @vitest-environment jsdom
import { act, render } from '@testing-library/react';
import perfBenchmark from '../../../docs/examples/perf-2000n-2500e.json';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument } from './documentIndex.ts';
import { loadScale } from '../testing/loadScale.ts';
import { estimateTextWidth } from './text.ts';
import { FULL_DETAIL_ZOOM } from './virtualise.ts';
import { fitToContent, type Viewport } from './viewport.ts';

// The synthetic benchmark for the M2 editor checklist, item 29: 2,000 nodes and 2,500 edges panned and zoomed at a
// sustained 60 fps. The checklist names a mid-range laptop, which a CI runner under jsdom is not, so this suite measures
// the same DiagramSvg/virtualise path the editor uses and reports the numbers, and fails only on a gross regression
// rather than the 16.7 ms frame budget itself. See the threshold constants below for the chosen headroom.
//
// The zoom-1 numbers alone are not evidence for ticking item 29: at zoom 1 culling mounts a few dozen of the 4,500
// elements, so they measure the cheap case. The expensive case is zoomed out (fit-to-content), where every node and
// edge is mounted; the fit-to-content mount and the zoomed-out pan and zoom frames below measure it. jsdom neither lays
// out nor paints, so this covers the React half of a frame only; the browser half was measured in Chrome: it re-shapes
// every mounted SVG label on each zoom step, which is why the canvas drops labels below a legible size, and updates and
// paints every mounted element's transform, markers and dashes, which is why it draws them simplified below
// FULL_DETAIL_ZOOM (both in virtualise.ts). The benchmark's edges name no routing mode, so nothing here exercises the
// router: the connector-routing half of item 29 is measured in routing.perf.test.ts.

const document = parseDocument(perfBenchmark);
const theme = diagramThemes.overpass;
const size = { width: 800, height: 600 };
/** A zoom of 1 puts roughly 4 x 5 cells of the 50 x 40 grid on screen, a representative working viewport. */
const viewport: Viewport = { x: 0, y: 0, zoom: 1 };
/** Fit-to-content over the whole grid: roughly zoom 0.065, with every node and edge on screen. */
const bounds = indexDocument(document).contentBounds;
const fitViewport = fitToContent(bounds, size);

/** Item 29's budget: 1000 ms / 60 fps. Kept as the number the checklist names, not the assertion threshold. */
const FRAME_BUDGET_MS = 1000 / 60;

// A hard 16.7 ms assertion would gate on CI/jsdom measurement noise, not on the code, so the suite fails only on a
// gross regression: 15x the frame budget for a single pan frame, and 30x for a cold mount (which also pays the full
// index build plus the first reconciliation). On the reference machine the measured pan-frame median was ~1 ms and a
// cold mount ~25 ms, so 15x/30x leaves a decade of headroom while still catching the kind of regression that would
// drop the editor below 60 fps. A regression inside normal working range is a reported number, not a red build.
// Zoomed out, the reference machine once measured a pan-frame median of ~19 ms (13 ms on an Apple M5), over the
// 16.7 ms budget, because every frame reconciled all 4,500 elements. DiagramSvg now reuses its element tree while the
// visible set still covers the view (virtualise.ts `visibleSetMemo`), so a zoomed-out pan or zoom frame measures
// ~0.1-0.6 ms on the M5: those frames are held to one frame budget, still some 30x headroom, which the old per-frame
// reconciliation would not reliably meet on a CI runner. DiagramSvg.reconcile.test.tsx pins the same property without
// timing. A fit-to-content mount is ~200-300 ms and gets 180x (3 s), the same decade of headroom over its measured
// cost. A zoom across FULL_DETAIL_ZOOM (virtualise.ts) redraws every mounted element once, simplified or in full, in
// one frame that is not sustained: ~20 ms in jsdom on an Apple M5 for the ~1,300 elements mounted there, held to the
// cold mount's 30x (500 ms), some 25x headroom, which catches a switch that starts rebuilding the index or re-resolving
// styles without flaking on a runner.
const PAN_FRAME_GROSS_MS = FRAME_BUDGET_MS * 15;
const ZOOMED_OUT_FRAME_GROSS_MS = FRAME_BUDGET_MS;
const MOUNT_GROSS_MS = FRAME_BUDGET_MS * 30;
const ZOOMED_OUT_MOUNT_GROSS_MS = FRAME_BUDGET_MS * 180;
const LOD_SWITCH_GROSS_MS = FRAME_BUDGET_MS * 30;

/** Median of a sample, robust to a first-run JIT or GC pause that skews a mean. */
function median(samples: number[]): number {
  const sorted = [...samples].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted[mid] ?? 0;
}

function time(action: () => void): number {
  const start = performance.now();
  act(action);
  return performance.now() - start;
}

// Every limit below is multiplied by how much slower the machine is than the reference right now (loadScale.ts), so a runner busy with other
// builds does not fail a regression test for being busy; a real regression is tens of times slower and still fails.
let scale = 1;

describe('performance benchmark (checklist item 29)', () => {
  beforeEach(() => {
    scale = loadScale();
  });

  it('virtualises 2,000 nodes and 2,500 edges: only what intersects the viewport is mounted', () => {
    const { container } = render(<DiagramSvg document={document} theme={theme} viewport={viewport} size={size} measure={estimateTextWidth} />);
    const mounted = container.querySelectorAll('g[data-id]').length;
    // The viewport shows a handful of the 50 x 40 grid; culling must keep the mounted set a small fraction of the
    // full 4,500 elements, otherwise the perf numbers below are measuring a naive full-tree reconciliation.
    expect(mounted).toBeGreaterThan(0);
    expect(mounted).toBeLessThan(200);
    expect(mounted).toBeLessThan(document.nodes.length + document.edges.length);
  });

  it('reports the cold mount cost of the 2,000 node / 2,500 edge document', () => {
    const mountMs = time(() => render(<DiagramSvg document={document} theme={theme} viewport={viewport} size={size} measure={estimateTextWidth} />));
    console.log(`cold mount: ${mountMs.toFixed(2)} ms (threshold ${MOUNT_GROSS_MS.toFixed(2)} ms)`);
    expect(mountMs).toBeLessThan(MOUNT_GROSS_MS * scale);
  });

  it('reports the pan-frame cost within the virtualised path', () => {
    const { rerender } = render(<DiagramSvg document={document} theme={theme} viewport={viewport} size={size} measure={estimateTextWidth} />);

    // Warm the reconciliation once so the sample is not the very first jsdom JIT, then sample a sequence of panned
    // frames: the sustained work a pan pays every 16.7 ms.
    const samples: number[] = [];
    for (let i = 1; i <= 30; i += 1) {
      const next: Viewport = { x: i * -37, y: i * -23, zoom: 1 };
      const ms = time(() => {
        rerender(<DiagramSvg document={document} theme={theme} viewport={next} size={size} measure={estimateTextWidth} />);
      });
      samples.push(ms);
    }
    const panFrame = median(samples);

    console.log(`pan frame: median ${panFrame.toFixed(2)} ms, max ${Math.max(...samples).toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${PAN_FRAME_GROSS_MS.toFixed(2)} ms)`);
    // The per-frame number is reported faithfully; the assertion only guards against a gross regression.
    expect(panFrame).toBeLessThan(PAN_FRAME_GROSS_MS * scale);
  });

  it('reports the fit-to-content mount, where every element is mounted', () => {
    let container: HTMLElement | undefined;
    const mountMs = time(() => {
      ({ container } = render(<DiagramSvg document={document} theme={theme} viewport={fitViewport} size={size} measure={estimateTextWidth} />));
    });
    const mounted = container?.querySelectorAll('g[data-id]').length ?? 0;
    console.log(`fit-to-content mount (zoom ${fitViewport.zoom.toFixed(3)}, ${String(mounted)} elements): ${mountMs.toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${ZOOMED_OUT_MOUNT_GROSS_MS.toFixed(2)} ms)`);
    expect(mounted).toBe(document.nodes.length + document.edges.length);
    expect(mountMs).toBeLessThan(ZOOMED_OUT_MOUNT_GROSS_MS * scale);
  });

  it('reports the zoomed-out pan-frame cost, where reconciliation covers the whole document', () => {
    const { rerender } = render(<DiagramSvg document={document} theme={theme} viewport={fitViewport} size={size} measure={estimateTextWidth} />);

    const samples: number[] = [];
    for (let i = 1; i <= 30; i += 1) {
      const next: Viewport = { ...fitViewport, x: fitViewport.x + (i % 2 === 0 ? 3 : -3), y: fitViewport.y + (i % 2 === 0 ? 2 : -2) };
      samples.push(
        time(() => {
          rerender(<DiagramSvg document={document} theme={theme} viewport={next} size={size} measure={estimateTextWidth} />);
        }),
      );
    }
    const panFrame = median(samples);

    console.log(`zoomed-out pan frame: median ${panFrame.toFixed(2)} ms, max ${Math.max(...samples).toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${ZOOMED_OUT_FRAME_GROSS_MS.toFixed(2)} ms)`);
    expect(panFrame).toBeLessThan(ZOOMED_OUT_FRAME_GROSS_MS * scale);
  });

  it('reports the zoomed-out zoom-frame cost, a pinch or Ctrl+wheel over the whole document', () => {
    const { rerender } = render(<DiagramSvg document={document} theme={theme} viewport={fitViewport} size={size} measure={estimateTextWidth} />);

    const samples: number[] = [];
    for (let i = 1; i <= 30; i += 1) {
      const next: Viewport = { ...fitViewport, zoom: fitViewport.zoom * (i % 2 === 0 ? 1.02 : 0.98) };
      samples.push(
        time(() => {
          rerender(<DiagramSvg document={document} theme={theme} viewport={next} size={size} measure={estimateTextWidth} />);
        }),
      );
    }
    const zoomFrame = median(samples);

    console.log(`zoomed-out zoom frame: median ${zoomFrame.toFixed(2)} ms, max ${Math.max(...samples).toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${ZOOMED_OUT_FRAME_GROSS_MS.toFixed(2)} ms)`);
    expect(zoomFrame).toBeLessThan(ZOOMED_OUT_FRAME_GROSS_MS * scale);
  });

  it('reports the level-of-detail switch, a zoom crossing FULL_DETAIL_ZOOM that redraws every mounted element', () => {
    // Zoomed about the content's centre, so the crossing itself mounts nothing new and the frame is the switch alone.
    const centre = { x: (bounds?.x ?? 0) + (bounds?.w ?? 0) / 2, y: (bounds?.y ?? 0) + (bounds?.h ?? 0) / 2 };
    const at = (zoom: number): Viewport => ({ x: size.width / 2 - centre.x * zoom, y: size.height / 2 - centre.y * zoom, zoom });
    const { container, rerender } = render(<DiagramSvg document={document} theme={theme} viewport={at(FULL_DETAIL_ZOOM * 1.01)} size={size} measure={estimateTextWidth} />);
    const mounted = container.querySelectorAll('g[data-id]').length;

    const samples: number[] = [];
    for (let i = 1; i <= 10; i += 1) {
      const zoom = FULL_DETAIL_ZOOM * (i % 2 === 1 ? 0.99 : 1.01);
      samples.push(
        time(() => {
          rerender(<DiagramSvg document={document} theme={theme} viewport={at(zoom)} size={size} measure={estimateTextWidth} />);
        }),
      );
      // Each sample is a real switch: the simplified frames draw no node transform group, the full ones one per node.
      expect(container.querySelectorAll('g.node[data-id] > g[transform]').length === 0).toBe(i % 2 === 1);
    }
    const switchFrame = median(samples);

    console.log(`level-of-detail switch (${String(mounted)} elements): median ${switchFrame.toFixed(2)} ms, max ${Math.max(...samples).toFixed(2)} ms (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${LOD_SWITCH_GROSS_MS.toFixed(2)} ms)`);
    expect(mounted).toBeGreaterThan(1000);
    expect(switchFrame).toBeLessThan(LOD_SWITCH_GROSS_MS * scale);
  });

  it('reports the index build over the full document, the pure-function half of the frame', () => {
    const start = performance.now();
    const built = indexDocument(document);
    const indexMs = performance.now() - start;
    console.log(`index build: ${indexMs.toFixed(2)} ms`);
    expect(built.nodesById.size).toBe(document.nodes.length);
    expect(indexMs).toBeLessThan(PAN_FRAME_GROSS_MS * scale);
  });
});
