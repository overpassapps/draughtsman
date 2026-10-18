/**
 * The derived geometry of a sequence diagram (document type `sequence`): a pure function of the document, never stored
 * in it. Participants are the nodes, in node order left to right; messages are the edges, in edge order top to bottom
 * (the order of `edges[]` is time). Nothing about a position is persisted, so inserting a message re-flows every row
 * below it on the next render, and an agent writes messages in the order they happen without computing a coordinate.
 *
 * Everything that draws or hit-tests a sequence document reads this one scene: `indexDocument` fills its `placed`,
 * `nodeBounds` and `edgePaths` from it, the canvas draws the lifelines and activation bars it lists, and the sidecar's
 * `sequence.scene` method bundles this very module for the server renderer (SvgRenderer.cs), the way `route.document`
 * bundles the router - so the editor, the export and the server cannot draw different pictures. Text width is the
 * shared `estimateTextWidth`, never a DOM measurement, for the same reason. The scene is plain data (no Maps) so it
 * crosses the sidecar's JSON-RPC boundary as it is.
 *
 * Rules, in the order they are applied (docs/sequence-diagrams.md is the prose version):
 * 1. Columns. A participant's header is `HEADER_WIDTH` wide (wider for a long label); neighbouring lifelines are at
 *    least a header-and-gap apart and far enough apart for every label crossing that gap, a label spread evenly over
 *    the gaps its message spans. A self message reserves room to the right of its lifeline for its label.
 * 2. Rows. Each message takes `ROW_HEIGHT` (`SELF_ROW_HEIGHT` for a self message) below the last, in edge order. A
 *    message whose ends are not both participants draws nothing and takes no row.
 * 3. Activation bars. Per-lifeline stacks: a message with `activate` opens a bar on its receiver, one with
 *    `deactivate` closes its sender's innermost open bar (nothing open: ignored; critique flags it). Nested bars are
 *    offset half a bar width each. A bar still open at the end runs to the last row. A message's ends stop at the
 *    edge of a bar open on that lifeline at that moment, not at the lifeline behind it.
 * 4. A message's label sits above its line, centred; a self message is a three-segment bracket to the lifeline's right
 *    with its label over the top segment.
 * 5. Notes. An edge of kind `note` is a row that is not an arrow: a box across the lifelines from its `from` participant
 *    to its `to` (either order), or centred on the one lifeline when they are the same, holding its label. An
 *    `interaction-use` is the same row as a `ref` frame (`refHeight` in a `refRowHeight` row) with a tab, its label
 *    centred under the tab: the name of the interaction it stands for.
 * 6. Create and destroy. A `create` message ends at its target's header, which is drawn at that row (its lifeline starts
 *    under it); a `destroy` message ends its target's lifeline there, with an X. Only the first of each per participant.
 * 7. Combined fragments. A node of kind `fragment` is a frame, never a column. Membership lives on each message
 *    (`props.in` the fragment's id, `props.operand` the index of the guard it sits under), nested fragments through the
 *    node's `parent`, and the frame is derived as the first to the last member row, so deleting a message never leaves
 *    a dangling reference. A frame opening adds a guard row (`fragmentGuardRow`) above its first member, a change of
 *    operand adds a guard row and a divider, and a frame closing adds `fragmentClosePad` below its last member; nested
 *    frames are inset `fragmentInset` a level. A fragment with no members draws as an empty one-row frame after the last
 *    row. A message between a fragment's first and last member that is not a member is drawn inside its frame
 *    (critique flags it as `non-contiguous-fragment`). A message row straight after a frame closes is pushed down until
 *    its label's plate clears that frame's bottom border by `fragmentLabelClearance` (the plate reaches above its own
 *    row), so no border crosses a label; a new frame opening in between already has its guard row for that, and a frame that opens
 *    straight after another closes starts `fragmentFrameGap` below its border, so two frames never share one.
 *
 * The literal numbers are pinned by sequenceScene.test.ts against a hand-computed example; the C# critique rule
 * `unbalanced-activation` (CritiqueAnalyser) walks the same activate/deactivate stacks.
 */

import { estimateTextWidth, labelFont } from '../canvas/textEstimate.ts';
import type { Rect } from '../canvas/geometry.ts';
import type { Document, Edge, Id, Node, Point } from './types.ts';

export const SEQUENCE_METRICS = {
  /** Least width of a participant header; a longer label widens it. */
  headerWidth: 120,
  headerHeight: 40,
  /** Space above the headers and left of the first one. */
  margin: 20,
  /** Least clear space between two neighbouring headers. */
  headerGap: 20,
  /** From the header's bottom edge to the top of the first message row. */
  firstRowOffset: 30,
  rowHeight: 36,
  selfRowHeight: 52,
  /** A self message's top segment, below its row's top. */
  selfTop: 20,
  selfWidth: 30,
  selfHeight: 16,
  activationWidth: 10,
  /** Room a label needs beyond its text, in the column and self-message rules. */
  labelPad: 24,
  /** A message label's plate is centred this far above its line. */
  labelLift: 14,
  /** A self message's label starts this far right of its bracket's left end. */
  selfLabelInset: 8,
  /** Padding either side of a label's text, as the label plate is drawn (labelEditing.ts `EDGE_LABEL_PAD_X`). */
  plateGap: 4,
  bottomMargin: 20,
  /** A note's box, centred in its row. */
  noteHeight: 28,
  /** An interaction use's frame and its row: the `ref` tab (`fragmentTagHeight`) on top and the name centred in what is under it. */
  refHeight: 48,
  refRowHeight: 60,
  /** The folded corner of a note's box. */
  noteFold: 8,
  /** A note over a single lifeline is at least this wide. */
  noteMinWidth: 100,
  /** A note across several lifelines reaches this far beyond the outer two. */
  notePadX: 10,
  /** Clear space kept between a single-lifeline note's edge and the neighbouring lifeline. */
  noteClearance: 12,
  /** A `create` message's row: the target's header (`headerHeight`) with 4 clear above and below it. */
  createRowHeight: 48,
  /** Half the side of the X that ends a destroyed lifeline. */
  destroyMark: 8,
  /** The row a frame opens (or starts a new operand) with: the operator tab and guard text sit in it. */
  fragmentGuardRow: 22,
  /** Space added below a frame's last member row; a nested frame closing on the same row steps out by it again. */
  fragmentClosePad: 8,
  /** A frame reaches this far beyond the outermost lifelines its members touch. */
  fragmentPad: 20,
  /** A frame reaches this much further out than the frame nested in it, each side. */
  fragmentInset: 10,
  /** The operator tab's height, at the frame's top left. */
  fragmentTagHeight: 18,
  /** Padding either side of the operator's text in its tab, and between the tab and the first guard. */
  fragmentTextPad: 8,
  /** The font size of the operator and guard text. */
  fragmentFontSize: 12,
  /** An actor header's stick figure takes this much of its height, centred over the lifeline; the name sits in the rest, under it (the header is no taller or wider than a participant's). */
  actorFigureHeight: 26,
  /** Half the height of a message label's plate, which is centred `labelLift` above its line (the drawn plate is 22.2 high, a shade under this 24). */
  labelPlateHalf: 12,
  /** Clear space kept between a frame's bottom border and the top of the plate of the message row that follows it. */
  fragmentLabelClearance: 2,
  /** Clear space between a frame's bottom border and the top border of the next frame that opens straight after it. */
  fragmentFrameGap: 6,
} as const;

/** The node kind of a combined fragment; every other node of a sequence document is a participant. */
export const FRAGMENT_KIND = 'fragment';
/** The edge kinds that are not plain messages. */
export const NOTE_KIND = 'note';
/** An interaction use: a `ref` frame across the lifelines it covers, its label the name of the interaction it stands for. */
export const INTERACTION_USE_KIND = 'interaction-use';

/** True for the edge kinds that are a box across lifelines rather than an arrow: a note and an interaction use. They take no activation and no arrowhead. */
export function isBoxRow(kind: string): boolean {
  return kind === NOTE_KIND || kind === INTERACTION_USE_KIND;
}
export const CREATE_KIND = 'create';
export const DESTROY_KIND = 'destroy';
/** The participant kind drawn as a UML stick figure rather than a box. */
export const ACTOR_KIND = 'actor';
/** The presentation shape an actor header is drawn with (canvas/shapes.tsx `actorRender`, SvgRenderer `ActorMarkup`). */
export const ACTOR_SHAPE = 'custom:sequence/actor';

/**
 * The shape a participant is drawn with: an `actor` is a stick figure whatever its stored shape says (documents written
 * before the figure stored `rounded`, the kind's default), every other kind keeps the shape it has.
 */
export function participantShape(node: Node): string {
  return node.kind === ACTOR_KIND && (node.shape === 'rounded' || node.shape === ACTOR_SHAPE) ? ACTOR_SHAPE : node.shape;
}

/** The font every width in the scene is estimated in; fixed so the editor, export and server agree. */
const SCENE_FONT = labelFont('sans-serif');
/** The font of an operator tab and a guard; estimated the same fixed way. */
const FRAGMENT_FONT = labelFont('sans-serif', SEQUENCE_METRICS.fragmentFontSize);

export interface SequenceHeader {
  id: Id;
  x: number;
  y: number;
  w: number;
  h: number;
  /** The lifeline's x. */
  cx: number;
}

export interface SequenceLifeline {
  id: Id;
  x: number;
  y0: number;
  y1: number;
}

export interface SequenceMessage {
  id: Id;
  /** From the sender's end to the receiver's; four points for a self message's bracket; a note's outline (closed, five points). */
  points: Point[];
  /** The y of the line (a self message: its top segment; a note: the middle of its box). */
  y: number;
  self: boolean;
  /** Where the label plate is centred, when the message has a label (a note: the middle of its box, no plate). */
  labelAt?: Point;
  /** The label text's estimated width; the plate adds `plateGap` either side. Present with `labelAt`. */
  labelWidth?: number;
  /** The row this message takes, top and bottom; a guard row above it or a frame's closing pad below it is not part of it. */
  rowTop: number;
  rowBottom: number;
  /** Present on a `note` or `interaction-use` row: the box drawn across the lifelines it covers; `tagWidth` is present only on an interaction use, whose frame has a `ref` tab that wide at its top left. */
  note?: { x: number; y: number; w: number; h: number; tagWidth?: number };
}

export interface SequenceBar {
  participant: Id;
  /** The message that opened it. */
  opener: Id;
  /** Nesting depth on its lifeline; 0 is the outermost. */
  depth: number;
  x: number;
  y: number;
  w: number;
  h: number;
}

/** The X that ends a destroyed participant's lifeline, centred on (x, y). */
export interface SequenceDestroy {
  participant: Id;
  x: number;
  y: number;
}

/** One operand of a frame: the guard text under the frame's top (the first) or under a divider (the others). */
export interface SequenceFragmentSection {
  /** The operand index the members of this section carry. */
  operand: number;
  guard: string;
  /** The top of its guard row; the first section's is the frame's top, the others' are where a divider is drawn. */
  y: number;
  /** Where its guard text starts (left-aligned, vertically centred): right of the operator tab for the first section, else at the frame's left inset. */
  guardAt: Point;
}

export interface SequenceFragment {
  id: Id;
  /** The text of the operator tab: `alt`, `opt`, `loop`, ... */
  operator: string;
  x: number;
  y: number;
  w: number;
  h: number;
  /** Nesting depth; 0 is outermost. Frames are listed outermost first so an inner one is drawn over its parent. */
  depth: number;
  tagWidth: number;
  tagHeight: number;
  sections: SequenceFragmentSection[];
  /** No message is a member: drawn as an empty one-row frame after the last row. */
  empty: boolean;
}

export interface SequenceScene {
  headers: SequenceHeader[];
  lifelines: SequenceLifeline[];
  messages: SequenceMessage[];
  bars: SequenceBar[];
  destroys: SequenceDestroy[];
  fragments: SequenceFragment[];
  /** The whole picture, margins included; undefined for a document with no participants. */
  bounds: Rect | undefined;
}

/** A boolean prop is on when it is `true` or the string "true" (the C# side, SequenceMessages.IsSet, accepts the same two). */
export function isSet(edge: Edge, key: 'activate' | 'deactivate'): boolean {
  const value = edge.props?.[key];
  return value === true || (typeof value === 'string' && value.toLowerCase() === 'true');
}

/** The item at `index`, which the caller has already established exists (every index here comes from the same lists it indexes). */
function at<T>(items: readonly T[], index: number): T {
  const item = items[index];
  if (item === undefined) {
    throw new Error(`sequenceScene: no item at index ${String(index)} of ${String(items.length)}`);
  }
  return item;
}

function textWidth(text: string): number {
  return estimateTextWidth(text, SCENE_FONT);
}

function fragmentTextWidth(text: string): number {
  return estimateTextWidth(text, FRAGMENT_FONT);
}

/** An operand index from a prop: a non-negative whole number, or a string of one; anything else is 0. */
function operandIndex(value: unknown): number {
  const number = typeof value === 'string' && /^\d+$/.test(value.trim()) ? Number(value) : value;
  return typeof number === 'number' && Number.isInteger(number) && number >= 0 ? number : 0;
}

/** The guard strings of a fragment node (`props.operands`); a non-string entry is an empty guard. */
export function fragmentOperands(node: Node): string[] {
  const value = node.props?.operands;
  return Array.isArray(value) ? value.map((guard) => (typeof guard === 'string' ? guard : '')) : [];
}

/**
 * A guard as stored, bare: the text without the UML brackets an older document or an importer may have kept around it.
 * The one stored form is the bare text; `guardDisplay` draws the brackets.
 */
export function bareGuard(text: string): string {
  const trimmed = text.trim();
  return trimmed.startsWith('[') && trimmed.endsWith(']') ? trimmed.slice(1, -1).trim() : trimmed;
}

/** A guard as drawn and edited in place: `[text]`, whichever of the two stored forms it was, and nothing for none. */
export function guardDisplay(text: string): string {
  const bare = bareGuard(text);
  return bare.length === 0 ? '' : `[${bare}]`;
}

/** The text of a fragment's operator tab: `props.operator`, else the node's label. */
export function fragmentOperator(node: Node): string {
  const value = node.props?.operator;
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : node.label;
}

/** One level of the fragments a message sits in: the fragment and the operand of it the message is under. */
export interface FragmentLevel {
  fragment: Id;
  operand: number;
}

/** The fragment nodes of a document by id (the first of a repeated id), the ones a message's `in` can name. */
export function fragmentNodes(document: Document): Map<Id, Node> {
  const found = new Map<Id, Node>();
  for (const node of document.nodes) {
    if (node.kind === FRAGMENT_KIND && !found.has(node.id)) {
      found.set(node.id, node);
    }
  }
  return found;
}

/** `id` and its fragment ancestors, outermost first; stops at a `parent` that is not a fragment and at a cycle. */
export function fragmentAncestry(fragments: ReadonlyMap<Id, Node>, id: Id): Id[] {
  const chain: Id[] = [];
  const seen = new Set<Id>();
  let current: Id | undefined = id;
  while (current !== undefined && fragments.has(current) && !seen.has(current)) {
    seen.add(current);
    chain.unshift(current);
    current = fragments.get(current)?.parent;
  }
  return chain;
}

/**
 * The fragments each message sits in, by edge id, outermost first: the message's own `in` fragment (under its own
 * `operand`) and the ancestors it nests in through `parent`. The operand a nested fragment sits under in its parent is
 * its own `props.operand` when it has one, else the operand of the parent's nearest earlier direct member (0 when
 * there is none), fixed at the first use. A message with no `in`, or one that names no fragment, is in none and has no entry.
 */
export function sequenceMemberships(document: Document): Map<Id, FragmentLevel[]> {
  const fragments = fragmentNodes(document);
  const lastOperand = new Map<Id, number>();
  const nestedOperand = new Map<Id, number>();
  const found = new Map<Id, FragmentLevel[]>();
  for (const edge of document.edges) {
    const inside = edge.props?.in;
    if (typeof inside !== 'string' || !fragments.has(inside)) {
      continue;
    }
    const chain = fragmentAncestry(fragments, inside);
    const own = operandIndex(edge.props?.operand);
    const levels = chain.map<FragmentLevel>((fragment, level) => {
      const child = chain[level + 1];
      if (child === undefined) {
        return { fragment, operand: own };
      }
      let operand = nestedOperand.get(child);
      if (operand === undefined) {
        const declared = fragments.get(child)?.props?.operand;
        operand = declared === undefined ? (lastOperand.get(fragment) ?? 0) : operandIndex(declared);
        nestedOperand.set(child, operand);
      }
      return { fragment, operand };
    });
    lastOperand.set(inside, own);
    found.set(edge.id, levels);
  }
  return found;
}

/** What a drawn row is: a plain message, a note, a message that creates its target, or one that destroys it. */
type Role = 'message' | 'note' | 'ref' | 'create' | 'destroy';

interface Item {
  edge: Edge;
  from: number;
  to: number;
  labelWidth: number;
  self: boolean;
  role: Role;
  levels: readonly FragmentLevel[];
}

/** A frame while the rows are laid out. */
interface OpenFrame {
  node: Node;
  depth: number;
  parent: Id | undefined;
  top: number;
  bottom: number;
  x0: number;
  x1: number;
  sections: { operand: number; guard: string; y: number }[];
  current: number;
}

export function sequenceScene(document: Document): SequenceScene {
  const m = SEQUENCE_METRICS;
  const fragments = fragmentNodes(document);
  const participants = [] as { id: Id; label: string; width: number }[];
  const column = new Map<Id, number>();
  for (const node of document.nodes) {
    if (column.has(node.id) || node.kind === FRAGMENT_KIND) {
      continue;
    }
    column.set(node.id, participants.length);
    participants.push({ id: node.id, label: node.label, width: Math.max(m.headerWidth, Math.ceil(textWidth(node.label) + m.labelPad)) });
  }
  if (participants.length === 0) {
    return { headers: [], lifelines: [], messages: [], bars: [], destroys: [], fragments: [], bounds: undefined };
  }

  // Only a row between two participants is drawn; it is the same list the rows, gaps and bars are built from.
  const memberships = sequenceMemberships(document);
  const created = new Set<number>();
  const destroyed = new Set<number>();
  const items: Item[] = document.edges.flatMap((edge) => {
    const from = column.get(edge.from.node);
    const to = column.get(edge.to.node);
    if (from === undefined || to === undefined) {
      return [];
    }
    const self = from === to;
    let role: Role = 'message';
    if (edge.kind === NOTE_KIND) {
      role = 'note';
    } else if (edge.kind === INTERACTION_USE_KIND) {
      role = 'ref';
    } else if (edge.kind === CREATE_KIND && !self && !created.has(to)) {
      created.add(to);
      role = 'create';
    } else if (edge.kind === DESTROY_KIND && !destroyed.has(to)) {
      destroyed.add(to);
      role = 'destroy';
    }
    return [{ edge, from, to, self, role, labelWidth: edge.label === undefined || edge.label.length === 0 ? 0 : textWidth(edge.label), levels: memberships.get(edge.id) ?? [] }];
  });

  // 1. Columns: the gap after each participant grows to fit its headers and the labels crossing it.
  const gap = participants.slice(1).map((next, i) => (at(participants, i).width + next.width) / 2 + m.headerGap);
  let firstCentre = m.margin + at(participants, 0).width / 2;
  for (const { from, to, labelWidth, role } of items) {
    if (role === 'note' || role === 'ref') {
      const lo = Math.min(from, to);
      const hi = Math.max(from, to);
      if (hi > lo) {
        const need = (labelWidth + m.labelPad - 2 * m.notePadX) / (hi - lo);
        for (let g = lo; g < hi; g++) {
          gap[g] = Math.max(at(gap, g), need);
        }
      } else {
        // A note over one lifeline stays clear of both neighbours, and of the picture's left edge.
        const half = noteWidth(labelWidth) / 2;
        if (lo > 0) {
          gap[lo - 1] = Math.max(at(gap, lo - 1), half + m.noteClearance);
        }
        if (lo < gap.length) {
          gap[lo] = Math.max(at(gap, lo), half + m.noteClearance);
        }
        if (lo === 0) {
          firstCentre = Math.max(firstCentre, m.margin + half);
        }
      }
      continue;
    }
    if (from === to) {
      if (labelWidth > 0 && from < gap.length) {
        gap[from] = Math.max(at(gap, from), labelWidth + m.labelPad);
      }
      continue;
    }
    if (labelWidth === 0) {
      continue;
    }
    const lo = Math.min(from, to);
    const hi = Math.max(from, to);
    const need = (labelWidth + m.labelPad) / (hi - lo);
    for (let g = lo; g < hi; g++) {
      gap[g] = Math.max(at(gap, g), need);
    }
  }
  const centres = [firstCentre];
  for (const g of gap) {
    centres.push(at(centres, centres.length - 1) + g);
  }
  const headerBottom = m.margin + m.headerHeight;
  // A created participant's header is drawn at its create row, not at the top.
  const headerTop = participants.map<number>(() => m.margin);
  const destroyedAt = new Map<number, number>();
  const destroys: SequenceDestroy[] = [];

  // Where in the item list each frame is first and last a member.
  const last = new Map<Id, number>();
  items.forEach((item, index) => {
    for (const level of item.levels) {
      last.set(level.fragment, index);
    }
  });

  // 2 to 7. Rows top to bottom in message order, with each lifeline's stack of open activation bars and each frame's state.
  const open: { opener: Id; y: number; depth: number }[][] = participants.map(() => []);
  const bars: SequenceBar[] = [];
  const closeBar = (participant: number, bar: { opener: Id; y: number; depth: number }, y1: number): void => {
    if (y1 > bar.y) {
      bars.push({ participant: at(participants, participant).id, opener: bar.opener, depth: bar.depth, x: barX(at(centres, participant), bar.depth), y: bar.y, w: m.activationWidth, h: y1 - bar.y });
    }
  };
  const frames = new Map<Id, OpenFrame>();
  const sceneMessages: SequenceMessage[] = [];
  let rowTop = headerBottom + m.firstRowOffset;
  let right = m.margin + participants.reduce((total, p) => Math.max(total, p.width), 0);
  // The y of the bottom border of the frame(s) that closed on the previous row, until the next row has cleared it.
  let closedBorder: number | undefined;
  items.forEach((item, index) => {
    const { edge, from, to, labelWidth, self, role } = item;
    const border = closedBorder;
    closedBorder = undefined;

    // Frames opening, and frames whose next member is under another operand, each take a guard row before the row.
    item.levels.forEach((level, depth) => {
      const guards = guardsOf(fragments, level.fragment);
      const frame = frames.get(level.fragment);
      if (frame === undefined) {
        const node = fragments.get(level.fragment);
        if (node !== undefined) {
          if (border !== undefined && rowTop === border) {
            // A frame opening right under one that just closed would share its border: keep them apart.
            rowTop += m.fragmentFrameGap;
          }
          frames.set(level.fragment, { node, depth, parent: item.levels[depth - 1]?.fragment, top: rowTop, bottom: rowTop, x0: Infinity, x1: -Infinity, sections: [{ operand: level.operand, guard: guards[level.operand] ?? '', y: rowTop }], current: level.operand });
          rowTop += m.fragmentGuardRow;
        }
      } else if (level.operand !== frame.current) {
        frame.sections.push({ operand: level.operand, guard: guards[level.operand] ?? '', y: rowTop });
        frame.current = level.operand;
        rowTop += m.fragmentGuardRow;
      }
    });

    const boxRow = role === 'note' || role === 'ref';
    const rowHeight = role === 'create' ? m.createRowHeight : role === 'ref' ? m.refRowHeight : self && !boxRow ? m.selfRowHeight : m.rowHeight;
    if (border !== undefined && labelWidth > 0 && !boxRow) {
      // The plate is centred `labelLift` above the line and reaches `labelPlateHalf` either side of that: room a frame's closing pad may not give it.
      const lineOffset = self && role !== 'create' ? m.selfTop : rowHeight / 2;
      const overhang = m.labelLift + m.labelPlateHalf - lineOffset + m.fragmentLabelClearance - (rowTop - border);
      if (overhang > 0) {
        rowTop += overhang;
      }
    }
    const rowBottom = rowTop + rowHeight;
    let extent: [number, number];
    let message: SequenceMessage;

    if (boxRow) {
      const lo = Math.min(from, to);
      const hi = Math.max(from, to);
      // Across lifelines the box reaches `notePadX` past the outer two (the gaps were widened to hold its text); over one it is `noteWidth`.
      const width = hi > lo ? Math.max(at(centres, hi) - at(centres, lo) + 2 * m.notePadX, labelWidth > 0 ? labelWidth + m.labelPad : 0) : noteWidth(labelWidth);
      const x = hi > lo ? at(centres, lo) - m.notePadX : at(centres, lo) - width / 2;
      const height = role === 'ref' ? m.refHeight : m.noteHeight;
      const y = rowTop + (rowHeight - height) / 2;
      const note: NonNullable<SequenceMessage['note']> = { x, y, w: width, h: height };
      if (role === 'ref') {
        note.tagWidth = Math.ceil(fragmentTextWidth('ref') + 2 * m.fragmentTextPad);
      }
      message = { id: edge.id, points: [{ x, y }, { x: x + width, y }, { x: x + width, y: y + height }, { x, y: y + height }, { x, y }], y: y + height / 2, self: false, rowTop, rowBottom, note };
      if (labelWidth > 0) {
        // A note's text is centred in its box; an interaction use's is centred in what is under its `ref` tab.
        message.labelAt = { x: x + width / 2, y: role === 'ref' ? y + m.fragmentTagHeight + (height - m.fragmentTagHeight) / 2 : y + height / 2 };
        message.labelWidth = labelWidth;
      }
      extent = [x, x + width];
      right = Math.max(right, x + width + m.margin);
    } else {
      const y = role === 'create' ? rowTop + rowHeight / 2 : self ? rowTop + m.selfTop : rowTop + rowHeight / 2;
      if (role === 'create') {
        headerTop[to] = rowTop + (rowHeight - m.headerHeight) / 2;
      }

      if (isSet(edge, 'activate')) {
        const stack = at(open, to);
        // A bar on a participant this message creates starts under its header.
        stack.push({ opener: edge.id, y: role === 'create' ? at(headerTop, to) + m.headerHeight : y, depth: stack.length });
      }
      // The bars visible at this message: the receiver's including one it just opened, the sender's including one it is about to close.
      const receiverDepth = at(open, to).length;
      const senderDepth = at(open, from).length;
      if (isSet(edge, 'deactivate')) {
        const bar = at(open, from).pop();
        if (bar !== undefined) {
          closeBar(from, bar, y);
        }
      }

      const rightwards = to >= from;
      const receiverX = role === 'create' ? (rightwards ? at(centres, to) - at(participants, to).width / 2 : at(centres, to) + at(participants, to).width / 2) : lifelineEdge(at(centres, to), !rightwards, receiverDepth);
      const points: Point[] = self ? bracket(at(centres, from), y, senderDepth) : [{ x: lifelineEdge(at(centres, from), rightwards, senderDepth), y }, { x: receiverX, y }];
      message = { id: edge.id, points, y, self, rowTop, rowBottom };
      extent = [Math.min(...points.map((p) => p.x)), Math.max(...points.map((p) => p.x))];
      if (role === 'create') {
        extent = [Math.min(extent[0], at(centres, to) - at(participants, to).width / 2), Math.max(extent[1], at(centres, to) + at(participants, to).width / 2)];
      }
      if (labelWidth > 0) {
        message.labelWidth = labelWidth;
        if (self) {
          const start = at(points, 0).x + m.selfLabelInset;
          message.labelAt = { x: start + labelWidth / 2, y: y - m.labelLift };
          right = Math.max(right, start + labelWidth + m.plateGap * 2 + m.margin);
          extent[1] = Math.max(extent[1], start + labelWidth + m.plateGap * 2);
        } else {
          message.labelAt = { x: (at(points, 0).x + at(points, 1).x) / 2, y: y - m.labelLift };
        }
      }
      if (self) {
        right = Math.max(right, at(points, 1).x + m.margin);
      }
      if (role === 'destroy') {
        // The target's lifeline ends here, with an X; every bar still open on it ends here too.
        const endY = self ? y + m.selfHeight : y;
        for (const bar of at(open, to).splice(0)) {
          closeBar(to, bar, endY);
        }
        destroyedAt.set(to, endY);
        destroys.push({ participant: at(participants, to).id, x: at(centres, to), y: endY });
      }
    }
    sceneMessages.push(message);
    rowTop = rowBottom;

    for (const level of item.levels) {
      const frame = frames.get(level.fragment);
      if (frame !== undefined) {
        frame.x0 = Math.min(frame.x0, extent[0]);
        frame.x1 = Math.max(frame.x1, extent[1]);
      }
    }
    // Frames whose last member this was close, the innermost first, each with a little room below.
    for (const level of [...item.levels].reverse()) {
      if (last.get(level.fragment) === index) {
        const frame = frames.get(level.fragment);
        if (frame !== undefined) {
          rowTop += m.fragmentClosePad;
          frame.bottom = rowTop;
          closedBorder = rowTop;
        }
      }
    }
  });

  // Frames no message is a member of: an empty one-row frame each, stacked after the last row and spanning every lifeline.
  const empties: OpenFrame[] = [];
  for (const node of fragments.values()) {
    if (frames.has(node.id)) {
      continue;
    }
    const guards = fragmentOperands(node).map(guardDisplay);
    const frame: OpenFrame = { node, depth: 0, parent: undefined, top: rowTop, bottom: 0, x0: at(centres, 0), x1: at(centres, centres.length - 1), sections: [{ operand: 0, guard: guards[0] ?? '', y: rowTop }], current: 0 };
    rowTop += m.fragmentGuardRow + m.rowHeight + m.fragmentClosePad;
    frame.bottom = rowTop;
    empties.push(frame);
  }

  // A bar nothing closed runs to the end of the last row.
  for (let p = 0; p < open.length; p++) {
    for (const bar of at(open, p)) {
      closeBar(p, bar, rowTop);
    }
  }
  bars.sort((a, b) => a.y - b.y || a.depth - b.depth || (a.participant < b.participant ? -1 : a.participant > b.participant ? 1 : 0));

  const bottom = Math.max(rowTop, headerBottom + m.firstRowOffset) + m.bottomMargin;

  // Frame boxes, the innermost first so a parent can reach beyond the frames nested in it.
  const emptySet = new Set(empties);
  const ordered = [...frames.values(), ...empties].sort((a, b) => b.depth - a.depth);
  const boxes = new Map<Id, { x: number; right: number }>();
  const placed = new Map<Id, SequenceFragment>();
  for (const frame of ordered) {
    const operator = fragmentOperator(frame.node);
    const tagWidth = Math.ceil(fragmentTextWidth(operator) + 2 * m.fragmentTextPad);
    let x = frame.x0 - m.fragmentPad;
    let edge = frame.x1 + m.fragmentPad;
    const inner = boxes.get(frame.node.id);
    if (inner !== undefined) {
      x = Math.min(x, inner.x - m.fragmentInset);
      edge = Math.max(edge, inner.right + m.fragmentInset);
    }
    // The tab and the first guard, or a later guard on its own, must fit inside the frame.
    const needed = Math.max(...frame.sections.map((section, i) => (i === 0 ? tagWidth + m.fragmentTextPad : m.fragmentTextPad) + fragmentTextWidth(section.guard) + m.fragmentTextPad * 1.5));
    edge = Math.max(edge, x + needed);
    x = Math.floor(x);
    edge = Math.ceil(edge);
    if (frame.parent !== undefined) {
      const outer = boxes.get(frame.parent);
      boxes.set(frame.parent, { x: Math.min(outer?.x ?? Infinity, x), right: Math.max(outer?.right ?? -Infinity, edge) });
    }
    placed.set(frame.node.id, {
      id: frame.node.id,
      operator,
      x,
      y: frame.top,
      w: edge - x,
      h: frame.bottom - frame.top,
      depth: frame.depth,
      tagWidth,
      tagHeight: m.fragmentTagHeight,
      sections: frame.sections.map((section, i) => ({ ...section, guardAt: { x: i === 0 ? x + tagWidth + m.fragmentTextPad : x + m.fragmentTextPad, y: section.y + m.fragmentTagHeight / 2 } })),
      empty: emptySet.has(frame),
    });
  }
  const sceneFragments = [...placed.values()].sort((a, b) => a.depth - b.depth || a.y - b.y);
  for (const fragment of sceneFragments) {
    right = Math.max(right, fragment.x + fragment.w + m.margin);
  }
  const left = Math.min(0, ...sceneFragments.map((fragment) => fragment.x - m.margin / 2));

  const headers = participants.map<SequenceHeader>((p, i) => ({ id: p.id, x: at(centres, i) - p.width / 2, y: at(headerTop, i), w: p.width, h: m.headerHeight, cx: at(centres, i) }));
  right = Math.max(right, ...headers.map((h) => h.x + h.w + m.margin));
  return {
    headers,
    lifelines: headers.map((h, i) => ({ id: h.id, x: h.cx, y0: h.y + h.h, y1: destroyedAt.get(i) ?? bottom })),
    messages: sceneMessages,
    bars,
    destroys,
    fragments: sceneFragments,
    bounds: { x: left, y: 0, w: right - left, h: bottom },
  };
}

/** A single-lifeline note's width: the least width, or its text and padding. */
function noteWidth(labelWidth: number): number {
  return Math.max(SEQUENCE_METRICS.noteMinWidth, Math.ceil(labelWidth + SEQUENCE_METRICS.labelPad));
}

function guardsOf(fragments: ReadonlyMap<Id, Node>, id: Id): string[] {
  const node = fragments.get(id);
  return node === undefined ? [] : fragmentOperands(node).map(guardDisplay);
}

/** The left edge of a bar at nesting `depth` on the lifeline at `cx`; each level is offset half a bar width to the right. */
function barX(cx: number, depth: number): number {
  return cx - SEQUENCE_METRICS.activationWidth / 2 + (depth * SEQUENCE_METRICS.activationWidth) / 2;
}

/** Where a message meets the lifeline at `cx` on its `rightSide` or left, given `depth` bars open there. */
function lifelineEdge(cx: number, rightSide: boolean, depth: number): number {
  const half = SEQUENCE_METRICS.activationWidth / 2;
  if (depth === 0) {
    return cx;
  }
  return rightSide ? cx + half + ((depth - 1) * SEQUENCE_METRICS.activationWidth) / 2 : cx - half;
}

/** A self message: out to the right of the lifeline (or its bars), down, and back. */
function bracket(cx: number, y: number, depth: number): Point[] {
  const m = SEQUENCE_METRICS;
  const x = lifelineEdge(cx, true, depth);
  return [
    { x, y },
    { x: x + m.selfWidth, y },
    { x: x + m.selfWidth, y: y + m.selfHeight },
    { x, y: y + m.selfHeight },
  ];
}
