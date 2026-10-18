/**
 * The edge label editor (the M2 editor checklist, item 11): a plain-text field centred on the midpoint of the
 * edge's current route, drawn as EdgeView draws the label there (font, size, line height, plate colour and padding) at
 * the current zoom. The caller passes the point from the route the canvas is drawing, so the field follows the edge
 * when it re-routes under it. Behaviour is the node label editor's (useLabelField.ts), on one line: EdgeView draws an
 * edge label as a single line of text, so Enter commits too and a pasted line break becomes a space.
 *
 * Nothing here writes the model: the caller receives the committed text and decides whether it changed.
 */

import type { CSSProperties } from 'react';
import type { Id, Point } from '../model/types.ts';
import { EDGE_LABEL_PAD_X, EDGE_LABEL_PAD_Y, pointFrameTransform, type LabelEditEnd } from './labelEditing.ts';
import type { EdgeLabelStyle } from './renderedStyle.ts';
import { LABEL_FONT_SIZE, LABEL_LINE_HEIGHT } from './text.ts';
import { useLabelField } from './useLabelField.ts';
import type { Viewport } from './viewport.ts';

export interface EdgeLabelEditorProps {
  edgeId: Id;
  /** The label when the editor opened; an edge with none opens empty. */
  label: string;
  /** World point the label is centred on: the midpoint of the route being drawn now. */
  at: Point;
  style: EdgeLabelStyle;
  viewport: Viewport;
  /** The field's accessible name; a connector label by default (a fragment's guard passes its own). */
  ariaLabel?: string;
  onCommit: (text: string, end: LabelEditEnd) => void;
  onCancel: (end: LabelEditEnd) => void;
}

export function EdgeLabelEditor({ edgeId, label, at, style, viewport, ariaLabel = 'Connector label', onCommit, onCancel }: EdgeLabelEditorProps) {
  const field = useLabelField(label, onCommit, onCancel, { singleLine: true });
  const frame: CSSProperties = { transform: pointFrameTransform(viewport, at) };
  const text: CSSProperties = {
    fontFamily: style.font,
    fontSize: LABEL_FONT_SIZE,
    lineHeight: `${String(LABEL_FONT_SIZE * LABEL_LINE_HEIGHT)}px`,
    color: style.colour,
    background: style.background,
    padding: `${String(EDGE_LABEL_PAD_Y)}px ${String(EDGE_LABEL_PAD_X)}px`,
  };
  return (
    <div className="label-editor-frame" style={frame}>
      <div
        ref={field}
        className="label-editor edge-label-editor"
        style={text}
        contentEditable="plaintext-only"
        role="textbox"
        aria-multiline="false"
        aria-label={ariaLabel}
        data-editing-id={edgeId}
        spellCheck
      />
    </div>
  );
}
