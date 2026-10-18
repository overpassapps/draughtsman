// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { useDocumentEditor, type DocumentEditor } from '../editor/useDocumentEditor.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { DiagramCanvas } from './DiagramCanvas.tsx';
import { LABEL_PADDING, LABEL_TEXT_LAYOUT, type LineBreaker } from './text.ts';
import { applyTransform } from './transformTestSupport.ts';

// Checklist items 13 (open over the label, commit on click elsewhere or Cmd/Ctrl+Enter), 14 (Escape discards) and 15
// (the field and the rendered label break lines alike).

const size = { width: 800, height: 600 };

const document: Document = parseDocument({
  id: 'doc_label',
  schemaVersion: 1,
  title: 'Labels',
  type: 'flowchart',
  nodes: [
    { id: 'n_a', kind: 'service', label: 'Orders', shape: 'rectangle', geometry: { x: 0, y: 0, w: 100, h: 50 } },
    { id: 'n_b', kind: 'service', label: 'Billing', shape: 'rectangle', geometry: { x: 200, y: 0, w: 100, h: 50, rotation: 30 } },
  ],
  edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta: { createdAt: '2026-09-22T00:00:00.000Z', updatedAt: '2026-09-22T00:00:00.000Z', createdBy: 't' },
});

let editor: DocumentEditor;
const labelChanges: [string, string][] = [];

function setup(breakLines?: LineBreaker) {
  // The canvas wired to the real editor state, as DocumentViewer wires it.
  function Editor() {
    editor = useDocumentEditor('doc_label', document, '"dv1:loaded"');
    return (
      <DiagramCanvas
        document={editor.document}
        theme={diagramThemes.overpass}
        size={size}
        fitKey={document}
        breakLines={breakLines}
        onMoveNodes={editor.moveNodes}
        onMoveEnd={editor.moveEnd}
        onLabelChange={(id, label) => {
          labelChanges.push([id, label]);
          editor.setLabel(id, label);
        }}
      />
    );
  }
  const result = render(<Editor />);
  const canvas = result.container.querySelector<HTMLElement>('.diagram-canvas');
  if (!canvas) {
    throw new Error('canvas not rendered');
  }
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  const root = result.container;
  const shape = (id: string): SVGElement => {
    const found = root.querySelector<SVGElement>(`g[data-id="${id}"] .shape`);
    if (!found) {
      throw new Error(`no shape for ${id}`);
    }
    return found;
  };
  let pointerId = 0;
  const click = (id: string, init: { shiftKey?: boolean } = {}): void => {
    pointerId += 1;
    fireEvent.pointerDown(shape(id), { pointerId, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10, ...init });
    fireEvent.pointerUp(canvas, { pointerId });
  };
  const doubleClick = (id: string): void => {
    click(id);
    click(id);
    fireEvent.doubleClick(shape(id));
  };
  const pressEmptyCanvas = (): void => {
    pointerId += 1;
    fireEvent.pointerDown(canvas, { pointerId, button: 0, pointerType: 'mouse', clientX: 700, clientY: 550 });
    fireEvent.pointerUp(canvas, { pointerId });
  };
  const drag = (id: string, dx: number): void => {
    pointerId += 1;
    fireEvent.pointerDown(shape(id), { pointerId, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(canvas, { pointerId, clientX: 10 + dx, clientY: 10 });
    flushFrame();
    fireEvent.pointerUp(canvas, { pointerId });
  };
  const field = (): HTMLElement | null => root.querySelector<HTMLElement>('.label-editor');
  const openField = (): HTMLElement => {
    const found = field();
    if (!found) {
      throw new Error('label editor not open');
    }
    return found;
  };
  // The lines the renderer drew, one per tspan; none while the editor is drawing the label instead.
  const renderedLabel = (id: string): (string | null)[] => [...root.querySelectorAll(`g[data-id="${id}"] text.label tspan`)].map((line) => line.textContent);
  return { ...result, canvas, shape, click, doubleClick, pressEmptyCanvas, drag, field, openField, renderedLabel };
}

function flushFrame(): void {
  act(() => {
    vi.runAllTimers();
  });
}

const labelOf = (id: string): string | undefined => editor.document.nodes.find((n) => n.id === id)?.label;
const xOf = (id: string): number | undefined => editor.document.nodes.find((n) => n.id === id)?.geometry?.x;
const undo = (): void => {
  act(() => {
    editor.undo();
  });
};

describe('label editing', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    labelChanges.length = 0;
    // jsdom reports no focus while focus moves between elements; a real window the user is working in has it.
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(true);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('opens on a double-click with the label selected, and hides the rendered label while it is open', () => {
    const { doubleClick, field, openField, renderedLabel } = setup();
    expect(field()).toBeNull();
    expect(renderedLabel('n_a')).toEqual(['Orders']);

    doubleClick('n_a');
    const input = openField();
    expect(input.textContent).toBe('Orders');
    expect(globalThis.document.activeElement).toBe(input);
    expect(globalThis.getSelection()?.toString()).toBe('Orders');
    expect(renderedLabel('n_a')).toEqual([]);
    expect(renderedLabel('n_b')).toEqual(['Billing']);
  });

  // P1: nothing used to say why Enter does not finish a label, or that a click away keeps it.
  it('says how a label edit ends while it is open: Enter adds a line, Ctrl/Cmd+Enter or a click away finishes, Esc cancels', () => {
    const { doubleClick, openField, canvas, container } = setup();
    expect(container.querySelector('.label-edit-hint')).toBeNull();
    doubleClick('n_a');
    openField();
    const hint = container.querySelector('.label-edit-hint');
    expect(hint?.textContent).toMatch(/^Enter adds a line\. (Ctrl|Cmd)\+Enter or a click away finishes; Esc cancels\.$/);
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(container.querySelector('.label-edit-hint')).toBeNull();
    expect(canvas).toBeTruthy();
  });

  it('says Enter finishes a connector\'s one-line label, and is gone when it closes', () => {
    const { click, openField, container } = setup();
    click('n_a');
    const hit = container.querySelector('g[data-id="e_1"] .hit-area');
    const surface = container.querySelector('.diagram-canvas');
    if (hit === null || surface === null) {
      throw new Error('edge or canvas not rendered');
    }
    fireEvent.pointerDown(hit, { pointerId: 40, button: 0, pointerType: 'mouse', clientX: 150, clientY: 25 });
    fireEvent.pointerUp(surface, { pointerId: 40 });
    fireEvent.keyDown(surface, { key: 'Enter' });
    expect(openField().getAttribute('aria-label')).not.toBe(null);
    expect(container.querySelector('.label-edit-hint')?.textContent).toBe('Enter to finish, Esc to cancel.');
    fireEvent.keyDown(openField(), { key: 'Enter' });
    expect(container.querySelector('.label-edit-hint')).toBeNull();
  });

  it('opens on a double-click the browser targets at the canvas under pointer capture', () => {
    const { canvas, click, field } = setup();
    click('n_a');
    fireEvent.doubleClick(canvas);
    expect(field()?.getAttribute('data-editing-id')).toBe('n_a');
  });

  it('does not open on a double-click after a press on empty canvas', () => {
    const { canvas, pressEmptyCanvas, field } = setup();
    pressEmptyCanvas();
    fireEvent.doubleClick(canvas);
    expect(field()).toBeNull();
  });

  it('opens on Enter with exactly one node or edge selected, and not otherwise', () => {
    const { canvas, click, field, pressEmptyCanvas } = setup();
    fireEvent.keyDown(canvas, { key: 'Enter' });
    expect(field()).toBeNull();

    click('n_a');
    click('n_b', { shiftKey: true });
    fireEvent.keyDown(canvas, { key: 'Enter' });
    expect(field()).toBeNull();

    pressEmptyCanvas();
    const edge = canvas.querySelector('g[data-id="e_1"] polyline');
    if (!edge) {
      throw new Error('edge not rendered');
    }
    fireEvent.pointerDown(edge, { pointerId: 99, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(canvas, { pointerId: 99 });
    fireEvent.keyDown(canvas, { key: 'Enter' });
    expect(field()?.getAttribute('data-editing-id')).toBe('e_1');
    fireEvent.keyDown(field() ?? canvas, { key: 'Escape' });
    expect(field()).toBeNull();

    click('n_b');
    fireEvent.keyDown(canvas, { key: 'Enter' });
    expect(field()?.getAttribute('data-editing-id')).toBe('n_b');
  });

  it('lays the field over the node where the SVG draws it, with the renderer’s font, at the current zoom', () => {
    const { canvas, container, click, openField } = setup();
    // Zoom away from 1 first, so a field that ignored the zoom would land somewhere else.
    fireEvent.keyDown(canvas, { key: '=', metaKey: true });
    flushFrame();
    const text = container.querySelector('g[data-id="n_b"] text.label');
    const fontFamily = text?.getAttribute('font-family');
    const fontSize = text?.getAttribute('font-size');
    expect(fontFamily).toBeTruthy();

    click('n_b');
    fireEvent.keyDown(canvas, { key: 'Enter' });
    const input = openField();
    const frame = input.closest<HTMLElement>('.label-editor-frame');
    const world = container.querySelector('g.world')?.getAttribute('transform');
    const body = container.querySelector('g[data-id="n_b"] > g')?.getAttribute('transform');
    if (!frame || !world || !body) {
      throw new Error('missing transforms');
    }
    expect(world).not.toContain('scale(1)');
    // Every corner of the rotated node lands on the same screen point through the SVG's groups and the field's frame.
    for (const corner of [{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 50 }, { x: 0, y: 50 }]) {
      expect(applyTransform(frame.style.transform, corner)).toEqual(applyTransform([world, body], corner));
    }
    expect(input.style.fontFamily).toBe(fontFamily);
    expect(input.style.fontSize).toBe(`${String(fontSize)}px`);
  });

  it('lays the field out with the CSS and width the renderer breaks lines at, and draws the breaker’s lines once it closes', () => {
    const calls: [string, number, string][] = [];
    // A breaker whose lines no wrapping could produce, so the drawn lines can only have come from it.
    const breakLines: LineBreaker = (text, width, font) => {
      calls.push([text, width, font]);
      return text.split(' ').map((word, i) => `${String(i)}:${word}`);
    };
    const { container, doubleClick, openField, renderedLabel } = setup(breakLines);
    expect(renderedLabel('n_a')).toEqual(['0:Orders']);
    const drawnWith = calls.find(([text]) => text === 'Orders');

    doubleClick('n_a');
    const input = openField();
    const box = input.closest<HTMLElement>('.label-editor-box');
    if (!box || drawnWith === undefined) {
      throw new Error('label editor box missing');
    }
    // The field's text box is exactly as wide as the width the renderer breaks at, in the same font.
    expect(Number.parseFloat(box.style.width) - Number.parseFloat(box.style.paddingLeft) - Number.parseFloat(box.style.paddingRight)).toBe(drawnWith[1]);
    expect(box.style.paddingLeft).toBe(`${String(LABEL_PADDING)}px`);
    expect(drawnWith[2]).toContain(input.style.fontFamily);
    expect(drawnWith[2]).toContain(input.style.fontSize);
    // And laid out with the very CSS the DOM breaker lays the label out with.
    for (const [property, value] of Object.entries(LABEL_TEXT_LAYOUT)) {
      expect(input.style.getPropertyValue(property.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`))).toBe(String(value));
    }

    input.textContent = 'Order  service  platform';
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(labelOf('n_a')).toBe('Order  service  platform');
    expect(calls.filter(([text]) => text === 'Order  service  platform')).toEqual([['Order  service  platform', drawnWith[1], drawnWith[2]]]);
    expect(renderedLabel('n_a')).toEqual(['0:Order', '1:', '2:service', '3:', '4:platform']);
    // Drawn with its spaces as typed, as the field showed them.
    expect(container.querySelector('g[data-id="n_a"] text.label')?.getAttribute('xml:space')).toBe('preserve');
  });

  it('trims a leading and a trailing line break from the committed label, and keeps one in the middle', () => {
    const { doubleClick, openField } = setup();
    doubleClick('n_a');
    const input = openField();
    input.textContent = '\n\nOrders\nservice\n';
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(labelOf('n_a')).toBe('Orders\nservice');
  });

  it('commits on Cmd/Ctrl+Enter as exactly one undo step, and returns focus to the canvas', () => {
    const { canvas, drag, doubleClick, openField, field, renderedLabel } = setup();
    drag('n_a', 40);
    expect(xOf('n_a')).toBe(40);

    doubleClick('n_a');
    openField().textContent = 'Order service';
    fireEvent.keyDown(openField(), { key: 'Enter', ctrlKey: true });
    expect(field()).toBeNull();
    expect(labelOf('n_a')).toBe('Order service');
    expect(renderedLabel('n_a')).toEqual(['Order', 'service']);
    expect(globalThis.document.activeElement).toBe(canvas);

    // One undo takes back the label and nothing else; the next takes back the drag before it.
    undo();
    expect(labelOf('n_a')).toBe('Orders');
    expect(xOf('n_a')).toBe(40);
    undo();
    expect(xOf('n_a')).toBe(0);
  });

  it('commits when a press elsewhere takes focus, and keeps plain Enter as a line break', () => {
    const { doubleClick, openField, field, pressEmptyCanvas } = setup();
    doubleClick('n_a');
    const input = openField();
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(field()).not.toBeNull();
    input.innerHTML = 'Order<div>service</div>';
    pressEmptyCanvas();
    expect(field()).toBeNull();
    expect(labelOf('n_a')).toBe('Order\nservice');
  });

  it('stores one line for a single trailing Enter (Chrome writes "\\n\\n"), and no trailing break at all', () => {
    const { doubleClick, openField, pressEmptyCanvas } = setup();
    doubleClick('n_a');
    const input = openField();
    input.textContent = 'Billing\n\n';
    pressEmptyCanvas();
    expect(labelOf('n_a')).toBe('Billing');
  });

  it('stays open and writes nothing when the window, not the canvas, takes focus', () => {
    const { drag, doubleClick, openField, field } = setup();
    drag('n_a', 40);
    doubleClick('n_a');
    const input = openField();
    input.textContent = 'Half typed';
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(false);
    fireEvent.blur(input);
    expect(field()).toBe(input);
    expect(input.textContent).toBe('Half typed');
    expect(labelChanges).toEqual([]);
    expect(labelOf('n_a')).toBe('Orders');
    // The drag is still the top of the stack: the window blur left nothing above it.
    undo();
    expect(xOf('n_a')).toBe(0);
    expect(labelOf('n_a')).toBe('Orders');
  });

  it('writes nothing and records no undo step when an unchanged label is committed', () => {
    const { drag, doubleClick, openField } = setup();
    drag('n_a', 40);

    doubleClick('n_a');
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(labelChanges).toEqual([['n_a', 'Orders']]);
    // The drag is still the top of the stack: an unchanged label left nothing above it.
    undo();
    expect(xOf('n_a')).toBe(0);
    expect(labelOf('n_a')).toBe('Orders');
  });

  it('discards on Escape: the model, the undo stack and the save state are untouched', () => {
    const { canvas, drag, doubleClick, openField, field, renderedLabel } = setup();
    drag('n_a', 40);

    doubleClick('n_a');
    openField().textContent = 'Something else';
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(field()).toBeNull();
    expect(labelChanges).toEqual([]);
    expect(labelOf('n_a')).toBe('Orders');
    expect(renderedLabel('n_a')).toEqual(['Orders']);
    expect(globalThis.document.activeElement).toBe(canvas);

    // Nothing sits above the drag on the undo stack, and there is nothing below it either.
    undo();
    expect(xOf('n_a')).toBe(0);
    expect(labelOf('n_a')).toBe('Orders');
    undo();
    expect(labelOf('n_a')).toBe('Orders');
  });

  it('writes nothing to the Y.Doc on Escape', () => {
    const { doubleClick, openField } = setup();
    // Save state tells whether the Y.Doc changed at all: "saved" until its first update, "unsaved" after.
    expect(editor.saveStatus).toBe('saved');
    doubleClick('n_a');
    openField().textContent = 'Something else';
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(editor.saveStatus).toBe('saved');
    // The same edit committed does reach the Y.Doc, so the check above can fail.
    doubleClick('n_a');
    openField().textContent = 'Something else';
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(editor.saveStatus).toBe('unsaved');
  });

  it('keeps every key it receives from the canvas and the window-level undo', () => {
    const { canvas, container, drag, doubleClick, openField, field } = setup();
    drag('n_a', 40);
    const reachedCanvas: string[] = [];
    const reachedWindow: string[] = [];
    const onCanvas = (e: KeyboardEvent): void => void reachedCanvas.push(e.key);
    const onWindow = (e: KeyboardEvent): void => void reachedWindow.push(e.key);
    canvas.addEventListener('keydown', onCanvas);
    window.addEventListener('keydown', onWindow);
    const worldBefore = container.querySelector('g.world')?.getAttribute('transform');

    doubleClick('n_a');
    const input = openField();
    for (const init of [{ key: 'Delete' }, { key: 'Backspace' }, { key: 'ArrowLeft' }, { key: 'z', metaKey: true }, { key: '=', metaKey: true }, { key: ' ' }]) {
      fireEvent.keyDown(input, init);
    }
    flushFrame();
    expect(reachedCanvas).toEqual([]);
    expect(reachedWindow).toEqual([]);
    expect(xOf('n_a')).toBe(40);
    expect(container.querySelector('g.world')?.getAttribute('transform')).toBe(worldBefore);
    expect(canvas.classList.contains('is-pannable')).toBe(false);
    expect(field()).not.toBeNull();

    // The same keys at the canvas do reach it once the field is closed, and Cmd/Ctrl+Z there undoes the drag.
    fireEvent.keyDown(input, { key: 'Escape' });
    fireEvent.keyDown(canvas, { key: 'Delete' });
    fireEvent.keyDown(canvas, { key: 'z', metaKey: true });
    expect(reachedCanvas).toEqual(['Delete', 'z']);
    expect(reachedWindow).toEqual(['Delete', 'z']);
    expect(xOf('n_a')).toBe(0);
    canvas.removeEventListener('keydown', onCanvas);
    window.removeEventListener('keydown', onWindow);
  });

  it('keeps a press inside the field from reaching the canvas', () => {
    const { container, doubleClick, openField, field } = setup();
    doubleClick('n_a');
    fireEvent.pointerDown(openField(), { pointerId: 50, button: 0, pointerType: 'mouse', clientX: 10, clientY: 10 });
    fireEvent.doubleClick(openField());
    expect(field()).not.toBeNull();
    expect(container.querySelector('.selection-node[data-selection-id="n_a"]')).not.toBeNull();
  });

  it('pastes rich text as plain text and stores no markup', () => {
    const { doubleClick, openField } = setup();
    doubleClick('n_a');
    const input = openField();
    fireEvent.paste(input, { clipboardData: { getData: (type: string) => (type === 'text/plain' ? 'Payments API' : '<b>Payments</b> <i>API</i>') } });
    expect(input.innerHTML).toBe('Payments API');
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(labelOf('n_a')).toBe('Payments API');
  });

  it('stores only the text of markup that got into the field some other way', () => {
    const { doubleClick, openField } = setup();
    doubleClick('n_a');
    openField().innerHTML = '<b>Bold</b> <a href="https://example.com">link</a>';
    fireEvent.keyDown(openField(), { key: 'Enter', metaKey: true });
    expect(labelOf('n_a')).toBe('Bold link');
  });
});

describe('label editing without an editor', () => {
  it('does not open on a read-only canvas or one with nowhere to send the label', () => {
    for (const props of [{ readOnly: true, onLabelChange: vi.fn() }, {}]) {
      const { container, unmount } = render(<DiagramCanvas document={document} theme={diagramThemes.overpass} size={size} {...props} />);
      const shape = container.querySelector('g[data-id="n_a"] .shape');
      if (!shape) {
        throw new Error('no shape');
      }
      fireEvent.doubleClick(shape);
      expect(container.querySelector('.label-editor')).toBeNull();
      unmount();
    }
  });

  it('closes without sending anything when the edited node disappears from the document', () => {
    const onLabelChange = vi.fn();
    const { container, rerender } = render(<DiagramCanvas document={document} theme={diagramThemes.overpass} size={size} fitKey={document} onLabelChange={onLabelChange} />);
    const shape = container.querySelector('g[data-id="n_a"] .shape');
    if (!shape) {
      throw new Error('no shape');
    }
    fireEvent.doubleClick(shape);
    expect(container.querySelector('.label-editor')).not.toBeNull();

    const without: Document = { ...document, nodes: document.nodes.filter((n) => n.id !== 'n_a'), edges: [] };
    rerender(<DiagramCanvas document={without} theme={diagramThemes.overpass} size={size} fitKey={document} onLabelChange={onLabelChange} />);
    expect(container.querySelector('.label-editor')).toBeNull();
    expect(onLabelChange).not.toHaveBeenCalled();
  });
});
