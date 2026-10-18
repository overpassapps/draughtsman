// @vitest-environment jsdom
import { act, fireEvent, render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { useDocumentEditor, type DocumentEditor } from '../editor/useDocumentEditor.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Point } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';
import { membersOfText } from './labelEditing.ts';
import { worldToScreen, type Viewport } from './viewport.ts';
import { createRef } from 'react';

// Double-clicking a compartment of a UML class, or the row area of an ER entity, edits its members as text, one per line (docs/uml-class-compartments.md, docs/er-entities.md).

const size = { width: 800, height: 600 };

const document: Document = parseDocument({
  id: 'doc_members',
  schemaVersion: 1,
  title: 'Members',
  type: 'freeform',
  nodes: [
    { id: 'n_cls', kind: 'class', label: 'Order', shape: 'custom:uml/class', geometry: { x: 0, y: 0, w: 156, h: 84 }, props: { attributes: ['- id: int'], operations: ['+ pay(): void'] } },
    { id: 'n_plain', kind: 'service', label: 'Plain', shape: 'rectangle', geometry: { x: 300, y: 0, w: 100, h: 50 } },
    { id: 'n_ent', kind: 'entity', label: 'Users', shape: 'custom:er/entity', geometry: { x: 500, y: 0, w: 264, h: 56 }, props: { columns: ['PK Id int not null'] } },
  ],
  edges: [],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
});

let editor: DocumentEditor;
let viewport: Viewport = { x: 0, y: 0, zoom: 1 };
const memberChanges: [string, string, string[]][] = [];
const labelChanges: [string, string][] = [];
const handle = createRef<DiagramCanvasHandle>();

function setup(withMembers = true) {
  function Editor() {
    editor = useDocumentEditor('doc_members', document, '"dv1:loaded"');
    return (
      <DiagramCanvas
        ref={handle}
        document={editor.document}
        theme={diagramThemes.overpass}
        size={size}
        fitKey={document}
        onViewportChange={(next) => {
          viewport = next;
        }}
        onLabelChange={(id, label) => {
          labelChanges.push([id, label]);
          editor.setLabel(id, label);
        }}
        onMembersChange={
          withMembers
            ? (id, part, members) => {
                memberChanges.push([id, part, members]);
                editor.setClassMembers(id, part, members);
              }
            : undefined
        }
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
  /** Double-clicks the class at a node-local point, through the same screen mapping the canvas uses. */
  const doubleClickAt = (id: string, local: Point): void => {
    const node = editor.document.nodes.find((n) => n.id === id);
    const shape = root.querySelector<SVGElement>(`g[data-id="${id}"] .shape`);
    if (!node?.geometry || !shape) {
      throw new Error(`no node ${id}`);
    }
    const screen = worldToScreen(viewport, { x: node.geometry.x + local.x, y: node.geometry.y + local.y });
    fireEvent.doubleClick(shape, { clientX: screen.x, clientY: screen.y });
  };
  const field = (): HTMLElement | null => root.querySelector<HTMLElement>('.label-editor');
  const openField = (): HTMLElement => {
    const found = field();
    if (!found) {
      throw new Error('member editor not open');
    }
    return found;
  };
  const node = (id: string) => editor.document.nodes.find((n) => n.id === id);
  return { ...result, canvas, doubleClickAt, field, openField, node };
}

describe('member editing', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    memberChanges.length = 0;
    labelChanges.length = 0;
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(true);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('opens the attributes over their compartment on a double-click there, one member per line', () => {
    const { doubleClickAt, field, container } = setup();
    // The class is 84 tall: name band 0-28, attributes 28-56, operations 56-84.
    doubleClickAt('n_cls', { x: 40, y: 40 });
    const input = field();
    expect(input?.getAttribute('data-editing-part')).toBe('attributes');
    expect(input?.textContent).toBe('- id: int');
    // The drawn attributes give way to the field; the name and the operations stay drawn.
    const drawn = [...container.querySelectorAll('g[data-id="n_cls"] text')].map((text) => text.textContent);
    expect(drawn).toContain('Order');
    expect(drawn).toContain('pay(): void');
    expect(drawn).not.toContain('id: int');
  });

  it('opens the operations from a double-click in the operations compartment, and the name band still edits the label', () => {
    const first = setup();
    first.doubleClickAt('n_cls', { x: 40, y: 70 });
    expect(first.field()?.getAttribute('data-editing-part')).toBe('operations');
    expect(first.field()?.textContent).toBe('+ pay(): void');
    first.unmount();

    const second = setup();
    second.doubleClickAt('n_cls', { x: 40, y: 12 });
    expect(second.field()?.hasAttribute('data-editing-part')).toBe(false);
    expect(second.field()?.textContent).toBe('Order');
  });

  it('commits the lines as the compartment and refits the box in one step; Escape discards', () => {
    const { doubleClickAt, field, openField, node } = setup();
    doubleClickAt('n_cls', { x: 40, y: 40 });
    const input = openField();
    input.textContent = '- id: int\n\n+ total: money  \n# audit: Log';
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(memberChanges).toEqual([['n_cls', 'attributes', ['- id: int', '+ total: money', '# audit: Log']]]);
    expect(node('n_cls')?.props).toEqual({ attributes: ['- id: int', '+ total: money', '# audit: Log'], operations: ['+ pay(): void'] });
    // Header 28 + three attribute rows (64) + one operation row (28): the box was 84 and is now 120.
    expect(node('n_cls')?.geometry?.h).toBe(120);
    expect(field()).toBeNull();

    act(() => {
      editor.undo();
    });
    expect(node('n_cls')?.props).toEqual({ attributes: ['- id: int'], operations: ['+ pay(): void'] });
    expect(node('n_cls')?.geometry?.h).toBe(84);

    doubleClickAt('n_cls', { x: 40, y: 40 });
    openField().textContent = 'thrown away';
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(memberChanges.length).toBe(1);
    expect(node('n_cls')?.props?.attributes).toEqual(['- id: int']);
  });

  it('falls back to the name when the canvas takes no member lists, and never edits members of another shape', () => {
    const noMembers = setup(false);
    noMembers.doubleClickAt('n_cls', { x: 40, y: 40 });
    expect(noMembers.field()?.hasAttribute('data-editing-part')).toBe(false);
    expect(noMembers.field()?.textContent).toBe('Order');
    noMembers.unmount();

    const plain = setup();
    plain.doubleClickAt('n_plain', { x: 50, y: 40 });
    expect(plain.field()?.hasAttribute('data-editing-part')).toBe(false);
    expect(plain.field()?.textContent).toBe('Plain');
  });

  it('offers Edit attributes and Edit operations only with exactly one class selected', () => {
    const { field } = setup();
    const actions = () => handle.current?.actions ?? [];
    const editAttributes = () => actions().find((action) => action.id === 'canvas.editAttributes');
    expect(editAttributes()?.enabled()).toBe(false);
    act(() => {
      handle.current?.select(new Set(['n_plain']));
    });
    expect(editAttributes()?.enabled()).toBe(false);
    act(() => {
      handle.current?.select(new Set(['n_cls']));
    });
    expect(editAttributes()?.enabled()).toBe(true);
    expect(actions().find((action) => action.id === 'canvas.editOperations')?.enabled()).toBe(true);
    act(() => {
      handle.current?.select(new Set(['n_cls', 'n_plain']));
    });
    expect(editAttributes()?.enabled()).toBe(false);

    act(() => {
      handle.current?.select(new Set(['n_cls']));
    });
    act(() => {
      actions().find((action) => action.id === 'canvas.editOperations')?.run();
    });
    expect(field()?.getAttribute('data-editing-part')).toBe('operations');
  });
});

describe('column editing on an ER entity', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    memberChanges.length = 0;
    vi.spyOn(globalThis.document, 'hasFocus').mockReturnValue(true);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('opens the columns over the row area on a double-click below the name band, and the name band still edits the label', () => {
    const rows = setup();
    // The entity is 56 tall: name band 0-28, rows 28-56.
    rows.doubleClickAt('n_ent', { x: 60, y: 40 });
    expect(rows.field()?.getAttribute('data-editing-part')).toBe('columns');
    expect(rows.field()?.textContent).toBe('PK Id int not null');
    // The drawn rows give way to the field; the name stays drawn.
    const drawn = [...rows.container.querySelectorAll('g[data-id="n_ent"] text')].map((text) => text.textContent);
    expect(drawn).toEqual(['Users']);
    rows.unmount();

    const name = setup();
    name.doubleClickAt('n_ent', { x: 60, y: 12 });
    expect(name.field()?.hasAttribute('data-editing-part')).toBe(false);
    expect(name.field()?.textContent).toBe('Users');
  });

  it('commits the lines as columns and refits the box in one step; Escape discards', () => {
    const { doubleClickAt, field, openField, node } = setup();
    doubleClickAt('n_ent', { x: 60, y: 40 });
    const input = openField();
    input.textContent = 'PK Id int not null\n\nFK RoleId int not null  \nEmail nvarchar(100) null';
    fireEvent.keyDown(input, { key: 'Enter', metaKey: true });
    expect(memberChanges).toEqual([['n_ent', 'columns', ['PK Id int not null', 'FK RoleId int not null', 'Email nvarchar(100) null']]]);
    expect(node('n_ent')?.props).toEqual({ columns: ['PK Id int not null', 'FK RoleId int not null', 'Email nvarchar(100) null'] });
    // Name band 28 + three rows (64): the box was 56 and is now 92.
    expect(node('n_ent')?.geometry?.h).toBe(92);
    expect(field()).toBeNull();

    act(() => {
      editor.undo();
    });
    expect(node('n_ent')?.props).toEqual({ columns: ['PK Id int not null'] });
    expect(node('n_ent')?.geometry?.h).toBe(56);

    doubleClickAt('n_ent', { x: 60, y: 40 });
    openField().textContent = 'thrown away';
    fireEvent.keyDown(openField(), { key: 'Escape' });
    expect(memberChanges.length).toBe(1);
    expect(node('n_ent')?.props?.columns).toEqual(['PK Id int not null']);
  });

  it('offers Edit columns only for one entity, and Edit attributes and operations never for it', () => {
    const { field } = setup();
    const actions = () => handle.current?.actions ?? [];
    const enabled = (id: string) => actions().find((action) => action.id === id)?.enabled();
    act(() => {
      handle.current?.select(new Set(['n_ent']));
    });
    expect(enabled('canvas.editColumns')).toBe(true);
    expect(enabled('canvas.editAttributes')).toBe(false);
    expect(enabled('canvas.editOperations')).toBe(false);
    act(() => {
      handle.current?.select(new Set(['n_cls']));
    });
    expect(enabled('canvas.editColumns')).toBe(false);
    expect(enabled('canvas.editAttributes')).toBe(true);
    act(() => {
      handle.current?.select(new Set(['n_ent', 'n_plain']));
    });
    expect(enabled('canvas.editColumns')).toBe(false);

    act(() => {
      handle.current?.select(new Set(['n_ent']));
    });
    act(() => {
      actions().find((action) => action.id === 'canvas.editColumns')?.run();
    });
    expect(field()?.getAttribute('data-editing-part')).toBe('columns');
    expect(actions().find((action) => action.id === 'canvas.editColumns')?.label).toBe('Edit columns');
  });
});

describe('membersOfText', () => {
  it('splits on line breaks, trims trailing space and drops blank lines, keeping order and inner spacing', () => {
    expect(membersOfText('a\r\n\r\n  b  \n\t\nc d')).toEqual(['a', '  b', 'c d']);
    expect(membersOfText('')).toEqual([]);
    expect(membersOfText('  \n ')).toEqual([]);
  });
});
