/**
 * The label editor (the M2 editor checklist, items 13, 14 and 15): a plain-text contenteditable laid exactly over a
 * node's label box, with the renderer's own font, size, line height and wrapping width (text.ts, NodeView.tsx) at the
 * current zoom and rotation. Its text is laid out with LABEL_TEXT_LAYOUT, the CSS the renderer's line breaker lays
 * the label out with (text.ts `createDomLineBreaker`), so the lines typed here are the lines drawn when it closes. It is an HTML overlay rather than a foreignObject because foreignObject under a scaled or
 * rotated SVG group is still unreliable in WebKit.
 *
 * The field's behaviour (useLabelField.ts) is shared with the edge label editor: it owns every key and press while
 * open, Cmd/Ctrl+Enter or focus moving elsewhere in the page commits, focus leaving the window does not, and Escape
 * discards. Enter alone inserts a line break, which the label keeps and the renderer honours.
 *
 * Nothing here writes the model: the caller receives the committed text and decides whether it changed.
 */

import type { CSSProperties } from 'react';
import type { NodeKindStyle } from '../theme/diagramTheme.ts';
import type { PlacedNode } from './documentIndex.ts';
import { labelEditorLayout, nodeFrameTransform, type LabelEditEnd } from './labelEditing.ts';
import { LABEL_FONT_SIZE, LABEL_PADDING, LABEL_TEXT_LAYOUT, labelBaselineShift } from './text.ts';
import { useLabelField } from './useLabelField.ts';
import type { Viewport } from './viewport.ts';

export interface LabelEditorProps {
  node: PlacedNode;
  style: NodeKindStyle;
  /** Containers set their label top-left, as NodeView does; everything else centres it. */
  container: boolean;
  viewport: Viewport;
  onCommit: (text: string, end: LabelEditEnd) => void;
  onCancel: (end: LabelEditEnd) => void;
}

export function LabelEditor({ node, style, container, viewport, onCommit, onCancel }: LabelEditorProps) {
  const field = useLabelField(node.label, onCommit, onCancel);

  const layout = labelEditorLayout(node, style, container);
  const frame: CSSProperties = {
    width: node.geometry.w,
    height: node.geometry.h,
    transform: nodeFrameTransform(viewport, node.geometry),
  };
  const box: CSSProperties = {
    left: layout.box.x,
    top: layout.box.y,
    width: layout.box.w,
    height: layout.box.h,
    paddingLeft: LABEL_PADDING,
    paddingRight: LABEL_PADDING,
    paddingTop: layout.top ?? 0,
    justifyContent: layout.top === undefined ? 'center' : 'flex-start',
  };
  // The layout the renderer breaks lines with (text.ts), so the lines seen while editing are the lines drawn after.
  const fontSize = style.fontSize ?? LABEL_FONT_SIZE;
  const text: CSSProperties = {
    ...LABEL_TEXT_LAYOUT,
    fontFamily: style.font,
    fontSize,
    ...(style.bold ? { fontWeight: 'bold' as const } : {}),
    lineHeight: `${String(layout.lineHeight)}px`,
    color: style.textColor,
    textAlign: container ? 'start' : 'center',
  };
  // The fraction of a pixel by which the SVG sets a line lower than an HTML line box does (text.ts).
  const shift = labelBaselineShift(style.font);
  if (shift !== 0) {
    text.transform = `translateY(${String(shift)}px)`;
  }
  return (
    <div className="label-editor-frame" style={frame}>
      <div className="label-editor-box" style={box}>
        <div ref={field} className="label-editor" style={text} contentEditable="plaintext-only" role="textbox" aria-multiline="true" aria-label="Label" data-editing-id={node.id} spellCheck />
      </div>
    </div>
  );
}
