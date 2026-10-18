/**
 * The Container section of the right properties panel (design/atlas/properties-panel.md, "Container: as Node plus a
 * Children count and 'Fit to contents' button"), with Group and Ungroup beside it so both are reachable without their
 * shortcuts (checklist item 28), as they are from the top bar's Arrange menu (editor/EditorMenu.tsx). Like
 * the Arrange section it is always shown, and a control that cannot act is disabled rather than hidden.
 *
 * Group needs a selected node, Ungroup a selected container-kind node, and Fit to contents a selected node that holds
 * children (model/grouping.ts). A mouse press hands focus back to the canvas, as the Arrange buttons do.
 */

import type { MouseEvent } from 'react';
import { platformShortcut } from './platform.ts';

export interface ContainerPanelProps {
  canGroup: boolean;
  canUngroup: boolean;
  canFit: boolean;
  /** Direct children of the one selected container, or undefined when the selection is not exactly one container. */
  childCount: number | undefined;
  onGroup: () => void;
  onUngroup: () => void;
  onFit: () => void;
  onMouseDown: (e: MouseEvent<HTMLButtonElement>) => void;
}

export function ContainerPanel({ canGroup, canUngroup, canFit, childCount, onGroup, onUngroup, onFit, onMouseDown }: ContainerPanelProps) {
  return (
    <section className="panel-section" aria-labelledby="container-heading">
      <h2 id="container-heading" className="panel-heading">
        Container
      </h2>
      <div className="panel-row panel-row-pairs" role="group" aria-label="Group">
        <button type="button" disabled={!canGroup} onMouseDown={onMouseDown} onClick={onGroup} title={`Group (${platformShortcut('Cmd/Ctrl+G')})`}>
          Group
        </button>
        <button type="button" disabled={!canUngroup} onMouseDown={onMouseDown} onClick={onUngroup} title={`Ungroup (${platformShortcut('Cmd/Ctrl+Shift+G')})`}>
          Ungroup
        </button>
      </div>
      <div className="panel-row panel-row-pairs">
        <button type="button" disabled={!canFit} onMouseDown={onMouseDown} onClick={onFit} title="Resize the container to fit its children">
          Fit to contents
        </button>
      </div>
      {childCount !== undefined && <p className="panel-hint">{childCount === 1 ? '1 child' : `${String(childCount)} children`}</p>}
    </section>
  );
}
