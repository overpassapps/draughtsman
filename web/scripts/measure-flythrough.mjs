/**
 * Real-browser frame timing of a flythrough (docs/flythrough.md, "Frame rate"). jsdom neither lays out nor paints, so
 * the jsdom benchmark (src/editor/PresentationStage.perf.test.tsx) covers only the React half of a frame; this drives a
 * headed Chrome through Present over a running server, plays the tour, and records the interval between animation frames
 * and every long task while it does, at each CPU throttle rate asked for.
 *
 * It is a measurement aid, not a test: it needs a server with a signed-in account, a document that has a tour, and a
 * visible Chrome window (a hidden tab never runs requestAnimationFrame, which is why a browser-extension tab
 * cannot be used). Configuration is by environment:
 *
 *   BASE_URL=http://127.0.0.1:5180 DOC_ID=doc_... EMAIL=... PASSWORD=... \
 *   PLAYWRIGHT_CORE=/path/to/playwright-core/index.mjs node web/scripts/measure-flythrough.mjs [--throttle 1,2,4] [--seconds 24]
 *
 * Output is one JSON object per throttle rate: the display's own frame interval (measured idle first, since a 120 Hz
 * panel's budget is not 16.7 ms), the median, 95th, 99th and worst frame interval during the flight, the share of
 * frames that missed a 60 Hz deadline (over 20 ms) and a 30 Hz one (over 36 ms), and the long tasks.
 */

const { BASE_URL, DOC_ID, EMAIL, PASSWORD } = process.env;
if (!BASE_URL || !DOC_ID || !EMAIL || !PASSWORD) {
  console.error('Set BASE_URL, DOC_ID, EMAIL and PASSWORD (see the header of this file).');
  process.exit(2);
}
const arg = (name, fallback) => {
  const at = process.argv.indexOf(`--${name}`);
  return at >= 0 ? process.argv[at + 1] : fallback;
};
const throttles = arg('throttle', '1,2,4').split(',').map(Number);
const seconds = Number(arg('seconds', '24'));
const { chromium } = await import(process.env.PLAYWRIGHT_CORE ?? 'playwright-core');

const browser = await chromium.launch({ headless: false, channel: 'chrome' });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
await page.goto(`${BASE_URL}/`);
await page.getByLabel('Email').fill(EMAIL);
await page.getByLabel('Password').fill(PASSWORD);
await page.getByRole('button', { name: 'Sign in' }).click();
await page.waitForLoadState('networkidle');
await page.goto(`${BASE_URL}/d/${DOC_ID}`);
await page.waitForSelector('.viewer');
await page.waitForTimeout(1500);

const session = await context.newCDPSession(page);
const stats = (values) => {
  const sorted = [...values].sort((a, b) => a - b);
  const at = (q) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * q))] ?? 0;
  return { frames: values.length, median: at(0.5), p95: at(0.95), p99: at(0.99), max: sorted.at(-1) ?? 0 };
};
const round = (n) => Math.round(n * 100) / 100;

const results = [];
for (const rate of throttles) {
  await session.send('Emulation.setCPUThrottlingRate', { rate });
  await page.keyboard.press('Meta+Enter');
  await page.waitForSelector('.present');
  await page.waitForTimeout(800);
  // Idle first: the display's own frame interval.
  const idle = await page.evaluate(
    () =>
      new Promise((resolve) => {
        const deltas = [];
        let last = performance.now();
        const tick = (now) => {
          deltas.push(now - last);
          last = now;
          if (deltas.length < 90) requestAnimationFrame(tick);
          else resolve(deltas.slice(5));
        };
        requestAnimationFrame(tick);
      }),
  );
  await page.evaluate(() => {
    window.__flight = { deltas: [], longTasks: [], running: true };
    let last = performance.now();
    const tick = (now) => {
      if (!window.__flight.running) return;
      window.__flight.deltas.push(now - last);
      last = now;
      requestAnimationFrame(tick);
    };
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) window.__flight.longTasks.push(entry.duration);
    }).observe({ type: 'longtask', buffered: false });
    requestAnimationFrame(tick);
  });
  await page.keyboard.press('p');
  await page.waitForTimeout(seconds * 1000);
  const flight = await page.evaluate(() => {
    window.__flight.running = false;
    return window.__flight;
  });
  await page.keyboard.press('p');
  await page.keyboard.press('Escape');
  const interval = stats(idle).median;
  const summary = stats(flight.deltas);
  results.push({
    throttle: rate,
    displayIntervalMs: round(interval),
    flightFrames: summary.frames,
    medianMs: round(summary.median),
    p95Ms: round(summary.p95),
    p99Ms: round(summary.p99),
    worstMs: round(summary.max),
    over20ms: round(flight.deltas.filter((d) => d > 20).length / flight.deltas.length),
    over36ms: round(flight.deltas.filter((d) => d > 36).length / flight.deltas.length),
    longTasks: flight.longTasks.length,
    longestTaskMs: round(Math.max(0, ...flight.longTasks)),
  });
  console.log(JSON.stringify(results.at(-1)));
}
await session.send('Emulation.setCPUThrottlingRate', { rate: 1 });
await browser.close();
