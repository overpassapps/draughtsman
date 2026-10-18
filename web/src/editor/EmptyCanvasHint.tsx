/**
 * The one line on an empty canvas (design/atlas/empty-loading-error-states.md, "New document, empty canvas"): where the
 * shapes, connectors and command palette are. Muted, dismissible, pointer-transparent, and gone for good once dismissed
 * or once the person has drawn a node (canvasHint.ts keeps that per browser). Opening a document that already has nodes
 * hides the hint there but does not use it up: only a node appearing on a canvas that was empty does.
 */
import { useEffect, useState } from 'react';
import { canvasHintDismissed, paletteModifier, rememberCanvasHintDismissed } from './canvasHint.ts';

interface Props {
  /** Whether the document has any node yet: the first one ends the hint for good. */
  hasNodes: boolean;
}

export function EmptyCanvasHint({ hasNodes }: Props) {
  const [dismissed, setDismissed] = useState(canvasHintDismissed);
  // Used up by a node appearing on a canvas that was empty: adjusted during render (not in an effect) so the hint cannot
  // flash back if the canvas is emptied again in the same session.
  const [usedUp, setUsedUp] = useState(false);
  const [hadNodes, setHadNodes] = useState(hasNodes);
  if (hadNodes !== hasNodes) {
    setHadNodes(hasNodes);
    if (hasNodes) {
      setUsedUp(true);
    }
  }

  useEffect(() => {
    if (usedUp) {
      rememberCanvasHintDismissed();
    }
  }, [usedUp]);

  if (dismissed || usedUp || hasNodes) {
    return null;
  }
  return (
    <div className="canvas-hint" role="note">
      <span>Press S for shapes, C for connectors, or {paletteModifier()}+K for everything.</span>
      <button
        type="button"
        className="canvas-hint-close"
        aria-label="Dismiss this hint"
        onClick={() => {
          rememberCanvasHintDismissed();
          setDismissed(true);
        }}
      >
        ×
      </button>
    </div>
  );
}
