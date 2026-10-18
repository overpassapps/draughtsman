/**
 * The Arrange section of the right properties panel (design/atlas/properties-panel.md, "Multiple: Arrange (align six
 * ways, distribute two ways, order)"). It is shown for every selection rather than only a multiple one, because
 * order applies to a single node too; the top bar's Arrange menu (editor/EditorMenu.tsx) lists the same commands.
 * Align needs two nodes and distribute three (align.ts); order needs any selected element. A control that cannot act
 * is disabled, not hidden, so the section keeps its shape. Left and Right move along a row (DocumentViewer).
 *
 * A mouse press on a button hands focus back to the canvas, as the zoom cluster does, so the selection's keyboard
 * shortcuts keep working after a click; Tab still reaches every button, whose accessible name says what it does.
 */

import type { MouseEvent } from 'react';
import { MIN_ALIGN_NODES, MIN_DISTRIBUTE_NODES, type AlignCommand, type DistributeCommand } from '../canvas/align.ts';
import type { ArrangeCommand } from '../model/arrange.ts';
import { platformShortcut } from './platform.ts';

export interface ArrangePanelProps {
  /** Selected ids that name a node: what align and distribute act on. */
  nodeCount: number;
  /** Selected ids that name a node or an edge: what order acts on. */
  elementCount: number;
  onAlign: (command: AlignCommand) => void;
  onDistribute: (command: DistributeCommand) => void;
  onArrange: (command: ArrangeCommand) => void;
  onMouseDown: (e: MouseEvent<HTMLButtonElement>) => void;
  /**
   * A document whose positions are derived from element order (a sequence diagram, model/typeCapabilities.ts): there is
   * nothing to align or distribute, and the order commands read as moving earlier or later (design/atlas/sequence-editor.md).
   */
  derivedGeometry?: boolean;
}

const ALIGN: readonly { command: AlignCommand; text: string }[] = [
  { command: 'left', text: 'Left' },
  { command: 'centre', text: 'Centre' },
  { command: 'right', text: 'Right' },
  { command: 'top', text: 'Top' },
  { command: 'middle', text: 'Middle' },
  { command: 'bottom', text: 'Bottom' },
];

const DISTRIBUTE: readonly { command: DistributeCommand; text: string }[] = [
  { command: 'horizontal', text: 'Horizontally' },
  { command: 'vertical', text: 'Vertically' },
];

const ORDER: readonly { command: ArrangeCommand; text: string; shortcut: string }[] = [
  { command: 'bringToFront', text: 'Bring to front', shortcut: 'Cmd/Ctrl+Alt+]' },
  { command: 'bringForward', text: 'Bring forward', shortcut: 'Cmd/Ctrl+]' },
  { command: 'sendBackward', text: 'Send backward', shortcut: 'Cmd/Ctrl+[' },
  { command: 'sendToBack', text: 'Send to back', shortcut: 'Cmd/Ctrl+Alt+[' },
];

/** The order commands for a document whose order is its meaning: later is further right, or further down. */
const SEQUENCE_ORDER_TEXT: Readonly<Record<ArrangeCommand, string>> = {
  bringToFront: 'Move to end',
  bringForward: 'Move later',
  sendBackward: 'Move earlier',
  sendToBack: 'Move to start',
};

export function ArrangePanel({ nodeCount, elementCount, onAlign, onDistribute, onArrange, onMouseDown, derivedGeometry = false }: ArrangePanelProps) {
  const canAlign = nodeCount >= MIN_ALIGN_NODES;
  const canDistribute = nodeCount >= MIN_DISTRIBUTE_NODES;
  const canOrder = elementCount > 0;
  return (
    <section className="panel-section" aria-labelledby="arrange-heading">
      <h2 id="arrange-heading" className="panel-heading">
        Arrange
      </h2>
      {!derivedGeometry && (
      <div className="panel-row" role="group" aria-label="Align">
        {ALIGN.map(({ command, text }) => (
          <button
            key={command}
            type="button"
            disabled={!canAlign}
            onMouseDown={onMouseDown}
            onClick={() => {
              onAlign(command);
            }}
            aria-label={`Align ${text.toLowerCase()}`}
          >
            {text}
          </button>
        ))}
      </div>
      )}
      {!derivedGeometry && (
      <div className="panel-row panel-row-pairs" role="group" aria-label="Distribute">
        {DISTRIBUTE.map(({ command, text }) => (
          <button
            key={command}
            type="button"
            disabled={!canDistribute}
            onMouseDown={onMouseDown}
            onClick={() => {
              onDistribute(command);
            }}
            aria-label={`Distribute ${text.toLowerCase()}`}
          >
            {text}
          </button>
        ))}
      </div>
      )}
      <div className="panel-row panel-row-pairs" role="group" aria-label="Order">
        {ORDER.map(({ command, text, shortcut }) => (
          <button
            key={command}
            type="button"
            disabled={!canOrder}
            onMouseDown={onMouseDown}
            onClick={() => {
              onArrange(command);
            }}
            title={`${derivedGeometry ? SEQUENCE_ORDER_TEXT[command] : text} (${platformShortcut(shortcut)})`}
          >
            {derivedGeometry ? SEQUENCE_ORDER_TEXT[command] : text}
          </button>
        ))}
      </div>
      {!derivedGeometry && !canDistribute && <p className="panel-hint">Select two or more shapes to align them, three or more to distribute.</p>}
    </section>
  );
}
