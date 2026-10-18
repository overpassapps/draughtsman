/**
 * The edits a sequence diagram's fragment, note and activation commands make (design/atlas/sequence-fragments.md), as
 * pure functions over the document that return a `SequenceEdit` for one Yjs transaction: wrap a run of messages in a
 * combined fragment, add an operand, add a note, toggle an activation bar, set a guard or an operator, and the membership a
 * message inherits from where it lands. Nothing here stores a position: membership is the `in` and `operand` props of a
 * message (and `parent` and `operand` of a nested fragment), the frame is derived from them by model/sequenceScene.ts,
 * so an edit that changes who is a member changes the picture and nothing else.
 *
 * Every command is a function that says `undefined` when it does not apply (the menu greys it out on the same answer),
 * so an action's `enabled` and its `run` cannot disagree.
 */

import * as Y from 'yjs';
import { LOCAL_ORIGIN, addNodes, setEdgeProps, setNodeLabel, setNodeParents, setNodeProps } from './editor.ts';
import { insertEdgeAt } from './sequenceEdit.ts';
import { FRAGMENT_KIND, bareGuard, INTERACTION_USE_KIND, NOTE_KIND, fragmentNodes, fragmentOperands, isBoxRow, isSet, sequenceMemberships, type FragmentLevel } from './sequenceScene.ts';
import type { IdGenerator } from './clipboard.ts';
import { randomIdGenerator } from './clipboard.ts';
import type { Document, Edge, Id, Node, Props } from './types.ts';

/** The operators the Wrap in fragment command offers; any other operator is written in the DSL or set by the operator command. */
export const WRAP_OPERATORS = ['alt', 'opt', 'loop', 'par'] as const;
export type WrapOperator = (typeof WRAP_OPERATORS)[number];

/** Operators whose frame holds several operands (branches), so an operand can be added to it. */
const MULTI_OPERAND = new Set(['alt', 'par', 'critical']);

/** The first guard a new fragment carries; `par` has none (its branches are unlabelled). */
function firstGuard(operator: string): string {
  return operator === 'par' ? '' : 'condition';
}

/** The guard a new operand of an `alt` carries. */
const ELSE_GUARD = 'else';

/** What one command writes, in one Yjs transaction. Absent parts write nothing. */
export interface SequenceEdit {
  addNodes?: Node[];
  nodeProps?: Map<Id, Props | null>;
  nodeParents?: Map<Id, Id | null>;
  nodeLabels?: Map<Id, string>;
  edgeProps?: Map<Id, Props | null>;
  /** Edges to insert, each at an index of the edge order as it stands before any insert. */
  insertEdges?: { edge: Edge; index: number }[];
}

/** Writes `edit` in one transaction under `origin`; the caller closes the undo step on either side of it. */
export function applySequenceEdit(doc: Y.Doc, edit: SequenceEdit, origin: unknown = LOCAL_ORIGIN): void {
  doc.transact(() => {
    if (edit.addNodes !== undefined) {
      addNodes(doc, edit.addNodes, origin);
    }
    if (edit.nodeProps !== undefined) {
      setNodeProps(doc, edit.nodeProps, origin);
    }
    if (edit.nodeParents !== undefined) {
      setNodeParents(doc, edit.nodeParents, origin);
    }
    for (const [id, label] of edit.nodeLabels ?? []) {
      setNodeLabel(doc, id, label, origin);
    }
    if (edit.edgeProps !== undefined) {
      setEdgeProps(doc, edit.edgeProps, origin);
    }
    for (const { edge, index } of edit.insertEdges ?? []) {
      insertEdgeAt(doc, edge, index, origin);
    }
  }, origin);
}

/** The edges the scene draws a row for, in edge order: both ends are participants (a fragment is not one). */
function drawnRows(document: Document): Edge[] {
  const participants = new Set(document.nodes.filter((node) => node.kind !== FRAGMENT_KIND).map((node) => node.id));
  return document.edges.filter((edge) => participants.has(edge.from.node) && participants.has(edge.to.node));
}

/** `props` without the given keys and with `set` laid over it; null when nothing is left (an empty prop bag is stored as none). */
function withProps(props: Props | undefined, set: Record<string, unknown>, remove: readonly string[] = []): Props | null {
  const dropped = new Set(remove);
  const next = new Map<string, unknown>(Object.entries(props ?? {}).filter(([key]) => !dropped.has(key)));
  for (const [key, value] of Object.entries(set)) {
    if (value === undefined) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
  }
  return next.size === 0 ? null : Object.fromEntries(next);
}

/** A message's `operand` prop for `operand`: absent for 0, which is the default. */
function operandProp(operand: number): number | undefined {
  return operand === 0 ? undefined : operand;
}

// ---- empty frames ----

/**
 * The fragments a removal left with nothing in them: `before` held a row or a nested fragment in each, `after` holds
 * none, so the last message left the frame (a cascade from a deleted participant included) and the frame would be
 * drawn empty and stacked. Nesting cascades: a frame that only held such a frame is emptied too. A frame that was
 * already empty before the removal is not touched, and the frame someone deletes on its own keeps its messages
 * (removeElements), so this never removes what the user did not empty.
 */
export function emptiedFragments(before: Document, after: Document): Set<Id> {
  const held = (document: Document, id: Id, gone: ReadonlySet<Id>): boolean =>
    document.edges.some((edge) => edge.props?.in === id) || document.nodes.some((node) => node.kind === FRAGMENT_KIND && node.parent === id && !gone.has(node.id));
  const emptied = new Set<Id>();
  const candidates = [...fragmentNodes(after).keys()].filter((id) => held(before, id, new Set()));
  for (let changed = true; changed; ) {
    changed = false;
    for (const id of candidates) {
      if (!emptied.has(id) && !held(after, id, emptied)) {
        emptied.add(id);
        changed = true;
      }
    }
  }
  return emptied;
}

/**
 * The operands a removal left with no row in them, taken out in one `SequenceEdit`: `before` held a row (or a nested
 * fragment) under each, `after` holds none, and the fragment still has another operand. Its guard goes and every later
 * operand moves down one, in the guards and in each member's and nested fragment's `operand`, so the branches after it keep
 * their rows and guards. An operand that was already empty before the removal is left alone, as is the fragment's only
 * operand (`emptiedFragments` removes that frame). Undefined when nothing was emptied. Run it after the fragments
 * `emptiedFragments` names are gone, in the same undo step as the removal.
 */
export function emptiedOperands(before: Document, after: Document): SequenceEdit | undefined {
  const operandOf = (props: Props | undefined): number => (typeof props?.operand === 'number' ? props.operand : 0);
  const heldOperands = (document: Document, id: Id): Set<number> =>
    new Set([
      ...document.edges.filter((edge) => edge.props?.in === id).map((edge) => operandOf(edge.props)),
      ...document.nodes.filter((node) => node.kind === FRAGMENT_KIND && node.parent === id).map((node) => operandOf(node.props)),
    ]);
  const edgeProps = new Map<Id, Props | null>();
  const nodeProps = new Map<Id, Props | null>();
  for (const [id, node] of fragmentNodes(after)) {
    const held = heldOperands(after, id);
    const was = heldOperands(before, id);
    const guards = fragmentOperands(node);
    const width = Math.max(guards.length, ...held, ...was) + 1;
    const gone = [...was].filter((operand) => !held.has(operand)).sort((x, y) => x - y);
    // Never the last operand standing: that frame is emptied, not shrunk.
    if (gone.length === 0 || held.size === 0) {
      continue;
    }
    const shift = (operand: number): number => operand - gone.filter((g) => g < operand).length;
    const removed = new Set(gone);
    while (guards.length < width - 1) {
      guards.push('');
    }
    nodeProps.set(id, withProps(node.props, { operands: guards.filter((_guard, operand) => !removed.has(operand)) }));
    for (const edge of after.edges.filter((candidate) => candidate.props?.in === id)) {
      edgeProps.set(edge.id, withProps(edge.props, { operand: operandProp(shift(operandOf(edge.props))) }));
    }
    for (const child of after.nodes.filter((candidate) => candidate.kind === FRAGMENT_KIND && candidate.parent === id)) {
      const moved = shift(operandOf(child.props));
      nodeProps.set(child.id, withProps(child.props, { operand: moved === 0 ? undefined : moved }));
    }
  }
  return nodeProps.size === 0 ? undefined : { nodeProps, edgeProps };
}

// ---- membership by slot ----

/** The fragment (and the operand of it) a row inserted at `index` of the edge order belongs to. */
export interface SlotMembership {
  fragment: Id;
  operand: number;
}

/**
 * The fragment a row placed at `index` of `document.edges` (before the edge now there) inherits: the innermost fragment
 * both its neighbours are in, with the operand of it the earlier one sits under, so a row between two members of a
 * frame is a member and a row above its first member or below its last is not (design/atlas/sequence-fragments.md).
 * `skip` names an edge being moved, which is not a neighbour of itself.
 */
export function membershipAt(document: Document, index: number, skip?: Id): SlotMembership | undefined {
  const others = document.edges.filter((edge) => edge.id !== skip);
  const before = others[index - 1];
  const after = others[index];
  if (before === undefined || after === undefined) {
    return undefined;
  }
  const memberships = sequenceMemberships(document);
  const earlier = memberships.get(before.id) ?? [];
  const later = memberships.get(after.id) ?? [];
  let common: FragmentLevel | undefined;
  for (let level = 0; level < Math.min(earlier.length, later.length); level++) {
    if (earlier[level]?.fragment !== later[level]?.fragment) {
      break;
    }
    common = earlier[level];
  }
  return common === undefined ? undefined : { fragment: common.fragment, operand: common.operand };
}

/** `edge` with its membership set to `membership` (or cleared), leaving every other prop alone. */
export function withMembership(edge: Edge, membership: SlotMembership | undefined): Edge {
  const props = withProps(edge.props, membership === undefined ? {} : { in: membership.fragment, operand: operandProp(membership.operand) }, membership === undefined ? ['in', 'operand'] : ['operand']);
  const next: Edge = { ...edge };
  if (props === null) {
    delete next.props;
  } else {
    next.props = props;
  }
  return next;
}

// ---- wrap in fragment ----

/** A `f_N` id no node or edge uses. */
function freshFragmentId(document: Document): Id {
  const taken = new Set([...document.nodes, ...document.edges].map((element) => element.id));
  for (let n = 1; ; n++) {
    if (!taken.has(`f_${String(n)}`)) {
      return `f_${String(n)}`;
    }
  }
}

export interface WrapResult {
  edit: SequenceEdit;
  fragment: Id;
  /** The operand whose guard is opened for editing next. */
  operand: number;
}

/**
 * Wraps the messages `ids` in a new fragment with `operator`, or undefined when they cannot be: the selection must be
 * drawn rows only (messages and notes), next to each other in the edge order, and every fragment it touches must be
 * inside it entirely (a frame cannot cross another's border). The new frame nests in the deepest fragment operand all
 * of them share, takes over each message that was a direct member of that context, and takes each fragment that sat in it
 * as a child. One `SequenceEdit`, so one undo step.
 */
export function wrapInFragment(document: Document, ids: ReadonlySet<Id>, operator: string): WrapResult | undefined {
  const rows = drawnRows(document);
  const selected = rows.flatMap((edge, index) => (ids.has(edge.id) ? [index] : []));
  const first = selected[0];
  const last = selected[selected.length - 1];
  // Only drawn rows are wrapped, and next to each other: a message that is not selected in between would be framed too.
  if (first === undefined || last === undefined || selected.length !== ids.size || last - first + 1 !== selected.length) {
    return undefined;
  }
  const memberships = sequenceMemberships(document);
  const chains = selected.map((index) => memberships.get(rows[index]?.id ?? '') ?? []);
  const head = chains[0] ?? [];
  let shared = 0;
  while (chains.every((chain) => chain[shared] !== undefined && chain[shared]?.fragment === head[shared]?.fragment && chain[shared]?.operand === head[shared]?.operand)) {
    shared++;
  }
  // The fragments directly inside the shared context that the selection touches must be wholly selected.
  const children = new Set(chains.flatMap((chain) => (chain[shared] === undefined ? [] : [chain[shared]?.fragment ?? ''])));
  const selectedIds = new Set(selected.map((index) => rows[index]?.id ?? ''));
  for (const child of children) {
    const whole = rows.every((row) => !(memberships.get(row.id)?.some((level) => level.fragment === child) ?? false) || selectedIds.has(row.id));
    if (!whole) {
      return undefined;
    }
  }

  const parent = shared > 0 ? head[shared - 1] : undefined;
  const id = freshFragmentId(document);
  const props: Props = { operator, operands: [firstGuard(operator)] };
  if (parent !== undefined) {
    props.operand = parent.operand;
  }
  const fragment: Node = { id, kind: FRAGMENT_KIND, label: operator, shape: 'rectangle', props, ...(parent === undefined ? {} : { parent: parent.fragment }) };

  const edgeProps = new Map<Id, Props | null>();
  selected.forEach((index, at) => {
    const row = rows[index];
    // A row inside a child fragment stays in it; the child moves into the new frame instead.
    if (row !== undefined && (chains[at]?.length ?? 0) === shared) {
      edgeProps.set(row.id, withProps(row.props, { in: id }, ['operand']));
    }
  });
  const nodeProps = new Map<Id, Props | null>();
  const nodeParents = new Map<Id, Id | null>();
  const nodes = fragmentNodes(document);
  for (const child of children) {
    nodeParents.set(child, id);
    nodeProps.set(child, withProps(nodes.get(child)?.props, { operand: 0 }));
  }
  return { edit: { addNodes: [fragment], edgeProps, nodeProps, nodeParents }, fragment: id, operand: 0 };
}

// ---- add operand ----

export interface OperandResult {
  edit: SequenceEdit;
  fragment: Id;
  /** The new operand, whose guard is opened for editing next. */
  operand: number;
}

/** Whether `operator` (any case) is one whose frame holds branches. */
export function isMultiOperand(operator: string): boolean {
  return MULTI_OPERAND.has(operator.toLowerCase());
}

/**
 * Splits the fragment `messageId` is directly a member of at that message: it and everything after it in the same operand
 * move to a new operand right after, with an empty guard (`[else]` for an alt), and later operands shift up by one.
 * Undefined unless the fragment is an alt, par or critical, the message is a member and is not the first row of its
 * operand (that would leave the operand before it empty, and so not drawn).
 */
export function addOperand(document: Document, messageId: Id): OperandResult | undefined {
  if (fragmentNodes(document).has(messageId)) {
    return addOperandAtEnd(document, messageId);
  }
  const memberships = sequenceMemberships(document);
  const own = memberships.get(messageId);
  const level = own?.[own.length - 1];
  const node = level === undefined ? undefined : fragmentNodes(document).get(level.fragment);
  if (level === undefined || node === undefined || !isMultiOperand(fragmentOperator(node))) {
    return undefined;
  }
  const rows = drawnRows(document);
  const at = rows.findIndex((row) => row.id === messageId);
  const sameOperand = (row: Edge): boolean => memberships.get(row.id)?.find((candidate) => candidate.fragment === level.fragment)?.operand === level.operand;
  if (at < 0 || rows.findIndex(sameOperand) === at) {
    return undefined;
  }

  const split = level.operand;
  const moved = (operand: number, index: number): number => (operand < split ? operand : operand === split ? (index >= at ? split + 1 : split) : operand + 1);
  const edgeProps = new Map<Id, Props | null>();
  const nodeProps = new Map<Id, Props | null>();
  // Direct members keep their row; each fragment nested in this one moves as a whole, by where its first row is.
  const firstRowOf = new Map<Id, number>();
  rows.forEach((row, index) => {
    const levels = memberships.get(row.id) ?? [];
    const inside = levels.findIndex((candidate) => candidate.fragment === level.fragment);
    if (inside < 0) {
      return;
    }
    const here = levels[inside];
    const child = levels[inside + 1];
    if (child === undefined) {
      if (here !== undefined) {
        edgeProps.set(row.id, withProps(row.props, { operand: operandProp(moved(here.operand, index)) }));
      }
    } else if (!firstRowOf.has(child.fragment)) {
      firstRowOf.set(child.fragment, index);
      if (here !== undefined) {
        nodeProps.set(child.fragment, withProps(fragmentNodes(document).get(child.fragment)?.props, { operand: moved(here.operand, index) }));
      }
    }
  });

  const guards = fragmentOperands(node);
  while (guards.length < split + 1) {
    guards.push('');
  }
  guards.splice(split + 1, 0, fragmentOperator(node).toLowerCase() === 'alt' ? ELSE_GUARD : '');
  nodeProps.set(node.id, withProps(node.props, { operands: guards }));
  return { edit: { edgeProps, nodeProps }, fragment: node.id, operand: split + 1 };
}

/**
 * Adds an operand at the end of the fragment `fragmentId` (what Add operand does with the frame itself selected): the
 * last row directly in its last operand starts the new one, through `addOperand`'s split. An operand with no row is not
 * drawn, so a new one must take a row with it; undefined when the fragment is not an alt, par or critical, or when its
 * last operand holds a single row (splitting would leave the operand before empty).
 */
function addOperandAtEnd(document: Document, fragmentId: Id): OperandResult | undefined {
  const memberships = sequenceMemberships(document);
  const levelOf = (row: Edge) => memberships.get(row.id)?.find((level) => level.fragment === fragmentId);
  const inside = drawnRows(document).filter((row) => levelOf(row) !== undefined);
  const lastOperand = Math.max(-1, ...inside.map((row) => levelOf(row)?.operand ?? 0));
  const direct = inside.filter((row) => memberships.get(row.id)?.at(-1)?.fragment === fragmentId && levelOf(row)?.operand === lastOperand);
  const last = direct.at(-1);
  return last === undefined ? undefined : addOperand(document, last.id);
}

/** The operator text a fragment draws in its tab. */
function fragmentOperator(node: Node): string {
  const value = node.props?.operator;
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : node.label;
}

/** Sets the guard text of operand `operand` of a fragment; undefined when it is already that text or there is no such fragment. */
export function setGuard(document: Document, fragmentId: Id, operand: number, text: string): SequenceEdit | undefined {
  const node = fragmentNodes(document).get(fragmentId);
  if (node === undefined || operand < 0) {
    return undefined;
  }
  const guards = fragmentOperands(node);
  const next = bareGuard(text.replace(/[\r\n]+/g, ' '));
  if (bareGuard(guards[operand] ?? '') === next) {
    return undefined;
  }
  while (guards.length <= operand) {
    guards.push('');
  }
  guards[operand] = next;
  return { nodeProps: new Map([[node.id, withProps(node.props, { operands: guards })]]) };
}

/** Sets a fragment's operator (its tab text) and its label to match; undefined when it is already that operator. */
export function setOperator(document: Document, fragmentId: Id, operator: string): SequenceEdit | undefined {
  const node = fragmentNodes(document).get(fragmentId);
  if (node === undefined || fragmentOperator(node) === operator) {
    return undefined;
  }
  return { nodeProps: new Map([[node.id, withProps(node.props, { operator })]]), nodeLabels: new Map([[node.id, operator]]) };
}

// ---- notes ----

export interface NoteResult {
  edit: SequenceEdit;
  note: Id;
}

/**
 * A new, empty note row right after `afterId` (a message, or another note), over the same participants; at the end, over the
 * first participant, when there is none. It is a member of the fragment both its neighbours are in, as any row inserted
 * there is. Undefined for a document with no participant.
 */
export function addNote(document: Document, afterId: Id | undefined, generateId: IdGenerator = randomIdGenerator, kind: typeof NOTE_KIND | typeof INTERACTION_USE_KIND = NOTE_KIND): NoteResult | undefined {
  const participants = document.nodes.filter((node) => node.kind !== FRAGMENT_KIND);
  const first = participants[0];
  if (first === undefined) {
    return undefined;
  }
  const anchor = afterId === undefined ? -1 : document.edges.findIndex((edge) => edge.id === afterId);
  const after = anchor < 0 ? undefined : document.edges[anchor];
  const index = anchor < 0 ? document.edges.length : anchor + 1;
  const taken = new Set([...document.nodes, ...document.edges].map((element) => element.id));
  let id = generateId('edge');
  for (let attempt = 0; taken.has(id); attempt++) {
    if (attempt > 1000) {
      throw new Error('Could not generate a free edge id in 1000 attempts.');
    }
    id = generateId('edge');
  }
  // Right after the last message of a frame the note stays inside it (the neighbour rule alone would put it just below the
  // border); it joins the innermost frame that message is in.
  const afterLevel = after === undefined ? undefined : sequenceMemberships(document).get(after.id)?.at(-1);
  const membership = afterLevel === undefined ? membershipAt(document, index) : { fragment: afterLevel.fragment, operand: afterLevel.operand };
  const note = withMembership({ id, kind, ...(kind === INTERACTION_USE_KIND ? { label: 'Interaction' } : {}), from: { node: after?.from.node ?? first.id }, to: { node: after?.to.node ?? first.id } }, membership);
  return { edit: { insertEdges: [{ edge: note, index }] }, note: id };
}

// ---- activation ----

export interface ActivationResult {
  edit: SequenceEdit;
  /** Whether the message now opens an activation bar. */
  active: boolean;
}

/** Which message closes each bar: the same per-lifeline stack walk the scene draws and the critique rule checks. */
function activationClosers(document: Document): Map<Id, Id> {
  const open = new Map<Id, Id[]>();
  const closers = new Map<Id, Id>();
  for (const edge of document.edges) {
    if (isSet(edge, 'activate')) {
      open.set(edge.to.node, [...(open.get(edge.to.node) ?? []), edge.id]);
    }
    if (isSet(edge, 'deactivate')) {
      const opener = open.get(edge.from.node)?.pop();
      if (opener !== undefined) {
        closers.set(opener, edge.id);
      }
    }
  }
  return closers;
}

/**
 * Turns the activation bar a message opens on its receiver on or off, as one edit. Off removes `activate` and the
 * `deactivate` of the message that closed it. On sets `activate` and closes the bar at the receiver's first later message
 * back out (a `return` when there is one, else any message the receiver sends that does not already close a bar); when
 * there is none the bar is left open, which the scene draws to the last row and the critique rule flags. Bars pair
 * innermost first, so with another bar already open on the receiver the new one closes at the first close after it.
 * Undefined for a note, which has no lifeline to activate, and for a message that is not there.
 */
export function toggleActivation(document: Document, messageId: Id): ActivationResult | undefined {
  const at = document.edges.findIndex((edge) => edge.id === messageId);
  const message = document.edges[at];
  if (message === undefined || isBoxRow(message.kind)) {
    return undefined;
  }
  const edgeProps = new Map<Id, Props | null>();
  if (isSet(message, 'activate')) {
    edgeProps.set(message.id, withProps(message.props, {}, ['activate']));
    const closer = document.edges.find((edge) => edge.id === activationClosers(document).get(message.id));
    if (closer !== undefined) {
      edgeProps.set(closer.id, withProps(closer.props, {}, ['deactivate']));
    }
    return { edit: { edgeProps }, active: false };
  }
  edgeProps.set(message.id, withProps(message.props, { activate: true }));
  const later = document.edges.slice(at + 1).filter((edge) => edge.from.node === message.to.node && !isBoxRow(edge.kind) && !isSet(edge, 'deactivate'));
  const closer = later.find((edge) => edge.kind === 'return') ?? later[0];
  if (closer !== undefined) {
    edgeProps.set(closer.id, withProps(closer.props, { deactivate: true }));
  }
  return { edit: { edgeProps }, active: true };
}
