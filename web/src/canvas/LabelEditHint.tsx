/**
 * The line shown while a label is being edited (QA P1): in a node's label Enter adds a line and the edit ends with
 * Cmd/Ctrl+Enter or a click away; in a connector's one-line label Enter ends it. Escape cancels in both. Without it
 * nothing says why Enter does not finish the edit, or that clicking away keeps what was typed. Drawn at the foot of the
 * canvas in screen space, so it is the same size at every zoom and never turns with a rotated shape.
 */

import { modifierName } from '../editor/platform.ts';

export interface LabelEditHintProps {
  /** A connector's label is one line, so Enter finishes it; a node's label breaks lines with Enter. */
  singleLine: boolean;
}

export function LabelEditHint({ singleLine }: LabelEditHintProps) {
  return (
    <div className="label-edit-hint" role="note">
      {singleLine ? 'Enter to finish, Esc to cancel.' : `Enter adds a line. ${modifierName()}+Enter or a click away finishes; Esc cancels.`}
    </div>
  );
}
