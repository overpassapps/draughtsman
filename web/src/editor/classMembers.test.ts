// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import type { Document } from '../model/types.ts';
import { useDocumentEditor } from './useDocumentEditor.ts';

const loaded: Document = {
  id: 'doc_cls',
  schemaVersion: 1,
  title: 'Classes',
  type: 'freeform',
  nodes: [
    { id: 'n_cls', kind: 'class', label: 'Order', shape: 'custom:uml/class', geometry: { x: 10, y: 20, w: 300, h: 84, rotation: 15 }, props: { attributes: ['- id: int'], operations: ['+ pay(): void'], owner: 'billing' } },
    { id: 'n_if', kind: 'interface', label: 'Repo', shape: 'custom:uml/interface', geometry: { x: 400, y: 20, w: 156, h: 102 } },
    { id: 'n_box', kind: 'service', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 300, w: 100, h: 50 } },
    { id: 'n_new', kind: 'class', label: 'Unplaced', shape: 'custom:uml/class' },
    { id: 'n_ent', kind: 'entity', label: 'Users', shape: 'custom:er/entity', geometry: { x: 700, y: 20, w: 300, h: 56, rotation: 5 }, props: { columns: ['PK Id int not null'], owner: 'auth' } },
  ],
  edges: [],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 't' },
};

function open() {
  return renderHook(() => useDocumentEditor('doc_cls', loaded, '"dv1:loaded"'));
}

const node = (doc: Document, id: string) => doc.nodes.find((n) => n.id === id);

describe('setClassMembers', () => {
  it('writes the list, keeps other props, refits the height to the content and the width only upward, all as one undo step', () => {
    const { result } = open();
    act(() => {
      result.current.setClassMembers('n_cls', 'attributes', ['- id: int', '+ total: money', '# audit: Log']);
    });
    expect(node(result.current.document, 'n_cls')).toMatchObject({
      props: { attributes: ['- id: int', '+ total: money', '# audit: Log'], operations: ['+ pay(): void'], owner: 'billing' },
      // Header 28 + three attribute rows 64 + one operation row 28; the 300 wide box is not narrowed; position and rotation stay.
      geometry: { x: 10, y: 20, w: 300, h: 120, rotation: 15 },
    });
    act(() => {
      result.current.undo();
    });
    expect(result.current.document).toEqual(loaded);
    act(() => {
      result.current.redo();
    });
    expect(node(result.current.document, 'n_cls')?.geometry?.h).toBe(120);
  });

  it('widens the box to a long member and drops the prop when the list empties', () => {
    const { result } = open();
    act(() => {
      result.current.setClassMembers('n_if', 'operations', ['+ findByCustomerAndStatus(customer: Customer): List']);
    });
    const widened = node(result.current.document, 'n_if');
    expect(widened?.geometry?.w).toBe(Math.ceil(16 + 12 + 'findByCustomerAndStatus(customer: Customer): List'.length * 7.5));
    // The interface's stereotype row: header 46 + a one-row attributes compartment 28 + one operation row 28.
    expect(widened?.geometry?.h).toBe(102);

    act(() => {
      result.current.setClassMembers('n_cls', 'attributes', []);
    });
    const cleared = node(result.current.document, 'n_cls');
    expect(cleared?.props).toEqual({ operations: ['+ pay(): void'], owner: 'billing' });
    expect(cleared?.geometry?.h).toBe(84);
  });

  it('writes nothing for an unchanged list, a node that is not a class, or an unknown node', () => {
    const { result } = open();
    const before = result.current.document;
    act(() => {
      result.current.setClassMembers('n_cls', 'attributes', ['- id: int']);
      result.current.setClassMembers('n_box', 'attributes', ['- x']);
      result.current.setClassMembers('n_missing', 'operations', ['+ y()']);
    });
    expect(result.current.document).toEqual(before);
    // No step was recorded, so there is nothing to undo.
    act(() => {
      result.current.undo();
    });
    expect(result.current.document).toEqual(before);
  });

  it('sets the members of a class that has no geometry yet without inventing one', () => {
    const { result } = open();
    act(() => {
      result.current.setClassMembers('n_new', 'operations', ['+ go()']);
    });
    const unplaced = node(result.current.document, 'n_new');
    expect(unplaced?.props).toEqual({ operations: ['+ go()'] });
    expect(unplaced?.geometry).toBeUndefined();
  });
});

describe('setClassMembers on an ER entity', () => {
  it('writes the columns, keeps other props, refits the height to the rows and never narrows, as one undo step', () => {
    const { result } = open();
    act(() => {
      result.current.setClassMembers('n_ent', 'columns', ['PK Id int not null', 'Email nvarchar(100) null', 'Name nvarchar(50) not null']);
    });
    expect(node(result.current.document, 'n_ent')).toMatchObject({
      props: { columns: ['PK Id int not null', 'Email nvarchar(100) null', 'Name nvarchar(50) not null'], owner: 'auth' },
      // Name band 28 + three rows 64; the 300 wide box is not narrowed; position and rotation stay.
      geometry: { x: 700, y: 20, w: 300, h: 92, rotation: 5 },
    });
    act(() => {
      result.current.undo();
    });
    expect(result.current.document).toEqual(loaded);
  });

  it('refuses a part the shape does not have, in both directions', () => {
    const { result } = open();
    const before = result.current.document;
    act(() => {
      result.current.setClassMembers('n_ent', 'attributes', ['- x']);
      result.current.setClassMembers('n_cls', 'columns', ['PK Id int']);
    });
    expect(result.current.document).toEqual(before);
  });

  it('widens the box for a wide row and drops the prop when the list empties', () => {
    const { result } = open();
    act(() => {
      result.current.setClassMembers('n_ent', 'columns', ['PK VeryLongColumnNameForAWideTable nvarchar(400) not null']);
    });
    // key 16.5 + name 31 * 7.5 + type 13 * 7.5 + null 66 + three gaps 36 + padding 16.
    expect(node(result.current.document, 'n_ent')?.geometry?.w).toBe(Math.ceil(16.5 + 232.5 + 97.5 + 66 + 36 + 16));
    act(() => {
      result.current.setClassMembers('n_ent', 'columns', []);
    });
    const cleared = node(result.current.document, 'n_ent');
    expect(cleared?.props).toEqual({ owner: 'auth' });
    expect(cleared?.geometry?.h).toBe(56);
  });
});
