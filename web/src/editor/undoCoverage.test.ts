// @vitest-environment jsdom
/**
 * The undo audit for checklist item 16: every kind of edit the editor can write, each checked for one Cmd/Ctrl+Z
 * restoring exactly the model before it and one Cmd/Ctrl+Shift+Z putting exactly the edited model back, through the
 * window shortcuts the editor registers (not by calling undo directly). Each edit follows an earlier, finished edit, so
 * an undo that reached past its own step, or a step that swallowed the one before it, shows as a wrong model. Then a
 * long session: well over a hundred steps, a save in the middle, undone one step at a time back to the loaded document
 * and redone to the end, every intermediate model compared.
 */
import { act, fireEvent, renderHook } from '@testing-library/react';
import { StrictMode } from 'react';
import { copy } from '../model/clipboard.ts';
import { addKeyframe, duplicateKeyframe, moveKeyframe, removeKeyframe, setLoop, updateKeyframe } from '../model/flythrough.ts';
import { createParticipant } from '../model/sequenceEdit.ts';
import { checkoutFragments, checkoutSequence } from '../model/sequenceFixture.ts';
import { sequenceMemberships } from '../model/sequenceScene.ts';
import type { Document, Geometry, Id, Point } from '../model/types.ts';
import { useDocumentEditor, type DocumentEditor } from './useDocumentEditor.ts';

const RIGHT = { id: 'right', x: 1, y: 0.5 };
const LEFT = { id: 'left', x: 0, y: 0.5 };
const TOP = { id: 'top', x: 0.5, y: 0 };

/** A container with two children and an edge between them, two free nodes, and an orthogonal edge with waypoints. */
const loaded: Document = {
  id: 'doc_undo',
  schemaVersion: 1,
  title: 'Undo audit',
  type: 'architecture',
  nodes: [
    { id: 'c_box', kind: 'container', label: 'Box', shape: 'rectangle', geometry: { x: 0, y: 0, w: 400, h: 200 } },
    { id: 'n_a', kind: 'service', label: 'A', shape: 'rectangle', parent: 'c_box', geometry: { x: 40, y: 40, w: 100, h: 50 }, ports: [LEFT, RIGHT] },
    { id: 'n_b', kind: 'service', label: 'B', shape: 'rectangle', parent: 'c_box', geometry: { x: 240, y: 40, w: 100, h: 50 }, ports: [LEFT, RIGHT] },
    { id: 'n_c', kind: 'service', label: 'C', shape: 'rounded', geometry: { x: 600, y: 0, w: 100, h: 50 }, style: { fill: '#ffffff' } },
    { id: 'n_d', kind: 'database', label: 'D', shape: 'cylinder', geometry: { x: 600, y: 300, w: 100, h: 60 } },
    // No case touches n_z: the edits before and after each case's own edit move it.
    { id: 'n_z', kind: 'service', label: 'Z', shape: 'rectangle', geometry: { x: 0, y: 600, w: 100, h: 50 } },
    // A UML class, for the member-list command (its box refits to the members in the same step).
    { id: 'n_cls', kind: 'class', label: 'Order', shape: 'custom:uml/class', geometry: { x: 1200, y: 0, w: 156, h: 84 }, props: { attributes: ['- id: int'], operations: ['+ pay(): void'] } },
    // An ER entity, for the column-list command (its box refits to the rows in the same step).
    { id: 'n_ent', kind: 'entity', label: 'Users', shape: 'custom:er/entity', geometry: { x: 1500, y: 0, w: 264, h: 56 }, props: { columns: ['PK Id int not null'] } },
    // A pool with two lanes, for the swimlane commands.
    { id: 'n_pool', kind: 'pool', label: 'Pool', shape: 'custom:swimlane/pool', geometry: { x: 0, y: 800, w: 400, h: 240 } },
    { id: 'n_lane1', kind: 'lane', label: 'Lane 1', shape: 'custom:swimlane/lane', parent: 'n_pool', geometry: { x: 0, y: 848, w: 400, h: 96 } },
    { id: 'n_lane2', kind: 'lane', label: 'Lane 2', shape: 'custom:swimlane/lane', parent: 'n_pool', geometry: { x: 0, y: 944, w: 400, h: 96 } },
  ],
  edges: [
    { id: 'e_ab', kind: 'flow', label: 'calls', from: { node: 'n_a', port: 'right' }, to: { node: 'n_b', port: 'left' }, routing: { mode: 'straight' } },
    { id: 'e_cd', kind: 'flow', from: { node: 'n_c' }, to: { node: 'n_d' }, routing: { mode: 'orthogonal', waypoints: [{ x: 650, y: 150 }] } },
    { id: 'e_bc', kind: 'data', from: { node: 'n_b', port: 'right' }, to: { node: 'n_c' }, routing: { mode: 'straight' }, style: { arrowStart: 'none', arrowEnd: 'open' } },
    // A connector onto a row of the ER entity (column port), which follows the column when it is renamed.
    { id: 'e_col', kind: 'association', from: { node: 'n_z' }, to: { node: 'n_ent', port: 'col:Id' }, routing: { mode: 'straight' } },
  ],
  meta: { createdAt: '2026-09-28T00:00:00.000Z', updatedAt: '2026-09-28T00:00:00.000Z', createdBy: 't' },
  // A camera path of two keyframes, for the flythrough commands.
  flythrough: {
    keyframes: [
      { id: 'k_1', view: { x: 0, y: 0, w: 800, h: 500 }, caption: 'Overview' },
      { id: 'k_2', view: { x: 40, y: 40, w: 320, h: 200 }, durationMs: 900, easing: 'easeOut' },
    ],
  },
};

const undoKey = () => fireEvent.keyDown(window, { key: 'z', metaKey: true });
const redoKey = () => fireEvent.keyDown(window, { key: 'z', metaKey: true, shiftKey: true });
// The other platform's modifier, so both halves of "Cmd/Ctrl" are exercised.
const undoCtrl = () => fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
const redoCtrl = () => fireEvent.keyDown(window, { key: 'Z', ctrlKey: true, shiftKey: true });

function geometryOf(document: Document, id: Id): Geometry {
  const geometry = document.nodes.find((node) => node.id === id)?.geometry;
  if (geometry === undefined) {
    throw new Error(`${id} has no geometry`);
  }
  return geometry;
}

/** A gesture as the canvas writes it: several frames on the way to `to`, each a moveNodes call, then one moveEnd. */
function gesture(editor: DocumentEditor, id: Id, to: Partial<Geometry>, frames = 4, waypoints?: (f: number) => ReadonlyMap<Id, Point[]>): void {
  const from = geometryOf(editor.document, id);
  for (let i = 1; i <= frames; i++) {
    const f = i / frames;
    const frame: Geometry = { ...from };
    for (const key of ['x', 'y', 'w', 'h', 'rotation'] as const) {
      const target = to[key];
      if (target !== undefined) {
        frame[key] = (from[key] ?? 0) + (target - (from[key] ?? 0)) * f;
      }
    }
    editor.moveNodes(new Map([[id, frame]]), waypoints?.(f));
  }
}

interface Case {
  name: string;
  /** Checklist wording the case answers to, for the audit table. */
  covers: string;
  /**
   * A gesture ends its own step with moveEnd, so what comes before it must already be closed. A discrete command closes
   * a step before and after itself, so it is checked straight after a gesture that has not ended yet.
   */
  gesture: boolean;
  edit: (editor: () => DocumentEditor) => void | Promise<void>;
}

const cases: Case[] = [
  {
    name: 'move (drag, several frames)',
    covers: 'geometry: move',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_c', { x: 640, y: 20 });
      e().moveEnd();
    },
  },
  {
    name: 'resize (handle drag)',
    covers: 'geometry: resize',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_d', { x: 590, w: 140, h: 90 });
      e().moveEnd();
    },
  },
  {
    name: 'rotate (handle drag)',
    covers: 'geometry: rotate',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_c', { rotation: 45 });
      e().moveEnd();
    },
  },
  {
    name: 'nudge (one arrow press)',
    covers: 'geometry: nudge (item 20)',
    gesture: true,
    edit: (e) => {
      e().moveNodes(new Map([['n_d', { ...geometryOf(e().document, 'n_d'), x: 601 }]]));
      e().moveEnd();
    },
  },
  {
    name: 'container drag (children and inner edge waypoints travel)',
    covers: 'geometry: move with contents (item 26)',
    gesture: true,
    edit: (e) => {
      const start = e().document;
      for (let i = 1; i <= 3; i++) {
        const dx = 10 * i;
        e().moveNodes(
          new Map(['c_box', 'n_a', 'n_b'].map((id) => [id, { ...geometryOf(start, id), x: geometryOf(start, id).x + dx }])),
          new Map([['e_cd', [{ x: 650 + dx, y: 150 }]]]),
        );
      }
      e().moveEnd();
    },
  },
  {
    name: 'drop into a container (drag plus reparent)',
    covers: 'reparenting: into (item 27)',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_c', { x: 150, y: 120 });
      e().reparentNodes(new Map([['n_c', 'c_box']]));
      e().moveEnd();
    },
  },
  {
    name: 'drag out of a container (drag plus reparent)',
    covers: 'reparenting: out of (item 27)',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_a', { x: 450, y: 400 });
      e().reparentNodes(new Map([['n_a', null]]));
      e().moveEnd();
    },
  },
  {
    name: 'node label',
    covers: 'label text',
    gesture: false,
    edit: (e) => {
      e().setLabel('n_c', 'Orders\nservice');
    },
  },
  {
    name: 'edge label (changed)',
    covers: 'label text (item 11)',
    gesture: false,
    edit: (e) => {
      e().setLabel('e_ab', 'publishes');
    },
  },
  {
    name: 'edge label (added where there was none)',
    covers: 'label text (item 11)',
    gesture: false,
    edit: (e) => {
      e().setLabel('e_cd', 'reads');
    },
  },
  {
    name: 'edge label (cleared)',
    covers: 'label text (item 11)',
    gesture: false,
    edit: (e) => {
      e().setLabel('e_ab', '');
    },
  },
  {
    name: 'edge kind',
    covers: 'style: edge kind (item 12)',
    gesture: false,
    edit: (e) => {
      e().setEdgeKind('e_bc', 'dependency');
    },
  },
  {
    name: 'edge arrowheads (start and end set)',
    covers: 'style: edge arrowheads',
    gesture: false,
    edit: (e) => {
      e().setEdgeEnds('e_ab', 'many', 'inheritance');
    },
  },
  {
    name: 'edge arrowheads (reset to default)',
    covers: 'style: edge arrowheads',
    gesture: false,
    edit: (e) => {
      e().setEdgeEnds('e_bc', undefined, undefined);
    },
  },
  {
    name: 'node style (fill across a multi-selection)',
    covers: 'style: node fill',
    gesture: false,
    edit: (e) => {
      e().setNodeStyle(new Set(['n_c', 'n_d']), 'fill', '#e04832');
    },
  },
  {
    name: 'node style (font size)',
    covers: 'style: node font size',
    gesture: false,
    edit: (e) => {
      e().setNodeStyle(new Set(['n_c']), 'fontSize', 18);
    },
  },
  {
    name: 'node style (bold)',
    covers: 'style: node bold',
    gesture: false,
    edit: (e) => {
      e().setNodeStyle(new Set(['n_c']), 'bold', true);
    },
  },
  {
    name: 'node style (reset a field to the theme default)',
    covers: 'style: node reset',
    gesture: false,
    edit: (e) => {
      e().setNodeStyle(new Set(['n_c']), 'fill', undefined);
    },
  },
  {
    name: 'node props (point an image node at an uploaded asset)',
    covers: 'props: node asset',
    gesture: false,
    edit: (e) => {
      e().setNodeProps('n_c', { assetId: 'asset_abc123', fileName: 'logo.png' });
    },
  },
  {
    name: 'class members (attributes edited, box refits)',
    covers: 'props: UML class members',
    gesture: false,
    edit: (e) => {
      e().setClassMembers('n_cls', 'attributes', ['- id: int', '+ total: money']);
    },
  },
  {
    name: 'entity columns (list edited, box refits)',
    covers: 'props: ER entity columns',
    gesture: false,
    edit: (e) => {
      e().setClassMembers('n_ent', 'columns', ['PK Id int not null', 'Email nvarchar(100) null']);
    },
  },
  {
    name: 'entity column renamed (the connector on its row follows, in the same step)',
    covers: 'props: ER entity column rename, edge port',
    gesture: false,
    edit: (e) => {
      e().setClassMembers('n_ent', 'columns', ['PK UserId int not null']);
    },
  },
  {
    name: 'edge style (stroke width across a multi-selection)',
    covers: 'style: edge stroke width',
    gesture: false,
    edit: (e) => {
      e().setEdgeStyle(new Set(['e_ab', 'e_cd']), 'strokeWidth', 3);
    },
  },
  {
    name: 'edge style (dash, then reset)',
    covers: 'style: edge dash',
    gesture: false,
    edit: (e) => {
      e().setEdgeStyle(new Set(['e_bc']), 'dash', 'dashed');
    },
  },
  {
    name: 'edge routing mode',
    covers: 'routing: mode',
    gesture: false,
    edit: (e) => {
      e().setEdgeRoutingMode('e_ab', 'curved');
    },
  },
  {
    name: 'reset route (clear pinned waypoints)',
    covers: 'routing: waypoints (reset route)',
    gesture: false,
    edit: (e) => {
      e().resetRoute('e_cd');
    },
  },
  {
    name: 'add node (shape panel)',
    covers: 'node creation',
    gesture: false,
    edit: (e) => {
      e().addNode({ id: 'n_new', kind: 'service', label: 'New', shape: 'rounded', geometry: { x: 800, y: 0, w: 160, h: 80 } });
    },
  },
  {
    name: 'connect (declares default ports on a portless node)',
    covers: 'edge creation (item 8)',
    gesture: false,
    edit: (e) => {
      e().connect({ from: { node: 'n_c', port: RIGHT }, to: { node: 'n_d', port: TOP } });
    },
  },
  {
    name: 'connect selected shapes in selection order (several edges, one step)',
    covers: 'edge creation from the keyboard (QA S5)',
    gesture: false,
    edit: (e) => {
      e().connectSelected(new Set(['n_d', 'n_z', 'n_c']));
    },
  },
  {
    name: 're-attach an edge end',
    covers: 'edge edit',
    gesture: false,
    edit: (e) => {
      e().reconnect({ edge: 'e_ab', end: 'to', to: { node: 'n_b', port: { id: 'right', x: 1, y: 0.5 } } });
    },
  },
  {
    name: 'delete a node with its edges',
    covers: 'node deletion (item 21)',
    gesture: false,
    edit: (e) => {
      e().deleteElements(new Set(['n_c']));
    },
  },
  {
    name: 'delete an edge alone',
    covers: 'edge deletion',
    gesture: false,
    edit: (e) => {
      e().deleteElements(new Set(['e_cd']));
    },
  },
  {
    name: 'delete a container (cascade to children and their edges)',
    covers: 'node deletion: container cascade',
    gesture: false,
    edit: (e) => {
      e().deleteElements(new Set(['c_box']));
    },
  },
  {
    name: 'group (Cmd/Ctrl+G)',
    covers: 'reparenting: group (item 22)',
    gesture: false,
    edit: (e) => {
      e().group(new Set(['n_c', 'n_d']), 40);
    },
  },
  {
    name: 'ungroup (Cmd/Ctrl+Shift+G)',
    covers: 'reparenting: ungroup (item 22)',
    gesture: false,
    edit: (e) => {
      // c_box holds an edge between its children but has none of its own, so it can be ungrouped.
      e().ungroup(new Set(['c_box']));
    },
  },
  {
    name: 'fit container to contents',
    covers: 'geometry: fit (item 26)',
    gesture: false,
    edit: (e) => {
      e().fitToContents(new Set(['c_box']), 40);
    },
  },
  {
    name: 'add a lane to a pool',
    covers: 'swimlane: add lane',
    gesture: false,
    edit: (e) => {
      e().addLane('n_pool');
    },
  },
  {
    name: 'remove a lane from a pool',
    covers: 'swimlane: remove lane',
    gesture: false,
    edit: (e) => {
      e().removeLane('n_lane1');
    },
  },
  {
    name: 'move a lane earlier in its pool',
    covers: 'swimlane: reorder lane',
    gesture: false,
    edit: (e) => {
      e().moveLane('n_lane2', -1);
    },
  },
  {
    name: 'set a pool’s lane orientation',
    covers: 'swimlane: orientation',
    gesture: false,
    edit: (e) => {
      e().setPoolOrientation('n_pool', 'vertical');
    },
  },
  {
    name: 'resize a pool (lanes repack in one step)',
    covers: 'swimlane: lanes resize with the pool',
    gesture: true,
    edit: (e) => {
      gesture(e(), 'n_pool', { w: 500 });
      e().moveEnd();
    },
  },
  {
    name: 'align',
    covers: 'geometry: align (item 24)',
    gesture: false,
    edit: (e) => {
      e().align(new Set(['n_a', 'n_d']), 'top');
    },
  },
  {
    name: 'distribute',
    covers: 'geometry: distribute (item 24)',
    gesture: false,
    edit: (e) => {
      e().distribute(new Set(['n_a', 'n_c', 'n_d']), 'vertical');
    },
  },
  {
    name: 'arrange (bring to front)',
    covers: 'arrange order (item 25)',
    gesture: false,
    edit: (e) => {
      e().arrange(new Set(['n_a']), 'bringToFront');
    },
  },
  {
    name: 'arrange (send backward)',
    covers: 'arrange order (item 25)',
    gesture: false,
    edit: (e) => {
      e().arrange(new Set(['n_d']), 'sendBackward');
    },
  },
  {
    name: 'paste',
    covers: 'node and edge creation: paste (items 18, 19)',
    gesture: false,
    edit: (e) => {
      e().pasteElements(copy(e().document, ['n_a', 'n_b', 'e_ab']), { x: 20, y: 20 });
    },
  },
  {
    name: 'duplicate (Cmd/Ctrl+D)',
    covers: 'node creation: duplicate (item 21)',
    gesture: false,
    edit: (e) => {
      e().duplicateElements(new Set(['n_c', 'n_d', 'e_cd']), { x: 20, y: 20 });
    },
  },
  {
    name: 'cut (the delete half; the copy half writes nothing)',
    covers: 'node deletion: cut',
    gesture: false,
    edit: (e) => {
      e().deleteElements(new Set(['n_d']));
    },
  },
  {
    name: 'flythrough: add a keyframe from the current view',
    covers: 'flythrough: capture (Tour panel, Add keyframe)',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => addKeyframe(current, { x: 100, y: 100, w: 640, h: 400 })?.flythrough);
    },
  },
  {
    name: 'flythrough: edit a keyframe (caption, timing and easing in one commit)',
    covers: 'flythrough: caption, duration, hold, easing',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : updateKeyframe(current, 'k_2', { caption: 'The API', durationMs: 2000, holdMs: 4000, easing: 'linear' })));
    },
  },
  {
    name: 'flythrough: re-capture a keyframe from the current view',
    covers: 'flythrough: update to the current view',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : updateKeyframe(current, 'k_1', { view: { x: -50, y: -50, w: 900, h: 560 } })));
    },
  },
  {
    name: 'flythrough: reorder keyframes',
    covers: 'flythrough: reorder',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : moveKeyframe(current, 'k_2', 0)));
    },
  },
  {
    name: 'flythrough: duplicate a keyframe',
    covers: 'flythrough: duplicate',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : duplicateKeyframe(current, 'k_1')?.flythrough));
    },
  },
  {
    name: 'flythrough: delete a keyframe',
    covers: 'flythrough: delete',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : removeKeyframe(current, 'k_1')));
    },
  },
  {
    name: 'flythrough: turn looping on',
    covers: 'flythrough: loop',
    gesture: false,
    edit: (e) => {
      e().editFlythrough((current) => (current === undefined ? current : setLoop(current, true)));
    },
  },
  {
    name: 'flythrough: clear the whole tour',
    covers: 'flythrough: clear',
    gesture: false,
    edit: (e) => {
      e().editFlythrough(() => undefined);
    },
  },
  {
    name: 'DSL Text apply (style, kind, title, add and remove at once)',
    covers: 'style and kind via Text (DSL apply)',
    gesture: false,
    edit: (e) => {
      const base = e().document;
      const next: Document = {
        ...base,
        title: 'From text',
        nodes: [
          ...base.nodes
            .filter((node) => node.id !== 'n_d')
            .map((node) => (node.id === 'n_c' ? { ...node, kind: 'queue', style: { fill: '#e04832', stroke: '#151e33' } } : node)),
          { id: 'n_text', kind: 'service', label: 'Typed', shape: 'rectangle' },
        ],
        edges: base.edges.filter((edge) => edge.to.node !== 'n_d' && edge.from.node !== 'n_d'),
      };
      e().applyTextDocument(next, base);
    },
  },
  {
    name: 'DSL Text apply onto a diagram that moved on (three-way merge, "Keep mine")',
    covers: 'Text (DSL) apply merged into a changed diagram',
    gesture: false,
    edit: (e) => {
      const current = e().document;
      // The text was made when the title was different; the diagram has the title it has now, which the text left alone.
      const base: Document = { ...current, title: 'Title when the text was made' };
      const next: Document = {
        ...base,
        nodes: [...base.nodes.map((node) => (node.id === 'n_c' ? { ...node, label: 'Typed label' } : node)), { id: 'n_typed', kind: 'service', label: 'Typed node', shape: 'rectangle' }],
      };
      expect(e().mergeTextDocument(next, base).conflicts).toEqual([]);
    },
  },
  {
    name: 'DSL Text conflicts resolved to the text\'s side',
    covers: 'Text (DSL) apply: use my text for a clash',
    gesture: false,
    edit: (e) => {
      const current = e().document;
      const next: Document = { ...current, nodes: current.nodes.map((node) => (node.id === 'n_c' ? { ...node, label: 'Typed label' } : node)) };
      e().takeTextConflicts(next, [{ kind: 'node', id: 'n_c', reason: 'both-changed', fields: ['label'] }]);
    },
  },
  {
    name: 'tidy layout (server POST /api/layout)',
    covers: 'layout: Tidy (checklist item 28)',
    gesture: false,
    edit: async (e) => {
      // Tidy reads the server's laid-out document; stub the one request it makes, shifting every position so the
      // applied diff is a real change the audit can undo.
      const fetchMock = vi.fn<typeof fetch>((_input, init) => {
        const request = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as Document;
        const document: Document = {
          ...request,
          nodes: request.nodes.map((node) => (node.geometry === undefined ? node : { ...node, geometry: { ...node.geometry, y: node.geometry.y + 5 } })),
        };
        return Promise.resolve(new Response(JSON.stringify({ document, warnings: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      });
      vi.stubGlobal('fetch', fetchMock);
      try {
        await e().tidy('tree');
      } finally {
        vi.unstubAllGlobals();
      }
    },
  },
];

const zAt = (editor: DocumentEditor, y: number): Map<Id, Geometry> => new Map([['n_z', { ...geometryOf(editor.document, 'n_z'), y }]]);

function openEditor() {
  // StrictMode as main.tsx renders it: it re-runs effects on a live component, which must not break undo.
  return renderHook(() => useDocumentEditor(loaded.id, loaded, '"dv1:loaded"'), { wrapper: StrictMode });
}

describe('undo coverage (checklist item 16)', () => {
  it.each(cases)('$name: one Cmd/Ctrl+Z restores the exact model before it, one Cmd/Ctrl+Shift+Z the exact model after', async ({ gesture: isGesture, edit }) => {
    const { result } = openEditor();
    act(() => {
      // The edit before: finished ahead of a gesture, still open ahead of a command, which must close it itself.
      result.current.moveNodes(zAt(result.current, 610));
      if (isGesture) {
        result.current.moveEnd();
      }
    });
    const prior = structuredClone(result.current.document);
    await act(async () => {
      await edit(() => result.current);
    });
    const after = structuredClone(result.current.document);
    // The edit must have changed something, or the rest of the test proves nothing.
    expect(after).not.toEqual(prior);
    act(() => {
      // The edit after, a write that would join the case's own step if the case had left it open.
      result.current.moveNodes(zAt(result.current, 620));
      result.current.moveEnd();
    });
    const next = structuredClone(result.current.document);

    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(after);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(after);

    // The same again with Ctrl; one more undo past the case reaches the edit before it, and only that.
    act(() => {
      undoCtrl();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      undoCtrl();
    });
    expect(result.current.document).toEqual(loaded);
    act(() => {
      redoCtrl();
      redoCtrl();
      redoCtrl();
    });
    expect(result.current.document).toEqual(next);
  });

  // Escape mid-gesture (canvas/DiagramCanvas.tsx): the frames written so far are rolled back through moveCancel, which
  // must leave the undo stack as if the gesture never began, and nothing on the redo stack to bring it back.
  it.each([
    ['move', 'n_c', { x: 640, y: 20 }, undefined],
    ['resize', 'n_d', { x: 590, w: 140, h: 90 }, undefined],
    ['rotate', 'n_c', { rotation: 45 }, undefined],
    ['move with a connector waypoint travelling', 'n_c', { x: 640 }, () => new Map<Id, Point[]>([['e_cd', [{ x: 690, y: 150 }]]])],
  ] as const)('a cancelled %s adds no undo step and no redo step', (_name, id, to, waypoints) => {
    const { result } = openEditor();
    act(() => {
      result.current.moveNodes(zAt(result.current, 610));
      result.current.moveEnd();
    });
    const prior = structuredClone(result.current.document);
    act(() => {
      gesture(result.current, id, to, 4, waypoints);
    });
    expect(result.current.document).not.toEqual(prior);
    act(() => {
      result.current.moveCancel();
    });
    expect(result.current.document).toEqual(prior);

    // Redo has nothing to bring back, and the one step behind is exactly the edit before the cancelled gesture.
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(loaded);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(loaded);
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(prior);
  });

  it('keeps well over a hundred steps through a session with a save in it, undoing each back to the loaded document and redoing each to the end', async () => {
    const fetchMock = vi.fn<typeof fetch>((_input, init) => Promise.resolve(new Response(typeof init?.body === 'string' ? init.body : '', { status: 200, headers: { ETag: '"dv1:saved"' } })));
    vi.stubGlobal('fetch', fetchMock);
    try {
      const { result } = openEditor();
      const snapshots: Document[] = [structuredClone(result.current.document)];
      const record = (): void => {
        const now = structuredClone(result.current.document);
        expect(now).not.toEqual(snapshots.at(-1));
        snapshots.push(now);
      };
      const STEPS = 150;
      for (let i = 0; i < STEPS; i++) {
        if (i === STEPS / 2) {
          await act(() => result.current.save());
          expect(result.current.saveStatus).toBe('saved');
        }
        act(() => {
          const editor = result.current;
          switch (i % 6) {
            case 0:
              editor.moveNodes(new Map([['n_d', { ...geometryOf(editor.document, 'n_d'), x: geometryOf(editor.document, 'n_d').x + 1 }]]));
              editor.moveEnd();
              break;
            case 1:
              editor.setLabel('n_c', `C ${String(i)}`);
              break;
            case 2:
              editor.addNode({ id: `n_s${String(i)}`, kind: 'service', label: `S${String(i)}`, shape: 'rectangle', geometry: { x: 800 + i, y: i, w: 80, h: 40 } });
              break;
            case 3:
              editor.arrange(new Set([`n_s${String(i - 1)}`]), 'sendToBack');
              break;
            case 4:
              gesture(editor, 'n_c', { rotation: (i * 7) % 360 });
              editor.moveEnd();
              break;
            default:
              editor.deleteElements(new Set([`n_s${String(i - 3)}`]));
          }
        });
        record();
      }
      expect(snapshots).toHaveLength(STEPS + 1);

      for (let i = STEPS - 1; i >= 0; i--) {
        act(() => {
          undoKey();
        });
        expect(result.current.document).toEqual(snapshots[i]);
      }
      expect(result.current.document).toEqual(loaded);
      expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);

      for (let i = 1; i <= STEPS; i++) {
        act(() => {
          redoKey();
        });
        expect(result.current.document).toEqual(snapshots[i]);
      }
      expect(result.current.actions.find((action) => action.id === 'document.redo')?.enabled()).toBe(false);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

/**
 * The sequence editor's edits (design/atlas/sequence-editor.md), each one undo step. A sequence document derives its
 * geometry, so the edit before and after each case is a label change rather than a move.
 */
describe('undo coverage: sequence editor', () => {
  const sequence = checkoutSequence();
  const openSequence = () => renderHook(() => useDocumentEditor(sequence.id, sequence, '"dv1:seq"'), { wrapper: StrictMode });

  const sequenceCases: { name: string; edit: (editor: () => DocumentEditor) => void }[] = [
    {
      name: 'add participant (palette click)',
      edit: (e) => {
        e().addNode(createParticipant(e().document, { kind: 'participant', shape: 'rectangle', label: 'Participant' }));
      },
    },
    {
      name: 'add message at a slot (connector drag)',
      edit: (e) => {
        e().insertMessage('p_user', 'p_pay', 2);
      },
    },
    {
      name: 'add self message at the end',
      edit: (e) => {
        e().insertMessage('p_web', 'p_web', 99);
      },
    },
    {
      name: 'change message type',
      edit: (e) => {
        e().setEdgeKind('m_2', 'async');
      },
    },
    {
      name: 'label a message',
      edit: (e) => {
        e().setLabel('m_3', 'charge(total, currency)');
      },
    },
    {
      name: 'move a message later (Arrange > Bring forward)',
      edit: (e) => {
        e().arrange(new Set(['m_2']), 'bringForward');
      },
    },
    {
      name: 'move a message to the start (Arrange > Send to back)',
      edit: (e) => {
        e().arrange(new Set(['m_5']), 'sendToBack');
      },
    },
    {
      name: 'move a participant later (Arrange > Bring forward)',
      edit: (e) => {
        e().arrange(new Set(['p_user']), 'bringForward');
      },
    },
    {
      name: 'reorder a message by dragging it to a slot',
      edit: (e) => {
        e().reorderElement('m_1', { x: 0, y: 250 });
      },
    },
    {
      name: 'reorder a participant by dragging it sideways',
      edit: (e) => {
        e().reorderElement('p_user', { x: 300, y: 0 });
      },
    },
    {
      name: 'delete a participant, which removes its messages',
      edit: (e) => {
        e().deleteElements(new Set(['p_api']));
      },
    },
    {
      name: 'delete a message',
      edit: (e) => {
        e().deleteElements(new Set(['m_4']));
      },
    },
  ];

  it.each(sequenceCases)('$name: one undo restores the exact model before it, one redo the exact model after', ({ edit }) => {
    const { result } = openSequence();
    act(() => {
      result.current.setLabel('p_user', 'Customer (before)');
    });
    const prior = structuredClone(result.current.document);
    act(() => {
      edit(() => result.current);
    });
    const after = structuredClone(result.current.document);
    expect(after).not.toEqual(prior);
    act(() => {
      result.current.setLabel('p_pay', 'Payments (after)');
    });
    const next = structuredClone(result.current.document);

    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(after);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(after);
    act(() => {
      undoCtrl();
      undoCtrl();
    });
    expect(result.current.document).toEqual({ ...sequence, nodes: sequence.nodes });
    act(() => {
      redoCtrl();
      redoCtrl();
      redoCtrl();
    });
    expect(result.current.document).toEqual(next);
  });

  it('a drop that changes nothing records no undo step', () => {
    const { result } = openSequence();
    act(() => {
      result.current.reorderElement('m_3', { x: 0, y: 180 });
      result.current.reorderElement('p_web', { x: 200, y: 0 });
      result.current.arrange(new Set(['m_7']), 'bringToFront');
    });
    expect(result.current.document).toEqual(sequence);
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });

  it('writes no geometry or routing for a sequence document, whatever the canvas or a stale caller sends', () => {
    const { result } = openSequence();
    act(() => {
      result.current.moveNodes(new Map([['p_web', { x: 900, y: 900, w: 10, h: 10 }]]), new Map([['m_1', [{ x: 1, y: 1 }]]]));
      result.current.moveEnd();
      result.current.setEdgeRoutingMode('m_1', 'curved');
      result.current.resetRoute('m_1');
      result.current.align(new Set(['p_web', 'p_api']), 'left');
    });
    expect(result.current.document).toEqual(sequence);
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });
});

/**
 * Milestone 2's edits (design/atlas/sequence-fragments.md), each one undo step, on docs/examples/checkout-fragments.json:
 * an alt with m_3 and m_4 under [card ok] and m_5 under [declined], a note (n_1), a created participant and a destroy.
 * Each follows an earlier, finished edit and is followed by one, like every case above, so an undo that reached past its
 * step or a step that swallowed a neighbour shows as a wrong model.
 */
describe('undo coverage: sequence fragments, notes and activation', () => {
  const fragments = checkoutFragments();
  const open = () => renderHook(() => useDocumentEditor(fragments.id, fragments, '"dv1:frag"'), { wrapper: StrictMode });

  const cases: { name: string; edit: (editor: () => DocumentEditor) => void }[] = [
    {
      name: 'wrap messages in a fragment',
      edit: (e) => {
        expect(e().wrapInFragment(new Set(['m_1', 'm_2']), 'loop')).toEqual({ fragment: 'f_2', operand: 0 });
      },
    },
    {
      name: 'wrap a message inside an existing fragment operand (nests)',
      edit: (e) => {
        expect(e().wrapInFragment(new Set(['m_4']), 'opt')).toEqual({ fragment: 'f_2', operand: 0 });
      },
    },
    {
      name: 'add an operand to an alt',
      edit: (e) => {
        expect(e().addFragmentOperand('m_4')).toEqual({ fragment: 'f_1', operand: 1 });
      },
    },
    {
      name: 'add a note after a message',
      edit: (e) => {
        expect(e().addNote('m_2')).toBeDefined();
      },
    },
    {
      name: 'add a note at the end with nothing selected',
      edit: (e) => {
        expect(e().addNote(undefined)).toBeDefined();
      },
    },
    {
      name: 'toggle an activation bar on',
      edit: (e) => {
        expect(e().toggleActivation('m_1')).toBe(true);
      },
    },
    {
      name: 'toggle an activation bar off (and the deactivate that closed it)',
      edit: (e) => {
        expect(e().toggleActivation('m_2')).toBe(false);
      },
    },
    {
      name: 'edit a guard',
      edit: (e) => {
        e().setFragmentGuard('f_1', 0, '[payment taken]');
      },
    },
    {
      name: 'change a fragment’s operator',
      edit: (e) => {
        e().setFragmentOperator('f_1', 'par');
      },
    },
    {
      name: 'label a note',
      edit: (e) => {
        e().setLabel('n_1', 'A different note');
      },
    },
    {
      name: 'change a message to a create message',
      edit: (e) => {
        e().setEdgeKind('m_6', 'create');
      },
    },
    {
      name: 'add a message between two members of a fragment (it becomes a member)',
      edit: (e) => {
        e().insertMessage('p_web', 'p_api', 4);
      },
    },
    {
      name: 'drag a member out of its fragment (it stops being a member)',
      edit: (e) => {
        e().reorderElement('m_3', { x: 0, y: 0 });
      },
    },
    {
      name: 'move a member out of its fragment with Arrange > Move earlier',
      edit: (e) => {
        e().arrange(new Set(['m_3']), 'sendBackward');
      },
    },
    {
      name: 'delete a member of a fragment',
      edit: (e) => {
        e().deleteElements(new Set(['m_4']));
      },
    },
    {
      name: 'delete a note',
      edit: (e) => {
        e().deleteElements(new Set(['n_1']));
      },
    },
    {
      name: 'delete the participant whose messages fill a fragment (the emptied frame goes with them)',
      edit: (e) => {
        e().deleteElements(new Set(['p_api']));
      },
    },
  ];

  it.each(cases)('$name: one undo restores the exact model before it, one redo the exact model after', ({ edit }) => {
    const { result } = open();
    act(() => {
      result.current.setLabel('p_user', 'Customer (before)');
    });
    const prior = structuredClone(result.current.document);
    act(() => {
      edit(() => result.current);
    });
    const after = structuredClone(result.current.document);
    expect(after).not.toEqual(prior);
    act(() => {
      result.current.setLabel('p_pay', 'Payments (after)');
    });
    const next = structuredClone(result.current.document);

    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(after);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(prior);
    act(() => {
      redoKey();
    });
    expect(result.current.document).toEqual(after);
    act(() => {
      undoCtrl();
      undoCtrl();
    });
    expect(result.current.document).toEqual(fragments);
    act(() => {
      redoCtrl();
      redoCtrl();
      redoCtrl();
    });
    expect(result.current.document).toEqual(next);
  });

  it('removes a fragment the delete emptied in the same step, and one undo brings the frame and its messages back', () => {
    const { result } = open();
    // p_api is an end of m_2..m_6, which is every message f_1 holds.
    act(() => {
      result.current.deleteElements(new Set(['p_api']));
    });
    expect(result.current.document.nodes.some((node) => node.id === 'f_1')).toBe(false);
    expect(result.current.document.edges.some((edge) => edge.props?.in === 'f_1')).toBe(false);
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(fragments);
    // A delete that leaves a message in the frame keeps it: p_pay only holds m_3 and m_4, and m_5 stays.
    act(() => {
      result.current.deleteElements(new Set(['p_pay']));
    });
    expect(result.current.document.nodes.some((node) => node.id === 'f_1')).toBe(true);
    expect(result.current.document.edges.find((edge) => edge.id === 'm_5')?.props?.in).toBe('f_1');
  });

  it('drops an operand the delete emptied and renumbers the later ones in the same step, and one undo restores it', () => {
    const { result } = open();
    // p_pay only holds m_3 and m_4, which are all of f_1's first operand; m_5 keeps the second, whose guard becomes the first.
    const guards = (): unknown => result.current.document.nodes.find((node) => node.id === 'f_1')?.props?.operands;
    const before = guards();
    expect(before).toHaveLength(2);
    act(() => {
      result.current.deleteElements(new Set(['p_pay']));
    });
    expect(guards()).toEqual([(before as string[])[1]]);
    expect(result.current.document.edges.find((edge) => edge.id === 'm_5')?.props?.operand).toBeUndefined();
    act(() => {
      undoKey();
    });
    expect(result.current.document).toEqual(fragments);
  });

  it('a command that does not apply writes nothing and records no undo step', () => {
    const { result } = open();
    act(() => {
      // m_1 and m_3 are not next to each other; m_3 is the first message of its operand; a guard already says this;
      // the note takes no activation bar; a fragment node is no message.
      expect(result.current.wrapInFragment(new Set(['m_1', 'm_3']), 'alt')).toBeUndefined();
      expect(result.current.addFragmentOperand('m_3')).toBeUndefined();
      result.current.setFragmentGuard('f_1', 0, '[card ok]');
      expect(result.current.toggleActivation('n_1')).toBeUndefined();
      expect(result.current.addFragmentOperand('f_1')).toBeUndefined();
      result.current.setFragmentOperator('f_1', 'alt');
    });
    expect(result.current.document).toEqual(fragments);
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });

  it('gives a message dropped between two members the fragment, and one dragged out of it none', () => {
    const { result } = open();
    // Edge order: m_1 m_2 n_1 m_3 m_4 m_5 ... A new message at index 4 sits between m_3 and m_4.
    let added = '';
    act(() => {
      added = result.current.insertMessage('p_web', 'p_api', 4);
    });
    expect(result.current.document.edges[4]?.props).toEqual({ in: 'f_1' });
    expect(sequenceMemberships(result.current.document).get(added)).toEqual([{ fragment: 'f_1', operand: 0 }]);
    // Dropped above the frame, at the very top, it is outside.
    act(() => {
      result.current.reorderElement(added, { x: 0, y: 0 });
    });
    expect(result.current.document.edges[0]?.id).toBe(added);
    expect(result.current.document.edges[0]?.props).toBeUndefined();
    // ... and the other members kept theirs.
    expect(sequenceMemberships(result.current.document).get('m_3')).toEqual([{ fragment: 'f_1', operand: 0 }]);
  });

  it('drops the last participant past the end of a diagram that stores a fragment after it as no edit at all', () => {
    const { result } = open();
    const before = result.current.document;
    act(() => {
      result.current.reorderElement('p_mail', { x: 5000, y: 0 });
    });
    expect(result.current.document).toEqual(before);
    expect(result.current.saveStatus).toBe('saved');
    expect(result.current.actions.find((action) => action.id === 'document.undo')?.enabled()).toBe(false);
  });

  it('keeps a member in its fragment when it moves inside it, and the drop of a fragment node is ignored', () => {
    const { result } = open();
    act(() => {
      result.current.arrange(new Set(['m_3']), 'bringForward');
    });
    // m_3 now follows m_4, between m_4 and m_5: both are members, so it stays one.
    expect(result.current.document.edges.slice(3, 6).map((edge) => edge.id)).toEqual(['m_4', 'm_3', 'm_5']);
    expect(sequenceMemberships(result.current.document).get('m_3')).toEqual([{ fragment: 'f_1', operand: 0 }]);
    const nodes = result.current.document.nodes.map((node) => node.id);
    act(() => {
      result.current.reorderElement('f_1', { x: 900, y: 0 });
    });
    expect(result.current.document.nodes.map((node) => node.id)).toEqual(nodes);
  });
});
