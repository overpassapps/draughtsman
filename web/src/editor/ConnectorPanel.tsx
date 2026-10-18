/**
 * The Connector section of the right properties panel for one selected edge (design/atlas/properties-panel.md, "Edge:
 * Connector (label, kind, from and to shown read-only)"). Kind is the section's control (checklist item 12): switching
 * it writes the edge's `kind` alone, as one undo step, and the arrowheads, stroke and dash follow from the diagram
 * theme's entry for the new kind; nothing is written to `style`. The line beside the picker is drawn from the same
 * resolved style and markers as the canvas, so it shows what the kind looks like in this theme. The label is edited on
 * the canvas (double-click the edge), so here it is shown read-only for now.
 *
 * The arrowhead ends are the trial's addition (build-spec amendment): a start picker, an end picker, a "both ways" one
 * that sets both ends to the selected head, a "swap ends" action and a "reset" back to the kind's default. Each change
 * is one undo step. Options are grouped (Basic, ER, UML) from model/arrowHeads.ts so the list stays usable.
 *
 * The pickers are native selects: Tab reaches them and the arrow keys and type-ahead work as the platform's do. A kind
 * already on the edge that the vocabulary does not name is kept in the list and flagged, never replaced unasked.
 */

import { useId } from 'react';
import { ArrowMarker } from '../canvas/markers.tsx';
import { markerId } from '../canvas/markerIds.ts';
import { DASH_ARRAYS } from '../canvas/shapes.tsx';
import { ARROW_HEAD_GROUPS, ARROW_HEAD_LABELS } from '../model/arrowHeads.ts';
import { edgeKindOptions } from '../model/edgeKinds.ts';
import { typeCapabilities } from '../model/typeCapabilities.ts';
import type { ArrowHead, Document, Edge, Endpoint, RoutingMode } from '../model/types.ts';
import type { DiagramTheme, EdgeKindStyle } from '../theme/diagramTheme.ts';
import { resolveEdgeStyle } from '../theme/resolveStyle.ts';

export interface ConnectorPanelProps {
  edge: Edge;
  document: Document;
  /** The effective diagram theme: the workspace theme with the document's overrides applied. */
  theme: DiagramTheme;
  onKindChange: (kind: string) => void;
  /** Writes the edge's arrowhead ends; `undefined` for an end removes that override (reset to the kind default). */
  onEndsChange: (arrowStart: ArrowHead | undefined, arrowEnd: ArrowHead | undefined) => void;
  onRoutingModeChange: (mode: RoutingMode) => void;
  onResetRoute: () => void;
}


const ROUTING_MODES: readonly { mode: RoutingMode; label: string }[] = [
  { mode: 'orthogonal', label: 'Orthogonal' },
  { mode: 'straight', label: 'Straight' },
  { mode: 'curved', label: 'Curved' },
];

function endpointText(document: Document, end: Endpoint): string {
  const node = document.nodes.find((candidate) => candidate.id === end.node);
  const name = node === undefined || node.label.length === 0 ? end.node : node.label;
  return end.port === undefined ? name : `${name} (${end.port})`;
}

/** A short line drawn with the edge's resolved stroke, dash and arrowheads. */
function KindPreview({ style, prefix }: { style: EdgeKindStyle; prefix: string }) {
  const start = markerId(prefix, style.arrowStart, style.stroke);
  const end = markerId(prefix, style.arrowEnd, style.stroke);
  const heads = [style.arrowStart, style.arrowEnd].filter((arrow) => arrow !== 'none');
  return (
    <svg className="panel-kind-preview" viewBox="0 0 56 16" width="56" height="16" aria-hidden="true">
      <defs>
        {[...new Set(heads)].map((arrow) => (
          <ArrowMarker key={arrow} prefix={prefix} arrow={arrow} colour={style.stroke} />
        ))}
      </defs>
      <line
        x1={6}
        y1={8}
        x2={50}
        y2={8}
        stroke={style.stroke}
        strokeWidth={style.strokeWidth}
        strokeDasharray={DASH_ARRAYS[style.dash]}
        markerStart={start === undefined ? undefined : `url(#${start})`}
        markerEnd={end === undefined ? undefined : `url(#${end})`}
      />
    </svg>
  );
}

/** The option groups of a head picker, rendered as <optgroup> elements. */
function HeadPicker({ id, value, onPick, label }: { id: string; value: ArrowHead; onPick: (head: ArrowHead) => void; label: string }) {
  return (
    <select id={id} value={value} aria-label={label} onChange={(e) => { onPick(e.target.value as ArrowHead); }}>
      {ARROW_HEAD_GROUPS.map((group) => (
        <optgroup key={group.label} label={group.label}>
          {group.heads.map((head) => (
            <option key={head} value={head}>
              {ARROW_HEAD_LABELS[head]}
            </option>
          ))}
        </optgroup>
      ))}
    </select>
  );
}

export function ConnectorPanel({ edge, document, theme, onKindChange, onEndsChange, onRoutingModeChange, onResetRoute }: ConnectorPanelProps) {
  const id = useId();
  const themed = Object.keys(theme.edges).filter((kind) => kind !== 'default');
  const options = edgeKindOptions(document.type, themed, edge.kind);
  const unknown = options.some((option) => option.unknown);
  const resolved = resolveEdgeStyle(theme, edge);
  const start = resolved.arrowStart;
  const end = resolved.arrowEnd;
  const mode = edge.routing?.mode ?? 'orthogonal';
  // A sequence message is always the derived straight line or bracket, so its routing is not a choice (typeCapabilities.ts).
  const derived = typeCapabilities(document.type).derivedGeometry;
  const pinned = (edge.routing?.waypoints?.length ?? 0) > 0;
  // useId's colons are legal in an id but not in the url(#...) a marker is referenced by.
  const prefix = `kind-preview-${id.replace(/[^A-Za-z0-9_-]/g, '')}-`;
  return (
    <section className="panel-section" aria-labelledby={`${id}-heading`}>
      <h2 id={`${id}-heading`} className="panel-heading">
        Connector
      </h2>
      <dl className="panel-fields">
        <dt>Label</dt>
        <dd>{edge.label !== undefined && edge.label.length > 0 ? edge.label : <span className="panel-muted">None</span>}</dd>
        <dt>
          <label htmlFor={`${id}-kind`}>Kind</label>
        </dt>
        <dd className="panel-kind">
          <select
            id={`${id}-kind`}
            value={edge.kind}
            aria-describedby={unknown ? `${id}-unknown` : undefined}
            onChange={(e) => {
              onKindChange(e.target.value);
            }}
          >
            {options.map((option) => (
              <option key={option.kind} value={option.kind}>
                {option.unknown ? `${option.kind} (not in vocabulary)` : option.kind}
              </option>
            ))}
          </select>
          <KindPreview style={resolved} prefix={prefix} />
        </dd>
        <dt>
          <label htmlFor={`${id}-start`}>Start</label>
        </dt>
        <dd className="panel-end">
          <HeadPicker id={`${id}-start`} value={start} label="Arrowhead at the start end" onPick={(head) => { onEndsChange(head, edge.style?.arrowEnd); }} />
        </dd>
        <dt>
          <label htmlFor={`${id}-end`}>End</label>
        </dt>
        <dd className="panel-end">
          <HeadPicker id={`${id}-end`} value={end} label="Arrowhead at the end" onPick={(head) => { onEndsChange(edge.style?.arrowStart, head); }} />
        </dd>
        <dt>
          <label htmlFor={`${id}-both`}>Both ways</label>
        </dt>
        <dd className="panel-end">
          <HeadPicker id={`${id}-both`} value={end} label="Set both ends to the same head" onPick={(head) => { onEndsChange(head, head); }} />
        </dd>
        <dt>Ends</dt>
        <dd className="panel-end-actions">
          <button type="button" onClick={() => { onEndsChange(end, start); }}>
            Swap ends
          </button>
          <button type="button" onClick={() => { onEndsChange(undefined, undefined); }}>
            Reset to default
          </button>
        </dd>
        {!derived && (
        <>
        <dt>
          <label htmlFor={`${id}-routing`}>Routing</label>
        </dt>
        <dd className="panel-routing">
          <select
            id={`${id}-routing`}
            value={mode}
            onChange={(e) => {
              onRoutingModeChange(e.target.value as RoutingMode);
            }}
          >
            {ROUTING_MODES.map((option) => (
              <option key={option.mode} value={option.mode}>
                {option.label}
              </option>
            ))}
          </select>
          {pinned && (
            <button type="button" onClick={onResetRoute}>
              Reset route
            </button>
          )}
        </dd>
        </>
        )}
        <dt>From</dt>
        <dd>{endpointText(document, edge.from)}</dd>
        <dt>To</dt>
        <dd>{endpointText(document, edge.to)}</dd>
      </dl>
      {unknown && (
        <p id={`${id}-unknown`} className="panel-hint">
          {document.type === 'freeform' ? 'This diagram theme does not style this kind.' : `This kind is not in the ${document.type} vocabulary.`} It is kept until
          you pick another.
        </p>
      )}
    </section>
  );
}