import { SEQUENCE_METRICS, type SequenceFragment, type SequenceMessage, type SequenceScene } from '../model/sequenceScene.ts';
import type { Edge } from '../model/types.ts';
import type { EdgeKindStyle, NodeKindStyle } from '../theme/diagramTheme.ts';
import { EDGE_HIT_WIDTH } from './EdgeView.tsx';
import { DASH_ARRAYS, fmt } from './shapes.tsx';

/** A coordinate as path text. */
function n(value: number): string {
  return String(fmt(value));
}
import { LABEL_FONT_SIZE } from './text.ts';

/** What the layer needs to paint the parts that belong to a surface rather than to a node: a note's fill and text. */
export interface SequenceLayerPaint {
  font: string;
  /** The surface's own colour: a note's fill and an operator tab's, so both sit on the picture the way an edge label does. */
  background: string;
  colour: string;
}

export interface SequenceLayerProps {
  scene: SequenceScene;
  styles: ReadonlyMap<string, NodeKindStyle>;
  edgeStyles: ReadonlyMap<string, EdgeKindStyle>;
  /** The document's edge of an id: a note is an edge, and its label is the text the box shows. */
  edgeOf: (id: string) => Edge | undefined;
  paint: SequenceLayerPaint;
  /** Whether this render is an interaction surface: frames and notes are selectable only then. */
  interactive: boolean;
  /** The element whose text an editor is drawing instead; its text is left out. */
  editing?: string | undefined;
  /** The guard being edited in place; its text is left out so the editor is the only one drawn. */
  editingGuard?: { fragment: string; operand: number } | undefined;
}

/**
 * What a sequence diagram draws besides its participant headers (NodeView) and messages (EdgeView): each lifeline, a
 * dashed line from its header to the bottom of the picture, the activation bars along them, the X that ends a destroyed
 * lifeline, the combined-fragment frames and the note boxes, all from the derived scene (model/sequenceScene.ts). The
 * lifeline parts take the participant's own resolved style, so re-theming or a per-node style change carries down its
 * lifeline. Drawn beneath the nodes and edges, so a message's arrowhead lies over a bar. Lifelines and bars are inert to the
 * pointer (the header is what selects a participant, and the canvas hit-tests a lifeline itself for the connector tool,
 * sequenceHit.ts); a frame and a note are elements of the document, so each is a `<g data-id>` of its own class
 * (`node` for a fragment, `edge` for a note, as DiagramCanvas hit-tests by), a frame selectable only by its border and
 * its tab so the messages inside it stay clickable.
 */
export function SequenceLayer({ scene, styles, edgeStyles, edgeOf, paint, interactive, editing, editingGuard }: SequenceLayerProps) {
  const notes = scene.messages.filter((message) => message.note !== undefined);
  return (
    <g className="sequence-layer" style={{ pointerEvents: 'none' }}>
      {scene.lifelines.map((lifeline) => (
        <line
          key={lifeline.id}
          className="lifeline"
          data-lifeline={lifeline.id}
          x1={fmt(lifeline.x)}
          y1={fmt(lifeline.y0)}
          x2={fmt(lifeline.x)}
          y2={fmt(lifeline.y1)}
          stroke={styles.get(lifeline.id)?.stroke}
          strokeWidth={1}
          strokeDasharray={DASH_ARRAYS.dashed}
        />
      ))}
      {scene.bars.map((bar) => {
        const style = styles.get(bar.participant);
        return <rect key={`${bar.participant}:${bar.opener}`} className="activation" data-lifeline={bar.participant} x={fmt(bar.x)} y={fmt(bar.y)} width={fmt(bar.w)} height={fmt(bar.h)} fill={style?.fill} stroke={style?.stroke} strokeWidth={1} />;
      })}
      {scene.fragments.map((fragment) => (
        <FragmentFrame key={fragment.id} fragment={fragment} style={styles.get(fragment.id)} paint={paint} interactive={interactive} editingGuard={editingGuard?.fragment === fragment.id ? editingGuard.operand : undefined} />
      ))}
      {notes.map((message) => (
        <NoteBox key={message.id} message={message} edge={edgeOf(message.id)} style={edgeStyles.get(message.id)} paint={paint} interactive={interactive} editing={editing} />
      ))}
      {scene.destroys.map((destroy) => {
        const half = SEQUENCE_METRICS.destroyMark;
        const stroke = styles.get(destroy.participant)?.stroke;
        return (
          <path
            key={destroy.participant}
            className="destroy"
            data-lifeline={destroy.participant}
            d={`M ${n(destroy.x - half)} ${n(destroy.y - half)} L ${n(destroy.x + half)} ${n(destroy.y + half)} M ${n(destroy.x + half)} ${n(destroy.y - half)} L ${n(destroy.x - half)} ${n(destroy.y + half)}`}
            fill="none"
            stroke={stroke}
            strokeWidth={2}
          />
        );
      })}
    </g>
  );
}

function FragmentFrame({ fragment, style, paint, interactive, editingGuard }: { fragment: SequenceFragment; style: NodeKindStyle | undefined; paint: SequenceLayerPaint; interactive: boolean; editingGuard: number | undefined }) {
  const m = SEQUENCE_METRICS;
  const stroke = style?.stroke;
  const tag = `M ${n(fragment.x)} ${n(fragment.y)} H ${n(fragment.x + fragment.tagWidth)} V ${n(fragment.y + fragment.tagHeight - 6)} L ${n(fragment.x + fragment.tagWidth - 6)} ${n(fragment.y + fragment.tagHeight)} H ${n(fragment.x)} Z`;
  const textColour = style?.textColor ?? paint.colour;
  return (
    <g className="node fragment" data-id={fragment.id} data-kind="fragment">
      {interactive && (
        <rect
          className="hit-area"
          x={fmt(fragment.x)}
          y={fmt(fragment.y)}
          width={fmt(fragment.w)}
          height={fmt(fragment.h)}
          fill="none"
          stroke="transparent"
          strokeWidth={EDGE_HIT_WIDTH}
          vectorEffect="non-scaling-stroke"
          style={{ pointerEvents: 'stroke' }}
        />
      )}
      <rect className="frame" x={fmt(fragment.x)} y={fmt(fragment.y)} width={fmt(fragment.w)} height={fmt(fragment.h)} fill="none" stroke={stroke} strokeWidth={style?.strokeWidth ?? 1} />
      {fragment.sections.slice(1).map((section) => (
        <line key={`divider:${String(section.operand)}:${n(section.y)}`} className="divider" x1={fmt(fragment.x)} y1={fmt(section.y)} x2={fmt(fragment.x + fragment.w)} y2={fmt(section.y)} stroke={stroke} strokeWidth={1} strokeDasharray={DASH_ARRAYS.dashed} />
      ))}
      <path className="tag" d={tag} fill={style === undefined || style.fill === 'transparent' ? paint.background : style.fill} stroke={stroke} strokeWidth={1} style={interactive ? { pointerEvents: 'all' } : undefined} />
      <text className="operator" x={fmt(fragment.x + fragment.tagWidth / 2)} y={fmt(fragment.y + fragment.tagHeight / 2)} fill={textColour} fontFamily={paint.font} fontSize={m.fragmentFontSize} fontWeight={600} textAnchor="middle" dominantBaseline="central" style={{ userSelect: 'none', pointerEvents: 'none' }}>
          {fragment.operator}
        </text>
      {
        fragment.sections.map((section) =>
          section.guard.length === 0 || section.operand === editingGuard ? null : (
            <text key={`guard:${String(section.operand)}:${n(section.y)}`} className="guard" data-operand={section.operand} x={fmt(section.guardAt.x)} y={fmt(section.guardAt.y)} fill={textColour} fontFamily={paint.font} fontSize={m.fragmentFontSize} dominantBaseline="central" style={{ userSelect: 'none', pointerEvents: 'none' }}>
              {section.guard}
            </text>
          ),
        )}
    </g>
  );
}

function NoteBox({ message, edge, style, paint, interactive, editing }: { message: SequenceMessage; edge: Edge | undefined; style: EdgeKindStyle | undefined; paint: SequenceLayerPaint; interactive: boolean; editing: string | undefined }) {
  const note = message.note;
  if (note === undefined) {
    return null;
  }
  const { x, y, w, h } = note;
  if (note.tagWidth !== undefined) {
    return <InteractionUseFrame message={message} edge={edge} style={style} paint={paint} interactive={interactive} editing={editing} tagWidth={note.tagWidth} />;
  }
  const fold = SEQUENCE_METRICS.noteFold;
  const outline = `M ${n(x)} ${n(y)} H ${n(x + w - fold)} L ${n(x + w)} ${n(y + fold)} V ${n(y + h)} H ${n(x)} Z`;
  const corner = `M ${n(x + w - fold)} ${n(y)} V ${n(y + fold)} H ${n(x + w)}`;
  const stroke = style?.stroke ?? paint.colour;
  return (
    <g className="edge note" data-id={message.id} data-kind={edge?.kind ?? 'note'}>
      <path className="note-box" d={outline} fill={paint.background} stroke={stroke} strokeWidth={style?.strokeWidth ?? 1} strokeLinejoin="round" style={interactive ? { pointerEvents: 'all' } : undefined} />
      <path className="note-fold" d={corner} fill="none" stroke={stroke} strokeWidth={style?.strokeWidth ?? 1} />
      {message.labelAt !== undefined && editing !== message.id && (
        <text x={fmt(message.labelAt.x)} y={fmt(message.labelAt.y)} fill={paint.colour} fontFamily={paint.font} fontSize={LABEL_FONT_SIZE} textAnchor="middle" dominantBaseline="central" style={{ userSelect: 'none', pointerEvents: 'none' }}>
          {edge?.label ?? ''}
        </text>
      )}
    </g>
  );
}

/**
 * An interaction use: a `ref` frame across the lifelines it covers, filled with the surface's colour so the lifelines do not
 * run through it, the cut-corner `ref` tab (the same shape as a combined fragment's operator tab) at its top left and the
 * name of the interaction centred under the tab. An element of the document like a note, so it is selected by its outline and
 * tab; the name is the edge's label.
 */
function InteractionUseFrame({ message, edge, style, paint, interactive, editing, tagWidth }: { message: SequenceMessage; edge: Edge | undefined; style: EdgeKindStyle | undefined; paint: SequenceLayerPaint; interactive: boolean; editing: string | undefined; tagWidth: number }) {
  const m = SEQUENCE_METRICS;
  const { x, y, w, h } = must(message.note);
  const stroke = style?.stroke ?? paint.colour;
  const strokeWidth = style?.strokeWidth ?? 1;
  const tag = `M ${n(x)} ${n(y)} H ${n(x + tagWidth)} V ${n(y + m.fragmentTagHeight - 6)} L ${n(x + tagWidth - 6)} ${n(y + m.fragmentTagHeight)} H ${n(x)} Z`;
  return (
    <g className="edge note interaction-use" data-id={message.id} data-kind={edge?.kind ?? 'interaction-use'}>
      <rect className="note-box" x={fmt(x)} y={fmt(y)} width={fmt(w)} height={fmt(h)} fill={paint.background} stroke={stroke} strokeWidth={strokeWidth} style={interactive ? { pointerEvents: 'all' } : undefined} />
      <path className="tag" d={tag} fill={paint.background} stroke={stroke} strokeWidth={1} />
      <text className="operator" x={fmt(x + tagWidth / 2)} y={fmt(y + m.fragmentTagHeight / 2)} fill={paint.colour} fontFamily={paint.font} fontSize={m.fragmentFontSize} fontWeight={600} textAnchor="middle" dominantBaseline="central" style={{ userSelect: 'none', pointerEvents: 'none' }}>
        ref
      </text>
      {message.labelAt !== undefined && editing !== message.id && (
        <text x={fmt(message.labelAt.x)} y={fmt(message.labelAt.y)} fill={paint.colour} fontFamily={paint.font} fontSize={LABEL_FONT_SIZE} textAnchor="middle" dominantBaseline="central" style={{ userSelect: 'none', pointerEvents: 'none' }}>
          {edge?.label ?? ''}
        </text>
      )}
    </g>
  );
}

function must<T>(value: T | undefined): T {
  if (value === undefined) {
    throw new Error('expected a value, got none');
  }
  return value;
}
