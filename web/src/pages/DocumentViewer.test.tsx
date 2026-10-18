// @vitest-environment jsdom
import './loadTolerance.ts';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { LIVE_SYNC_INTERVAL_MS } from '../editor/useDocumentEditor.ts';
import { parseDocument } from '../model/parse.ts';
import { imageDimensions, preloadAssetDataUrls, uploadAsset } from '../model/assets.ts';
import type { Document } from '../model/types.ts';
import { loadScale } from '../testing/loadScale.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

// The upload/dimension network and image-decode seams are stubbed so a drop or paste can be driven in jsdom, where
// neither fetch to /api/assets nor an <img> decode runs for real. Everything else in the module stays actual.
vi.mock('../model/assets.ts', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../model/assets.ts')>();
  return { ...actual, uploadAsset: vi.fn(), imageDimensions: vi.fn(), preloadAssetDataUrls: vi.fn() };
});

const document = parseDocument(orderService);

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

describe('DocumentViewer', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    // jsdom has no 2D canvas; the viewer falls back to the heuristic measurer, without the "not implemented" noise.
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('keeps the keyboard shortcuts live after a zoom-cluster button is pressed with the mouse', () => {
    const { container } = render(<DocumentViewer document={document} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    const zoomIn = screen.getByRole('button', { name: 'Zoom in' });

    // The press must not move focus to the button (mousedown default cancelled) and must hand it to the canvas.
    const mouseDownNotCancelled = fireEvent.mouseDown(zoomIn);
    expect(mouseDownNotCancelled).toBe(false);
    expect(globalThis.document.activeElement).toBe(canvas);
    fireEvent.click(zoomIn);
    flushFrame();
    expect(screen.getByText('125%')).toBeTruthy();

    // Ctrl+= straight after the click zooms the canvas instead of falling through to the browser.
    const keyNotCancelled = fireEvent.keyDown(globalThis.document.activeElement ?? canvas, { key: '=', metaKey: true });
    expect(keyNotCancelled).toBe(false);
    flushFrame();
    expect(screen.getByText('156%')).toBeTruthy();
  });

  it('announces the discrete zoom commands and stays quiet through a wheel gesture', () => {
    const { container } = render(<DocumentViewer document={document} />);
    const canvas = container.querySelector<HTMLElement>('.diagram-canvas');
    const readout = container.querySelector('.viewer-zoom');
    if (!canvas || !readout) {
      throw new Error('viewer not rendered');
    }
    const status = within(screen.getByRole('group', { name: 'Zoom' })).getByRole('status');

    fireEvent.click(screen.getByRole('button', { name: 'Zoom in' }));
    flushFrame();
    expect(readout.textContent).toBe('125%');
    expect(status.textContent).toBe('Zoom 125%');

    // A pinch or wheel zoom keeps the readout live but must not narrate a percentage per frame.
    fireEvent.wheel(canvas, { deltaY: -100, ctrlKey: true, clientX: 0, clientY: 0 });
    flushFrame();
    expect(readout.textContent).not.toBe('125%');
    expect(status.textContent).toBe('Zoom 125%');
  });

  describe('editing', () => {
    // jsdom lays nothing out; give the canvas a real box so the SVG renders and presses land on nodes.
    beforeEach(() => {
      vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
      vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      // Export reads every image asset; the stubbed resolver returns none so an unrelated export action is quiet.
      vi.mocked(preloadAssetDataUrls).mockReset().mockResolvedValue(new Map());
      vi.mocked(uploadAsset).mockReset();
      vi.mocked(imageDimensions).mockReset();
    });
    afterEach(() => {
      vi.unstubAllGlobals();
    });

    function renderEditor(preview = false, shown: Document = document) {
      const view = render(<DocumentViewer  {...(preview ? {} : { id: document.id })} document={shown} etag={'"dv1:loaded"'} />);
      const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
      const nodeId = document.nodes[0]?.id ?? '';
      const shape = view.container.querySelector(`g[data-id="${nodeId}"] .shape`);
      if (!canvas || !shape) {
        throw new Error('editor not rendered');
      }
      canvas.setPointerCapture = () => undefined;
      canvas.releasePointerCapture = () => undefined;
      canvas.hasPointerCapture = () => false;
      const world = () => view.container.querySelector('g.world')?.getAttribute('transform');
      const nodeTransform = () => view.container.querySelector(`g[data-id="${nodeId}"]`)?.outerHTML;
      return { ...view, canvas, shape, world, nodeTransform };
    }

    function drag(canvas: HTMLElement, shape: Element, steps: number): void {
      fireEvent.pointerDown(shape, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
      for (let i = 1; i <= steps; i++) {
        fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 100 + i * 10, clientY: 100 + i * 5 });
        flushFrame();
      }
      fireEvent.pointerUp(canvas, { pointerId: 1 });
      flushFrame();
    }

    it('keeps previews read-only when a node is dragged', () => {
      const { container, canvas, shape, nodeTransform } = renderEditor(true);
      const before = nodeTransform();
      drag(canvas, shape, 3);
      expect(nodeTransform()).toBe(before);
      expect(container.querySelector('.selection-node')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
    });

    it('disables Save until the request finishes', async () => {
      let finish: (response: Response) => void = () => undefined;
      vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => { finish = resolve; })));
      renderEditor();
      const save = screen.getByRole<HTMLButtonElement>('button', { name: 'Save' });
      fireEvent.click(save);
      expect(save.disabled).toBe(true);
      await act(() => { finish(new Response('{}', { status: 200 })); return Promise.resolve(); });
      expect(save.disabled).toBe(false);
    });

    describe('renaming the title (design/atlas/editor-shell.md, "document title (click to rename)")', () => {
      beforeEach(() => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200 }))));
      });

      it('turns into an input with the text selected on click, and Enter saves it through the rename PATCH', async () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: document.title }));
        const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Diagram title' });
        expect(input.value).toBe(document.title);
        expect(input.selectionStart).toBe(0);
        expect(input.selectionEnd).toBe(document.title.length);

        vi.mocked(fetch).mockResolvedValueOnce(new Response('{}', { status: 200, headers: { ETag: '"dv1:renamed"' } }));
        fireEvent.change(input, { target: { value: 'Renamed diagram' } });
        fireEvent.keyDown(input, { key: 'Enter' });

        await waitFor(() => {
          expect(vi.mocked(fetch)).toHaveBeenCalledWith(`/api/documents/${document.id}`, expect.objectContaining({ method: 'PATCH' }));
        });
        expect(screen.getByRole('button', { name: 'Renamed diagram' })).toBeTruthy();
        expect(screen.queryByRole('textbox', { name: 'Diagram title' })).toBeNull();
      });

      it('restores the old title on Escape, saving nothing', () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: document.title }));
        const input = screen.getByRole('textbox', { name: 'Diagram title' });
        fireEvent.change(input, { target: { value: 'Abandoned edit' } });
        fireEvent.keyDown(input, { key: 'Escape' });

        expect(screen.getByRole('button', { name: document.title })).toBeTruthy();
        expect(vi.mocked(fetch)).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'PATCH' }));
      });

      it('does not save an empty or whitespace-only title', () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: document.title }));
        const input = screen.getByRole('textbox', { name: 'Diagram title' });
        fireEvent.change(input, { target: { value: '   ' } });
        fireEvent.keyDown(input, { key: 'Enter' });

        expect(screen.getByRole('button', { name: document.title })).toBeTruthy();
        expect(vi.mocked(fetch)).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'PATCH' }));
      });

      it('commits once when Enter is followed by blur before the PATCH resolves, with no false conflict error', async () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: document.title }));
        const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Diagram title' });
        const renamePatches = () => vi.mocked(fetch).mock.calls.filter(([, init]) => init?.method === 'PATCH');

        let resolveFetch: (response: Response) => void = () => undefined;
        vi.mocked(fetch).mockImplementationOnce(() => new Promise<Response>((resolve) => { resolveFetch = resolve; }));
        fireEvent.change(input, { target: { value: 'Renamed diagram' } });
        fireEvent.keyDown(input, { key: 'Enter' });
        // Enter's commit is still in flight (no response yet): the blur that follows it in a real gesture must not
        // fire a second PATCH against the same edit.
        fireEvent.blur(input);
        expect(renamePatches()).toHaveLength(1);

        await act(() => {
          resolveFetch(new Response('{}', { status: 200, headers: { ETag: '"dv1:renamed"' } }));
          return Promise.resolve();
        });

        expect(renamePatches()).toHaveLength(1);
        expect(screen.getByRole('button', { name: 'Renamed diagram' })).toBeTruthy();
        expect(screen.queryByRole('alert')).toBeNull();
      });

      it('shows an inline error and keeps the old title when the rename is refused', async () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: document.title }));
        const input = screen.getByRole('textbox', { name: 'Diagram title' });
        vi.mocked(fetch).mockResolvedValueOnce(new Response('{"title":"Document has changed"}', { status: 412 }));
        fireEvent.change(input, { target: { value: 'Renamed diagram' } });
        fireEvent.keyDown(input, { key: 'Enter' });

        await waitFor(() => {
          expect(screen.getByRole('alert').textContent).toContain('Nothing was saved');
        });
        expect(screen.getByRole('button', { name: document.title })).toBeTruthy();
      });

      it('is offered as "Rename diagram" in the Document group, alongside Save', () => {
        renderEditor();
        fireEvent.keyDown(window, { key: 'k', metaKey: true });
        expect(screen.getByRole('option', { name: 'Rename diagram' })).toBeTruthy();
      });

      it('opens with the title input already active and selected for a diagram just created (New diagram)', () => {
        render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} initialTitleEditActive />);
        const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'Diagram title' });
        expect(input.value).toBe(document.title);
        expect(input.selectionStart).toBe(0);
        expect(input.selectionEnd).toBe(document.title.length);
      });
    });

    it('moves a dragged node without re-fitting the view, and one Cmd+Z undoes the whole drag', () => {
      const { canvas, shape, world, nodeTransform } = renderEditor();
      const viewBefore = world();
      const nodeBefore = nodeTransform();

      drag(canvas, shape, 5);
      expect(nodeTransform()).not.toBe(nodeBefore);
      expect(world()).toBe(viewBefore);

      fireEvent.keyDown(window, { key: 'z', metaKey: true });
      expect(nodeTransform()).toBe(nodeBefore);
    });

    function click(canvas: HTMLElement, target: Element): void {
      fireEvent.pointerDown(target, { pointerId: 7, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
      fireEvent.pointerUp(canvas, { pointerId: 7 });
    }
    const undo = () => fireEvent.keyDown(window, { key: 'z', metaKey: true });
    const redo = () => fireEvent.keyDown(window, { key: 'z', metaKey: true, shiftKey: true });
    const placement = (container: HTMLElement, id: string) => container.querySelector(`g[data-id="${id}"] > g`)?.getAttribute('transform');
    const drawnIds = (container: HTMLElement) => [...container.querySelectorAll('g[data-id]')].map((g) => g.getAttribute('data-id'));

    it('nudges the model on every arrow press and undoes one press per Cmd+Z', () => {
      const { container, canvas, shape } = renderEditor();
      click(canvas, shape);
      // n_web starts at (40, 40) in the example.
      expect(placement(container, 'n_web')).toBe('translate(40 40)');
      for (const expected of ['translate(41 40)', 'translate(42 40)', 'translate(43 40)']) {
        fireEvent.keyDown(canvas, { key: 'ArrowRight' });
        expect(placement(container, 'n_web')).toBe(expected);
      }
      fireEvent.keyDown(canvas, { key: 'ArrowDown', shiftKey: true });
      expect(placement(container, 'n_web')).toBe('translate(43 50)');
      for (const expected of ['translate(43 40)', 'translate(42 40)', 'translate(41 40)', 'translate(40 40)']) {
        undo();
        expect(placement(container, 'n_web')).toBe(expected);
      }
      // Four presses, four steps: a fifth undo has nothing left.
      undo();
      expect(placement(container, 'n_web')).toBe('translate(40 40)');
    });

    it('snaps a drag to the grid as exactly one undo step, and the grid toggle turns the grid and its snap off and on', () => {
      const { container, canvas, shape } = renderEditor();
      const dragBy = (dx: number, dy: number): void => {
        // Found afresh each time: a drag lifts the node into the drag layer, so the element mounted before it is gone.
        const target = container.querySelector('g[data-id="n_web"] .shape');
        if (!target) {
          throw new Error('n_web not rendered');
        }
        fireEvent.pointerDown(target, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
        // Several frames on the way, so a snap that recorded a step per frame would show up as extra undo steps.
        for (const f of [0.4, 0.8, 1]) {
          fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 100 + dx * f, clientY: 100 + dy * f });
          flushFrame();
        }
        fireEvent.pointerUp(canvas, { pointerId: 1 });
        flushFrame();
      };
      const gridButton = screen.getByRole('button', { name: 'Grid' });
      click(canvas, shape);
      // One step before the drag, so an undo that went past the drag would show.
      fireEvent.keyDown(canvas, { key: 'ArrowDown' });
      expect(placement(container, 'n_web')).toBe('translate(40 41)');

      // The fitted view is 100%. 53 right puts n_web's edges at 93 and 413, 7 from the grid lines at 100 and 420 and
      // clear of every other node, so the grid takes it to 100; its top (41) drops onto the line at 40.
      expect(gridButton.getAttribute('aria-pressed')).toBe('true');
      expect(container.querySelector('.canvas-grid')).not.toBeNull();
      dragBy(53, 0);
      expect(placement(container, 'n_web')).toBe('translate(100 40)');
      undo();
      expect(placement(container, 'n_web')).toBe('translate(40 41)');
      undo();
      expect(placement(container, 'n_web')).toBe('translate(40 40)');

      fireEvent.keyDown(window, { key: "'", metaKey: true });
      expect(gridButton.getAttribute('aria-pressed')).toBe('false');
      expect(container.querySelector('.canvas-grid')).toBeNull();
      dragBy(53, 0);
      expect(placement(container, 'n_web')).toBe('translate(93 40)');

      fireEvent.click(gridButton);
      expect(gridButton.getAttribute('aria-pressed')).toBe('true');
      expect(container.querySelector('.canvas-grid')).not.toBeNull();
    });

    it('resizes through a handle as one undo step', () => {
      const { container, canvas, shape, nodeTransform } = renderEditor();
      click(canvas, shape);
      const before = nodeTransform();
      const corner = container.querySelector('.selection-node[data-selection-id="n_web"] [data-handle="4"]');
      if (!corner) {
        throw new Error('no resize handle');
      }
      fireEvent.pointerDown(corner, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 500, clientY: 500 });
      for (let i = 1; i <= 4; i++) {
        fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 500 + i * 10, clientY: 500 + i * 10 });
        flushFrame();
      }
      fireEvent.pointerUp(canvas, { pointerId: 1 });
      const resized = nodeTransform();
      expect(resized).not.toBe(before);
      // Top-left anchor: the node grew in place rather than moving.
      expect(placement(container, 'n_web')).toBe('translate(40 40)');
      undo();
      expect(nodeTransform()).toBe(before);
      redo();
      expect(nodeTransform()).toBe(resized);
    });

    it('rotates through the rotate handle as one undo step, after a nudge that stays its own', () => {
      const { container, canvas, shape, nodeTransform } = renderEditor();
      click(canvas, shape);
      fireEvent.keyDown(canvas, { key: 'ArrowRight' });
      const nudged = nodeTransform();
      const outline = container.querySelector('.selection-node[data-selection-id="n_web"] .selection-outline');
      const handle = container.querySelector('.selection-node[data-selection-id="n_web"] [data-rotate-handle]');
      if (!outline || !handle) {
        throw new Error('no rotate handle');
      }
      // The handle sits straight above the centre, so a press there dragged level with the centre to its right is 90°.
      const centreX = Number(outline.getAttribute('x')) + Number(outline.getAttribute('width')) / 2;
      const centreY = Number(outline.getAttribute('y')) + Number(outline.getAttribute('height')) / 2;
      const handleY = Number(handle.getAttribute('cy'));
      fireEvent.pointerDown(handle, { pointerId: 1, button: 0, pointerType: 'mouse', clientX: centreX, clientY: handleY });
      for (let i = 1; i <= 4; i++) {
        const angle = (i / 4) * (Math.PI / 2);
        const r = centreY - handleY;
        fireEvent.pointerMove(canvas, { pointerId: 1, clientX: centreX + r * Math.sin(angle), clientY: centreY - r * Math.cos(angle) });
        flushFrame();
      }
      fireEvent.pointerUp(canvas, { pointerId: 1 });
      const rotated = nodeTransform();
      expect(placement(container, 'n_web')).toBe('translate(41 40) rotate(90 160 40)');
      undo();
      expect(nodeTransform()).toBe(nudged);
      redo();
      expect(nodeTransform()).toBe(rotated);
      undo();
      undo();
      expect(placement(container, 'n_web')).toBe('translate(40 40)');
    });

    it('deletes a node with its edges on Delete, and one Cmd+Z restores them all', () => {
      const { container, canvas } = renderEditor();
      const before = drawnIds(container);
      const api = container.querySelector('g[data-id="n_api"] .shape');
      if (!api) {
        throw new Error('n_api not drawn');
      }
      click(canvas, api);
      fireEvent.keyDown(canvas, { key: 'Delete' });
      // n_api carries e_1, e_2 and e_3; every other node stays.
      expect(drawnIds(container).sort()).toEqual(before.filter((id) => id !== null && !['n_api', 'e_1', 'e_2', 'e_3'].includes(id)).sort());
      undo();
      expect(drawnIds(container)).toEqual(before);
    });

    it('adds a selected node at the view centre from the rail, stacks the next one, and undoes each separately', () => {
      const { container } = renderEditor();
      const before = drawnIds(container);
      const add = screen.getByRole('button', { name: 'Add service' });
      fireEvent.click(add);
      const first = drawnIds(container).filter((id) => !before.includes(id));
      expect(first).toHaveLength(1);
      expect([...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'))).toEqual(first);
      expect(container.querySelector(`g[data-id="${first[0] ?? ''}"]`)?.getAttribute('data-kind')).toBe('service');

      fireEvent.click(add);
      const both = drawnIds(container).filter((id) => !before.includes(id));
      expect(both).toHaveLength(2);
      const [a, b] = both.map((id) => /translate\((\S+) (\S+)\)/.exec(placement(container, id ?? '') ?? '')?.slice(1).map(Number));
      // One grid step apart, down and to the right: every shipped diagram theme has a 20-unit grid (themes/*.yaml).
      expect(b?.[0]).toBe((a?.[0] ?? 0) + 20);
      expect(b?.[1]).toBe((a?.[1] ?? 0) + 20);

      undo();
      expect(drawnIds(container).filter((id) => !before.includes(id))).toEqual(first);
      undo();
      expect(drawnIds(container)).toEqual(before);
    });

    it('offers Select and Connector on the rail, picked by click or by V and C', () => {
      renderEditor();
      const select = screen.getByRole('button', { name: 'Select' });
      const connector = screen.getByRole('button', { name: 'Connector' });
      expect(select.getAttribute('aria-pressed')).toBe('true');
      expect(connector.getAttribute('title')).toBe('Connector (C)');
      fireEvent.keyDown(window, { key: 'c' });
      expect(connector.getAttribute('aria-pressed')).toBe('true');
      expect(select.getAttribute('aria-pressed')).toBe('false');
      // Ctrl+C is copy, never the tool; V goes back to Select.
      fireEvent.keyDown(window, { key: 'v', metaKey: true });
      expect(connector.getAttribute('aria-pressed')).toBe('true');
      fireEvent.keyDown(window, { key: 'v' });
      expect(select.getAttribute('aria-pressed')).toBe('true');
      fireEvent.click(connector);
      expect(connector.getAttribute('aria-pressed')).toBe('true');
    });

    describe('shape panel', () => {
      it('opens from the rail and with a bare S, focusing search, and closes on Escape, returning focus to the canvas', () => {
        const { canvas } = renderEditor();
        const shapesButton = screen.getByRole('button', { name: 'Shapes' });
        expect(screen.queryByRole('searchbox', { name: 'Search shapes' })).toBeNull();

        fireEvent.click(shapesButton);
        const search = screen.getByRole('searchbox', { name: 'Search shapes' });
        expect(shapesButton.getAttribute('aria-pressed')).toBe('true');
        expect(globalThis.document.activeElement).toBe(search);

        fireEvent.keyDown(search, { key: 'Escape' });
        expect(screen.queryByRole('searchbox', { name: 'Search shapes' })).toBeNull();
        expect(shapesButton.getAttribute('aria-pressed')).toBe('false');
        expect(globalThis.document.activeElement).toBe(canvas);

        fireEvent.keyDown(window, { key: 's' });
        expect(screen.getByRole('searchbox', { name: 'Search shapes' })).toBeTruthy();
      });

      it('closes when picking Select or Connector', () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        fireEvent.keyDown(window, { key: 'c' });
        expect(screen.queryByRole('searchbox', { name: 'Search shapes' })).toBeNull();
        expect(screen.getByRole('button', { name: 'Connector' }).getAttribute('aria-pressed')).toBe('true');
      });

      it('leaves a typed S in the search field to typing, never toggling the panel closed', () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        const search = screen.getByRole('searchbox', { name: 'Search shapes' });
        fireEvent.keyDown(search, { key: 's' });
        expect(screen.getByRole('searchbox', { name: 'Search shapes' })).toBeTruthy();
        fireEvent.change(search, { target: { value: 'serv' } });
        expect(screen.getByRole('button', { name: 'Service' })).toBeTruthy();
        expect(screen.queryByRole('button', { name: 'Database' })).toBeNull();
      });

      it("offers the architecture vocabulary's kinds and the presentation shapes, filtered by search", () => {
        renderEditor();
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        for (const label of ['Service', 'Database', 'Queue', 'Client / User', 'External system', 'Container / Zone', 'Note']) {
          expect(screen.getByRole('button', { name: label })).toBeTruthy();
        }
        for (const label of ['Rectangle', 'Rounded rectangle', 'Diamond', 'Cylinder', 'Pill', 'Hexagon', 'Image']) {
          expect(screen.getByRole('button', { name: label })).toBeTruthy();
        }
        // Flowchart-only kinds never appear for an architecture document.
        expect(screen.queryByRole('button', { name: 'Decision' })).toBeNull();

        fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'data' } });
        expect(screen.getByRole('button', { name: 'Database' })).toBeTruthy();
        expect(screen.queryByRole('button', { name: 'Service' })).toBeNull();
        expect(screen.queryByRole('button', { name: 'Rectangle' })).toBeNull();
      });

      it('arms a clicked kind and places it at the next canvas click point, selected, as one undo step', () => {
        const { container } = renderEditor();
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        const before = drawnIds(container);
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        fireEvent.click(screen.getByRole('button', { name: 'Database' }));
        // Choosing the shape must not place it yet (Bug 1: a fixed-anchor placement before any canvas click).
        expect(drawnIds(container)).toEqual(before);

        fireEvent.pointerDown(viewerCanvas, { clientX: 450, clientY: 230 });

        const added = drawnIds(container).filter((idValue) => !before.includes(idValue));
        expect(added).toHaveLength(1);
        const newId = added[0] ?? '';
        expect(container.querySelector(`g[data-id="${newId}"]`)?.getAttribute('data-kind')).toBe('database');
        expect([...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'))).toEqual([newId]);

        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('places a shape from the Shapes group with the generic shape kind, not a vocabulary kind', () => {
        const { container } = renderEditor();
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        const before = drawnIds(container);
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        fireEvent.click(screen.getByRole('button', { name: 'Hexagon' }));
        fireEvent.pointerDown(viewerCanvas, { clientX: 300, clientY: 200 });

        const added = drawnIds(container).filter((idValue) => !before.includes(idValue));
        const newId = added[0] ?? '';
        expect(container.querySelector(`g[data-id="${newId}"]`)?.getAttribute('data-kind')).toBe('shape');
        undo();
      });

      it('lands click-to-place at the click point, matching two different points to two different shapes', () => {
        const { container } = renderEditor();
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        const before = drawnIds(container);

        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        fireEvent.click(screen.getByRole('button', { name: 'Service' }));
        fireEvent.pointerDown(viewerCanvas, { clientX: 200, clientY: 150 });
        const firstId = drawnIds(container).find((idValue) => !before.includes(idValue)) ?? '';
        const [firstX, firstY] = (/translate\((\S+) (\S+)\)/.exec(placement(container, firstId) ?? '')?.slice(1) ?? []).map(Number);

        fireEvent.click(screen.getByRole('button', { name: 'Service' }));
        fireEvent.pointerDown(viewerCanvas, { clientX: 700, clientY: 500 });
        const secondId = drawnIds(container).find((idValue) => !before.includes(idValue) && idValue !== firstId) ?? '';
        const [secondX, secondY] = (/translate\((\S+) (\S+)\)/.exec(placement(container, secondId) ?? '')?.slice(1) ?? []).map(Number);

        expect(drawnIds(container).filter((idValue) => !before.includes(idValue))).toHaveLength(2);
        // Each click landed at its own point, not stacked near a shared fixed anchor (Bug 1).
        expect(Math.abs((firstX ?? 0) - (secondX ?? 0))).toBeGreaterThan(200);
        expect(Math.abs((firstY ?? 0) - (secondY ?? 0))).toBeGreaterThan(200);

        undo();
        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('drops a dragged shape at the pointer, selected, as one undo step, distinct from a click-to-place at a different point', () => {
        const { container } = renderEditor();
        const before = drawnIds(container);
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        // A real drag: pointerdown on the glyph (not a click), then the pointer moving onto the canvas and releasing
        // there — no dataTransfer, no dragstart/dragend, matching how a real press-drag reaches the canvas (Bug B).
        fireEvent.pointerDown(screen.getByRole('button', { name: 'Queue' }), { pointerId: 5, button: 0, pointerType: 'mouse', clientX: 5, clientY: 5 });
        fireEvent.pointerMove(viewerCanvas, { pointerId: 5, clientX: 20, clientY: 20 });
        fireEvent.pointerUp(viewerCanvas, { pointerId: 5, clientX: 20, clientY: 20 });

        const dropped = drawnIds(container).filter((idValue) => !before.includes(idValue));
        expect(dropped).toHaveLength(1);
        const droppedId = dropped[0] ?? '';
        expect(container.querySelector(`g[data-id="${droppedId}"]`)?.getAttribute('data-kind')).toBe('queue');
        expect([...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'))).toEqual([droppedId]);
        const [dropX, dropY] = (/translate\((\S+) (\S+)\)/.exec(placement(container, droppedId) ?? '')?.slice(1) ?? []).map(Number);

        // A click-to-place at a distant point must land there, not stacked on the drop.
        fireEvent.click(screen.getByRole('button', { name: 'Queue' }));
        fireEvent.pointerDown(viewerCanvas, { clientX: 900, clientY: 700 });
        const clickedId = drawnIds(container).find((idValue) => !before.includes(idValue) && idValue !== droppedId) ?? '';
        const [clickedX, clickedY] = (/translate\((\S+) (\S+)\)/.exec(placement(container, clickedId) ?? '')?.slice(1) ?? []).map(Number);
        expect(Math.abs((dropX ?? 0) - (clickedX ?? 0))).toBeGreaterThan(100);
        expect(Math.abs((dropY ?? 0) - (clickedY ?? 0))).toBeGreaterThan(100);

        undo();
        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('places nothing for a pointerup whose id does not match any armed drag', () => {
        const { container } = renderEditor();
        const before = drawnIds(container);
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        fireEvent.pointerUp(viewerCanvas, { pointerId: 999, clientX: 20, clientY: 20 });
        expect(drawnIds(container)).toEqual(before);
      });

      it('drops nothing and disarms when the drag pointer is released off the canvas entirely', () => {
        const { container } = renderEditor();
        const before = drawnIds(container);
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        fireEvent.click(screen.getByRole('button', { name: 'Shapes' }));
        fireEvent.pointerDown(screen.getByRole('button', { name: 'Queue' }), { pointerId: 6, button: 0, pointerType: 'mouse', clientX: 5, clientY: 5 });
        // Released somewhere the canvas never sees (e.g. back over the panel); only the window ever hears it.
        fireEvent.pointerUp(window, { pointerId: 6, clientX: 5, clientY: 5 });

        expect(drawnIds(container)).toEqual(before);
        // The armed pointer was cleared, not left stale: releasing the *same* id over the canvas afterwards places nothing.
        fireEvent.pointerUp(viewerCanvas, { pointerId: 6, clientX: 20, clientY: 20 });
        expect(drawnIds(container)).toEqual(before);
      });
    });

    const shapeOf = (container: HTMLElement, id: string, part = '.shape') => {
      const found = container.querySelector(`g[data-id="${id}"] ${part}`);
      if (!found) {
        throw new Error(`${id} not drawn`);
      }
      return found;
    };
    const shiftClick = (canvas: HTMLElement, target: Element) => {
      fireEvent.pointerDown(target, { pointerId: 8, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100, shiftKey: true });
      fireEvent.pointerUp(canvas, { pointerId: 8, shiftKey: true });
    };
    const enabled = (name: string) => !screen.getByRole<HTMLButtonElement>('button', { name }).disabled;

    it('applies Text changes without losing surviving selection and undoes the command once', async () => {
      const next = structuredClone(document);
      next.title = 'From DSL';
      const web = next.nodes.find((node) => node.id === 'n_web');
      if (!web) throw new Error('Missing node');
      web.label = 'Text changed web';
      vi.stubGlobal('fetch', vi.fn<typeof fetch>((url) => Promise.resolve(url === '/api/export/dsl'
        ? new Response('original DSL')
        : new Response(JSON.stringify({ document: next, warnings: [] })))));
      const { container, canvas } = renderEditor();
      click(canvas, shapeOf(container, 'n_web'));
      expect(enabled('Bring to front')).toBe(true);
      expect(screen.queryByRole('button', { name: 'Text (DSL)' })).toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Text' }));
      const field = screen.getByRole<HTMLTextAreaElement>('textbox', { name: 'Document DSL' });
      await waitFor(() => { expect(field.value).toBe('original DSL'); });
      fireEvent.change(field, { target: { value: 'changed DSL' } });
      fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
      await waitFor(() => { expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('From DSL'); });
      expect(container.querySelector('g[data-id="n_web"]')?.textContent).toContain('Text changed web');
      fireEvent.click(screen.getByRole('button', { name: 'Properties' }));
      expect(enabled('Bring to front')).toBe(true);
      fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
      expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Order service');
      expect(container.querySelector('g[data-id="n_web"]')?.textContent).toContain('Web app');
      fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
      expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Order service');
      fireEvent.keyDown(window, { key: 'z', ctrlKey: true, shiftKey: true });
      expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('From DSL');
      expect(enabled('Bring to front')).toBe(true);
    });

    it('widens the right panel for the Text tab only, so DSL lines are not cut off (P7)', () => {
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('draughtsman 1'))));
      const { container } = renderEditor();
      const panel = (): Element | null => container.querySelector('.viewer-panel');
      expect(panel()?.classList.contains('viewer-panel-wide')).toBe(false);
      fireEvent.click(screen.getByRole('button', { name: 'Text' }));
      expect(panel()?.classList.contains('viewer-panel-wide')).toBe(true);
      fireEvent.click(screen.getByRole('button', { name: 'Properties' }));
      expect(panel()?.classList.contains('viewer-panel-wide')).toBe(false);
    });

    it('enables align from two selected nodes, distribute from three and order from one', () => {
      const { container, canvas } = renderEditor();
      const arrangeSection = screen.getByRole('region', { name: 'Arrange' });
      expect(arrangeSection.querySelectorAll('button')).toHaveLength(12);
      expect([enabled('Align left'), enabled('Distribute horizontally'), enabled('Bring to front')]).toEqual([false, false, false]);
      click(canvas, shapeOf(container, 'n_web'));
      expect([enabled('Align left'), enabled('Distribute horizontally'), enabled('Bring to front')]).toEqual([false, false, true]);
      // An edge counts for order but not for align.
      shiftClick(canvas, shapeOf(container, 'e_1', '.hit-area'));
      expect([enabled('Align left'), enabled('Distribute horizontally')]).toEqual([false, false]);
      shiftClick(canvas, shapeOf(container, 'n_cache'));
      expect([enabled('Align bottom'), enabled('Distribute vertically')]).toEqual([true, false]);
      shiftClick(canvas, shapeOf(container, 'n_db'));
      expect([enabled('Align bottom'), enabled('Distribute vertically')]).toEqual([true, true]);
    });

    // S5: connectors without a pointer. L joins the selected shapes in the order they were selected, as one undo step.
    it('connects the selected shapes in selection order on L, as one undo step, and only from two shapes', async () => {
      const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      const edgesDrawn = () => container.querySelectorAll('g.edge').length;
      const before = edgesDrawn();

      // One shape selected: L does nothing.
      click(canvas, shapeOf(container, 'n_web'));
      fireEvent.keyDown(window, { key: 'l' });
      expect(edgesDrawn()).toBe(before);

      // Selected n_db, then n_web, then n_cache: the chain runs n_db -> n_web -> n_cache, not document order.
      click(canvas, shapeOf(container, 'n_db'));
      shiftClick(canvas, shapeOf(container, 'n_web'));
      shiftClick(canvas, shapeOf(container, 'n_cache'));
      fireEvent.keyDown(window, { key: 'l' });
      expect(edgesDrawn()).toBe(before + 2);

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const sent = put.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body;
      if (typeof sent !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const added = (JSON.parse(sent) as typeof document).edges.filter((edge) => !document.edges.some((existing) => existing.id === edge.id));
      expect(added.map((edge) => [edge.from.node, edge.to.node])).toEqual([['n_db', 'n_web'], ['n_web', 'n_cache']]);
      // Each end is on the side facing the other shape, not a port left over from a drag.
      expect(added.map((edge) => [edge.from.port, edge.to.port])).toEqual([['top', 'bottom'], ['right', 'top']]);

      // The new connectors are selected, so L is off again, and one undo takes both away.
      fireEvent.keyDown(window, { key: 'l' });
      expect(edgesDrawn()).toBe(before + 2);
      undo();
      expect(edgesDrawn()).toBe(before);
    });

    it('lists Connect selected shapes in the command palette, disabled until two shapes are selected', () => {
      const { container, canvas } = renderEditor();
      const entry = () => {
        fireEvent.keyDown(window, { key: 'k', metaKey: true });
        const search = screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
        fireEvent.change(search, { target: { value: 'connect selected' } });
        const option = within(screen.getByRole('listbox', { name: 'Commands' })).getByRole('option');
        const found = { name: option.getAttribute('aria-label'), disabled: option.getAttribute('aria-disabled') === 'true' };
        fireEvent.keyDown(search, { key: 'Escape' });
        return found;
      };
      expect(entry()).toEqual({ name: 'Connect selected shapes, L', disabled: true });
      click(canvas, shapeOf(container, 'n_web'));
      shiftClick(canvas, shapeOf(container, 'n_cache'));
      expect(entry()).toEqual({ name: 'Connect selected shapes, L', disabled: false });
    });

    it('aligns the selection from the panel as one undo step', () => {
      const { container, canvas } = renderEditor();
      click(canvas, shapeOf(container, 'n_web'));
      shiftClick(canvas, shapeOf(container, 'n_cache'));
      // n_web at (40, 40) 320 wide, n_cache at (400, 360) 160 wide: right edges meet at 560.
      fireEvent.click(screen.getByRole('button', { name: 'Align right' }));
      expect(placement(container, 'n_web')).toBe('translate(240 40)');
      expect(placement(container, 'n_cache')).toBe('translate(400 360)');
      undo();
      expect(placement(container, 'n_web')).toBe('translate(40 40)');
    });

    it('brings the selection to the front on Cmd+Alt+], keeps it through a save and reload, and undoes it in one step', async () => {
      const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', fetchMock);
      const { container, canvas, unmount } = renderEditor();
      const before = drawnIds(container);
      click(canvas, shapeOf(container, 'n_db'));
      fireEvent.keyDown(canvas, { key: '‘', code: 'BracketRight', metaKey: true, altKey: true });
      const after = drawnIds(container);
      // n_db is drawn after its siblings inside c_vpc now; nothing else moved.
      expect(after.indexOf('n_db')).toBeGreaterThan(after.indexOf('n_cache'));
      expect(before.indexOf('n_db')).toBeLessThan(before.indexOf('n_cache'));
      expect(after.filter((id) => id !== 'n_db')).toEqual(before.filter((id) => id !== 'n_db'));

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const save = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT');
      const saved = parseDocument(JSON.parse(save?.[1]?.body as string));
      undo();
      expect(drawnIds(container)).toEqual(before);
      unmount();

      const reloaded = render(<DocumentViewer id={saved.id} document={saved} etag={'"dv1:next"'} />);
      expect(drawnIds(reloaded.container)).toEqual(after);
    });

    it('sends the selection backward from the panel and back on Cmd+[', () => {
      const { container, canvas } = renderEditor();
      const before = drawnIds(container);
      click(canvas, shapeOf(container, 'n_cache'));
      fireEvent.click(screen.getByRole('button', { name: 'Send to back' }));
      const sent = drawnIds(container);
      expect(sent.indexOf('n_cache')).toBeLessThan(sent.indexOf('n_api'));
      undo();
      expect(drawnIds(container)).toEqual(before);
      fireEvent.keyDown(canvas, { key: '[', ctrlKey: true });
      const backward = drawnIds(container);
      expect(backward.indexOf('n_cache')).toBeLessThan(backward.indexOf('n_db'));
      expect(backward.indexOf('n_cache')).toBeGreaterThan(backward.indexOf('n_api'));
    });

    it('keeps Cmd+[ from the browser in the editor even with nothing selected, and leaves it alone on a preview', () => {
      const editor = renderEditor();
      // fireEvent returns false when a listener called preventDefault.
      expect(fireEvent.keyDown(editor.canvas, { key: '[', metaKey: true })).toBe(false);
      editor.unmount();
      const preview = renderEditor(true);
      expect(fireEvent.keyDown(preview.canvas, { key: '[', metaKey: true })).toBe(true);
      expect(screen.queryByRole('region', { name: 'Properties' })).toBeNull();
    });

    it('shows no tool rail on a read-only preview', () => {
      renderEditor(true);
      expect(screen.queryByRole('toolbar', { name: 'Tools' })).toBeNull();
    });

    it('connects two nodes as one undo step, and a later move of an endpoint leaves the saved edge as drawn', async () => {
      const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      const transform = container.querySelector('g.world')?.getAttribute('transform') ?? '';
      const [tx, ty, zoom] = (/translate\((\S+) (\S+)\) scale\((\S+)\)/.exec(transform) ?? []).slice(1).map(Number);
      const at = (x: number, y: number) => ({ clientX: x * (zoom ?? 1) + (tx ?? 0), clientY: y * (zoom ?? 1) + (ty ?? 0) });
      const edgesDrawn = () => container.querySelectorAll('g.edge').length;
      const before = edgesDrawn();

      // n_web (40,40 320x80) has no ports; the release lands on the body of n_cache (400,360 160x80): the port facing
      // n_web's pinned bottom port (200,120) is its left, not the top it happens to be nearest the pointer.
      fireEvent.keyDown(window, { key: 'c' });
      fireEvent.pointerDown(canvas, { pointerId: 1, button: 0, pointerType: 'mouse', ...at(200, 120) });
      fireEvent.pointerMove(canvas, { pointerId: 1, pointerType: 'mouse', ...at(470, 375) });
      fireEvent.pointerUp(canvas, { pointerId: 1, pointerType: 'mouse', ...at(470, 375) });
      expect(edgesDrawn()).toBe(before + 1);

      fireEvent.keyDown(window, { key: 'z', metaKey: true });
      expect(edgesDrawn()).toBe(before);
      fireEvent.keyDown(window, { key: 'z', metaKey: true, shiftKey: true });
      expect(edgesDrawn()).toBe(before + 1);

      // Back to Select and move the source node.
      fireEvent.keyDown(window, { key: 'v' });
      const web = container.querySelector('g[data-id="n_web"] .shape');
      if (!web) {
        throw new Error('n_web not drawn');
      }
      fireEvent.pointerDown(web, { pointerId: 2, button: 0, pointerType: 'mouse', clientX: 0, clientY: 0 });
      fireEvent.pointerMove(canvas, { pointerId: 2, clientX: 60, clientY: 0 });
      flushFrame();
      fireEvent.pointerUp(canvas, { pointerId: 2 });

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const sent = put.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body;
      if (typeof sent !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const body = JSON.parse(sent) as typeof document;
      const added = body.edges.filter((edge) => !document.edges.some((existing) => existing.id === edge.id));
      expect(added).toHaveLength(1);
      expect(added[0]).toMatchObject({ kind: 'flow', from: { node: 'n_web', port: 'bottom' }, to: { node: 'n_cache', port: 'left' }, routing: { mode: 'orthogonal' } });
      expect(added[0]?.routing?.waypoints).toBeUndefined();
      expect(body.nodes.find((n) => n.id === 'n_web')?.geometry?.x).toBe(40 + 60 / (zoom ?? 1));
      expect(body.nodes.find((n) => n.id === 'n_web')?.ports?.map((port) => port.id)).toEqual(['top', 'right', 'bottom', 'left']);
      expect(body.edges.filter((edge) => document.edges.some((existing) => existing.id === edge.id))).toEqual(document.edges);
    });

    it('connects from a shape’s handle in the select tool, selects the new connector, and re-attaches its end, each one undo step', async () => {
      const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      const transform = container.querySelector('g.world')?.getAttribute('transform') ?? '';
      const [tx, ty, zoom] = (/translate\((\S+) (\S+)\) scale\((\S+)\)/.exec(transform) ?? []).slice(1).map(Number);
      const at = (x: number, y: number) => ({ clientX: x * (zoom ?? 1) + (tx ?? 0), clientY: y * (zoom ?? 1) + (ty ?? 0) });
      const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
      const added = () => [...container.querySelectorAll('g.edge')].map((g) => g.getAttribute('data-id') ?? '').filter((id) => !document.edges.some((edge) => edge.id === id));
      const panel = screen.getByRole('region', { name: 'Properties' });

      // Still in the select tool: hover n_web (40,40 320x80), then drag from the handle set out below its bottom port
      // and release on n_cache (400,360 160x80), (the arrow end moves from the port facing n_web, so its left).
      const bottomHandle = at(200, 120);
      fireEvent.pointerMove(canvas, { ...mouse, ...at(200, 80) });
      fireEvent.pointerDown(canvas, { ...mouse, clientX: bottomHandle.clientX, clientY: bottomHandle.clientY + 16 });
      fireEvent.pointerMove(canvas, { ...mouse, ...at(470, 375) });
      fireEvent.pointerUp(canvas, { ...mouse, ...at(470, 375) });
      const [edgeId] = added();
      expect(added()).toHaveLength(1);
      // Selected at once: the Connector section follows it and its ends are draggable.
      expect(container.querySelector(`.selection-edge[data-selection-id="${edgeId ?? ''}"]`)).not.toBeNull();
      expect(panel.querySelector('select')).not.toBeNull();

      // Drag its arrow end from n_cache's left onto n_db (40,360 160x80), within the ring of its top port.
      const end = container.querySelector(`.edge-endpoint[data-edge-id="${edgeId ?? ''}"][data-edge-end="to"]`);
      if (!end) {
        throw new Error('arrow end not drawn');
      }
      fireEvent.pointerDown(canvas, { ...mouse, clientX: Number(end.getAttribute('cx')), clientY: Number(end.getAttribute('cy')) });
      fireEvent.pointerMove(canvas, { ...mouse, ...at(120, 370) });
      fireEvent.pointerUp(canvas, { ...mouse, ...at(120, 370) });

      const saved = async () => {
        fireEvent.click(screen.getByRole('button', { name: 'Save' }));
        await act(() => Promise.resolve());
        const sent = put.mock.lastCall?.[1]?.body;
        if (typeof sent !== 'string') {
          throw new Error('Save sent no JSON body');
        }
        return JSON.parse(sent) as typeof document;
      };
      const reattached = (await saved()).edges.find((edge) => edge.id === edgeId);
      expect(reattached).toEqual({ id: edgeId, kind: 'flow', from: { node: 'n_web', port: 'bottom' }, to: { node: 'n_db', port: 'top' }, routing: { mode: 'orthogonal' } });

      // One Cmd+Z puts the end back on n_cache; the next removes the connector; redo brings both back in turn.
      undo();
      expect((await saved()).edges.find((edge) => edge.id === edgeId)?.to).toEqual({ node: 'n_cache', port: 'left' });
      undo();
      expect(added()).toHaveLength(0);
      redo();
      redo();
      expect((await saved()).edges.find((edge) => edge.id === edgeId)?.to).toEqual({ node: 'n_db', port: 'top' });
    });

    for (const tool of ['connector', 'select'] as const) {
      it(`fans out two connectors from one port in the ${tool} tool, leaving the first where it was drawn`, async () => {
        const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
        vi.stubGlobal('fetch', put);
        const { container, canvas } = renderEditor();
        const transform = container.querySelector('g.world')?.getAttribute('transform') ?? '';
        const [tx, ty, zoom] = (/translate\((\S+) (\S+)\) scale\((\S+)\)/.exec(transform) ?? []).slice(1).map(Number);
        const at = (x: number, y: number) => ({ clientX: x * (zoom ?? 1) + (tx ?? 0), clientY: y * (zoom ?? 1) + (ty ?? 0) });
        const mouse = { pointerId: 1, button: 0, pointerType: 'mouse' };
        if (tool === 'connector') {
          fireEvent.keyDown(window, { key: 'c' });
        }
        // n_web's bottom port is at (200,120). The connector tool presses the port itself; the select tool presses its
        // connection handle 16px below, on the port side, within a port's reach of the first connector's tail.
        const bottom = at(200, 120);
        const press = tool === 'connector' ? bottom : { clientX: bottom.clientX, clientY: bottom.clientY + 8 };
        const connectTo = (x: number, y: number) => {
          fireEvent.pointerMove(canvas, { ...mouse, ...at(200, 80) });
          fireEvent.pointerDown(canvas, { ...mouse, ...press });
          fireEvent.pointerMove(canvas, { ...mouse, ...at(x, y) });
          fireEvent.pointerUp(canvas, { ...mouse, ...at(x, y) });
        };
        // To n_cache (400,360 160x80), then, with that connector still selected, to n_db (40,360 160x80).
        connectTo(470, 375);
        const [first] = [...container.querySelectorAll('.selection-edge')].map((e) => e.getAttribute('data-selection-id'));
        expect(first).toBeDefined();
        connectTo(120, 370);

        fireEvent.click(screen.getByRole('button', { name: 'Save' }));
        await act(() => Promise.resolve());
        const sent = put.mock.lastCall?.[1]?.body;
        if (typeof sent !== 'string') {
          throw new Error('Save sent no JSON body');
        }
        const added = (JSON.parse(sent) as typeof document).edges.filter((edge) => !document.edges.some((existing) => existing.id === edge.id));
        expect(added.map((edge) => [edge.from, edge.to])).toEqual([
          [{ node: 'n_web', port: 'bottom' }, { node: 'n_cache', port: 'left' }],
          [{ node: 'n_web', port: 'bottom' }, { node: 'n_db', port: 'top' }],
        ]);
        expect(added[0]?.id).toBe(first);
      });
    }

    it('switches a selected connector’s kind from the properties panel, changing its arrowheads without writing a style', async () => {
      const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      const panel = screen.getByRole('region', { name: 'Properties' });
      expect(panel.querySelector('select')).toBeNull();
      // What the marker at the end of e_1's drawn line draws: a filled path, an open one, or nothing.
      const endHead = (): string => {
        const reference = container.querySelector('g[data-id="e_1"] polyline:not(.hit-area)')?.getAttribute('marker-end');
        if (reference === null || reference === undefined) {
          return 'none';
        }
        const path = container.querySelector(`marker[id="${reference.slice(5, -1)}"] path`);
        return path?.getAttribute('fill') === 'none' ? 'open' : 'filled';
      };
      expect(endHead()).toBe('filled');

      const hit = container.querySelector('g[data-id="e_1"] polyline.hit-area');
      if (!hit) {
        throw new Error('e_1 not drawn');
      }
      click(canvas, hit);
      const kind = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Kind' });
      expect(kind.value).toBe('flow');
      // The architecture vocabulary's edge kinds, in its order.
      expect([...kind.options].map((option) => option.value)).toEqual(['flow', 'data', 'dependency', 'association', 'dotted-report']);

      fireEvent.change(kind, { target: { value: 'data' } });
      expect(kind.value).toBe('data');
      expect(endHead()).toBe('open');
      fireEvent.change(kind, { target: { value: 'association' } });
      expect(endHead()).toBe('none');

      // Each switch is one undo step: one Cmd+Z goes back to data, the next to flow.
      undo();
      expect(endHead()).toBe('open');
      expect(kind.value).toBe('data');
      undo();
      expect(endHead()).toBe('filled');
      expect(kind.value).toBe('flow');
      redo();
      expect(kind.value).toBe('data');

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const sent = put.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body;
      if (typeof sent !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const body = JSON.parse(sent) as typeof document;
      // Only the kind changed on e_1: no style was written, and every other edge is exactly as loaded.
      expect(body.edges.find((edge) => edge.id === 'e_1')).toEqual({ ...document.edges.find((edge) => edge.id === 'e_1'), kind: 'data' });
      expect(body.edges.find((edge) => edge.id === 'e_1')).not.toHaveProperty('style');
      expect(body.edges.filter((edge) => edge.id !== 'e_1')).toEqual(document.edges.filter((edge) => edge.id !== 'e_1'));

      // A node selection has no Connector section.
      const api = container.querySelector('g[data-id="n_api"] .shape');
      if (!api) {
        throw new Error('n_api not drawn');
      }
      click(canvas, api);
      expect(panel.querySelector('select')).toBeNull();
    });

it('sets a selected connector’s arrowhead ends, both ways, swapped and reset, each as one undo step', async () => {
      const put = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      // The marker id an end references, or undefined when it draws none.
      const endRef = (attribute: 'marker-start' | 'marker-end'): string | undefined => {
        const reference = container.querySelector('g[data-id="e_1"] polyline:not(.hit-area)')?.getAttribute(attribute);
        return reference === null || reference === undefined ? undefined : reference.slice(5, -1);
      };

      const hit = container.querySelector('g[data-id="e_1"] polyline.hit-area');
      if (!hit) {
        throw new Error('e_1 not drawn');
      }
      click(canvas, hit);

      const start = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Arrowhead at the start end' });
      const end = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Arrowhead at the end' });
      const both = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Set both ends to the same head' });

      fireEvent.change(start, { target: { value: 'many' } });
      expect(endRef('marker-start')).toContain('arrow-many');
      fireEvent.change(end, { target: { value: 'composition' } });
      expect(endRef('marker-end')).toContain('arrow-composition');

      // Swap exchanges the two drawn ends; one Cmd+Z restores them.
      fireEvent.click(screen.getByRole('button', { name: 'Swap ends' }));
      expect(endRef('marker-start')).toContain('arrow-composition');
      expect(endRef('marker-end')).toContain('arrow-many');
      undo();
      expect(endRef('marker-start')).toContain('arrow-many');
      expect(endRef('marker-end')).toContain('arrow-composition');
      redo();
      expect(endRef('marker-start')).toContain('arrow-composition');
      expect(endRef('marker-end')).toContain('arrow-many');

      // Both ways writes the chosen head at both ends, one undo step away from the swapped ends.
      fireEvent.change(both, { target: { value: 'inheritance' } });
      expect(endRef('marker-start')).toContain('arrow-inheritance');
      expect(endRef('marker-end')).toContain('arrow-inheritance');
      undo();
      expect(endRef('marker-start')).toContain('arrow-composition');
      expect(endRef('marker-end')).toContain('arrow-many');
      redo();
      expect(endRef('marker-start')).toContain('arrow-inheritance');
      expect(endRef('marker-end')).toContain('arrow-inheritance');

      // Reset clears both overrides, so the ends return to the flow kind's default arrow at the end.
      fireEvent.click(screen.getByRole('button', { name: 'Reset to default' }));
      expect(endRef('marker-start')).toBeUndefined();
      expect(endRef('marker-end')).toContain('arrow-arrow');

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const sent = put.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body;
      if (typeof sent !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const body = JSON.parse(sent) as typeof document;
      // After reset the edge carries no style, exactly as loaded.
      expect(body.edges.find((edge) => edge.id === 'e_1')).toEqual(document.edges.find((edge) => edge.id === 'e_1'));
    });

    it('switches a selected connector’s routing mode from the properties panel, and reset route clears pinned waypoints', async () => {
      const put = vi.fn<typeof fetch>(() => Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } })));
      vi.stubGlobal('fetch', put);
      const { container, canvas } = renderEditor();
      const hit = container.querySelector('g[data-id="e_1"] polyline.hit-area');
      if (!hit) {
        throw new Error('e_1 not drawn');
      }
      click(canvas, hit);
      const routing = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Routing' });
      expect(routing.value).toBe('orthogonal');
      expect([...routing.options].map((option) => option.value)).toEqual(['orthogonal', 'straight', 'curved']);

      fireEvent.change(routing, { target: { value: 'curved' } });
      expect(routing.value).toBe('curved');

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const sent = put.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body;
      if (typeof sent !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const saved = JSON.parse(sent) as typeof document;
      expect(saved.edges.find((edge) => edge.id === 'e_1')?.routing).toEqual({ mode: 'curved' });

      // An edge whose waypoints are cleared returns to derived routing; a pinned edge offers Reset route.
      const pinned = document.edges.find((edge) => edge.id === 'e_3');
      expect(pinned?.routing?.waypoints).not.toBeUndefined();
      const hit3 = container.querySelector('g[data-id="e_3"] polyline.hit-area');
      if (!hit3) {
        throw new Error('e_3 not drawn');
      }
      click(canvas, hit3);
      fireEvent.click(screen.getByRole('button', { name: 'Reset route' }));
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());
      const last = put.mock.calls.at(-1)?.[1]?.body;
      if (typeof last !== 'string') {
        throw new Error('Save sent no JSON body');
      }
      const lastDoc = JSON.parse(last) as typeof document;
      expect(lastDoc.edges.find((edge) => edge.id === 'e_3')?.routing?.waypoints).toBeUndefined();
      expect(lastDoc.edges.find((edge) => edge.id === 'e_3')?.routing?.mode).toBe('orthogonal');
    });

    it('switches a selected connector’s kind from the command palette with exactly the model the panel picker writes', async () => {
      async function switchKind(usePalette: boolean): Promise<Document['edges'][number] | undefined> {
        const saves: string[] = [];
        vi.stubGlobal(
          'fetch',
          vi.fn<typeof fetch>((_url, init) => {
            saves.push(init?.body as string);
            return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } }));
          }),
        );
        const { container, canvas, unmount } = renderEditor();
        const hit = container.querySelector('g[data-id="e_1"] polyline.hit-area');
        if (!hit) {
          throw new Error('e_1 not drawn');
        }
        click(canvas, hit);
        if (usePalette) {
          fireEvent.keyDown(window, { key: 'k', metaKey: true });
          const search = screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
          fireEvent.change(search, { target: { value: 'set connector kind to data' } });
          fireEvent.keyDown(search, { key: 'Enter' });
        } else {
          const kind = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Kind' });
          fireEvent.change(kind, { target: { value: 'data' } });
        }
        fireEvent.click(screen.getByRole('button', { name: 'Save' }));
        await act(() => Promise.resolve());
        unmount();
        const body = JSON.parse(saves[saves.length - 1] ?? 'null') as typeof document;
        return body.edges.find((edge) => edge.id === 'e_1');
      }
      const byPanel = await switchKind(false);
      const byPalette = await switchKind(true);
      expect(byPanel).toMatchObject({ kind: 'data' });
      expect(byPalette).toEqual(byPanel);
    });

    describe('command palette', () => {
      const openPalette = (from: Element | Window = window): HTMLInputElement => {
        fireEvent.keyDown(from, { key: 'k', metaKey: true });
        return screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
      };
      const options = () => within(screen.getByRole('listbox', { name: 'Commands' })).getAllByRole('option').map((option) => ({ name: option.getAttribute('aria-label'), disabled: option.getAttribute('aria-disabled') === 'true' }));
      const runCommand = (query: string, expected: string): void => {
        const search = openPalette();
        fireEvent.change(search, { target: { value: query } });
        const active = search.getAttribute('aria-activedescendant');
        expect(active === null ? null : globalThis.document.getElementById(active)?.getAttribute('aria-label')).toBe(expected);
        fireEvent.keyDown(search, { key: 'Enter' });
        expect(screen.queryByRole('dialog', { name: 'Command palette' })).toBeNull();
      };
      // Everything each save sent, so two editors' models can be compared as the server would receive them.
      const saves: string[] = [];
      beforeEach(() => {
        saves.length = 0;
        vi.stubGlobal('fetch', vi.fn<typeof fetch>((_url, init) => {
          saves.push(init?.body as string);
          return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } }));
        }));
      });
      async function savedModel(edit: (editor: ReturnType<typeof renderEditor>) => void): Promise<unknown> {
        const editor = renderEditor();
        edit(editor);
        fireEvent.click(screen.getByRole('button', { name: 'Save' }));
        await act(() => Promise.resolve());
        editor.unmount();
        return JSON.parse(saves[saves.length - 1] ?? 'null');
      }

      it('opens on Ctrl+K and lists every editor action with its shortcut, disabled where it cannot run', () => {
        renderEditor();
        const search = openPalette();
        expect(screen.getByRole('dialog', { name: 'Command palette' })).toBeTruthy();
        expect(globalThis.document.activeElement).toBe(search);
        // Nothing selected and nothing done yet: history and the selection commands are listed but disabled; the Tab walk
        // can still start at either end.
        expect(options()).toEqual([
          { name: 'Undo, Ctrl+Z', disabled: true },
          { name: 'Redo, Ctrl+Shift+Z', disabled: true },
          { name: 'Edit label, Enter', disabled: true },
          { name: 'Delete, Delete', disabled: true },
          { name: 'Duplicate, Ctrl+D', disabled: true },
          { name: 'Copy, Ctrl+C', disabled: true },
          { name: 'Cut, Ctrl+X', disabled: true },
          { name: 'Paste, Ctrl+V', disabled: false },
          { name: 'Connect selected shapes, L', disabled: true },
          { name: 'Add fly-through keyframe', disabled: false },
          { name: 'Add service', disabled: false },
          { name: 'Select tool, V', disabled: false },
          { name: 'Connector tool, C', disabled: false },
          { name: 'Shapes panel, S', disabled: false },
          { name: 'Select next node, Tab', disabled: false },
          { name: 'Select previous node, Shift+Tab', disabled: false },
          { name: 'Nudge left 1px, ←', disabled: true },
          { name: 'Nudge left 10px, Shift+←', disabled: true },
          { name: 'Nudge right 1px, →', disabled: true },
          { name: 'Nudge right 10px, Shift+→', disabled: true },
          { name: 'Nudge up 1px, ↑', disabled: true },
          { name: 'Nudge up 10px, Shift+↑', disabled: true },
          { name: 'Nudge down 1px, ↓', disabled: true },
          { name: 'Nudge down 10px, Shift+↓', disabled: true },
          { name: 'Bring to front, Ctrl+Alt+]', disabled: true },
          { name: 'Bring forward, Ctrl+]', disabled: true },
          { name: 'Send backward, Ctrl+[', disabled: true },
          { name: 'Send to back, Ctrl+Alt+[', disabled: true },
          { name: 'Group, Ctrl+G', disabled: true },
          { name: 'Ungroup, Ctrl+Shift+G', disabled: true },
          { name: 'Tidy layout (Layered)', disabled: false },
          { name: 'Tidy layout (Stress)', disabled: false },
          { name: 'Tidy layout (Tree)', disabled: false },
          { name: 'Align left', disabled: true },
          { name: 'Align centre', disabled: true },
          { name: 'Align right', disabled: true },
          { name: 'Align top', disabled: true },
          { name: 'Align middle', disabled: true },
          { name: 'Align bottom', disabled: true },
          { name: 'Distribute horizontally', disabled: true },
          { name: 'Distribute vertically', disabled: true },
          { name: 'Fit to contents', disabled: true },
          { name: 'Zoom in, Ctrl+=', disabled: false },
          { name: 'Zoom out, Ctrl+-', disabled: false },
          { name: 'Fit to screen, Ctrl+0', disabled: false },
          { name: 'Zoom to 100%, Ctrl+1', disabled: false },
          // The example carries a tour, so Present can run.
          { name: 'Present fly-through, Ctrl+Enter', disabled: false },
          { name: 'Toggle Tour panel', disabled: false },
          { name: 'Toggle History panel', disabled: false },
          { name: 'Toggle Review panel', disabled: false },
          { name: "Toggle Text (DSL)", disabled: false },
          { name: "Toggle grid, Ctrl+'", disabled: false },
          { name: 'Focus next region, F6', disabled: false },
          { name: 'Focus previous region, Shift+F6', disabled: false },
          // Help's entries are registry records too, so the palette finds them (menus show them under Help).
          { name: 'Keyboard shortcuts, ?', disabled: false },
          { name: 'About', disabled: false },
          { name: 'Save', disabled: false },
          { name: 'Rename diagram', disabled: false },
          { name: 'New flowchart diagram', disabled: false },
          { name: 'New architecture diagram', disabled: false },
          { name: 'New freeform diagram', disabled: false },
          { name: 'New sequence diagram', disabled: false },
          { name: 'New ER diagram', disabled: false },
          { name: 'New UML class diagram', disabled: false },
          { name: 'New BPMN process', disabled: false },
          { name: 'New Swimlane diagram', disabled: false },
          { name: 'New Org chart', disabled: false },
          { name: 'Import SQL…', disabled: false },
          { name: 'Import Mermaid…', disabled: false },
          { name: 'Import draw.io…', disabled: false },
          { name: 'Import Visio…', disabled: false },
          { name: 'Back to documents', disabled: false },
          { name: 'Export PNG', disabled: false },
          { name: 'Export SVG', disabled: false },
          { name: 'Export PDF…', disabled: false },
          { name: 'Export Mermaid', disabled: false },
          { name: 'Copy as Mermaid', disabled: false },
          { name: 'Export DSL', disabled: false },
          { name: 'Export fly-through (HTML)', disabled: false },
        ]);
        // The highlight starts on the first action that can run.
        expect(globalThis.document.getElementById(search.getAttribute('aria-activedescendant') ?? '')?.getAttribute('aria-label')).toBe('Paste, Ctrl+V');
      });

      it('enables the selection commands once something is selected', () => {
        const { canvas, shape } = renderEditor();
        click(canvas, shape);
        openPalette();
        const disabled = (name: string) => options().find((option) => option.name === name)?.disabled;
        expect([disabled('Edit label, Enter'), disabled('Delete, Delete'), disabled('Duplicate, Ctrl+D'), disabled('Nudge up 1px, ↑'), disabled('Bring to front, Ctrl+Alt+]')]).toEqual([false, false, false, false, false]);
        expect(disabled('Align left')).toBe(true);
      });

      it('nudges, brings to front and deletes from the palette with exactly the model its shortcut writes', async () => {
        const untouched = await savedModel(() => undefined);
        const cases: { select: string; key: Parameters<typeof fireEvent.keyDown>[1]; query: string; option: string }[] = [
          { select: 'n_web', key: { key: 'ArrowRight' }, query: 'nudge right 1px', option: 'Nudge right 1px, →' },
          { select: 'n_db', key: { key: '‘', code: 'BracketRight', metaKey: true, altKey: true }, query: 'front', option: 'Bring to front, Ctrl+Alt+]' },
          { select: 'n_api', key: { key: 'Delete' }, query: 'delete', option: 'Delete, Delete' },
        ];
        for (const { select, key, query, option } of cases) {
          const byKey = await savedModel(({ container, canvas }) => {
            click(canvas, shapeOf(container, select));
            fireEvent.keyDown(canvas, key);
          });
          const byPalette = await savedModel(({ container, canvas }) => {
            click(canvas, shapeOf(container, select));
            runCommand(query, option);
          });
          expect(byKey).not.toEqual(untouched);
          expect(byPalette).toEqual(byKey);
        }
      });

      it('records one undo step per palette command, and undoes and redoes from the palette as the keys do', () => {
        const { container, canvas, shape } = renderEditor();
        click(canvas, shape);
        // A step before, so an undo that went past the palette's step would show.
        fireEvent.keyDown(canvas, { key: 'ArrowDown' });
        runCommand('nudge right 10px', 'Nudge right 10px, Shift+→');
        expect(placement(container, 'n_web')).toBe('translate(50 41)');
        undo();
        expect(placement(container, 'n_web')).toBe('translate(40 41)');
        runCommand('redo', 'Redo, Ctrl+Shift+Z');
        expect(placement(container, 'n_web')).toBe('translate(50 41)');
        runCommand('undo', 'Undo, Ctrl+Z');
        expect(placement(container, 'n_web')).toBe('translate(40 41)');
        runCommand('undo', 'Undo, Ctrl+Z');
        expect(placement(container, 'n_web')).toBe('translate(40 40)');
      });

      it('closes on Escape with nothing run and focus back on the canvas, ring showing', () => {
        const { container, canvas, shape } = renderEditor();
        click(canvas, shape);
        expect(canvas.hasAttribute('data-pointer-focus')).toBe(true);
        const search = openPalette(canvas);
        fireEvent.change(search, { target: { value: 'delete' } });
        fireEvent.keyDown(search, { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: 'Command palette' })).toBeNull();
        expect(globalThis.document.activeElement).toBe(canvas);
        expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
        expect(container.querySelector('g[data-id="n_web"]')).not.toBeNull();
      });

      it('moves the highlight with the arrow keys and runs it on Enter, keeping single-letter tools out of the search', () => {
        renderEditor();
        const connector = screen.getByRole('button', { name: 'Connector' });
        const search = openPalette();
        // Typing C searches; it does not pick the connector tool behind the palette.
        fireEvent.keyDown(search, { key: 'c' });
        fireEvent.change(search, { target: { value: 'tool' } });
        expect(connector.getAttribute('aria-pressed')).toBe('false');
        fireEvent.keyDown(search, { key: 'ArrowDown' });
        fireEvent.keyDown(search, { key: 'Enter' });
        expect(connector.getAttribute('aria-pressed')).toBe('true');
      });

      it('opens from a text field in the chrome, but not from inside the label editor', () => {
        const { container, canvas, shape } = renderEditor();
        openPalette(screen.getByRole('combobox', { name: 'Theme' }));
        fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
        click(canvas, shape);
        runCommand('edit label', 'Edit label, Enter');
        const editor = container.querySelector('.label-editor');
        if (!editor) {
          throw new Error('label editor not open');
        }
        fireEvent.keyDown(editor, { key: 'k', metaKey: true });
        expect(screen.queryByRole('dialog', { name: 'Command palette' })).toBeNull();
      });

      it('lists only what a read-only preview can do', () => {
        renderEditor(true);
        openPalette();
        expect(options().map((option) => option.name)).toEqual(['Zoom in, Ctrl+=', 'Zoom out, Ctrl+-', 'Fit to screen, Ctrl+0', 'Zoom to 100%, Ctrl+1', "Toggle grid, Ctrl+'", 'Focus next region, F6', 'Focus previous region, Shift+F6', 'Present fly-through, Ctrl+Enter', 'Keyboard shortcuts, ?', 'About', 'Export PNG', 'Export SVG', 'Export PDF…', 'Export Mermaid', 'Copy as Mermaid', 'Export DSL', 'Export fly-through (HTML)']);
      });

      it('opens the Import SQL paste box from the palette', () => {
        renderEditor();
        runCommand('import sql', 'Import SQL…');
        expect(screen.getByRole('dialog', { name: 'Import SQL' })).toBeTruthy();
        fireEvent.keyDown(screen.getByRole('textbox', { name: 'SQL' }), { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: 'Import SQL' })).toBeNull();
      });

      it('opens the Import Mermaid paste box from the palette', () => {
        renderEditor();
        runCommand('import mermaid', 'Import Mermaid…');
        expect(screen.getByRole('dialog', { name: 'Import Mermaid' })).toBeTruthy();
        fireEvent.keyDown(screen.getByRole('textbox', { name: 'Mermaid' }), { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: 'Import Mermaid' })).toBeNull();
      });

      it('opens the Import draw.io dialog from the palette', () => {
        renderEditor();
        runCommand('import draw.io', 'Import draw.io…');
        expect(screen.getByRole('dialog', { name: 'Import draw.io' })).toBeTruthy();
        fireEvent.keyDown(screen.getByRole('textbox', { name: 'draw.io XML' }), { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: 'Import draw.io' })).toBeNull();
      });

      it('opens the Import Visio dialog from the palette', () => {
        renderEditor();
        runCommand('import visio', 'Import Visio…');
        expect(screen.getByRole('dialog', { name: 'Import Visio' })).toBeTruthy();
        fireEvent.keyDown(screen.getByLabelText('Visio file'), { key: 'Escape' });
        expect(screen.queryByRole('dialog', { name: 'Import Visio' })).toBeNull();
      });

      it('finds Import Visio from the words people look for it by', () => {
        renderEditor();
        runCommand('lucidchart', 'Import Visio…');
        expect(screen.getByRole('dialog', { name: 'Import Visio' })).toBeTruthy();
      });

      describe('Export SQL', () => {
        const withTable = parseDocument({
          ...orderService,
          nodes: [
            ...orderService.nodes,
            { id: 'n_users', kind: 'entity', label: 'Users', shape: 'custom:er/entity', geometry: { x: 900, y: 0, w: 200, h: 90 }, props: { columns: ['PK Id int not null'] } },
          ],
        });

        it('is offered only for a diagram that holds tables', () => {
          renderEditor();
          openPalette();
          expect(options().some((option) => option.name?.startsWith('Export SQL') === true)).toBe(false);
        });

        it('downloads the DDL for the chosen dialect', async () => {
          const createUrl = vi.fn(() => 'blob:sql');
          Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: vi.fn() });
          const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
          const requests: { url: string; body: string }[] = [];
          vi.stubGlobal('fetch', vi.fn<typeof fetch>((url, init) => {
            requests.push({ url: typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url, body: init?.body as string });
            return Promise.resolve(new Response('create table "Users" (...)', { status: 200 }));
          }));

          renderEditor(false, withTable);
          runCommand('export sql postgres', 'Export SQL: PostgreSQL');

          await vi.waitFor(() => {
            expect(click).toHaveBeenCalled();
          });
          // The editor also asks /api/health what the server can do; only the export is under test here.
          const exported = requests.filter((request) => request.url.startsWith('/api/export/sql'));
          expect(exported.map((request) => request.url)).toEqual(['/api/export/sql?dialect=postgres']);
          expect(JSON.parse(exported[0]?.body ?? '{}')).toMatchObject({ id: document.id });
          expect(createUrl).toHaveBeenCalled();
        });
      });

      describe('Export Mermaid', () => {
        const MERMAID = 'flowchart TD\n    a["Web app"]\n';
        const installClipboard = (writeText: (text: string) => Promise<void>) => {
          Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
        };
        const stubExport = (status = 200, warnings?: string, notes?: string[]) => {
          const requests: string[] = [];
          vi.stubGlobal('fetch', vi.fn<typeof fetch>((url) => {
            requests.push(typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url);
            return Promise.resolve(new Response(status === 200 ? MERMAID : JSON.stringify({ detail: 'the server said no' }), { status, headers: { ...(warnings === undefined ? {} : { 'X-Draughtsman-Export-Warnings': warnings }), ...(notes === undefined ? {} : { 'X-Draughtsman-Export-Notes': encodeURIComponent(JSON.stringify(notes)) }) } }));
          }));
          return requests;
        };
        afterEach(() => {
          Reflect.deleteProperty(navigator, 'clipboard');
        });

        it('is offered for any diagram with content, and only then', () => {
          renderEditor();
          openPalette();
          expect(options().some((option) => option.name === 'Export Mermaid' && !option.disabled)).toBe(true);
          expect(options().some((option) => option.name === 'Copy as Mermaid' && !option.disabled)).toBe(true);
          expect(options().some((option) => option.name === 'Export DSL' && !option.disabled)).toBe(true);
        });

        // P8: Export Mermaid is a file, with a list of what it simplified; Copy as Mermaid is the clipboard; Export DSL is a file.
        const captureDownloads = () => {
          const names: string[] = [];
          Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:x'), revokeObjectURL: vi.fn() });
          vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
            names.push(this.download);
          });
          return names;
        };

        it('Export Mermaid saves a .mmd file named for the diagram and, when faithful, says only that it saved', async () => {
          const names = captureDownloads();
          stubExport();
          renderEditor();
          runCommand('export mermaid', 'Export Mermaid');
          await screen.findByText('Saved order-service.mmd.');
          expect(names).toEqual(['order-service.mmd']);
          expect(screen.queryByRole('dialog', { name: 'Export Mermaid' })).toBeNull();
        });

        it('Export Mermaid lists what was simplified, so a lossy file is never silent', async () => {
          const names = captureDownloads();
          stubExport(200, '2', ['n_a: shape \'document\' has no Mermaid equivalent; drawn as a rectangle', 'e_1: arrowhead \'crowsFoot\' has no Mermaid equivalent; drawn as a plain arrow']);
          renderEditor();
          runCommand('export mermaid', 'Export Mermaid');
          const dialog = await screen.findByRole('dialog', { name: 'Export Mermaid' });
          expect(names).toEqual(['order-service.mmd']);
          expect(dialog.textContent).toContain('Saved as order-service.mmd. Mermaid has no spelling for 2 things');
          expect(within(within(dialog).getByRole('list', { name: 'Simplified in the Mermaid export' })).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
            "n_a: shape 'document' has no Mermaid equivalent; drawn as a rectangle",
            "e_1: arrowhead 'crowsFoot' has no Mermaid equivalent; drawn as a plain arrow",
          ]);
        });

        it('Export DSL saves the document\'s text as a .dsl file', async () => {
          const names = captureDownloads();
          const requests = stubExport();
          renderEditor();
          runCommand('export dsl', 'Export DSL');
          await screen.findByText('Saved order-service.dsl.');
          expect(names).toEqual(['order-service.dsl']);
          expect(requests.filter((url) => url === '/api/export/dsl')).toHaveLength(1);
        });

        it('copies the exported text to the clipboard and says so, with no dialog', async () => {
          const written: string[] = [];
          installClipboard((text) => {
            written.push(text);
            return Promise.resolve();
          });
          const requests = stubExport();

          renderEditor();
          runCommand('copy as mermaid', 'Copy as Mermaid');

          await vi.waitFor(() => {
            expect(written).toEqual([MERMAID]);
          });
          expect(requests.filter((url) => url.startsWith('/api/export/mermaid'))).toEqual(['/api/export/mermaid']);
          await screen.findByText('Copied the diagram as Mermaid.');
          expect(screen.queryByRole('dialog', { name: 'Export Mermaid' })).toBeNull();
        });

        it('says the copy is lossy, not that it copied the diagram, when the server had to simplify something', async () => {
          const written: string[] = [];
          installClipboard((text) => {
            written.push(text);
            return Promise.resolve();
          });
          stubExport(200, '2');

          renderEditor();
          runCommand('copy as mermaid', 'Copy as Mermaid');

          await screen.findByText('Copied as Mermaid, but it is lossy: 2 things have no Mermaid equivalent and were simplified.');
          expect(screen.getByRole('dialog', { name: 'Export Mermaid' }).textContent).toContain('Copied to the clipboard. Mermaid has no spelling for 2 things');
          expect(written).toEqual([MERMAID]);
          expect(screen.queryByText('Copied the diagram as Mermaid.')).toBeNull();
        });

        it('shows the text in a dialog when the browser refuses the clipboard, and closes it on Escape', async () => {
          installClipboard(() => Promise.reject(new Error('NotAllowedError')));
          stubExport();

          renderEditor();
          runCommand('copy as mermaid', 'Copy as Mermaid');

          const dialog = await screen.findByRole('dialog', { name: 'Export Mermaid' });
          expect(within(dialog).getByRole<HTMLTextAreaElement>('textbox', { name: 'Mermaid text' }).value).toBe(MERMAID);
          expect(screen.queryByText('Copied the diagram as Mermaid.')).toBeNull();
          fireEvent.keyDown(dialog, { key: 'Escape' });
          expect(screen.queryByRole('dialog', { name: 'Export Mermaid' })).toBeNull();
        });

        it('says so, with no dialog, when the export itself fails', async () => {
          const written: string[] = [];
          installClipboard((text) => {
            written.push(text);
            return Promise.resolve();
          });
          stubExport(422);

          renderEditor();
          runCommand('copy as mermaid', 'Copy as Mermaid');

          await screen.findByText('Couldn’t export Mermaid: the server said no');
          expect(screen.queryByRole('dialog', { name: 'Export Mermaid' })).toBeNull();
          expect(written).toEqual([]);
        });
      });

      // Rendered exports draw the diagram and nothing else; whatever an older server's About answer says about a licence is ignored.
      describe('rendered exports carry no notice', () => {
        const readBlob = (blob: Blob | undefined): Promise<string> =>
          new Promise((resolve, reject) => {
            if (blob === undefined) {
              reject(new Error('nothing was downloaded'));
              return;
            }
            const reader = new FileReader();
            reader.onload = () => {
              resolve(reader.result as string);
            };
            reader.onerror = () => {
              reject(new Error('unreadable'));
            };
            reader.readAsText(blob);
          });
        /** Captures what each download is made of, and answers /api/about the way an earlier build did, licence state and all. */
        const stubDownloads = () => {
          const blobs: Blob[] = [];
          const names: string[] = [];
          Object.assign(URL, { createObjectURL: vi.fn((blob: Blob) => { blobs.push(blob); return 'blob:x'; }), revokeObjectURL: vi.fn() });
          vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
            names.push(this.download);
          });
          vi.stubGlobal('fetch', vi.fn<typeof fetch>((url) => {
            const address = typeof url === 'string' ? url : url instanceof URL ? url.toString() : url.url;
            if (address === '/api/about') {
              return Promise.resolve(new Response(JSON.stringify({ version: '0.1.0', buildDate: '2026-10-05', licenceState: 'unlicensed', supportContact: 'draughtsman@overpass.co.uk', noticesUrl: '/n' }), { status: 200 }));
            }
            return Promise.resolve(new Response(address === '/api/export/dsl' ? 'draughtsman 1' : '{}', { status: 200 }));
          }));
          return { blobs, names };
        };

        it('Export SVG is the picture alone', async () => {
          const { blobs, names } = stubDownloads();
          renderEditor();
          runCommand('export svg', 'Export SVG');
          await vi.waitFor(() => {
            expect(names).toEqual(['order-service.svg']);
          });

          const svg = await readBlob(blobs[0]);
          expect(svg).not.toMatch(/evaluation|notice/i);
          expect(svg).toContain('data-id="n_web"');
        });

        it('Export fly-through is the player alone', async () => {
          const { blobs, names } = stubDownloads();
          renderEditor();
          runCommand('export fly-through', 'Export fly-through (HTML)');
          await vi.waitFor(() => {
            expect(names).toEqual(['order-service-fly-through.html']);
          });

          const html = await readBlob(blobs[0]);
          expect(html).not.toMatch(/ft-notice|evaluation-notice|Evaluation copy/);
        });

        it('Export DSL is the document\'s own text', async () => {
          const { blobs, names } = stubDownloads();
          renderEditor();
          runCommand('export dsl', 'Export DSL');
          await vi.waitFor(() => {
            expect(names).toEqual(['order-service.dsl']);
          });

          expect(await readBlob(blobs[0])).toBe('draughtsman 1');
        });

        it('Present draws no notice line', async () => {
          stubDownloads();
          renderEditor();
          fireEvent.keyDown(window, { key: 'Enter', ctrlKey: true });

          await vi.waitFor(() => {
            expect(globalThis.document.querySelector('.present')).not.toBeNull();
          });
          expect(globalThis.document.querySelector('.present-notice')).toBeNull();
          expect(globalThis.document.querySelector('.present')?.hasAttribute('data-notice')).toBe(false);
        });
      });

      describe('the top bar menu and keyboard access (checklist item 28)', () => {
        const openMenu = (): HTMLElement => {
          fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
          return screen.getByRole('menu', { name: 'Menu' });
        };
        const menuItems = () => within(openMenu()).getAllByRole('menuitem').map((item) => ({ name: item.getAttribute('aria-label'), disabled: item.getAttribute('aria-disabled') === 'true' }));
        const runMenu = (name: string): void => {
          fireEvent.click(within(openMenu()).getByRole('menuitem', { name }));
          expect(screen.queryByRole('menu')).toBeNull();
        };
        const byName = (a: { name: string | null }, b: { name: string | null }) => (a.name ?? '').localeCompare(b.name ?? '');

        it('lists every palette action, each with the palette’s shortcut and state, and only those, plus the palette itself under Help', () => {
          const { container, canvas } = renderEditor();
          // One selected node, so the lists mix enabled and disabled entries.
          click(canvas, shapeOf(container, 'n_web'));
          openPalette();
          const palette = options();
          fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });
          const menu = menuItems();
          const help = within(screen.getByRole('group', { name: 'Help' })).getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'));
          expect(help).toEqual(['Command palette, Ctrl+K', 'Keyboard shortcuts, ?', 'About']);
          // Help's own registry records (the shortcut sheet, About) are listed by the palette and sit under Help in the menu.
          expect(menu.filter((item) => !help.includes(item.name)).sort(byName)).toEqual(palette.filter((option) => !help.includes(option.name)).sort(byName));
          expect(help.filter((name) => name !== 'Command palette, Ctrl+K').every((name) => palette.some((option) => option.name === name))).toBe(true);
          expect(within(screen.getByRole('menu')).getAllByRole('group').map((group) => group.getAttribute('aria-label'))).toEqual(['File', 'Edit', 'View', 'Arrange', 'Help']);
        });

        it('runs delete, group, bring to front, align and undo from the menu with exactly the model the shortcut, palette or panel writes', async () => {
          const untouched = await savedModel(() => undefined);
          const selectTwo = ({ container, canvas }: ReturnType<typeof renderEditor>): void => {
            // Not already left-aligned, as n_web and n_db are.
            click(canvas, shapeOf(container, 'n_web'));
            shiftClick(canvas, shapeOf(container, 'n_cache'));
          };
          const cases: { name: string; setUp: (editor: ReturnType<typeof renderEditor>) => void; direct: (editor: ReturnType<typeof renderEditor>) => void; query: string; option: string }[] = [
            { name: 'Delete', setUp: ({ container, canvas }) => { click(canvas, shapeOf(container, 'n_api')); }, direct: ({ canvas }) => fireEvent.keyDown(canvas, { key: 'Delete' }), query: 'delete', option: 'Delete, Delete' },
            { name: 'Group', setUp: ({ container, canvas }) => { click(canvas, shapeOf(container, 'n_cache')); }, direct: ({ canvas }) => fireEvent.keyDown(canvas, { key: 'g', metaKey: true }), query: 'group', option: 'Group, Ctrl+G' },
            { name: 'Bring to front', setUp: ({ container, canvas }) => { click(canvas, shapeOf(container, 'n_db')); }, direct: ({ canvas }) => fireEvent.keyDown(canvas, { key: '‘', code: 'BracketRight', metaKey: true, altKey: true }), query: 'front', option: 'Bring to front, Ctrl+Alt+]' },
            { name: 'Align left', setUp: selectTwo, direct: () => fireEvent.click(screen.getByRole('button', { name: 'Align left' })), query: 'align left', option: 'Align left' },
            { name: 'Undo', setUp: ({ container, canvas }) => { click(canvas, shapeOf(container, 'n_web')); fireEvent.keyDown(canvas, { key: 'ArrowDown' }); fireEvent.keyDown(canvas, { key: 'ArrowRight' }); }, direct: () => undo(), query: 'undo', option: 'Undo, Ctrl+Z' },
          ];
          // Group mints a random container id (model/grouping.ts); every other byte must match.
          const freshIdsNamed = (model: unknown): unknown => JSON.parse(JSON.stringify(model).replace(/"c_[a-z0-9]{4,}"/g, '"c_new"'));
          for (const { name, setUp, direct, query, option } of cases) {
            const byDirect = await savedModel((editor) => {
              setUp(editor);
              direct(editor);
            });
            const byPalette = await savedModel((editor) => {
              setUp(editor);
              runCommand(query, option);
            });
            const byMenu = await savedModel((editor) => {
              setUp(editor);
              runMenu(option);
            });
            // Undo leaves one nudge of two, so each case must have changed the untouched model and all three agree.
            expect({ name, changed: JSON.stringify(byDirect) !== JSON.stringify(untouched) }).toEqual({ name, changed: true });
            expect({ name, model: freshIdsNamed(byPalette) }).toEqual({ name, model: freshIdsNamed(byDirect) });
            expect({ name, model: freshIdsNamed(byMenu) }).toEqual({ name, model: freshIdsNamed(byDirect) });
          }
        });

        it('opens the palette from Help, and hands focus to the canvas before running an item chosen by key', () => {
          const { container, canvas } = renderEditor();
          click(canvas, shapeOf(container, 'n_web'));
          const button = screen.getByRole('button', { name: 'Menu' });
          // ArrowUp from the closed button opens the menu on its last item (About); Help's first is the palette.
          fireEvent.keyDown(button, { key: 'ArrowUp' });
          expect(globalThis.document.activeElement?.getAttribute('aria-label')).toBe('About');
          fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'ArrowUp' });
          fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'ArrowUp' });
          expect(globalThis.document.activeElement?.getAttribute('aria-label')).toBe('Command palette, Ctrl+K');
          fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'Enter' });
          expect(screen.getByRole('dialog', { name: 'Command palette' })).toBeTruthy();
          fireEvent.keyDown(screen.getByRole('combobox', { name: 'Search commands' }), { key: 'Escape' });

          fireEvent.keyDown(button, { key: 'Enter' });
          // Export shares its first letter with Edit label and File precedes Edit, so 'e' cycles through Export PNG,
          // Export SVG, Export PDF, Export Mermaid, Export DSL and Export fly-through (the example has a tour) before reaching Edit label.
          const typeAhead = (key: string): void => {
            fireEvent.keyDown(globalThis.document.activeElement ?? button, { key });
          };
          typeAhead('e');
          typeAhead('e');
          typeAhead('e');
          typeAhead('e');
          typeAhead('e');
          typeAhead('e');
          typeAhead('e');
          expect(globalThis.document.activeElement?.getAttribute('aria-label')).toBe('Edit label, Enter');
          fireEvent.keyDown(globalThis.document.activeElement ?? button, { key: 'Enter' });
          expect(container.querySelector('.label-editor')).not.toBeNull();
        });

        it('cycles F6 through top bar, rail, shape panel while open, canvas and properties panel, and Shift+F6 back', () => {
          const { canvas } = renderEditor();
          // The panel's first control is its Properties/Text switch, whatever is selected.
          const panel = within(screen.getByRole('region', { name: 'Properties' })).getByRole('button', { name: 'Properties' });
          const menuButton = screen.getByRole('button', { name: 'Menu' });
          const selectTool = screen.getByRole('button', { name: 'Select' });
          const f6 = (shiftKey = false): Element => {
            fireEvent.keyDown(globalThis.document.activeElement ?? window, { key: 'F6', shiftKey });
            return globalThis.document.activeElement ?? globalThis.document.body;
          };
          canvas.focus();
          expect(f6()).toBe(panel);
          expect(f6()).toBe(menuButton);
          expect(f6()).toBe(selectTool);
          expect(f6()).toBe(canvas);
          expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
          expect(f6(true)).toBe(selectTool);
          expect(f6(true)).toBe(menuButton);
          expect(f6(true)).toBe(panel);
          expect(f6(true)).toBe(canvas);

          // The shape panel joins the cycle while open, between the rail and the canvas, and F6 works from its search.
          fireEvent.keyDown(window, { key: 's' });
          const search = screen.getByRole('searchbox', { name: 'Search shapes' });
          expect(globalThis.document.activeElement).toBe(search);
          expect(f6()).toBe(canvas);
          expect(f6(true)).toBe(search);
          expect(f6(true)).toBe(selectTool);
        });

        it('leaves F6 alone while the palette is open, so focus stays in it', () => {
          renderEditor();
          const search = openPalette();
          fireEvent.keyDown(search, { key: 'F6' });
          expect(globalThis.document.activeElement).toBe(search);
          expect(screen.getByRole('dialog', { name: 'Command palette' })).toBeTruthy();
        });

        it('returns focus to the canvas, ring showing, on Escape from the rail, top bar or properties panel, and leaves the canvas’s own Escape alone', () => {
          const { container, canvas } = renderEditor();
          click(canvas, shapeOf(container, 'n_web'));
          for (const control of [screen.getByRole('button', { name: 'Select' }), screen.getByRole('combobox', { name: 'Theme' }), screen.getByRole('button', { name: 'Bring to front' }), screen.getByRole('button', { name: 'Zoom in' })]) {
            control.focus();
            expect(fireEvent.keyDown(control, { key: 'Escape' })).toBe(false);
            expect(globalThis.document.activeElement).toBe(canvas);
            expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
          }
          // On the canvas Escape is not the shell's: nothing claims it and the selection stays.
          expect(fireEvent.keyDown(canvas, { key: 'Escape' })).toBe(true);
          expect(container.querySelector('.selection-node')).not.toBeNull();
        });

        it('moves along the rail with Up and Down, wrapping, and along a properties row with Left and Right within that row', () => {
          const { container, canvas } = renderEditor();
          const rail = ['Select', 'Add service', 'Shapes', 'Connector', 'Undo', 'Redo'].map((name) => screen.getByRole('button', { name }));
          rail[0]?.focus();
          for (const expected of [...rail.slice(1), rail[0]]) {
            fireEvent.keyDown(globalThis.document.activeElement ?? canvas, { key: 'ArrowDown' });
            expect(globalThis.document.activeElement).toBe(expected);
          }
          fireEvent.keyDown(globalThis.document.activeElement ?? canvas, { key: 'ArrowUp' });
          expect(globalThis.document.activeElement).toBe(rail[5]);

          click(canvas, shapeOf(container, 'n_web'));
          shiftClick(canvas, shapeOf(container, 'n_db'));
          const left = screen.getByRole('button', { name: 'Align left' });
          left.focus();
          fireEvent.keyDown(left, { key: 'ArrowRight' });
          expect(globalThis.document.activeElement).toBe(screen.getByRole('button', { name: 'Align centre' }));
          fireEvent.keyDown(globalThis.document.activeElement ?? left, { key: 'ArrowLeft' });
          fireEvent.keyDown(globalThis.document.activeElement ?? left, { key: 'ArrowLeft' });
          // Wraps within Align, never into Distribute below it.
          expect(globalThis.document.activeElement).toBe(screen.getByRole('button', { name: 'Align bottom' }));
        });

        it('undoes and redoes from the rail, whose buttons stay focusable but inert while they cannot run', () => {
          const { container, canvas, shape } = renderEditor();
          const undoButton = screen.getByRole('button', { name: 'Undo' });
          const redoButton = screen.getByRole('button', { name: 'Redo' });
          expect([undoButton.getAttribute('aria-disabled'), redoButton.getAttribute('aria-disabled')]).toEqual(['true', 'true']);
          fireEvent.click(undoButton);
          click(canvas, shape);
          fireEvent.keyDown(canvas, { key: 'ArrowRight' });
          expect(placement(container, 'n_web')).toBe('translate(41 40)');
          expect(undoButton.getAttribute('aria-disabled')).toBe('false');
          fireEvent.click(undoButton);
          expect(placement(container, 'n_web')).toBe('translate(40 40)');
          expect([undoButton.getAttribute('aria-disabled'), redoButton.getAttribute('aria-disabled')]).toEqual(['true', 'false']);
          fireEvent.click(redoButton);
          expect(placement(container, 'n_web')).toBe('translate(41 40)');
        });

        it('walks the shape panel by keyboard: Down from search into the grid, arrows through it, Escape back to the canvas', () => {
          const { canvas } = renderEditor();
          fireEvent.keyDown(window, { key: 's' });
          const search = screen.getByRole('searchbox', { name: 'Search shapes' });
          const glyphs = within(screen.getByRole('group', { name: 'Architecture kinds' })).getAllByRole('button');
          expect(glyphs.length).toBeGreaterThan(4);
          fireEvent.keyDown(search, { key: 'ArrowDown' });
          expect(globalThis.document.activeElement).toBe(glyphs[0]);
          fireEvent.keyDown(glyphs[0] ?? search, { key: 'ArrowRight' });
          expect(globalThis.document.activeElement).toBe(glyphs[1]);
          fireEvent.keyDown(glyphs[1] ?? search, { key: 'ArrowDown' });
          expect(globalThis.document.activeElement).toBe(glyphs[4]);
          fireEvent.keyDown(glyphs[4] ?? search, { key: 'ArrowUp' });
          expect(globalThis.document.activeElement).toBe(glyphs[1]);
          fireEvent.keyDown(glyphs[1] ?? search, { key: 'Escape' });
          expect(screen.queryByRole('searchbox', { name: 'Search shapes' })).toBeNull();
          expect(globalThis.document.activeElement).toBe(canvas);
          expect(canvas.hasAttribute('data-pointer-focus')).toBe(false);
        });

        it('announces the label and kind of each node Tab walks to, from a live region outside the canvas image', () => {
          const { canvas } = renderEditor();
          canvas.focus();
          fireEvent.keyDown(canvas, { key: 'Tab' });
          const live = screen.getAllByRole('status').filter((status) => status.textContent === 'Web app, service');
          expect(live).toHaveLength(1);
          expect(live[0]?.closest('[role="img"]')).toBeNull();
          fireEvent.keyDown(canvas, { key: 'Tab' });
          expect(screen.getAllByRole('status').some((status) => status.textContent === 'VPC, container')).toBe(true);
        });
      });
    });

    describe('AI panel (build-spec-v0.1.md section 9, design/atlas/ai-panel.md)', () => {
      const healthResponse = (aiEnabled: boolean) => new Response(JSON.stringify({ ai: { enabled: aiEnabled } }), { status: 200, headers: { 'Content-Type': 'application/json' } });

      it('is hidden entirely until /api/health reports AI enabled', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(true))));
        renderEditor();
        expect(screen.queryByRole('button', { name: 'AI' })).toBeNull();

        await act(() => Promise.resolve());
        expect(screen.queryByRole('button', { name: 'AI' })).not.toBeNull();
      });

      it('stays hidden when /api/health reports AI disabled', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false))));
        renderEditor();
        await act(() => Promise.resolve());
        expect(screen.queryByRole('button', { name: 'AI' })).toBeNull();
      });

      it('opens the AI panel, exclusive of Properties and Text, once enabled', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(true))));
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(screen.getByRole('button', { name: 'AI' }));

        expect(screen.getByRole('button', { name: 'AI' }).getAttribute('aria-pressed')).toBe('true');
        expect(screen.getByRole('button', { name: 'Properties' }).getAttribute('aria-pressed')).toBe('false');
        expect(screen.getByRole('button', { name: 'Text' }).getAttribute('aria-pressed')).toBe('false');
        expect(screen.getByRole('textbox', { name: 'Describe the diagram' })).not.toBeNull();
      });
    });

    describe('PDF export (browser print by default, optional Chromium component)', () => {
      const healthResponse = (pdf: boolean, pdfHint?: string, pdfProblem?: string) => new Response(JSON.stringify({ export: { pdf, ...(pdfHint === undefined ? {} : { pdfHint }), ...(pdfProblem === undefined ? {} : { pdfProblem }) } }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      const openMenu = (): HTMLElement => {
        fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
        return screen.getByRole('menu', { name: 'Menu' });
      };
      const pdfHint = 'server-side PDF export needs the optional Chromium component. Install the browser with `npx playwright@1.63.0 install chromium`, then set export.chromium.enabled to true.';
      /** A popup the page "opened": records what is written into it and that it was printed. */
      const stubPopup = () => {
        const written: string[] = [];
        const popup = {
          document: { readyState: 'complete', open: vi.fn(), write: (html: string) => written.push(html), close: vi.fn(), fonts: undefined },
          addEventListener: vi.fn(),
          focus: vi.fn(),
          print: vi.fn(),
          close: vi.fn(),
        };
        const open = vi.fn(() => popup as unknown as Window);
        vi.stubGlobal('open', open);
        return { popup, written, open };
      };
      const printed = async (): Promise<void> => {
        // The print view is built after assets and fonts are read; give those microtasks and timers their turn.
        await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Export PDF' })).toBeNull(); });
      };

      it('always offers Export PDF in the File menu, before health answers and whatever it says', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false, pdfHint))));
        renderEditor();
        // Before the health check resolves, the entry is already there: browser print needs nothing from the server.
        expect(within(openMenu()).queryByRole('menuitem', { name: 'Export PDF…' })).not.toBeNull();
        fireEvent.keyDown(screen.getByRole('menu', { name: 'Menu' }), { key: 'Escape' });

        await act(() => Promise.resolve());
        const menu = openMenu();
        expect(within(menu).queryByRole('menuitem', { name: 'Export PDF…' })).not.toBeNull();
        // The dead-end entry is gone.
        expect(within(menu).queryByRole('menuitem', { name: 'PDF export not installed…' })).toBeNull();
      });

      it('with server PDF off, Export PDF opens the form and prints from the browser with the chosen page options, never asking the server for a PDF', async () => {
        const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false, pdfHint, 'disabled')));
        vi.stubGlobal('fetch', fetchMock);
        const { popup, written, open } = stubPopup();
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
        expect(within(dialog).getByText('Opens your browser’s print dialog: choose Save as PDF.')).not.toBeNull();
        expect(within(dialog).queryByRole('button', { name: 'Export PDF' })).toBeNull();
        fireEvent.change(within(dialog).getByLabelText('Page size'), { target: { value: 'a3' } });
        fireEvent.change(within(dialog).getByLabelText('Orientation'), { target: { value: 'portrait' } });
        fireEvent.change(within(dialog).getByLabelText('Margin (mm)'), { target: { value: '15' } });
        fireEvent.click(within(dialog).getByRole('button', { name: 'Open print dialog' }));
        // The window is opened inside the click, before the diagram is rendered.
        expect(open).toHaveBeenCalledTimes(1);
        await printed();

        expect(popup.print).toHaveBeenCalledTimes(1);
        expect(written).toHaveLength(1);
        const html = written[0] ?? '';
        // The page the user chose: A3 portrait, 15 mm round the drawing, no browser margin for a header or footer.
        expect(html).toContain('@page{size:297mm 420mm;margin:0}');
        expect(html).toContain('padding:15mm');
        expect(html).toContain('print-color-adjust:exact');
        // The drawing is the export SVG of this document, themed like SVG export.
        expect(html).toContain('data-surface="export"');
        expect(html).toContain(`data-document-id="${document.id}"`);
        expect(html).toContain('class="canvas-background"');
        const pdfCalls = fetchMock.mock.calls.map(([input]) => (typeof input === 'string' ? input : (input as Request).url)).filter((url) => url.includes('/pdf'));
        expect(pdfCalls).toEqual([]);
      });

      it('the printed view is the picture alone, with no evaluation footer', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false, pdfHint, 'disabled'))));
        const { popup, written } = stubPopup();
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        fireEvent.click(within(screen.getByRole('dialog', { name: 'Export PDF' })).getByRole('button', { name: 'Open print dialog' }));
        await printed();

        expect(popup.print).toHaveBeenCalledTimes(1);
        const html = written[0] ?? '';
        expect(html).not.toMatch(/evaluation|Evaluation copy/);
        expect(html).toContain('data-id="n_web"');
      });

      it('with the popup blocked it prints from a frame in the page instead', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false))));
        vi.stubGlobal('open', vi.fn(() => null));
        const printFrame = vi.fn();
        let frameSource = '';
        vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockReturnValue({ document: { fonts: undefined }, addEventListener: vi.fn(), focus: vi.fn(), print: printFrame } as unknown as Window);
        const srcdoc = Object.getOwnPropertyDescriptor(HTMLIFrameElement.prototype, 'srcdoc');
        vi.spyOn(HTMLIFrameElement.prototype, 'srcdoc', 'set').mockImplementation(function (this: HTMLIFrameElement, value: string) {
          frameSource = value;
          srcdoc?.set?.call(this, value);
          // jsdom does not navigate a frame the way a browser does, so say the document has loaded.
          setTimeout(() => { this.dispatchEvent(new Event('load')); }, 0);
        });
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        fireEvent.change(screen.getByLabelText('Page size'), { target: { value: 'letter' } });
        fireEvent.click(screen.getByRole('button', { name: 'Open print dialog' }));
        await printed();

        expect(globalThis.document.querySelector('iframe[data-print-frame]')).not.toBeNull();
        expect(printFrame).toHaveBeenCalledTimes(1);
        expect(frameSource).toContain('@page{size:279.4mm 215.9mm;margin:0}');
        expect(frameSource).toContain('data-surface="export"');
      });

      it('shows the dialog an error, and leaves no frame behind, when the print view cannot be made', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false))));
        const { popup } = stubPopup();
        popup.document.write = () => { throw new Error('document.write is blocked'); };
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        fireEvent.click(screen.getByRole('button', { name: 'Open print dialog' }));
        expect((await screen.findByRole('alert')).textContent).toContain('print view could not be opened');
        expect(popup.print).not.toHaveBeenCalled();
        expect(popup.close).toHaveBeenCalledTimes(1);
        expect(screen.getByRole('dialog', { name: 'Export PDF' })).not.toBeNull();
      });

      it('with server PDF on, the server’s file is the primary route and browser print the alternative', async () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(true))));
        const { popup } = stubPopup();
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
        expect(within(dialog).getByRole('button', { name: 'Export PDF' }).className).toContain('sql-import-create');
        fireEvent.click(within(dialog).getByRole('button', { name: 'Print in browser…' }));
        await printed();
        expect(popup.print).toHaveBeenCalledTimes(1);
      });

      it('carries health’s pdfProblem into the dialog as a note: a driver problem names the driver and offers no install command, and printing still works', async () => {
        const driverHint = 'server-side PDF export is enabled, but Playwright’s driver is not usable on this server: the Node runtime is missing. This is not a browser problem, so installing Chromium will not fix it.';
        vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(false, driverHint, 'driver'))));
        renderEditor();
        await act(() => Promise.resolve());
        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));

        const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
        const status = within(dialog).getByRole('status');
        expect(status.textContent).toContain('the Node runtime is missing');
        expect(status.textContent).not.toContain('npx playwright');
        expect(within(dialog).getByRole('button', { name: 'Open print dialog' })).not.toBeNull();
      });

      it('does not check /api/health on a read-only preview, but still offers Export PDF, as browser print', async () => {
        const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(healthResponse(true)));
        vi.stubGlobal('fetch', fetchMock);
        renderEditor(true);
        await act(() => Promise.resolve());
        expect(fetchMock).not.toHaveBeenCalled();
        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
        expect(within(dialog).queryByRole('button', { name: 'Export PDF' })).toBeNull();
        expect(within(dialog).getByRole('button', { name: 'Open print dialog' })).not.toBeNull();
      });

      it('downloads the document as a PDF named after its title, with the page options chosen in the dialog', async () => {
        // A string body, not a jsdom Blob: Node's fetch (undici) Response cannot extractBody from a jsdom Blob.
        const fetchMock = vi.fn<typeof fetch>((input) => {
          const url = typeof input === 'string' ? input : (input as Request).url;
          return Promise.resolve(url.includes('/pdf') ? new Response('%PDF-1.4 fake', { status: 200, headers: { 'Content-Type': 'application/pdf' } }) : healthResponse(true));
        });
        vi.stubGlobal('fetch', fetchMock);
        const clicked: string[] = [];
        vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
          clicked.push(this.download);
        });
        renderEditor();
        await act(() => Promise.resolve());

        fireEvent.click(within(openMenu()).getByRole('menuitem', { name: 'Export PDF…' }));
        const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
        fireEvent.change(within(dialog).getByLabelText('Page size'), { target: { value: 'letter' } });
        fireEvent.change(within(dialog).getByLabelText('Orientation'), { target: { value: 'portrait' } });
        fireEvent.change(within(dialog).getByLabelText('Margin (mm)'), { target: { value: '15' } });
        fireEvent.click(within(dialog).getByRole('button', { name: 'Export PDF' }));
        await act(() => Promise.resolve());
        await act(() => Promise.resolve());

        const pdfCalls = fetchMock.mock.calls.map(([input]) => (typeof input === 'string' ? input : (input as Request).url)).filter((url) => url.includes('/pdf'));
        expect(pdfCalls).toEqual([`/api/documents/${document.id}/pdf?pageSize=letter&orientation=portrait&margin=15`]);
        expect(clicked).toEqual(['order-service.pdf']);
        expect(screen.queryByRole('dialog', { name: 'Export PDF' })).toBeNull();
      });
    });

    describe('containers', () => {
      /** The id of the nearest node drawn around `id`: its container on screen, or null at the top level. */
      const drawnParent = (container: HTMLElement, id: string) => container.querySelector(`g[data-id="${id}"]`)?.parentElement?.closest('g[data-id]')?.getAttribute('data-id') ?? null;
      const selected = (container: HTMLElement) => [...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'));
      const dragBy = (container: HTMLElement, canvas: HTMLElement, id: string, dx: number, dy: number) => {
        fireEvent.pointerDown(shapeOf(container, id), { pointerId: 1, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
        fireEvent.pointerMove(canvas, { pointerId: 1, clientX: 100 + dx, clientY: 100 + dy });
        flushFrame();
        const target = container.querySelector('.drop-target')?.getAttribute('data-drop-target') ?? null;
        fireEvent.pointerUp(canvas, { pointerId: 1 });
        flushFrame();
        return target;
      };

      it('drops a dragged node into a container and out of one, each drag with its drop one undo step', () => {
        const { container, canvas } = renderEditor();
        // At 100% a screen pixel is a canvas unit, so the drags below land where their comments say.
        expect(container.querySelector('.viewer-zoom')?.textContent).toBe('100%');
        expect(drawnParent(container, 'n_web')).toBeNull();
        // n_web (40,40 320x80) down 150 snaps to the grid at (40,200), inside c_vpc (16,168 568x300); the drag outlined
        // c_vpc.
        expect(dragBy(container, canvas, 'n_web', 0, 150)).toBe('c_vpc');
        expect(container.querySelector('.drop-target')).toBeNull();
        expect(drawnParent(container, 'n_web')).toBe('c_vpc');
        expect(placement(container, 'n_web')).toBe('translate(40 200)');

        // n_db (40,360 160x80) down 200 lands at (40,560), below c_vpc: out of it, and outlining nothing.
        expect(dragBy(container, canvas, 'n_db', 0, 200)).toBeNull();
        expect(drawnParent(container, 'n_db')).toBeNull();
        expect(placement(container, 'n_db')).toBe('translate(40 560)');

        undo();
        expect(drawnParent(container, 'n_db')).toBe('c_vpc');
        expect(placement(container, 'n_db')).toBe('translate(40 360)');
        expect(drawnParent(container, 'n_web')).toBe('c_vpc');
        undo();
        expect(drawnParent(container, 'n_web')).toBeNull();
        expect(placement(container, 'n_web')).toBe('translate(40 40)');
      });

      it('moves a container with everything inside it', () => {
        const { container, canvas } = renderEditor();
        // Right 20 and down 10 snaps c_vpc to the grid at (40,180): its contents move by the same (24,12).
        expect(dragBy(container, canvas, 'c_vpc', 20, 10)).toBeNull();
        expect([placement(container, 'c_vpc'), placement(container, 'n_api'), placement(container, 'n_db'), placement(container, 'n_cache')]).toEqual([
          'translate(40 180)',
          'translate(64 212)',
          'translate(64 372)',
          'translate(424 372)',
        ]);
        undo();
        expect(placement(container, 'c_vpc')).toBe('translate(16 168)');
        expect(placement(container, 'n_cache')).toBe('translate(400 360)');
      });

      it('groups the selection on Cmd+G and ungroups it on Cmd+Shift+G, children staying put, one undo step each', () => {
        const { container, canvas } = renderEditor();
        click(canvas, shapeOf(container, 'n_db'));
        shiftClick(canvas, shapeOf(container, 'n_cache'));
        fireEvent.keyDown(canvas, { key: 'g', metaKey: true });
        const [group] = drawnIds(container).filter((id) => id?.startsWith('c_') === true && id !== 'c_vpc');
        if (group === undefined || group === null) {
          throw new Error('no group drawn');
        }
        expect(selected(container)).toEqual([group]);
        expect(drawnParent(container, group)).toBe('c_vpc');
        expect([drawnParent(container, 'n_db'), drawnParent(container, 'n_cache')]).toEqual([group, group]);
        expect([placement(container, 'n_db'), placement(container, 'n_cache')]).toEqual(['translate(40 360)', 'translate(400 360)']);
        // Children span x 40..560 and y 360..440; two 20-unit grid steps of padding on every side.
        expect(placement(container, group)).toBe('translate(0 320)');
        expect(screen.getByText('2 children')).toBeTruthy();
        // That box (0,320 600x160) crosses c_vpc (16,168 568x300) on the left, right and bottom, so c_vpc grows there
        // to leave the same 40, and its top stays put.
        expect(placement(container, 'c_vpc')).toBe('translate(-40 168)');
        expect(container.querySelector('g[data-id="c_vpc"] .shape')?.getAttribute('width')).toBe('680');
        expect(container.querySelector('g[data-id="c_vpc"] .shape')?.getAttribute('height')).toBe('352');

        fireEvent.keyDown(canvas, { key: 'G', ctrlKey: true, shiftKey: true });
        expect(drawnIds(container)).not.toContain(group);
        expect(selected(container).sort()).toEqual(['n_cache', 'n_db']);
        expect([drawnParent(container, 'n_db'), drawnParent(container, 'n_cache')]).toEqual(['c_vpc', 'c_vpc']);
        expect([placement(container, 'n_db'), placement(container, 'n_cache')]).toEqual(['translate(40 360)', 'translate(400 360)']);

        undo();
        expect(drawnParent(container, 'n_db')).toBe(group);
        undo();
        expect(drawnIds(container)).not.toContain(group);
        expect(drawnParent(container, 'n_db')).toBe('c_vpc');
        expect(placement(container, 'c_vpc')).toBe('translate(16 168)');
      });

      it('groups, ungroups and fits to contents from the Container panel section', () => {
        const { container, canvas } = renderEditor();
        const fit = screen.getByRole('button', { name: 'Fit to contents' });
        expect([enabled('Group'), enabled('Ungroup'), enabled('Fit to contents')]).toEqual([false, false, false]);
        click(canvas, shapeOf(container, 'c_vpc'));
        expect([enabled('Group'), enabled('Ungroup'), enabled('Fit to contents')]).toEqual([true, true, true]);
        expect(screen.getByText('3 children')).toBeTruthy();
        fireEvent.click(fit);
        // Children span x 40..560 and y 200..440, so 40 on every side makes 0,160 600x320.
        expect(placement(container, 'c_vpc')).toBe('translate(0 160)');
        expect(container.querySelector('g[data-id="c_vpc"] .shape')?.getAttribute('width')).toBe('600');
        expect(container.querySelector('g[data-id="c_vpc"] .shape')?.getAttribute('height')).toBe('320');
        undo();
        expect(placement(container, 'c_vpc')).toBe('translate(16 168)');

        click(canvas, shapeOf(container, 'n_web'));
        expect([enabled('Group'), enabled('Ungroup'), enabled('Fit to contents')]).toEqual([true, false, false]);
        fireEvent.click(screen.getByRole('button', { name: 'Group' }));
        const [group] = selected(container);
        expect(group).toMatch(/^c_/);
        expect(drawnParent(container, 'n_web')).toBe(group);
        fireEvent.click(screen.getByRole('button', { name: 'Ungroup' }));
        expect(selected(container)).toEqual(['n_web']);
        expect(drawnParent(container, 'n_web')).toBeNull();
        expect(container.querySelector('.viewer-notice[role="status"]')?.textContent).toBe('');
      });

      it('fits a container to its contents from the command palette with exactly the model the panel button writes', async () => {
        async function runFit(usePalette: boolean): Promise<Document['nodes'][number] | undefined> {
          const saves: string[] = [];
          vi.stubGlobal(
            'fetch',
            vi.fn<typeof fetch>((_url, init) => {
              saves.push(init?.body as string);
              return Promise.resolve(new Response('{}', { status: 200, headers: { ETag: '"dv1:next"' } }));
            }),
          );
          const { container, canvas, unmount } = renderEditor();
          click(canvas, shapeOf(container, 'c_vpc'));
          if (usePalette) {
            fireEvent.keyDown(window, { key: 'k', metaKey: true });
            const search = screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
            fireEvent.change(search, { target: { value: 'fit to contents' } });
            fireEvent.keyDown(search, { key: 'Enter' });
          } else {
            fireEvent.click(screen.getByRole('button', { name: 'Fit to contents' }));
          }
          fireEvent.click(screen.getByRole('button', { name: 'Save' }));
          await act(() => Promise.resolve());
          unmount();
          const body = JSON.parse(saves[saves.length - 1] ?? 'null') as typeof document;
          return body.nodes.find((node) => node.id === 'c_vpc');
        }
        const byPanel = await runFit(false);
        const byPalette = await runFit(true);
        expect(byPanel?.geometry).toMatchObject({ x: 0, y: 160, w: 600, h: 320 });
        expect(byPalette).toEqual(byPanel);
      });

      it('refuses to ungroup a container with connections, from the shortcut and the panel alike, and says why', () => {
        const connected: Document = { ...document, edges: [...document.edges, { id: 'e_vpc', kind: 'data', from: { node: 'n_web' }, to: { node: 'c_vpc' } }] };
        const { container, canvas } = renderEditor(false, connected);
        const notice = container.querySelector('.viewer-notice[role="status"]');
        if (!notice) {
          throw new Error('no notice line');
        }
        click(canvas, shapeOf(container, 'c_vpc'));
        // A nudge first, so an undo afterwards shows which step is the latest.
        fireEvent.keyDown(canvas, { key: 'ArrowRight' });
        expect(placement(container, 'c_vpc')).toBe('translate(17 168)');
        expect(notice.textContent).toBe('');

        fireEvent.keyDown(canvas, { key: 'g', metaKey: true, shiftKey: true });
        expect(notice.textContent).toBe('Couldn’t ungroup VPC: it has connections. Remove them first.');
        expect(drawnIds(container)).toContain('c_vpc');
        expect(drawnIds(container)).toContain('e_vpc');
        expect(drawnParent(container, 'n_api')).toBe('c_vpc');
        expect(selected(container)).toEqual(['c_vpc']);

        fireEvent.click(screen.getByRole('button', { name: 'Ungroup' }));
        expect(drawnIds(container)).toContain('c_vpc');
        expect(notice.textContent).toContain('has connections');

        // Nothing was recorded: the undo takes back the nudge, and that edit clears the notice.
        undo();
        expect(placement(container, 'c_vpc')).toBe('translate(16 168)');
        expect(drawnIds(container)).toContain('c_vpc');
        expect(notice.textContent).toBe('');
      });

      it('ungroups the unconnected containers of a mixed selection, leaves the connected one and names it', () => {
        const connected: Document = { ...document, edges: [...document.edges, { id: 'e_vpc', kind: 'data', from: { node: 'n_web' }, to: { node: 'c_vpc' } }] };
        const { container, canvas } = renderEditor(false, connected);
        const notice = container.querySelector('.viewer-notice[role="status"]');
        if (!notice) {
          throw new Error('no notice line');
        }
        click(canvas, shapeOf(container, 'n_web'));
        fireEvent.keyDown(canvas, { key: 'g', metaKey: true });
        const [group] = selected(container);
        if (group === undefined || group === null) {
          throw new Error('no group selected');
        }
        shiftClick(canvas, shapeOf(container, 'c_vpc'));

        fireEvent.keyDown(canvas, { key: 'g', metaKey: true, shiftKey: true });
        expect(notice.textContent).toBe('Couldn’t ungroup VPC: it has connections. Remove them first.');
        expect(drawnIds(container)).not.toContain(group);
        expect(drawnParent(container, 'n_web')).toBeNull();
        expect(placement(container, 'n_web')).toBe('translate(40 40)');
        expect(drawnIds(container)).toContain('c_vpc');
        expect(drawnIds(container)).toContain('e_vpc');
        expect(drawnParent(container, 'n_api')).toBe('c_vpc');
        expect(selected(container).sort()).toEqual(['c_vpc', 'n_web']);

        // One undo step brings the group back; the one before it is the group itself.
        undo();
        expect(drawnParent(container, 'n_web')).toBe(group);
        undo();
        expect(drawnIds(container)).not.toContain(group);
      });
    });

    it('asks about a refused save and leaves the moved node where the user put it', async () => {
      vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{"title":"Document has changed"}', { status: 412 }))));
      const { canvas, shape, nodeTransform } = renderEditor();
      drag(canvas, shape, 2);
      const moved = nodeTransform();

      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await act(() => Promise.resolve());

      expect(screen.getByRole('alert').textContent).toContain('changed somewhere else');
      expect(screen.getByText('Out of date')).toBeTruthy();
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save' }).disabled).toBe(true);
      expect(nodeTransform()).toBe(moved);
    });

    describe('clipboard (checklist items 18, 19 and 21)', () => {
      /** The OS clipboard as the browser exposes it; installed per test and removed after. */
      function installSystemClipboard(initialText = '') {
        const state = { text: initialText };
        const clipboard = {
          readText: vi.fn(() => Promise.resolve(state.text)),
          writeText: vi.fn((text: string) => {
            state.text = text;
            return Promise.resolve();
          }),
        };
        Object.defineProperty(navigator, 'clipboard', { configurable: true, value: clipboard });
        return { state, clipboard };
      }
      afterEach(() => {
        Reflect.deleteProperty(navigator, 'clipboard');
      });

      const nodeShape = (container: HTMLElement, id: string): Element => {
        const shape = container.querySelector(`g[data-id="${id}"] .shape`);
        if (!shape) {
          throw new Error(`${id} not drawn`);
        }
        return shape;
      };
      function select(container: HTMLElement, canvas: HTMLElement, ...ids: string[]): void {
        ids.forEach((id, i) => {
          fireEvent.pointerDown(nodeShape(container, id), { pointerId: 20 + i, button: 0, pointerType: 'mouse', shiftKey: i > 0, clientX: 100, clientY: 100 });
          fireEvent.pointerUp(canvas, { pointerId: 20 + i });
        });
      }
      const selectedNodes = (container: HTMLElement) => [...container.querySelectorAll('.selection-node')].map((g) => g.getAttribute('data-selection-id'));
      const added = (container: HTMLElement, before: (string | null)[]) => drawnIds(container).filter((id) => !before.includes(id));
      const addedNodes = (container: HTMLElement, before: (string | null)[]) => added(container, before).filter((id) => container.querySelector(`g.node[data-id="${id ?? ''}"]`));
      const addedEdges = (container: HTMLElement, before: (string | null)[]) => added(container, before).filter((id) => container.querySelector(`g.edge[data-id="${id ?? ''}"]`));
      const edgePoints = (container: HTMLElement, id: string) =>
        (container.querySelector(`g[data-id="${id}"] polyline`)?.getAttribute('points') ?? '').split(' ').map((pair) => pair.split(',').map(Number));
      /**
       * Ctrl+C or X as a browser delivers it: the key, then a copy or cut event whose DataTransfer the browser puts
       * on the system clipboard (`clipboard.text`) when the event was prevented. Returns whether it was.
       */
      function clipboardKey(canvas: HTMLElement, key: 'c' | 'x', clipboard: { text: string }): boolean {
        canvas.focus();
        fireEvent.keyDown(canvas, { key, metaKey: true });
        const data = { text: '' };
        const clipboardData = {
          getData: (type: string) => (type === 'text/plain' ? data.text : ''),
          setData: (type: string, value: string) => {
            if (type === 'text/plain') {
              data.text = value;
            }
          },
        };
        const prevented = !(key === 'c' ? fireEvent.copy : fireEvent.cut)(canvas, { clipboardData });
        if (prevented) {
          clipboard.text = data.text;
        }
        return prevented;
      }
      /** Cmd+V as a browser delivers it: the key, then a paste event carrying the system clipboard's text. */
      async function paste(canvas: HTMLElement, clipboardText: string | undefined): Promise<void> {
        canvas.focus();
        await act(async () => {
          fireEvent.keyDown(canvas, { key: 'v', metaKey: true });
          if (clipboardText !== undefined) {
            fireEvent.paste(canvas, { clipboardData: { getData: (type: string) => (type === 'text/plain' ? clipboardText : '') } });
          }
          // The paste is asynchronous; let it, and the fallback read when no event came, settle.
          await new Promise((resolve) => setTimeout(resolve, 0));
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
      }

      it('pastes a copy one grid step down and right with new ids and the edge between the copied nodes, selected, undone by one Cmd+Z', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_api', 'n_db');
        expect(clipboardKey(canvas, 'c', system.state)).toBe(true);
        // The copy event carried the payload; the permission-gated Clipboard API was not needed.
        expect(system.clipboard.writeText).not.toHaveBeenCalled();

        await paste(canvas, system.state.text);
        // The browser's paste event brought the text: no permission-prompting clipboard read.
        expect(system.clipboard.readText).not.toHaveBeenCalled();
        const nodes = addedNodes(container, before);
        const edges = addedEdges(container, before);
        expect(nodes).toHaveLength(2);
        // Only e_2 joins n_api and n_db; e_1 and e_3 each have an endpoint that was not copied.
        expect(edges).toHaveLength(1);
        // n_api sits at (40, 200) and n_db at (40, 360); every shipped diagram theme has a 20-unit grid (themes/*.yaml).
        expect(nodes.map((id) => placement(container, id ?? ''))).toEqual(['translate(60 220)', 'translate(60 380)']);
        // The pasted edge runs between the pasted nodes: e_2's route, one grid step along.
        expect(edgePoints(container, edges[0] ?? '')).toEqual(edgePoints(container, 'e_2').map(([x, y]) => [(x ?? 0) + 20, (y ?? 0) + 20]));
        expect(selectedNodes(container)).toEqual(nodes);

        undo();
        expect(drawnIds(container)).toEqual(before);
        // The copy did not touch the model, so the undo before the paste has nothing left to take.
        undo();
        expect(drawnIds(container)).toEqual(before);
        expect(placement(container, 'n_api')).toBe('translate(40 200)');
      });

      it('pastes into a different document with the same relative layout, dropping edges that lost an endpoint', async () => {
        const system = installSystemClipboard();
        const source = renderEditor();
        select(source.container, source.canvas, 'n_api', 'n_db');
        clipboardKey(source.canvas, 'c', system.state);
        source.unmount();

        const other = parseDocument({ ...document, id: 'doc_other', title: 'Other', nodes: [{ id: 'n_api', kind: 'service', label: 'Clash', shape: 'rectangle', geometry: { x: 600, y: 600, w: 100, h: 50 } }], edges: [] });
        const target = render(<DocumentViewer id={other.id} document={other} etag={'"dv1:other"'} />);
        const canvas = target.container.querySelector<HTMLElement>('.diagram-canvas');
        if (!canvas) {
          throw new Error('editor not rendered');
        }
        await paste(canvas, system.state.text);
        const nodes = addedNodes(target.container, ['n_api']);
        expect(nodes).toHaveLength(2);
        expect(nodes).not.toContain('n_api');
        expect(addedEdges(target.container, ['n_api'])).toHaveLength(1);
        const [api, db] = nodes.map((id) => /translate\((\S+) (\S+)\)/.exec(placement(target.container, id ?? '') ?? '')?.slice(1).map(Number));
        // n_db sat 160 below n_api in the source; it still does.
        expect([(db?.[0] ?? 0) - (api?.[0] ?? 0), (db?.[1] ?? 0) - (api?.[1] ?? 0)]).toEqual([0, 160]);
        target.unmount();
      });

      it('pastes nothing when the system clipboard holds something other than a Draughtsman selection', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_web');
        clipboardKey(canvas, 'c', system.state);
        // Something copied elsewhere since: it wins over this tab's earlier copy.
        system.state.text = 'Plain text from another app';
        await paste(canvas, system.state.text);
        expect(drawnIds(container)).toEqual(before);
        expect(selectedNodes(container)).toEqual(['n_web']);
      });

      it('duplicates the selection one grid step along on Cmd+D without touching the clipboard, as one undo step', () => {
        const system = installSystemClipboard('untouched');
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_web');
        expect(fireEvent.keyDown(canvas, { key: 'd', metaKey: true })).toBe(false);
        const nodes = addedNodes(container, before);
        expect(nodes).toHaveLength(1);
        expect(placement(container, nodes[0] ?? '')).toBe('translate(60 60)');
        expect(selectedNodes(container)).toEqual(nodes);
        // Duplicating again steps on from the duplicate.
        fireEvent.keyDown(canvas, { key: 'd', metaKey: true });
        const both = addedNodes(container, before);
        expect(placement(container, both[1] ?? '')).toBe('translate(80 80)');
        expect(system.clipboard.writeText).not.toHaveBeenCalled();
        expect(system.state.text).toBe('untouched');
        undo();
        expect(addedNodes(container, before)).toEqual(nodes);
        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      /** Drags `id` by (dx, dy) screen pixels; the editor moves every descendant of a dragged container with it. */
      function dragNode(container: HTMLElement, canvas: HTMLElement, id: string, dx: number, dy: number): void {
        fireEvent.pointerDown(nodeShape(container, id), { pointerId: 40, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
        fireEvent.pointerMove(canvas, { pointerId: 40, clientX: 100 + dx, clientY: 100 + dy });
        flushFrame();
        fireEvent.pointerUp(canvas, { pointerId: 40 });
        flushFrame();
      }
      const offsetOf = (container: HTMLElement, id: string) => /translate\((\S+) (\S+)\)/.exec(placement(container, id) ?? '')?.slice(1).map(Number) ?? [];

      it('duplicates a container\'s child into the same container, so moving the container carries the duplicate', () => {
        installSystemClipboard();
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_db');
        fireEvent.keyDown(canvas, { key: 'd', metaKey: true });
        const duplicate = addedNodes(container, before)[0] ?? '';
        const [vpcX = 0, vpcY = 0] = offsetOf(container, 'c_vpc');
        const [dupX = 0, dupY = 0] = offsetOf(container, duplicate);

        dragNode(container, canvas, 'c_vpc', 40, 20);
        const [movedX = 0, movedY = 0] = offsetOf(container, 'c_vpc');
        expect([movedX - vpcX, movedY - vpcY]).not.toEqual([0, 0]);
        const [dupMovedX = 0, dupMovedY = 0] = offsetOf(container, duplicate);
        expect([dupMovedX - dupX, dupMovedY - dupY]).toEqual([movedX - vpcX, movedY - vpcY]);
      });

      it('pastes a container\'s child into another document at top level, even beside a container of the same id', async () => {
        const system = installSystemClipboard();
        const source = renderEditor();
        select(source.container, source.canvas, 'n_db');
        clipboardKey(source.canvas, 'c', system.state);
        source.unmount();

        const other = parseDocument({ ...document, id: 'doc_other', title: 'Other', nodes: document.nodes.filter((node) => node.id === 'c_vpc'), edges: [] });
        const target = render(<DocumentViewer id={other.id} document={other} etag={'"dv1:other"'} />);
        const canvas = target.container.querySelector<HTMLElement>('.diagram-canvas');
        if (!canvas) {
          throw new Error('editor not rendered');
        }
        canvas.setPointerCapture = () => undefined;
        canvas.releasePointerCapture = () => undefined;
        canvas.hasPointerCapture = () => false;
        await paste(canvas, system.state.text);
        const pasted = addedNodes(target.container, ['c_vpc'])[0] ?? '';
        const pastedAt = offsetOf(target.container, pasted);
        const [vpcX = 0, vpcY = 0] = offsetOf(target.container, 'c_vpc');

        dragNode(target.container, canvas, 'c_vpc', 40, 20);
        const [movedX = 0, movedY = 0] = offsetOf(target.container, 'c_vpc');
        expect([movedX - vpcX, movedY - vpcY]).not.toEqual([0, 0]);
        expect(offsetOf(target.container, pasted)).toEqual(pastedAt);
        target.unmount();
      });

      const selectEdge = (container: HTMLElement, canvas: HTMLElement, id: string): void => {
        const hit = container.querySelector(`g[data-id="${id}"] polyline`);
        if (!hit) {
          throw new Error(`${id} not drawn`);
        }
        fireEvent.pointerDown(hit, { pointerId: 30, button: 0, pointerType: 'mouse', clientX: 100, clientY: 100 });
        fireEvent.pointerUp(canvas, { pointerId: 30 });
      };

      it('leaves the earlier clipboard content in place when copying a selection with no node to carry', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        select(container, canvas, 'n_web');
        clipboardKey(canvas, 'c', system.state);
        const earlier = system.state.text;
        selectEdge(container, canvas, 'e_2');
        expect(clipboardKey(canvas, 'c', system.state)).toBe(false);
        expect(system.clipboard.writeText).not.toHaveBeenCalled();
        expect(system.state.text).toBe(earlier);

        const before = drawnIds(container);
        await paste(canvas, system.state.text);
        expect(addedNodes(container, before)).toHaveLength(1);
      });

      it('cuts an edges-only selection like Delete and empties the clipboard, so a later paste adds nothing', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        select(container, canvas, 'n_web');
        clipboardKey(canvas, 'c', system.state);
        const before = drawnIds(container);
        selectEdge(container, canvas, 'e_2');
        expect(clipboardKey(canvas, 'x', system.state)).toBe(true);
        expect(drawnIds(container)).not.toContain('e_2');
        expect(system.clipboard.writeText).not.toHaveBeenCalled();
        expect(JSON.parse(system.state.text)).toMatchObject({ format: 'draughtsman/selection', nodes: [], edges: [] });

        const afterCut = drawnIds(container);
        await paste(canvas, system.state.text);
        expect(drawnIds(container)).toEqual(afterCut);
        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('cuts the selection with its edges, pastes it back one grid step along, and undoes each as one step', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_db');
        clipboardKey(canvas, 'x', system.state);
        expect(drawnIds(container)).not.toContain('n_db');
        expect(drawnIds(container)).not.toContain('e_2');
        expect(selectedNodes(container)).toEqual([]);

        const afterCut = drawnIds(container);
        // A browser that fires no paste event on the canvas: the editor reads the clipboard itself.
        await paste(canvas, undefined);
        expect(system.clipboard.readText).toHaveBeenCalledTimes(1);
        const nodes = addedNodes(container, afterCut);
        expect(nodes).toHaveLength(1);
        expect(nodes).not.toContain('n_db');
        expect(placement(container, nodes[0] ?? '')).toBe('translate(60 380)');

        undo();
        expect(drawnIds(container)).toEqual(afterCut);
        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('copies through the copy event with no Clipboard API, as over plain HTTP, and pastes that into another document', async () => {
        const osClipboard = { text: '' };
        const source = renderEditor();
        select(source.container, source.canvas, 'n_api', 'n_db');
        expect(clipboardKey(source.canvas, 'c', osClipboard)).toBe(true);
        expect(JSON.parse(osClipboard.text)).toMatchObject({ format: 'draughtsman/selection', nodes: [{ id: 'n_api' }, { id: 'n_db' }] });
        source.unmount();

        const other = parseDocument({ ...document, id: 'doc_other', title: 'Other', nodes: [], edges: [] });
        const target = render(<DocumentViewer id={other.id} document={other} etag={'"dv1:other"'} />);
        const canvas = target.container.querySelector<HTMLElement>('.diagram-canvas');
        if (!canvas) {
          throw new Error('editor not rendered');
        }
        await paste(canvas, osClipboard.text);
        expect(addedNodes(target.container, [])).toHaveLength(2);
        target.unmount();
      });

      it('writes through the Clipboard API only when no copy event follows the key', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        select(container, canvas, 'n_web');
        await act(async () => {
          fireEvent.keyDown(canvas, { key: 'c', metaKey: true });
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
        expect(system.clipboard.writeText).toHaveBeenCalledTimes(1);
        expect(JSON.parse(system.state.text)).toMatchObject({ nodes: [{ id: 'n_web' }] });
      });

      /** Ctrl+K, a search query and Enter, as the palette runs a command. */
      function runPaletteCommand(query: string): void {
        fireEvent.keyDown(window, { key: 'k', metaKey: true });
        const search = screen.getByRole<HTMLInputElement>('combobox', { name: 'Search commands' });
        fireEvent.change(search, { target: { value: query } });
        fireEvent.keyDown(search, { key: 'Enter' });
      }

      it('copies from the palette through the async Clipboard API with exactly the payload the keyboard fallback writes', async () => {
        const keySystem = installSystemClipboard();
        const keyEditor = renderEditor();
        select(keyEditor.container, keyEditor.canvas, 'n_web');
        await act(async () => {
          fireEvent.keyDown(keyEditor.canvas, { key: 'c', metaKey: true });
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
        keyEditor.unmount();

        const paletteSystem = installSystemClipboard();
        const paletteEditor = renderEditor();
        select(paletteEditor.container, paletteEditor.canvas, 'n_web');
        // "copy" alone also matches Duplicate's "copy clone selection" keywords; "clipboard" narrows it to Copy.
        runPaletteCommand('copy clipboard');
        await act(async () => {
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
        paletteEditor.unmount();

        expect(keySystem.clipboard.writeText).toHaveBeenCalledTimes(1);
        expect(paletteSystem.clipboard.writeText).toHaveBeenCalledTimes(1);
        expect(JSON.parse(paletteSystem.state.text)).toEqual(JSON.parse(keySystem.state.text));
      });

      it('cuts from the palette with exactly the model the keyboard cut writes', () => {
        installSystemClipboard();
        const keyEditor = renderEditor();
        select(keyEditor.container, keyEditor.canvas, 'n_db');
        clipboardKey(keyEditor.canvas, 'x', { text: '' });
        const byKey = drawnIds(keyEditor.container);
        keyEditor.unmount();

        installSystemClipboard();
        const paletteEditor = renderEditor();
        select(paletteEditor.container, paletteEditor.canvas, 'n_db');
        runPaletteCommand('cut');
        const byPalette = drawnIds(paletteEditor.container);
        paletteEditor.unmount();

        expect(byKey).not.toContain('n_db');
        expect(byPalette).toEqual(byKey);
      });

      it('pastes from the palette through the async Clipboard API with exactly the placement the keyboard fallback writes', async () => {
        async function runCase(usePalette: boolean): Promise<(string | null | undefined)[]> {
          const system = installSystemClipboard();
          const source = renderEditor();
          select(source.container, source.canvas, 'n_api');
          clipboardKey(source.canvas, 'c', system.state);
          source.unmount();

          const target = renderEditor();
          const before = drawnIds(target.container);
          if (usePalette) {
            runPaletteCommand('paste');
            await act(async () => {
              await new Promise((resolve) => setTimeout(resolve, 0));
            });
          } else {
            await paste(target.canvas, undefined);
          }
          const nodes = addedNodes(target.container, before);
          const placements = nodes.map((id) => placement(target.container, id ?? ''));
          target.unmount();
          return placements;
        }
        const byKey = await runCase(false);
        const byPalette = await runCase(true);
        expect(byKey).toHaveLength(1);
        expect(byPalette).toEqual(byKey);
      });

      it('pastes nothing from other text on the clipboard even when this tab\'s last write was refused', async () => {
        const system = installSystemClipboard('Plain text from another app');
        system.clipboard.writeText.mockImplementation(() => Promise.reject(new DOMException('Denied', 'NotAllowedError')));
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_web');
        await act(async () => {
          fireEvent.keyDown(canvas, { key: 'c', metaKey: true });
          await new Promise((resolve) => setTimeout(resolve, 0));
        });
        expect(system.clipboard.writeText).toHaveBeenCalledTimes(1);
        await paste(canvas, system.state.text);
        await paste(canvas, undefined);
        expect(drawnIds(container)).toEqual(before);
      });

      it('cascades repeated pastes of the same copy one grid step further each time, until the next copy', async () => {
        const system = installSystemClipboard();
        const { container, canvas } = renderEditor();
        const before = drawnIds(container);
        select(container, canvas, 'n_web');
        clipboardKey(canvas, 'c', system.state);
        await paste(canvas, system.state.text);
        await paste(canvas, system.state.text);
        await paste(canvas, system.state.text);
        // n_web sits at (40, 40); the third paste lands three 20-unit grid steps from it.
        expect(addedNodes(container, before).map((id) => placement(container, id ?? ''))).toEqual(['translate(60 60)', 'translate(80 80)', 'translate(100 100)']);
        undo();
        expect(addedNodes(container, before)).toHaveLength(2);

        const pasted = drawnIds(container);
        select(container, canvas, 'n_web');
        clipboardKey(canvas, 'c', system.state);
        await paste(canvas, system.state.text);
        expect(addedNodes(container, pasted).map((id) => placement(container, id ?? ''))).toEqual(['translate(60 60)']);
      });
    });

    describe('an agent edit while the document is open (checklist item 30)', () => {
      /** The document endpoints as docs/api.md defines them: conditional GET, If-Match-guarded PUT. */
      function fakeServer() {
        let stored: Document = structuredClone(document);
        let version = '"dv1:loaded"';
        let writes = 0;
        const fetchMock = vi.fn<typeof fetch>((_input, init) => {
          const headers = new Headers(init?.headers);
          if (init?.method === 'PUT') {
            if (headers.get('If-Match') !== version) {
              return Promise.resolve(new Response('{}', { status: 412 }));
            }
            stored = JSON.parse(init.body as string) as Document;
            version = `"dv1:save${String(++writes)}"`;
            return Promise.resolve(new Response(JSON.stringify(stored), { status: 200, headers: { ETag: version } }));
          }
          return Promise.resolve(
            headers.get('If-None-Match') === version
              ? new Response(null, { status: 304, headers: { ETag: version } })
              : new Response(JSON.stringify(stored), { status: 200, headers: { ETag: version } }),
          );
        });
        vi.stubGlobal('fetch', fetchMock);
        return {
          fetchMock,
          /** What patch_document stores: the change, a new updatedAt and a provenance entry naming the agent. */
          agentWrite(change: (next: Document) => void): string {
            const next = structuredClone(stored);
            change(next);
            next.meta = { ...next.meta, updatedAt: '2026-09-28T10:00:00.000Z', aiProvenance: [...(next.meta.aiProvenance ?? []), { agent: 'agent:claude', at: '2026-09-28T10:00:00.000Z', action: 'updated', elementIds: [] }] };
            stored = next;
            version = `"dv1:agent${String(++writes)}"`;
            return version;
          },
          get stored() {
            return stored;
          },
        };
      }

      function renderLive() {
        const view = render(<DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} liveSyncIntervalMs={LIVE_SYNC_INTERVAL_MS} />);
        const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
        const shape = view.container.querySelector('g[data-id="n_web"] .shape');
        if (!canvas || !shape) {
          throw new Error('editor not rendered');
        }
        canvas.setPointerCapture = () => undefined;
        canvas.releasePointerCapture = () => undefined;
        canvas.hasPointerCapture = () => false;
        return { ...view, canvas, shape };
      }
      const nodeText = (container: HTMLElement, id: string) => container.querySelector(`g[data-id="${id}"]`)?.textContent ?? '';

      it('draws the agent’s change within two seconds, without a reload, and Ctrl+Z leaves it alone', async () => {
        const server = fakeServer();
        const { container } = renderLive();
        const before = drawnIds(container);
        // The poll is a real 1 s timer, so only the time the browser-side work takes grows with load: the limit is scaled by it.
        const scale = loadScale();

        server.agentWrite((next) => {
          next.nodes = next.nodes.map((node) => (node.id === 'n_web' ? { ...node, label: 'Web app (agent)' } : node));
          next.nodes.push({ id: 'n_agent', kind: 'service', label: 'Cache', shape: 'rectangle', geometry: { x: 40, y: 600, w: 160, h: 60 } });
        });
        const started = performance.now();
        await waitFor(() => { expect(nodeText(container, 'n_agent')).toContain('Cache'); }, { timeout: 15_000, interval: 20 });
        const elapsed = performance.now() - started;

        console.info(`agent edit on the canvas after ${elapsed.toFixed(0)} ms (poll interval ${String(LIVE_SYNC_INTERVAL_MS)} ms)`);
        expect(elapsed).toBeLessThan(2000 * scale);
        expect(nodeText(container, 'n_web')).toContain('Web app (agent)');
        expect(screen.getByText('Changes from agent:claude are now shown.')).toBeTruthy();
        expect(screen.getByText('Saved')).toBeTruthy();

        undo();
        expect(drawnIds(container).sort()).toEqual([...before, 'n_agent'].sort());
        expect(nodeText(container, 'n_web')).toContain('Web app (agent)');
      });

      it('keeps the local drag through a clashing agent move, says so, and saves from the agent’s version', async () => {
        const server = fakeServer();
        const { container, canvas, shape } = renderLive();
        drag(canvas, shape, 3);
        const dragged = placement(container, 'n_web');
        expect(dragged).not.toBe('translate(40 40)');

        const agentVersion = server.agentWrite((next) => {
          next.nodes = next.nodes.map((node) => (node.id === 'n_web' && node.geometry ? { ...node, geometry: { ...node.geometry, x: 500, y: 500 } } : node));
          next.title = 'Order service (agent)';
        });
        const band = await screen.findByText(/You and agent:claude both changed “Web app” \(geometry\)/, undefined, { timeout: 15_000 });

        // The local side is what is drawn; the agent's change to a field the user did not touch is in.
        expect(placement(container, 'n_web')).toBe(dragged);
        expect(screen.getByRole('heading', { name: 'Order service (agent)' })).toBeTruthy();
        expect(band.textContent).toContain('Your version is shown, and saving keeps it.');

        fireEvent.click(screen.getByRole('button', { name: 'Use their version' }));
        expect(placement(container, 'n_web')).toBe('translate(500 500)');
        expect(screen.queryByText(/You and agent:claude both changed/)).toBeNull();
        undo();
        expect(placement(container, 'n_web')).toBe(dragged);

        fireEvent.click(screen.getByRole('button', { name: 'Save' }));
        await waitFor(() => { expect(screen.getByText('Saved')).toBeTruthy(); });
        const puts = server.fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT');
        expect(new Headers(puts[0]?.[1]?.headers).get('If-Match')).toBe(agentVersion);
        expect(server.stored.title).toBe('Order service (agent)');
      });

      it('clears the clash notice and keeps the local version when the user keeps theirs', async () => {
        const server = fakeServer();
        const { container, canvas, shape } = renderLive();
        drag(canvas, shape, 2);
        const dragged = placement(container, 'n_web');
        server.agentWrite((next) => {
          next.nodes = next.nodes.map((node) => (node.id === 'n_web' && node.geometry ? { ...node, geometry: { ...node.geometry, x: 500, y: 500 } } : node));
        });
        await screen.findByText(/You and agent:claude both changed/, undefined, { timeout: 15_000 });

        fireEvent.click(screen.getByRole('button', { name: 'Keep my version' }));

        expect(screen.queryByText(/You and agent:claude both changed/)).toBeNull();
        expect(placement(container, 'n_web')).toBe(dragged);
      });
    });

    describe('images', () => {
      const addedId = (container: HTMLElement, before: readonly (string | null)[]): string | null | undefined => drawnIds(container).find((id) => !before.includes(id));
      const imageOf = (container: HTMLElement, id: string) => container.querySelector(`g[data-id="${id}"] image`);

      it('creates an image node from a file dropped on the canvas, sized to the picture and centred on the drop', async () => {
        const { container } = renderEditor();
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        const before = drawnIds(container);
        vi.mocked(uploadAsset).mockResolvedValue({ id: 'asset_drop', contentType: 'image/png', size: 12 });
        vi.mocked(imageDimensions).mockResolvedValue({ w: 480, h: 240 });

        const file = new File([new Uint8Array([0x89, 0x50, 0x4E, 0x47])], 'logo.png', { type: 'image/png' });
        fireEvent.drop(viewerCanvas, { dataTransfer: { files: [file], types: ['Files'] }, clientX: 300, clientY: 200 });

        await waitFor(() => { expect(addedId(container, before)).toBeDefined(); });
        const newId = addedId(container, before) ?? '';
        expect(container.querySelector(`g[data-id="${newId}"]`)?.getAttribute('data-kind')).toBe('image');
        // 480x240 is capped to 240x120; the canvas draws the served asset URL through <image>, never inlined HTML.
        const image = imageOf(container, newId);
        expect(image?.getAttribute('width')).toBe('240');
        expect(image?.getAttribute('height')).toBe('120');
        expect(image?.getAttribute('href')).toBe('/api/assets/asset_drop');

        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('creates an image node from a pasted picture at the view centre', async () => {
        const { container } = renderEditor();
        const before = drawnIds(container);
        vi.mocked(uploadAsset).mockResolvedValue({ id: 'asset_paste', contentType: 'image/png', size: 6 });
        vi.mocked(imageDimensions).mockResolvedValue({ w: 100, h: 200 });

        const file = new File([new Uint8Array([1, 2, 3])], 'pasted.png', { type: 'image/png' });
        fireEvent.paste(window, { clipboardData: { items: [{ kind: 'file', type: 'image/png', getAsFile: () => file }] } });

        await waitFor(() => { expect(addedId(container, before)).toBeDefined(); });
        const newId = addedId(container, before) ?? '';
        expect(container.querySelector(`g[data-id="${newId}"]`)?.getAttribute('data-kind')).toBe('image');
        const image = imageOf(container, newId);
        expect(image?.getAttribute('width')).toBe('100');
        expect(image?.getAttribute('height')).toBe('200');
        expect(image?.getAttribute('href')).toBe('/api/assets/asset_paste');

        undo();
        expect(drawnIds(container)).toEqual(before);
      });

      it('offers Replace image only for a single selected image node and re-points it at the new asset', async () => {
        const { container, canvas } = renderEditor();
        // Place an image node through the drop path, then replace it.
        vi.mocked(uploadAsset).mockResolvedValueOnce({ id: 'asset_one', contentType: 'image/png', size: 3 });
        vi.mocked(imageDimensions).mockResolvedValue({ w: 100, h: 100 });
        const viewerCanvas = container.querySelector('.viewer-canvas');
        if (!viewerCanvas) {
          throw new Error('viewer canvas not rendered');
        }
        const before = drawnIds(container);
        fireEvent.drop(viewerCanvas, { dataTransfer: { files: [new File([new Uint8Array([1])], 'a.png', { type: 'image/png' })], types: ['Files'] }, clientX: 200, clientY: 200 });
        await waitFor(() => { expect(addedId(container, before)).toBeDefined(); });
        const newId = addedId(container, before) ?? '';

        // Nothing selected: the action is absent.
        expect(screen.queryByRole('menuitem', { name: 'Replace image' })).toBeNull();

        click(canvas, container.querySelector(`g[data-id="${newId}"] .shape`) ?? container);
        vi.mocked(uploadAsset).mockResolvedValueOnce({ id: 'asset_two', contentType: 'image/png', size: 3 });
        // The menu lists it (checklist item 28); run it and drive the picker through the hidden input.
        fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
        fireEvent.click(screen.getByRole('menuitem', { name: 'Replace image' }));
        const input = container.querySelector<HTMLInputElement>('input[type="file"]');
        if (!input) {
          throw new Error('file input not rendered');
        }
        fireEvent.change(input, { target: { files: [new File([new Uint8Array([2])], 'b.png', { type: 'image/png' })] } });

        await waitFor(() => { expect(imageOf(container, newId)?.getAttribute('href')).toBe('/api/assets/asset_two'); });
      });
    });
  });
});
