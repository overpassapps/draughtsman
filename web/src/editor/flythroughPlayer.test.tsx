// @vitest-environment jsdom
/**
 * The standalone viewer against the editor (editor/flythroughPlayer.ts says why there are two). Each test opens the
 * exported HTML in a window of its own and holds the player's camera and state machine to the TypeScript ones over the
 * same tours: a grid of progress values for the camera, and seeded random traces of operations for the state machine,
 * so a change to either copy that the other does not follow fails here with the operation that diverged.
 */
import { JSDOM } from 'jsdom';
import orderService from '../../../docs/examples/order-service.json';
import { cameraAtProgress, cameraForView, pathEnd, viewportForCamera } from '../canvas/camera.ts';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { must } from '../model/sequenceFixture.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Flythrough, Keyframe, KeyframeEasing } from '../model/types.ts';
import { flythroughToHtml } from './exportFlythrough.tsx';
import { PLAYER_SCRIPT } from './flythroughPlayer.ts';
import { advance, initialPlayerState, jumpTo, next, pause, play, previous, seek, toggle, type PlayerOptions, type PlayerState } from './tourPlayer.ts';

interface Api {
  next: () => void;
  previous: () => void;
  play: () => void;
  pause: () => void;
  toggle: () => void;
  seek: (p: number) => void;
  jumpTo: (i: number) => void;
  advance: (dt: number) => void;
  state: () => { p: number; target: number | undefined; playing: boolean; holdLeftMs: number };
  cameraAt: (p: number, w: number, h: number) => { x: number; y: number; w: number };
  setReducedMotion: (value: boolean) => void;
  resize: (w: number, h: number) => void;
}

const example: Document = parseDocument(JSON.parse(JSON.stringify(orderService)));
const view = (x: number, y = 0, w = 400, h = 250) => ({ x, y, w, h });
const key = (id: string, x: number, extra: Partial<Keyframe> = {}): Keyframe => ({ id, view: view(x, (x * 7) % 300), ...extra });

const TOURS: Record<string, Flythrough> = {
  'the shipped example': must(example.flythrough),
  'mixed easings and timings': {
    keyframes: [key('a', 0), key('b', 900, { easing: 'easeIn', durationMs: 700, holdMs: 100 }), key('c', -400, { easing: 'linear', durationMs: 2000 }), key('d', 300, { easing: 'easeOut', holdMs: 0 }), key('e', 1500, { easing: 'cut' })],
  },
  'looping': { keyframes: [key('a', 0, { durationMs: 600, holdMs: 200 }), key('b', 1000, { durationMs: 900, holdMs: 300 }), key('c', 2500, { durationMs: 400, holdMs: 0 })], loop: true },
  'a cut back to the first on a loop': { keyframes: [key('a', 0, { easing: 'cut' }), key('b', 500), key('c', 900)], loop: true },
  'one keyframe': { keyframes: [key('only', 40)] },
  'zero durations and holds, looping': { keyframes: [key('a', 0, { durationMs: 0, holdMs: 0 }), key('b', 50, { durationMs: 0, holdMs: 0 })], loop: true },
};

function open(tour: Flythrough, options: { hash?: string; reduced?: boolean } = {}): { window: Window & typeof globalThis; api: Api } {
  const html = flythroughToHtml(example, tour, diagramThemes.overpass);
  const dom = new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    url: `http://localhost/tour.html${options.hash ?? ''}`,
    beforeParse: (window) => {
      // Time is driven by hand (api.advance), never by a frame the test does not see.
      window.requestAnimationFrame = () => 1;
      window.matchMedia = ((query: string) => ({ matches: options.reduced === true && query.includes('reduced-motion'), media: query, addEventListener: () => undefined, removeEventListener: () => undefined })) as unknown as typeof window.matchMedia;
    },
  });
  return { window: dom.window, api: (dom.window as unknown as { flythrough: Api }).flythrough };
}

/** The TypeScript half of a trace: the same operations on the pure state machine. */
function mirror(tour: Flythrough, options: PlayerOptions) {
  let state: PlayerState = initialPlayerState(tour);
  return {
    get state() {
      return state;
    },
    apply(op: string, arg: number): void {
      switch (op) {
        case 'next': state = next(state, tour, options); break;
        case 'previous': state = previous(state, tour, options); break;
        case 'play': state = play(state, tour); break;
        case 'pause': state = pause(state); break;
        case 'toggle': state = toggle(state, tour); break;
        case 'seek': state = seek(state, tour, arg); break;
        case 'jumpTo': state = jumpTo(state, tour, arg); break;
        default: state = advance(state, arg, tour, options);
      }
    },
  };
}

/** A small seeded generator, so a failing trace can be replayed from its seed. */
function random(seed: number): () => number {
  let s = seed;
  return () => {
    s = (s * 1664525 + 1013904223) % 4294967296;
    return s / 4294967296;
  };
}

const OPS = ['next', 'next', 'previous', 'play', 'pause', 'toggle', 'seek', 'jumpTo', 'advance', 'advance', 'advance', 'advance'] as const;

describe('the standalone viewer’s camera', () => {
  it.each(Object.entries(TOURS))('draws the same camera as canvas/camera.ts over %s, at every size and progress', (_name, tour) => {
    const { api } = open(tour);
    for (const size of [{ width: 1600, height: 900 }, { width: 390, height: 844 }, { width: 1920, height: 1080 }, { width: 640, height: 640 }]) {
      const end = pathEnd(tour);
      for (let p = -0.3; p <= end + 0.3; p += 0.0625) {
        const expected = cameraAtProgress(tour, p, size);
        const actual = api.cameraAt(p, size.width, size.height);
        expect(actual.x).toBeCloseTo(expected.x, 6);
        expect(actual.y).toBeCloseTo(expected.y, 6);
        expect(actual.w).toBeCloseTo(expected.w, 6);
      }
    }
  });

  it('agrees exactly at a keyframe, where the arithmetic has no slack to hide a difference', () => {
    const tour = must(TOURS['mixed easings and timings']);
    const { api } = open(tour);

    tour.keyframes.forEach((keyframe, index) => {
      expect(api.cameraAt(index, 1280, 720)).toEqual(cameraForView(keyframe.view, { width: 1280, height: 720 }));
    });
  });
});

describe('the standalone viewer’s playback', () => {
  it.each(Object.entries(TOURS))('follows the same state machine as tourPlayer.ts over %s, in motion and with reduced motion', (name, tour) => {
    for (const reduced of [false, true]) {
      for (let seed = 1; seed <= 12; seed++) {
        const { api } = open(tour, { reduced });
        const rng = random(seed * 7919 + name.length);
        const twin = mirror(tour, { reducedMotion: reduced });
        const trail: string[] = [];
        for (let step = 0; step < 160; step++) {
          const op = must(OPS[Math.floor(rng() * OPS.length)]);
          const arg = op === 'advance' ? Math.floor(rng() * 1800) : op === 'seek' ? rng() * (pathEnd(tour) + 0.4) : Math.floor(rng() * (tour.keyframes.length + 1));
          trail.push(`${op}(${String(arg)})`);
          twin.apply(op, arg);
          if (op === 'advance') {
            api.advance(arg);
          } else {
            (api as unknown as Record<string, (n: number) => void>)[op]?.(arg);
          }
          const actual = api.state();
          const expected = twin.state;
          const where = `${name}, reduced=${String(reduced)}, seed ${String(seed)}, after ${trail.slice(-6).join(' ')}`;
          expect(actual.playing, where).toBe(expected.playing);
          expect(actual.target === undefined, where).toBe(expected.target === undefined);
          if (expected.target !== undefined && actual.target !== undefined) {
            expect(actual.target, where).toBeCloseTo(expected.target, 9);
          }
          expect(actual.p, where).toBeCloseTo(expected.p, 9);
          expect(actual.holdLeftMs, where).toBeCloseTo(expected.holdLeftMs, 6);
        }
      }
    }
  });

  it('the traces above are not trivial: they move, play, finish and loop', () => {
    const tour = must(TOURS.looping);
    const { api } = open(tour);
    const seen = new Set<number>();
    let sawPlaying = false;
    let sawTarget = false;
    api.play();
    for (let i = 0; i < 400; i++) {
      api.advance(50);
      const state = api.state();
      seen.add(Math.round(state.p) % tour.keyframes.length);
      sawPlaying ||= state.playing;
      sawTarget ||= state.target !== undefined;
    }

    expect([...seen].sort()).toEqual([0, 1, 2]);
    expect(sawPlaying && sawTarget).toBe(true);
  });
});

describe('the exported file', () => {
  const tour = must(TOURS['the shipped example']);

  it('is one self-contained HTML document: the diagram, the tour and the player, no external resource', () => {
    const html = flythroughToHtml(example, tour, diagramThemes.overpass);

    expect(html.startsWith('<!doctype html>')).toBe(true);
    expect(html).toContain('data-id="n_web"');
    expect(html).toContain('Orders API');
    expect(html).toContain('id="flythrough-data"');
    for (const caption of tour.keyframes.flatMap((keyframe) => (keyframe.caption === undefined ? [] : [keyframe.caption]))) {
      expect(html).toContain(caption);
    }
    // Nothing is fetched: no script, stylesheet, image or frame source, and the only URL is the SVG namespace.
    expect(html).not.toMatch(/<script[^>]*\ssrc=/);
    expect(html).not.toMatch(/<link\b/);
    expect(html).not.toMatch(/<(img|iframe)\b/);
    expect(html).not.toMatch(/(src|href)="https?:/);
    expect(html.match(/https?:\/\/[^\s"'<>)]+/g)?.filter((url) => url !== 'http://www.w3.org/2000/svg')).toEqual([]);
    // The exported diagram is the export surface's: no selection handles, no grid.
    expect(html).not.toContain('canvas-grid');
    expect(html).toContain('data-surface="export"');
  });

  it('is a size worth emailing: the player is a few kilobytes and the file is dominated by the diagram', () => {
    const html = flythroughToHtml(example, tour, diagramThemes.overpass);

    expect(PLAYER_SCRIPT.length).toBeLessThan(12_000);
    expect(html.length).toBeLessThan(120_000);
    expect(html.length).toBeGreaterThan(PLAYER_SCRIPT.length);
  });

  it('keeps a caption that tries to close the script, break out of the page or forge a title as plain text', () => {
    const hostile: Flythrough = { keyframes: [{ id: 'k_1', view: view(0), caption: '</script><img src=x onerror=alert(1)>   done' }, key('k_2', 100)] };
    const titled = { ...example, title: 'A "quoted" <b>title</b> & more' };

    const html = flythroughToHtml(titled, hostile, diagramThemes.overpass);
    const dom = new JSDOM(html, { runScripts: 'dangerously', pretendToBeVisual: true, beforeParse: (window) => { window.requestAnimationFrame = () => 1; } });

    // The page still has exactly its two scripts, and no element the caption tried to make.
    expect(dom.window.document.querySelectorAll('script')).toHaveLength(2);
    expect(dom.window.document.querySelector('img')).toBeNull();
    expect(dom.window.document.getElementById('ft-caption')?.textContent).toBe('</script><img src=x onerror=alert(1)>   done');
    expect(dom.window.document.title).toBe('A "quoted" <b>title</b> & more');
    expect(html).not.toContain('<title>A "quoted"');
    expect(html).toContain('&lt;b&gt;title&lt;/b&gt;');
  });

  it('refuses to export a document with no tour, rather than a file that plays nothing', () => {
    expect(() => flythroughToHtml(example, { keyframes: [] }, diagramThemes.overpass)).toThrow(/no fly-through/);
  });
});

describe('the viewer in the page', () => {
  const tour: Flythrough = { keyframes: [key('a', 0, { caption: 'First' }), key('b', 600, { caption: 'Second', easing: 'cut' }), key('c', 1200)] };

  it('shows the first keyframe, paused, with its caption and counter, and the previous control disabled', () => {
    const { window } = open(tour);

    expect(window.document.getElementById('ft-counter')?.textContent).toBe('1 / 3');
    expect(window.document.getElementById('ft-caption')?.textContent).toBe('First');
    expect(window.document.getElementById('ft-caption')?.hasAttribute('hidden')).toBe(false);
    expect(window.document.getElementById('ft-prev')?.getAttribute('aria-disabled')).toBe('true');
    expect(window.document.getElementById('ft-next')?.getAttribute('aria-disabled')).toBe('false');
    expect(window.document.getElementById('ft-play')?.getAttribute('data-state')).toBe('paused');
  });

  it('sets the world’s transform to the camera’s viewport for the window it was sized to', () => {
    const { window, api } = open(tour);
    api.resize(1280, 720);

    const expected = viewportForCamera(cameraAtProgress(tour, 0, { width: 1280, height: 720 }), { width: 1280, height: 720 });
    const transform = window.document.querySelector('g.world')?.getAttribute('transform') ?? '';
    const numbers = transform.match(/-?\d+(\.\d+)?(e-?\d+)?/g)?.map(Number) ?? [];

    expect(numbers[0]).toBeCloseTo(expected.x, 5);
    expect(numbers[1]).toBeCloseTo(expected.y, 5);
    expect(numbers[2]).toBeCloseTo(expected.zoom, 8);
    expect(window.document.querySelector('svg')?.getAttribute('viewBox')).toBe('0 0 1280 720');
  });

  it('steps with the keys and the buttons, and a cut arrival is there at once', () => {
    const { window, api } = open(tour);

    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'ArrowRight' }));
    expect(api.state().p).toBe(1);
    expect(window.document.getElementById('ft-caption')?.textContent).toBe('Second');
    must(window.document.getElementById('ft-next')).click();
    expect(api.state().target).toBe(2);
    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Home' }));
    expect(api.state().p).toBe(0);
    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: '3' }));
    expect(api.state().p).toBe(2);
    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: '9' }));
    expect(api.state().p).toBe(2);
    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'PageUp' }));
    expect(api.state().target).toBe(1);
  });

  it('plays and pauses from P and from the button, and labels the button for what it will do', () => {
    const { window, api } = open(tour);
    const button = must(window.document.getElementById('ft-play'));

    window.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'p' }));
    expect(api.state().playing).toBe(true);
    expect(button.getAttribute('aria-label')).toBe('Pause');
    button.click();
    expect(api.state().playing).toBe(false);
    expect(button.getAttribute('aria-label')).toBe('Play');
  });

  it('leaves the keys the browser or the modifiers own alone', () => {
    const { window, api } = open(tour);

    const withCmd = new window.KeyboardEvent('keydown', { key: 'ArrowRight', metaKey: true, cancelable: true });
    window.dispatchEvent(withCmd);
    const plain = new window.KeyboardEvent('keydown', { key: 'ArrowRight', cancelable: true });
    window.dispatchEvent(plain);

    expect(withCmd.defaultPrevented).toBe(false);
    expect(plain.defaultPrevented).toBe(true);
    expect(api.state().p).toBe(1);
  });

  it('a scrub pauses the tour and puts the camera at that progress', () => {
    const { window, api } = open(tour);
    api.play();
    const range = window.document.getElementById('ft-range') as HTMLInputElement;
    range.value = '1.5';
    range.dispatchEvent(new window.Event('input'));

    expect(api.state().playing).toBe(false);
    expect(api.state().p).toBe(1.5);
  });

  it('starts playing when the address ends #play, and not otherwise', () => {
    expect(open(tour, { hash: '#play' }).api.state().playing).toBe(true);
    expect(open(tour).api.state().playing).toBe(false);
  });

  it('honours reduced motion: a step is a cut', () => {
    const smooth: Flythrough = { keyframes: [key('a', 0), key('b', 900)] };

    expect(open(smooth, { reduced: true }).window.document.body).toBeDefined();
    const still = open(smooth, { reduced: true });
    still.api.next();
    expect(still.api.state().p).toBe(1);
    const moving = open(smooth);
    moving.api.next();
    expect(moving.api.state().p).toBe(0);
    expect(moving.api.state().target).toBe(1);
  });

  it('draws one tick per keyframe under the scrubber, the first current', () => {
    const { window } = open(tour);

    expect(window.document.querySelectorAll('.ft-tick')).toHaveLength(3);
    expect(window.document.querySelectorAll('.ft-tick[data-current]')).toHaveLength(1);
    expect(window.document.querySelector('.ft-tick')?.hasAttribute('data-current')).toBe(true);
  });

  it('a one-keyframe tour disables the scrubber and both steps', () => {
    const { window } = open({ keyframes: [key('only', 0, { caption: 'Just this' })] });

    expect((window.document.getElementById('ft-range') as HTMLInputElement).disabled).toBe(true);
    expect(window.document.getElementById('ft-next')?.getAttribute('aria-disabled')).toBe('true');
    expect(window.document.getElementById('ft-prev')?.getAttribute('aria-disabled')).toBe('true');
  });

  it('easings are the same five names the model has', () => {
    const names: KeyframeEasing[] = ['cut', 'linear', 'easeIn', 'easeOut', 'easeInOut'];
    for (const easing of names) {
      const { api } = open({ keyframes: [key('a', 0), key('b', 2000, { easing })] });
      const expected = cameraAtProgress({ keyframes: [key('a', 0), key('b', 2000, { easing })] }, 0.3, { width: 800, height: 600 });
      const actual = api.cameraAt(0.3, 800, 600);
      expect(actual.x).toBeCloseTo(expected.x, 6);
    }
  });
});
