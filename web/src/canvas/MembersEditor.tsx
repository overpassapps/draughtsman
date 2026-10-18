/**
 * The member editor for a UML class (docs/uml-class-compartments.md) or an ER entity: a plain-text field laid exactly
 * over one member compartment (a class's attributes or operations, an entity's columns), one member per line, in the renderer's own font,
 * row height and padding at the current zoom and rotation. It is the label editor's sibling (LabelEditor.tsx) and
 * shares its behaviour (useLabelField.ts): it owns every key and press while open, Enter inserts a new line,
 * Cmd/Ctrl+Enter or focus moving elsewhere commits, and Escape discards.
 *
 * Nothing here writes the model: the caller receives the committed text and turns it into the member list.
 */

import type { CSSProperties } from 'react';
import type { NodeKindStyle } from '../theme/diagramTheme.ts';
import { ER_METRICS, erShape, layoutEntity, partBox as entityPartBox } from '../model/erShapes.ts';
import type { MemberPart } from '../model/memberParts.ts';
import { layoutClass, memberLines, partBox, umlShape, UML_METRICS } from '../model/umlShapes.ts';
import type { PlacedNode } from './documentIndex.ts';
import { nodeFrameTransform, type LabelEditEnd } from './labelEditing.ts';
import { labelBaselineShift } from './text.ts';
import { useLabelField } from './useLabelField.ts';
import type { Viewport } from './viewport.ts';

export interface MembersEditorProps {
  node: PlacedNode;
  part: MemberPart;
  style: NodeKindStyle;
  viewport: Viewport;
  onCommit: (text: string, end: LabelEditEnd) => void;
  onCancel: (end: LabelEditEnd) => void;
}

const PART_LABELS: Readonly<Record<MemberPart, string>> = {
  attributes: 'Attributes, one per line',
  operations: 'Operations, one per line',
  columns: 'Columns, one per line',
};

export function MembersEditor({ node, part, style, viewport, onCommit, onCancel }: MembersEditorProps) {
  const field = useLabelField(memberLines(node.props, part).join('\n'), onCommit, onCancel);
  const uml = umlShape(node.shape);
  const entity = erShape(node.shape);
  const { w, h } = node.geometry;
  // The compartment's box comes from the shape's own layout; a part the shape does not have has no editor.
  const area = entity !== undefined ? (part === 'columns' ? entityPartBox(layoutEntity(node.label, node.props, h), 'columns', w) : undefined) : uml !== undefined && part !== 'columns' ? partBox(layoutClass(uml, node.label, node.props, h), part, w) : undefined;
  if (area === undefined) {
    return null;
  }
  // A class and an entity share the row height, padding and font metrics.
  const m = entity === undefined ? UML_METRICS : ER_METRICS;
  const frame: CSSProperties = {
    width: node.geometry.w,
    height: node.geometry.h,
    transform: nodeFrameTransform(viewport, node.geometry),
  };
  const box: CSSProperties = {
    left: area.x,
    top: area.y,
    width: area.w,
    height: area.h,
    paddingLeft: m.padX,
    paddingRight: m.padX,
    paddingTop: m.padY,
    justifyContent: 'flex-start',
  };
  // Members never wrap (the renderer draws each on one line), so the field does not either.
  const text: CSSProperties = {
    whiteSpace: 'pre',
    fontFamily: style.font,
    fontSize: m.fontSize,
    lineHeight: `${String(m.rowHeight)}px`,
    color: style.textColor,
    textAlign: 'start',
  };
  const shift = labelBaselineShift(style.font);
  if (shift !== 0) {
    text.transform = `translateY(${String(shift)}px)`;
  }
  return (
    <div className="label-editor-frame" style={frame}>
      <div className="label-editor-box" style={box}>
        <div ref={field} className="label-editor" style={text} contentEditable="plaintext-only" role="textbox" aria-multiline="true" aria-label={PART_LABELS[part]} data-editing-id={node.id} data-editing-part={part} spellCheck={false} />
      </div>
    </div>
  );
}
