// @vitest-environment jsdom
/**
 * The flight over the checklist item 29 benchmark (docs/examples/perf-2000n-2500e.json: 2,000 nodes, 2,500 edges): a
 * tour that flies from the whole diagram in to a close view, across to the far corner and back out, played through
 * PresentationStage a 16 ms frame at a time. Like DiagramSvg.perf.test.tsx it measures the React half of a frame (jsdom
 * neither lays out nor paints) and reports the numbers, failing only on a gross regression rather than the 16.7 ms
 * budget; the browser half was measured in Chrome (docs/flythrough.md, "Frame rate"). What it does pin without timing is
 * the property the canvas's perf work rests on: a flight frame that mounts nothing new reconciles no element.
 */
import { act, render } from '@testing-library/react';
import perfBenchmark from '../../../docs/examples/perf-2000n-2500e.json';
import { cameraAtProgress, viewRectOf, viewportForCamera } from '../canvas/camera.ts';
import { indexDocument } from '../canvas/documentIndex.ts';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { must } from '../model/sequenceFixture.ts';
import { loadScale } from '../testing/loadScale.ts';
import { parseDocument } from '../model/parse.ts';
import type { Flythrough } from '../model/types.ts';
import { PresentationStage } from './PresentationStage.tsx';
import type { FrameSource } from './usePresentation.ts';

const document = parseDocument(perfBenchmark);
const size = { width: 1280, height: 720 };
const bounds = must(indexDocument(document).contentBounds);

/** Item 29's budget: 1000 ms / 60 fps. */
const FRAME_BUDGET_MS = 1000 / 60;
/** The suite fails at this, a gross regression, and reports the real number below it (see DiagramSvg.perf.test.tsx). */
const FLIGHT_FRAME_GROSS_MS = FRAME_BUDGET_MS * 3;

// Overview, a close view near the top-left of the grid, a close view across at the far corner, and back to the overview.
const close = (x: number, y: number) => ({ x, y, w: 800, h: 450 });
const tour: Flythrough = {
  keyframes: [
    { id: 'k_1', view: { x: bounds.x, y: bounds.y, w: bounds.w, h: bounds.h }, holdMs: 0, durationMs: 2000, easing: 'easeInOut' },
    { id: 'k_2', view: close(bounds.x + 200, bounds.y + 200), holdMs: 0, durationMs: 2000 },
    { id: 'k_3', view: close(bounds.x + bounds.w - 1000, bounds.y + bounds.h - 650), holdMs: 0, durationMs: 2500 },
    { id: 'k_4', view: { x: bounds.x, y: bounds.y, w: bounds.w, h: bounds.h }, holdMs: 0, durationMs: 2000 },
  ],
};

function frames(): FrameSource & { step: () => number } {
  let now = 0;
  let queued: (() => void) | undefined;
  return {
    now: () => now,
    request: (callback) => {
      queued = callback;
      return 1;
    },
    cancel: () => {
      queued = undefined;
    },
    /** One 16 ms frame, timed: the state update, the React render and the commit it causes. */
    step: () => {
      now += 16;
      const callback = queued;
      queued = undefined;
      const start = performance.now();
      act(() => {
        callback?.();
      });
      return performance.now() - start;
    },
  };
}

function median(samples: number[]): number {
  const sorted = [...samples].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)] ?? 0;
}

function mount() {
  const clock = frames();
  const view = render(<PresentationStage document={document} flythrough={tour} theme={diagramThemes.overpass} size={size} autoplay={true} onClose={() => undefined} routingBackend={() => undefined} frames={clock} />);
  return { ...view, clock };
}

describe('the flight over 2,000 nodes and 2,500 edges', () => {
  beforeEach(() => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('reports the frame cost of the whole tour, and holds it well inside a gross regression', () => {
    const { clock, container } = mount();
    const samples: number[] = [];
    const mountedAt: number[] = [];
    // Every keyframe has a 2 to 2.5 s arrival: 8.5 s of flight in 16 ms frames.
    for (let frame = 0; frame < Math.ceil(8500 / 16); frame++) {
      samples.push(clock.step());
      if (frame % 40 === 0) {
        mountedAt.push(container.querySelectorAll('g[data-id]').length);
      }
    }
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.floor(sorted.length * 0.95)] ?? 0;
    console.log(`flight frame: median ${median(samples).toFixed(2)} ms, p95 ${p95.toFixed(2)} ms, max ${Math.max(...samples).toFixed(2)} ms over ${String(samples.length)} frames (budget ${FRAME_BUDGET_MS.toFixed(2)} ms, regression threshold ${FLIGHT_FRAME_GROSS_MS.toFixed(2)} ms); elements mounted along the way: ${mountedAt.join(', ')}`);

    // Scaled by how much slower this machine is than the reference right now, so a busy runner does not fail it (loadScale.ts).
    expect(median(samples)).toBeLessThan(FLIGHT_FRAME_GROSS_MS * loadScale());
    // The flight really did pass through close views (few elements mounted) and the overview (all of them).
    expect(Math.min(...mountedAt)).toBeLessThan(300);
    expect(Math.max(...mountedAt)).toBeGreaterThan(1000);
  });

  it('the camera really flies: frames along the tour are at different viewports, finishing on the last keyframe', () => {
    const { clock, container } = mount();
    const transforms = new Set<string>();
    for (let frame = 0; frame < Math.ceil(9000 / 16); frame++) {
      clock.step();
      transforms.add(container.querySelector('g.world')?.getAttribute('transform') ?? '');
    }

    expect(transforms.size).toBeGreaterThan(200);
    const last = viewportForCamera(cameraAtProgress(tour, 3, size), size);
    expect(container.querySelector('g.world')?.getAttribute('transform')).toBe(`translate(${String(last.x)} ${String(last.y)}) scale(${String(last.zoom)})`);
    expect(must(viewRectOf(last, size)).w).toBeCloseTo(bounds.w, 0);
  });

  it('a frame of a hold reconciles nothing: the world transform is the only thing that changes while the camera moves in a mounted set', () => {
    const { clock, container } = mount();
    // Let the tour settle into its first flight, then compare two neighbouring frames of it.
    for (let frame = 0; frame < 10; frame++) {
      clock.step();
    }
    const before = container.querySelector('.nodes')?.innerHTML;
    clock.step();
    const after = container.querySelector('.nodes')?.innerHTML;

    // Zoomed out over the whole grid every element is mounted, so identical markup frame to frame is the tree being kept.
    expect(after).toBe(before);
  });
});
