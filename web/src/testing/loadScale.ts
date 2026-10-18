/**
 * How much slower this machine is running right now than the reference, for tests that compare a measured time with a number of
 * milliseconds. A CPU-bound test measures the host as much as the code: on a runner at load average 100 or more (other builds on the same
 * Mac) every thread gets a small share of the CPU, so a median that is 1 ms idle reads 10 ms and an absolute limit fails for no fault of the code.
 *
 * The scale is the median time of a fixed calibration workload measured now, over its median on an idle reference machine
 * (`REFERENCE_MS`); one never below 1, so an idle or faster machine keeps the limit exactly as written. A regression in the code under
 * test does not change the calibration, so a limit multiplied by the scale still fails on the same 10x or 30x slowdown it always did, and
 * the calibration is read just before the measurement, so it sees the load the measurement sees.
 *
 * Use it as `expect(median).toBeLessThan(LIMIT_MS * loadScale())`. Prefer a count of operations (renders, calls) to any time at all.
 */

/** Median of `calibrate()` on an idle Apple M-series laptop under Node 22 (measured as min 1.9, median ~2.0 ms). */
const REFERENCE_MS = 2.0;

const SAMPLES = 31;

/** A fixed amount of allocation, sorting and string work, the same mix as the render and routing paths the perf tests time. */
function calibrate(): number {
  const items: { id: string; x: number; y: number }[] = [];
  for (let i = 0; i < 20_000; i++) {
    items.push({ id: `n_${String(i)}`, x: (i * 7919) % 10_007, y: (i * 104_729) % 10_009 });
  }
  items.sort((a, b) => a.x - b.x || a.y - b.y);
  let total = 0;
  for (const item of items) {
    total += item.id.length + item.x;
  }
  return total;
}

/** The slowdown factor, at least 1. Takes about 60 ms idle, and `SAMPLES` times the loaded time of one calibration otherwise. */
export function loadScale(): number {
  calibrate();
  const samples: number[] = [];
  for (let i = 0; i < SAMPLES; i++) {
    const start = performance.now();
    calibrate();
    samples.push(performance.now() - start);
  }
  samples.sort((a, b) => a - b);
  const median = samples[Math.floor(samples.length / 2)] ?? REFERENCE_MS;
  return Math.max(1, median / REFERENCE_MS);
}
