// @vitest-environment jsdom
/**
 * The fly-through through the whole editor (design/atlas/flythrough.md): the Tour tab of the right-hand panel, capturing
 * the canvas's view as a keyframe, saving the tour with the document, and Present opened from the panel, the palette
 * and its shortcut. The canvas, the hook, the registry and the overlay are tested as they are wired together.
 */
import './loadTolerance.ts';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { viewRectOf } from '../canvas/camera.ts';
import { LIVE_SYNC_INTERVAL_MS } from '../editor/useDocumentEditor.ts';
import { must } from '../model/sequenceFixture.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Flythrough } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const withTour = parseDocument(JSON.parse(JSON.stringify(orderService)));
const withoutTour: Document = (() => {
  const copy = structuredClone(withTour);
  Reflect.deleteProperty(copy, 'flythrough');
  return copy;
})();

const saves: string[] = [];

function mount(document: Document, editable = true) {
  const view = render(<DocumentViewer {...(editable ? { id: document.id } : {})} document={document} etag={'"dv1:loaded"'} />);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  act(() => {
    vi.runAllTimers();
  });
  return { ...view, canvas };
}

/** The viewport the canvas is drawing now, read from the world group's transform as the tests of the canvas do. */
function canvasViewport(container: HTMLElement): { zoom: number; x: number; y: number } {
  const match = /translate\(([-\d.e]+)[ ,]+([-\d.e]+)\) scale\(([-\d.e]+)\)/.exec(container.querySelector('.diagram-canvas g.world')?.getAttribute('transform') ?? '');
  return { x: Number(match?.[1]), y: Number(match?.[2]), zoom: Number(match?.[3]) };
}

const openTour = (): void => {
  fireEvent.click(screen.getByRole('button', { name: 'Tour' }));
};
const savedTour = async (): Promise<Flythrough | undefined> => {
  fireEvent.click(screen.getByRole('button', { name: 'Save' }));
  await act(() => Promise.resolve());
  return (JSON.parse(saves.at(-1) ?? 'null') as Document).flythrough;
};
/** Runs the palette command whose name starts with `label`: by name, since a search also matches other commands' keywords. */
const runCommand = (label: string): void => {
  fireEvent.keyDown(window, { key: 'k', metaKey: true });
  const search = screen.getByRole('combobox', { name: 'Search commands' });
  fireEvent.change(search, { target: { value: label } });
  const option = within(screen.getByRole('listbox', { name: 'Commands' }))
    .getAllByRole('option')
    .find((candidate) => (candidate.getAttribute('aria-label') ?? '').toLowerCase().startsWith(label.toLowerCase()));
  fireEvent.click(must(option));
};

describe('DocumentViewer: the fly-through', () => {
  beforeEach(() => {
    saves.length = 0;
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((_url, init) => {
      saves.push(init?.body as string);
      return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } }));
    }));
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  describe('the Tour tab', () => {
    it('is a third view of the panel, beside Properties and Text, and shows the tour the document carries', () => {
      mount(withTour);

      openTour();

      expect(screen.getByRole('button', { name: 'Tour' }).getAttribute('aria-pressed')).toBe('true');
      expect(screen.getByRole('button', { name: 'Properties' }).getAttribute('aria-pressed')).toBe('false');
      const list = screen.getByRole('list', { name: 'Keyframes' });
      expect(within(list).getAllByRole('listitem')).toHaveLength(4);
      expect(within(list).getAllByRole('listitem')[2]?.textContent).toContain('Everything else sits inside the VPC');
    });

    it('is not there in a read-only preview, which has no panel to put it in', () => {
      mount(withTour, false);

      expect(screen.queryByRole('button', { name: 'Tour' })).toBeNull();
    });

    it('switches with the other views, and the others do not show while it does', () => {
      mount(withTour);

      openTour();
      expect(screen.queryByRole('button', { name: 'Group' })?.closest('[hidden]')).not.toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Text' }));
      expect(screen.getByRole('button', { name: 'Tour' }).getAttribute('aria-pressed')).toBe('false');
      expect(screen.getByRole('button', { name: 'Text' }).getAttribute('aria-pressed')).toBe('true');
      fireEvent.click(screen.getByRole('button', { name: 'Properties' }));
      expect(screen.queryByRole('button', { name: 'Group' })?.closest('[hidden]')).toBeNull();
    });
  });

  describe('generating', () => {
    it('Generate from containers builds an overview, the VPC and the rest from the example, in one undo step', () => {
      mount(withoutTour);
      openTour();
      const generate = screen.getByRole('button', { name: 'Generate from containers' });
      expect(generate.hasAttribute('disabled')).toBe(false);

      fireEvent.click(generate);

      expect([...globalThis.document.querySelectorAll('.tour-row-caption')].map((caption) => caption.textContent)).toEqual(['Overview', 'VPC', 'Everything else']);
      fireEvent.keyDown(window, { key: 'z', metaKey: true });
      expect(screen.getByText('No keyframes yet.')).toBeTruthy();
    });

    it('is disabled for a diagram with no container, where there is no structure to walk', () => {
      const flat = structuredClone(withoutTour);
      flat.nodes = flat.nodes.filter((node) => node.kind !== 'container');
      for (const node of flat.nodes) {
        Reflect.deleteProperty(node, 'parent');
      }
      mount(flat);
      openTour();

      expect(screen.getByRole('button', { name: 'Generate from containers' }).hasAttribute('disabled')).toBe(true);
    });
  });

  describe('capturing', () => {
    it('Add keyframe captures exactly the view the canvas is showing', () => {
      const { container } = mount(withoutTour);
      openTour();

      fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));

      const expected = must(viewRectOf(canvasViewport(container), { width: 1200, height: 800 }));
      const row = must(screen.getAllByRole('listitem')[0]);
      expect(row.textContent).toContain('Keyframe 1');
      fireEvent.click(must(row.querySelector('.tour-row-main')));
      // Saved as the very rectangle the canvas showed, so presenting it frames what the author framed.
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      const view = must(must((JSON.parse(saves.at(-1) ?? 'null') as Document).flythrough).keyframes[0]).view;
      expect(view.x).toBeCloseTo(expected.x, 6);
      expect(view.y).toBeCloseTo(expected.y, 6);
      expect(view.w).toBeCloseTo(expected.w, 6);
      expect(view.h).toBeCloseTo(expected.h, 6);
    });

    it('a capture after the canvas has been zoomed is a different rectangle, which the first one is not', () => {
      const { container } = mount(withoutTour);
      openTour();
      fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));
      fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
      act(() => {
        vi.runAllTimers();
      });
      fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      const [first, second] = must(must((JSON.parse(saves.at(-1) ?? 'null') as Document).flythrough).keyframes.slice(0, 2).length === 2 ? (JSON.parse(saves.at(-1) ?? 'null') as Document).flythrough : undefined).keyframes;
      expect(must(second).view.w).toBeLessThan(must(first).view.w);
      expect(must(second).view.w).toBeCloseTo(must(viewRectOf(canvasViewport(container), { width: 1200, height: 800 })).w, 6);
    });

    it('the palette adds a keyframe too, opens the Tour panel and says so', () => {
      mount(withoutTour);

      runCommand('add fly-through keyframe');

      expect(screen.getByRole('button', { name: 'Tour' }).getAttribute('aria-pressed')).toBe('true');
      expect(screen.getAllByRole('listitem')).toHaveLength(1);
      expect(screen.getByText('Added a fly-through keyframe from the current view.')).toBeTruthy();
    });

    it('selecting a row moves the canvas camera to frame that keyframe', () => {
      const { container } = mount(withTour);
      openTour();
      const before = canvasViewport(container);

      fireEvent.click(must(must(screen.getAllByRole('listitem')[1]).querySelector('.tour-row-main')));
      act(() => {
        vi.runAllTimers();
      });

      const after = canvasViewport(container);
      expect(after).not.toEqual(before);
      // k_web frames 0,0 400x250 in a 1200x800 canvas: the contain-fit is 3x wide and 3.2x tall, so zoom 3.
      expect(after.zoom).toBeCloseTo(3, 6);
      const shown = must(viewRectOf(after, { width: 1200, height: 800 }));
      expect(shown.x).toBeLessThanOrEqual(0 + 1e-6);
      expect(shown.x + shown.w).toBeGreaterThanOrEqual(400 - 1e-6);
    });
  });

  describe('saving', () => {
    it('saves the tour with the document, and a tour edit marks the document unsaved', async () => {
      mount(withoutTour);
      expect(screen.getByText('Saved')).toBeTruthy();
      openTour();

      fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));
      expect(screen.getByText('Unsaved changes')).toBeTruthy();

      const tour = await savedTour();
      expect(tour?.keyframes).toHaveLength(1);
      expect(tour?.keyframes[0]?.id).toBe('k_1');
    });

    it('saves a document with no tour with no flythrough member at all', async () => {
      mount(withoutTour);
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());

      expect(JSON.parse(saves.at(-1) ?? 'null')).not.toHaveProperty('flythrough');
    });

    it('carries the tour it was given through an unrelated edit and back out unchanged', async () => {
      mount(withTour);
      runCommand('toggle grid');
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());

      expect((JSON.parse(saves.at(-1) ?? 'null') as Document).flythrough).toEqual(withTour.flythrough);
    });

    it('undoes a keyframe added in the panel with one Cmd/Ctrl+Z, and puts it back with one redo', () => {
      mount(withoutTour);
      openTour();
      fireEvent.click(screen.getByRole('button', { name: 'Add keyframe' }));
      expect(screen.getAllByRole('listitem')).toHaveLength(1);

      fireEvent.keyDown(window, { key: 'z', metaKey: true });
      expect(screen.queryAllByRole('listitem')).toHaveLength(0);
      expect(screen.getByText('No keyframes yet.')).toBeTruthy();
      fireEvent.keyDown(window, { key: 'z', metaKey: true, shiftKey: true });
      expect(screen.getAllByRole('listitem')).toHaveLength(1);
    });
  });

  describe('Present', () => {
    it('opens from the panel button over the editor, on the selected keyframe, and makes the editor behind it inert', () => {
      const { container } = mount(withTour);
      openTour();
      fireEvent.click(must(must(screen.getAllByRole('listitem')[2]).querySelector('.tour-row-main')));

      fireEvent.click(screen.getByRole('button', { name: 'Present' }));

      expect(screen.getByRole('dialog', { name: 'Presentation' })).toBeTruthy();
      expect(screen.getByText('3 / 4')).toBeTruthy();
      expect(container.querySelector('.viewer-bar')?.hasAttribute('inert')).toBe(true);
      expect(container.querySelector('.viewer-body')?.hasAttribute('inert')).toBe(true);
    });

    it('opens from the palette and from Cmd/Ctrl+Enter, and closes with Escape, giving the canvas its focus back', () => {
      const { canvas } = mount(withTour);

      runCommand('present fly-through');
      expect(screen.getByRole('dialog', { name: 'Presentation' })).toBeTruthy();
      fireEvent.keyDown(screen.getByRole('dialog', { name: 'Presentation' }), { key: 'Escape' });
      expect(screen.queryByRole('dialog', { name: 'Presentation' })).toBeNull();
      expect(globalThis.document.activeElement).toBe(canvas);

      fireEvent.keyDown(window, { key: 'Enter', metaKey: true });
      expect(screen.getByRole('dialog', { name: 'Presentation' })).toBeTruthy();
      fireEvent.click(screen.getByRole('button', { name: 'Exit presentation' }));
      expect(screen.queryByRole('dialog', { name: 'Presentation' })).toBeNull();
      expect(canvasHasNoInert()).toBe(true);
    });

    it('gives focus back to the Present button that opened it, and to the canvas when nothing else had it', () => {
      const { canvas } = mount(withTour);
      openTour();
      const button = screen.getByRole('button', { name: 'Present' });
      button.focus();

      fireEvent.click(button);
      fireEvent.keyDown(screen.getByRole('dialog', { name: 'Presentation' }), { key: 'Escape' });
      expect(globalThis.document.activeElement).toBe(button);

      // Opened while the page itself had focus: the canvas takes it on close.
      (globalThis.document.activeElement as HTMLElement).blur();
      fireEvent.keyDown(window, { key: 'Enter', metaKey: true });
      fireEvent.keyDown(screen.getByRole('dialog', { name: 'Presentation' }), { key: 'Escape' });
      expect(globalThis.document.activeElement).toBe(canvas);
    });

    it('does nothing for a document with no tour: Present is disabled in the palette and its shortcut is not claimed', () => {
      mount(withoutTour);

      fireEvent.keyDown(window, { key: 'Enter', metaKey: true });
      expect(screen.queryByRole('dialog', { name: 'Presentation' })).toBeNull();
      fireEvent.keyDown(window, { key: 'k', metaKey: true });
      const present = within(screen.getByRole('listbox', { name: 'Commands' })).getByRole('option', { name: /Present fly-through/ });
      expect(present.getAttribute('aria-disabled')).toBe('true');
    });

    it('works in a read-only preview of a document that has a tour, and is not offered for one that has none', () => {
      const first = mount(withTour, false);
      runCommand('present fly-through');
      expect(screen.getByRole('dialog', { name: 'Presentation' })).toBeTruthy();
      first.unmount();

      mount(withoutTour, false);
      fireEvent.keyDown(window, { key: 'k', metaKey: true });
      expect(screen.queryByRole('option', { name: /Present fly-through/ })).toBeNull();
    });

    it('keeps the editor’s own shortcuts from acting while it is open', () => {
      mount(withTour);
      runCommand('present fly-through');

      fireEvent.keyDown(screen.getByRole('dialog', { name: 'Presentation' }), { key: 'k', metaKey: true });
      fireEvent.keyDown(screen.getByRole('dialog', { name: 'Presentation' }), { key: 'z', metaKey: true });

      expect(screen.queryByRole('dialog', { name: 'Command palette' })).toBeNull();
    });

    it('plays the document as it was when it opened: a change made elsewhere meanwhile does not move the show', async () => {
      const remote = structuredClone(withTour);
      remote.nodes = remote.nodes.map((node) => (node.id === 'n_web' ? { ...node, label: 'Storefront' } : node));
      remote.meta.updatedAt = '2026-09-30T12:00:00.000Z';
      vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame', 'setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(remote), { status: 200, headers: { ETag: '"dv1:remote"', 'Content-Type': 'application/json' } }))));
      const view = render(<DocumentViewer id={withTour.id} document={withTour} etag={'"dv1:loaded"'} liveSyncIntervalMs={LIVE_SYNC_INTERVAL_MS} />);
      act(() => {
        vi.runOnlyPendingTimers();
      });
      runCommand('present fly-through');
      const overlay = screen.getByRole('dialog', { name: 'Presentation' });
      expect(within(overlay).getByText('Web app')).toBeTruthy();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(LIVE_SYNC_INTERVAL_MS * 3);
      });

      // The show kept the label it opened with...
      expect(within(overlay).queryByText('Storefront')).toBeNull();
      expect(within(overlay).getByText('Web app')).toBeTruthy();
      // ...while the editor behind it did take the other writer's, so the assertion above is about the snapshot.
      fireEvent.keyDown(overlay, { key: 'Escape' });
      expect(view.container.querySelector('.diagram-canvas g[data-id="n_web"]')?.textContent).toContain('Storefront');
    });
  });

  describe('export', () => {
    it('is offered for a document with a tour, and not for one without', () => {
      mount(withoutTour);
      fireEvent.keyDown(window, { key: 'k', metaKey: true });
      expect(screen.queryByRole('option', { name: 'Export fly-through (HTML)' })).toBeNull();
      fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
    });

    it('downloads one HTML file named for the diagram, holding its tour', async () => {
      const blobs: Blob[] = [];
      const names: string[] = [];
      vi.stubGlobal('URL', { ...URL, createObjectURL: (blob: Blob) => (blobs.push(blob), 'blob:x'), revokeObjectURL: () => undefined });
      vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
        names.push(this.download);
      });
      mount(withTour);

      runCommand('export fly-through');
      await act(async () => {
        await vi.advanceTimersByTimeAsync(10);
      });

      expect(names).toEqual(['order-service-fly-through.html']);
      const html = await must(blobs[0]).text();
      expect(html.startsWith('<!doctype html>')).toBe(true);
      expect(html).toContain('Everything else sits inside the VPC');
      expect(must(blobs[0]).type).toContain('text/html');
    });
  });
});

function canvasHasNoInert(): boolean {
  return globalThis.document.querySelector('[inert]') === null;
}
