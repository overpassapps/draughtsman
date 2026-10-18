// @vitest-environment jsdom
import { act, fireEvent, render, screen } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { cameraAtProgress, viewportForCamera } from '../canvas/camera.ts';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { checkoutSequence, must } from '../model/sequenceFixture.ts';
import { parseDocument } from '../model/parse.ts';
import type { Flythrough, Keyframe } from '../model/types.ts';
import { CHROME_IDLE_MS, PresentationStage } from './PresentationStage.tsx';
import type { FrameSource } from './usePresentation.ts';

const SIZE = { width: 1200, height: 700 };
const document = parseDocument(JSON.parse(JSON.stringify(orderService)));

const key = (id: string, x: number, extra: Partial<Keyframe> = {}): Keyframe => ({ id, view: { x, y: 0, w: 400, h: 250 }, durationMs: 1000, holdMs: 500, ...extra });
const tour: Flythrough = { keyframes: [key('a', 0, { caption: 'First' }), key('b', 600, { caption: 'Second' }), key('c', 1200), key('d', 1800, { caption: 'Last' })] };

/** A clock and frame queue the test cranks by hand, so what a frame does is exactly what the test says. */
function frames(): FrameSource & { tick: (ms: number) => void } {
  let now = 0;
  let next = 1;
  const queue = new Map<number, () => void>();
  return {
    now: () => now,
    request: (callback) => {
      const handle = next++;
      queue.set(handle, callback);
      return handle;
    },
    cancel: (handle) => {
      queue.delete(handle);
    },
    tick: (ms) => {
      // Sixteen-millisecond frames, as a 60 Hz screen would run them.
      for (let left = ms; left > 0; left -= 16) {
        now += Math.min(16, left);
        const due = [...queue.entries()];
        queue.clear();
        act(() => {
          for (const [, callback] of due) {
            callback();
          }
        });
      }
    },
  };
}

function mount(options: { tour?: Flythrough; autoplay?: boolean; startIndex?: number; onClose?: () => void } = {}) {
  const clock = frames();
  const onClose = options.onClose ?? vi.fn();
  const view = render(
    <PresentationStage
      document={document}
      flythrough={options.tour ?? tour}
      theme={diagramThemes.overpass}
      size={SIZE}
      autoplay={options.autoplay}
      startIndex={options.startIndex}
      onClose={onClose}
      routingBackend={() => undefined}
      frames={clock}
    />,
  );
  const overlay = must(view.container.querySelector<HTMLElement>('.present'));
  return { ...view, clock, overlay, onClose };
}

const counter = (): string => must(screen.getByText(/^\d+ \/ \d+$/).textContent);
const caption = (): string => must(globalThis.document.querySelector('.present-caption')).textContent;
const transform = (): string => must(globalThis.document.querySelector('g.world')).getAttribute('transform') ?? '';
const press = (overlay: HTMLElement, name: string, init: KeyboardEventInit = {}): boolean => fireEvent.keyDown(overlay, { key: name, ...init });

/** jsdom has no matchMedia; this one answers the reduced-motion query as the test says and nothing else. */
function mockMedia(reduced: boolean): void {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: (query: string) => ({ matches: reduced && query.includes('reduced-motion'), media: query, onchange: null, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false }),
  });
}

beforeEach(() => {
  mockMedia(false);
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
  Reflect.deleteProperty(window, 'matchMedia');
});

describe('the Present overlay', () => {
  it('is a modal dialog named Presentation, focused on open, showing the first keyframe’s caption and counter', () => {
    const { overlay } = mount();

    expect(screen.getByRole('dialog', { name: 'Presentation' })).toBe(overlay);
    expect(overlay.getAttribute('aria-modal')).toBe('true');
    expect(globalThis.document.activeElement).toBe(overlay);
    expect(counter()).toBe('1 / 4');
    expect(caption()).toBe('First');
  });

  it('starts on the keyframe it is asked to, with the camera there', () => {
    const { clock } = mount({ startIndex: 2 });
    clock.tick(0);

    expect(counter()).toBe('3 / 4');
    const expected = viewportForCamera(cameraAtProgress(tour, 2, SIZE), SIZE);
    expect(transform()).toBe(`translate(${String(expected.x)} ${String(expected.y)}) scale(${String(expected.zoom)})`);
  });

  it('gives focus back to what had it, when it closes', () => {
    const opener = globalThis.document.createElement('button');
    globalThis.document.body.append(opener);
    opener.focus();

    const { unmount } = mount();
    expect(globalThis.document.activeElement).not.toBe(opener);
    unmount();

    expect(globalThis.document.activeElement).toBe(opener);
    opener.remove();
  });

  it('draws the diagram with its own ids, so it cannot collide with the canvas behind it', () => {
    const { container } = mount();

    expect(container.querySelector('svg')?.innerHTML).toContain('present-');
    expect(container.querySelector('[data-id="n_web"]')).not.toBeNull();
    expect(container.querySelector('.canvas-grid')).toBeNull();
  });
});

describe('a document whose positions are derived', () => {
  it('plays a sequence diagram: participants drawn from the derived scene, the camera flying over it', () => {
    const sequence = checkoutSequence();
    const clock = frames();
    const sequenceTour: Flythrough = { keyframes: [key('a', 0, { caption: 'All participants' }), key('b', 100, { caption: 'The first lifelines' })] };
    const { container } = render(<PresentationStage document={sequence} flythrough={sequenceTour} theme={diagramThemes.overpass} size={SIZE} onClose={() => undefined} routingBackend={() => undefined} frames={clock} />);

    expect(container.querySelector('g[data-id="p_user"]')).not.toBeNull();
    const before = must(container.querySelector('g.world')).getAttribute('transform');
    fireEvent.keyDown(must(container.querySelector('.present')), { key: 'ArrowRight' });
    clock.tick(1100);

    expect(must(container.querySelector('g.world')).getAttribute('transform')).not.toBe(before);
    expect(caption()).toBe('The first lifelines');
  });
});

describe('the page behind it', () => {
  it('is inert while the overlay is open, and only what the overlay made inert is let back in when it closes', () => {
    const clock = frames();
    const view = render(
      <div>
        <div data-testid="behind" />
        <div data-testid="already" {...{ inert: '' }} />
        <PresentationStage document={document} flythrough={tour} theme={diagramThemes.overpass} size={SIZE} onClose={() => undefined} routingBackend={() => undefined} frames={clock} />
      </div>,
    );
    const behind = screen.getByTestId('behind');
    const already = screen.getByTestId('already');

    expect(behind.hasAttribute('inert')).toBe(true);
    expect(already.hasAttribute('inert')).toBe(true);
    expect(must(view.container.querySelector('.present')).hasAttribute('inert')).toBe(false);
    view.rerender(
      <div>
        <div data-testid="behind" />
        <div data-testid="already" {...{ inert: '' }} />
      </div>,
    );

    expect(screen.getByTestId('behind').hasAttribute('inert')).toBe(false);
    // One that was inert before the overlay came is still inert: the overlay restores what it changed, not everything.
    expect(screen.getByTestId('already').hasAttribute('inert')).toBe(true);
  });
});

describe('the keys', () => {
  it.each([
    ['ArrowRight', 1],
    ['ArrowDown', 1],
    ['PageDown', 1],
    [' ', 1],
    ['Enter', 1],
  ])('%s goes on to the next keyframe', (name, target) => {
    const { overlay, clock } = mount();

    const notPrevented = press(overlay, name);
    clock.tick(1100);

    expect(notPrevented).toBe(false);
    expect(counter()).toBe(`${String(target + 1)} / 4`);
  });

  it.each(['ArrowLeft', 'ArrowUp', 'PageUp', 'Backspace'])('%s goes back', (name) => {
    const { overlay, clock } = mount({ startIndex: 2 });

    press(overlay, name);
    clock.tick(1100);

    expect(counter()).toBe('2 / 4');
  });

  it('Home and End go to the ends, and a number goes to that keyframe, at once', () => {
    const { overlay } = mount({ startIndex: 1 });

    press(overlay, 'End');
    expect(counter()).toBe('4 / 4');
    press(overlay, 'Home');
    expect(counter()).toBe('1 / 4');
    press(overlay, '3');
    expect(counter()).toBe('3 / 4');
    press(overlay, '9');
    expect(counter()).toBe('3 / 4');
    press(overlay, '4');
    expect(counter()).toBe('4 / 4');
  });

  it('P plays and pauses, and the play button says which it will do', () => {
    const { overlay } = mount();

    expect(screen.getByRole('button', { name: 'Play' })).toBeTruthy();
    press(overlay, 'p');
    expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy();
    press(overlay, 'P');
    expect(screen.getByRole('button', { name: 'Play' })).toBeTruthy();
  });

  it('Escape closes the presentation, but in full screen it leaves full screen first and keeps the show', () => {
    const { overlay, onClose } = mount();
    const exit = vi.fn(() => Promise.resolve());
    Object.defineProperty(globalThis.document, 'exitFullscreen', { configurable: true, value: exit });

    try {
      press(overlay, 'Escape');
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(exit).not.toHaveBeenCalled();

      Object.defineProperty(globalThis.document, 'fullscreenElement', { configurable: true, get: () => overlay });
      press(overlay, 'Escape');
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(exit).toHaveBeenCalledTimes(1);
    } finally {
      Reflect.deleteProperty(globalThis.document, 'fullscreenElement');
      Reflect.deleteProperty(globalThis.document, 'exitFullscreen');
    }
  });

  it('leaves a key with a modifier, and a key it does not use, to the browser', () => {
    const { overlay } = mount();

    expect(press(overlay, 'ArrowRight', { metaKey: true })).toBe(true);
    expect(press(overlay, 'r', { ctrlKey: true })).toBe(true);
    expect(press(overlay, 'x')).toBe(true);
    expect(counter()).toBe('1 / 4');
  });

  it('keeps every key from the editor behind it, the ones it uses and the ones it does not', () => {
    const { overlay } = mount();
    const behind = vi.fn();
    window.addEventListener('keydown', behind);

    press(overlay, 'ArrowRight');
    press(overlay, 'k', { metaKey: true });
    press(overlay, 'z', { metaKey: true });

    expect(behind).not.toHaveBeenCalled();
    window.removeEventListener('keydown', behind);
  });

  it('leaves Enter and Space to a focused button, and an arrow to the scrubber', () => {
    const { overlay } = mount();
    const next = screen.getByRole('button', { name: 'Next keyframe' });
    const range = screen.getByRole<HTMLInputElement>('slider', { name: 'Position in the tour' });

    expect(fireEvent.keyDown(next, { key: 'Enter' })).toBe(true);
    expect(fireEvent.keyDown(next, { key: ' ' })).toBe(true);
    expect(fireEvent.keyDown(range, { key: 'ArrowRight' })).toBe(true);
    expect(fireEvent.keyDown(range, { key: 'Home' })).toBe(true);
    // An arrow on a button still steps, which is what a clicker user with a button focused expects.
    expect(fireEvent.keyDown(next, { key: 'ArrowRight' })).toBe(false);
    expect(overlay).toBeTruthy();
  });
});

describe('motion', () => {
  it('flies between keyframes over the keyframe’s duration: the camera is between the two, then on the second', () => {
    const { overlay, clock } = mount();
    const before = transform();

    press(overlay, 'ArrowRight');
    clock.tick(500);
    const between = transform();
    clock.tick(700);
    const after = transform();

    expect(between).not.toBe(before);
    expect(after).not.toBe(between);
    const target = viewportForCamera(cameraAtProgress(tour, 1, SIZE), SIZE);
    expect(after).toBe(`translate(${String(target.x)} ${String(target.y)}) scale(${String(target.zoom)})`);
  });

  it('draws a frame that a hold does not change as no change at all: the viewport stays the same string', () => {
    const { clock } = mount({ autoplay: true });
    const held = transform();

    clock.tick(300);

    expect(transform()).toBe(held);
  });

  it('plays through: holds, flies, and the caption changes at the middle of the flight, not before', () => {
    const { clock } = mount({ autoplay: true });

    clock.tick(450);
    expect(caption()).toBe('First');
    clock.tick(100 + 400);
    expect(caption()).toBe('First');
    clock.tick(200);
    expect(caption()).toBe('Second');
    expect(counter()).toBe('2 / 4');
  });

  it('stops at the last keyframe of a tour that does not loop, and offers Replay', () => {
    const { clock } = mount({ autoplay: true });

    clock.tick(30_000);

    expect(counter()).toBe('4 / 4');
    expect(screen.getByRole('button', { name: 'Replay' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Replay' }));
    expect(counter()).toBe('1 / 4');
    expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy();
  });

  it('keeps going round a looping tour', () => {
    const { clock } = mount({ tour: { ...tour, loop: true }, autoplay: true });

    clock.tick(30_000);

    expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy();
  });

  it('reduced motion makes a step a cut', () => {
    mockMedia(true);
    const { overlay } = mount();

    press(overlay, 'ArrowRight');

    expect(counter()).toBe('2 / 4');
    const target = viewportForCamera(cameraAtProgress(tour, 1, SIZE), SIZE);
    expect(transform()).toBe(`translate(${String(target.x)} ${String(target.y)}) scale(${String(target.zoom)})`);
    expect(overlay.hasAttribute('data-reduced-motion')).toBe(true);
  });

  it('without reduced motion the overlay does not say so', () => {
    expect(mount().overlay.hasAttribute('data-reduced-motion')).toBe(false);
  });
});

describe('the controls', () => {
  it('previous is disabled at the start and next at the end of a tour that does not loop, and a press then does nothing', () => {
    const { overlay } = mount();
    const previous = screen.getByRole('button', { name: 'Previous keyframe' });
    const nextButton = screen.getByRole('button', { name: 'Next keyframe' });

    expect(previous.getAttribute('aria-disabled')).toBe('true');
    expect(nextButton.getAttribute('aria-disabled')).toBe('false');
    fireEvent.click(previous);
    expect(counter()).toBe('1 / 4');
    press(overlay, 'End');
    expect(nextButton.getAttribute('aria-disabled')).toBe('true');
    expect(previous.getAttribute('aria-disabled')).toBe('false');
    fireEvent.click(nextButton);
    expect(counter()).toBe('4 / 4');
  });

  it('on a looping tour neither end is disabled', () => {
    mount({ tour: { ...tour, loop: true } });

    expect(screen.getByRole('button', { name: 'Previous keyframe' }).getAttribute('aria-disabled')).toBe('false');
  });

  it('the scrubber spans the path, reads out the keyframe, pauses the tour and puts the camera where it is dragged', () => {
    const { clock } = mount({ autoplay: true });
    const range = screen.getByRole<HTMLInputElement>('slider', { name: 'Position in the tour' });

    expect(range.max).toBe('3');
    fireEvent.change(range, { target: { value: '2.4' } });
    clock.tick(100);

    expect(screen.getByRole('button', { name: 'Play' })).toBeTruthy();
    expect(Number(range.value)).toBeCloseTo(2.4, 6);
    expect(range.getAttribute('aria-valuetext')).toBe('Keyframe 3 of 4');
    const camera = viewportForCamera(cameraAtProgress(tour, 2.4, SIZE), SIZE);
    expect(transform()).toBe(`translate(${String(camera.x)} ${String(camera.y)}) scale(${String(camera.zoom)})`);
  });

  it('a one-keyframe tour disables the scrubber and both steps', () => {
    mount({ tour: { keyframes: [key('only', 0, { caption: 'Alone' })] } });

    expect((screen.getByRole<HTMLInputElement>('slider', { name: 'Position in the tour' })).disabled).toBe(true);
    expect(screen.getByRole('button', { name: 'Previous keyframe' }).getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByRole('button', { name: 'Next keyframe' }).getAttribute('aria-disabled')).toBe('true');
    expect(counter()).toBe('1 / 1');
  });

  it('draws a tick for every keyframe and marks the current one', () => {
    const { overlay } = mount();

    expect(globalThis.document.querySelectorAll('.present-tick')).toHaveLength(4);
    press(overlay, '3');
    const current = [...globalThis.document.querySelectorAll('.present-tick')].map((tick) => tick.hasAttribute('data-current'));
    expect(current).toEqual([false, false, true, false]);
  });

  it('the close button leaves', () => {
    const { onClose } = mount();

    fireEvent.click(screen.getByRole('button', { name: 'Exit presentation' }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows no caption box when the keyframe has none, but keeps the live region for the next one', () => {
    const { overlay } = mount();

    press(overlay, '3');
    const box = must(globalThis.document.querySelector('.present-caption'));

    expect(box.textContent).toBe('');
    expect(box.hasAttribute('data-empty')).toBe(true);
    expect(box.getAttribute('aria-live')).toBe('polite');
    press(overlay, '4');
    expect(box.hasAttribute('data-empty')).toBe(false);
  });
});

describe('full screen', () => {
  it('has no button where the browser cannot', () => {
    mount();

    expect(screen.queryByRole('button', { name: 'Full screen' })).toBeNull();
  });

  it('asks the overlay itself for full screen from the button and from F', () => {
    const request = vi.fn(() => Promise.resolve());
    Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', { configurable: true, value: request });
    try {
      const { overlay } = mount();

      fireEvent.click(screen.getByRole('button', { name: 'Full screen' }));
      press(overlay, 'f');

      expect(request).toHaveBeenCalledTimes(2);
    } finally {
      Reflect.deleteProperty(HTMLElement.prototype, 'requestFullscreen');
    }
  });
});

describe('the controls fading', () => {
  it('fade after a playing tour has been left alone, and come back on a pointer move or a key', () => {
    vi.useFakeTimers();
    const { overlay } = mount({ autoplay: true });

    expect(overlay.getAttribute('data-chrome')).toBe('shown');
    act(() => {
      vi.advanceTimersByTime(CHROME_IDLE_MS - 1);
    });
    expect(overlay.getAttribute('data-chrome')).toBe('shown');
    act(() => {
      vi.advanceTimersByTime(2);
    });
    expect(overlay.getAttribute('data-chrome')).toBe('hidden');
    fireEvent.pointerMove(overlay);
    expect(overlay.getAttribute('data-chrome')).toBe('shown');
    act(() => {
      vi.advanceTimersByTime(CHROME_IDLE_MS + 1);
    });
    expect(overlay.getAttribute('data-chrome')).toBe('hidden');
    press(overlay, 'x');
    expect(overlay.getAttribute('data-chrome')).toBe('shown');
  });

  it('never fade while a windowed tour is paused', () => {
    vi.useFakeTimers();
    const { overlay } = mount();

    act(() => {
      vi.advanceTimersByTime(CHROME_IDLE_MS * 4);
    });

    expect(overlay.getAttribute('data-chrome')).toBe('shown');
  });

  it('pausing brings them back for good', () => {
    vi.useFakeTimers();
    const { overlay } = mount({ autoplay: true });
    act(() => {
      vi.advanceTimersByTime(CHROME_IDLE_MS + 1);
    });
    expect(overlay.getAttribute('data-chrome')).toBe('hidden');

    press(overlay, 'p');
    act(() => {
      vi.advanceTimersByTime(CHROME_IDLE_MS * 3);
    });

    expect(overlay.getAttribute('data-chrome')).toBe('shown');
  });
});
