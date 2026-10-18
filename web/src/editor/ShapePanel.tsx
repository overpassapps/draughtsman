/**
 * The shape panel (design/atlas/shape-connector-palette.md): docks beside the tool rail, opened by the Shape tool (S)
 * or Cmd/Ctrl+Shift+S, closed by Escape or the tool switching (wired in DocumentViewer, which owns the open state).
 * A sequence document gets one group, Sequence (participant and actor), and no Shapes group. Two collapsible groups: the current document type's kind vocabulary (nodeKinds.ts; empty for freeform, extended by
 * FLOWCHART_SHAPES for a flowchart) and Shapes, the presentation shapes for freeform or for a plain box no kind fits
 * (SHAPE_OPTIONS). An architecture document also gets an Icons group below the kinds, closed by default, holding the
 * generic-technology icon library (model/icons.ts, ICON_OPTIONS); a flowchart gets a "More flowchart shapes" group in
 * the same place, also closed by default (MORE_FLOWCHART_SHAPES). Flowchart and freeform documents also get a BPMN group,
 * closed by default, whose glyphs are drawn at each template's own size (model/bpmnShapes.ts, BPMN_OPTIONS). Every document type but sequence gets a Basic shapes group after Shapes, closed by default (model/basicShapes.ts: ellipse, triangle, note, brace, cube and the rest), each placed on a kind the type's vocabulary knows. Search filters every group by name, including the
 * icon search text, ignoring collapsed state so a match is never hidden by it.
 *
 * A glyph is drawn with the same renderShape and resolved style the canvas uses, so it shows what placing it looks
 * like in the current theme rather than a generic icon. Clicking one calls onPlace; pressing and dragging one calls
 * onDragStart with the template and the pointer that started the gesture, and DocumentViewer tracks that pointer
 * itself (native HTML5 drag-and-drop was dropped: its dragstart never reliably fires for a synthetic or
 * fast/short press-drag, which is exactly the M2 final-check's "4 of 5 drops silently place nothing" bug). Both go
 * through the same NodeTemplate, so click and drag place the same node. Names show on hover and to screen readers
 * (title/aria-label) and, since QA-05, as a small visible caption under the drawing (design/atlas/shape-connector-palette.md).
 *
 * Keyboard (atlas, Keyboard and accessibility): the search field has focus when the panel opens and Down leaves it for
 * the first glyph; arrow keys move through a group's grid, Tab between groups; Enter or Space places the focused
 * glyph; Escape closes the panel (DocumentViewer then hands focus back to the canvas).
 *
 * Recent placements (the atlas's third section) are not tracked yet: a scope trim noted in AGENTS.md.
 */

import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { arrowFocusHandler } from './focusNavigation.ts';
import { BASIC_SHAPES, basicTemplate } from '../model/basicShapes.ts';
import { BPMN_OPTIONS } from '../model/bpmnShapes.ts';
import type { NodeTemplate } from '../model/create.ts';
import { ICON_OPTIONS } from '../model/icons.ts';
import { FLOWCHART_SHAPES, GENERIC_SHAPE_KIND, MORE_FLOWCHART_SHAPES, nodeKindOptions, SHAPE_OPTIONS, SWIMLANE_OPTIONS } from '../model/nodeKinds.ts';
import { ORG_OPTIONS } from '../model/orgShapes.ts';
import { STATE_SHAPE_OPTIONS } from '../model/stateShapes.ts';
import { UML_OPTIONS } from '../model/umlShapes.ts';
import { ER_OPTIONS } from '../model/erShapes.ts';
import type { DocumentType } from '../model/types.ts';
import { renderShape } from '../canvas/shapes.tsx';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';

/** A sequence command the panel offers as a labelled button: it acts on the selection, so it is not a placeable glyph. */
export interface PanelCommand {
  id: string;
  label: string;
  /** What to select first, shown under the label and as its tooltip. */
  hint: string;
  enabled: boolean;
  run: () => void;
}

export interface ShapePanelProps {
  /** A sequence document's fragment, note and operand commands, listed under the participants (design/atlas/sequence-editor.md). */
  commands?: readonly PanelCommand[] | undefined;
  documentType: DocumentType;
  /** The effective diagram theme (the document's token overrides already applied), for glyph previews. */
  theme: DiagramTheme;
  /** Places a new node of this template at the viewport centre, stacked, and selects it. */
  onPlace: (template: NodeTemplate) => void;
  /** A glyph's pointerdown: DocumentViewer tracks this pointer itself and places the template if it releases over the canvas. */
  onDragStart: (template: NodeTemplate, pointerId: number) => void;
  /**
   * The Image glyph's placement: upload a picture and create the node, rather than placing an empty shape. The picker
   * opens here because the picture, not a template, decides the node's size.
   */
  onPlaceImage: () => void;
  onClose: () => void;
}

const GLYPH_W = 48;
const GLYPH_H = 32;

/** Glyphs per row: `.shape-panel-grid`'s grid-template-columns in app.css, which the arrow keys must agree with. */
export const SHAPE_GRID_COLUMNS = 3;

const gridKeys = arrowFocusHandler({ kind: 'grid', columns: SHAPE_GRID_COLUMNS }, '.shape-panel-glyph');

interface Entry {
  key: string;
  template: NodeTemplate;
  searchText: string;
  /** The glyph's hover and screen-reader name; the template's own label when omitted (org boxes place a two-line label). */
  title?: string;
}

function kindEntries(documentType: DocumentType): Entry[] {
  const entries = nodeKindOptions(documentType).filter((option) => documentType !== 'flowchart' || option.kind !== 'subprocess').map((option) => ({
    key: `kind-${option.kind}`,
    template: { kind: option.kind, shape: option.defaultShape, label: option.label },
    searchText: `${option.label} ${option.kind}`.toLowerCase(),
  }));
  if (documentType === 'flowchart') {
    entries.push(...FLOWCHART_SHAPES.map((template) => ({ key: `flowchart-${template.shape}`, template, searchText: `${template.label} ${template.shape}`.toLowerCase() })));
  }
  return entries;
}

function shapeEntries(): Entry[] {
  return SHAPE_OPTIONS.map((option) => ({
    key: `shape-${option.shape}`,
    template: { kind: GENERIC_SHAPE_KIND, shape: option.shape, label: option.label },
    searchText: `${option.label} ${option.shape}`.toLowerCase(),
  }));
}

/** The general-purpose basic shapes (model/basicShapes.ts), placed on a kind the document type's vocabulary knows. */
function basicEntries(documentType: DocumentType): Entry[] {
  return BASIC_SHAPES.map((shape) => ({
    key: `basic-${shape.name}`,
    template: basicTemplate(shape, documentType),
    searchText: `${shape.label} ${shape.name} ${(shape.keywords ?? []).join(' ')}`.toLowerCase(),
  }));
}

function moreFlowchartEntries(): Entry[] {
  return MORE_FLOWCHART_SHAPES.map((template) => ({
    key: `more-flowchart-${template.shape}`,
    template,
    searchText: `${template.label} ${template.shape}`.toLowerCase(),
  }));
}

/** The UML state-machine shapes (model/stateShapes.ts), offered for flowchart and freeform documents. */
function stateEntries(): Entry[] {
  return STATE_SHAPE_OPTIONS.map((template) => ({
    key: `state-${template.shape}`,
    template,
    searchText: `${template.label} ${template.shape}`.toLowerCase(),
  }));
}

/** The swimlane containers (model/swimlanes.ts), offered for flowchart and freeform documents. */
function swimlaneEntries(): Entry[] {
  return SWIMLANE_OPTIONS.map((template) => ({
    key: `swimlane-${template.shape}`,
    template,
    searchText: `${template.label} ${template.shape} swimlane lane pool`.toLowerCase(),
  }));
}

/** The BPMN shapes (model/bpmnShapes.ts), offered for flowchart and freeform documents beside the swimlane pool and lane. */
function bpmnEntries(): Entry[] {
  return BPMN_OPTIONS.map((template) => ({
    key: `bpmn-${template.shape}`,
    template,
    searchText: `${template.label} ${template.shape}`.toLowerCase(),
  }));
}

function iconEntries(): Entry[] {
  return ICON_OPTIONS.map((option) => ({
    key: `icon-${option.name}`,
    template: { kind: option.kind, shape: option.shape, label: option.label, ...(option.size === undefined ? {} : { size: option.size }) },
    searchText: `${option.label} ${option.name} ${option.shape}`.toLowerCase(),
  }));
}

/** The org-chart boxes (model/orgShapes.ts), offered for architecture and freeform documents. */
function orgEntries(): Entry[] {
  return ORG_OPTIONS.map((option) => ({
    key: `org-${option.name}`,
    // The node lands with the two-line label (name and title); the glyph's own name stays the box's short label.
    template: { kind: option.kind, shape: option.shape, label: option.defaultLabel },
    searchText: `${option.label} ${option.name} ${option.shape}`.toLowerCase(),
    title: option.label,
  }));
}

/** The UML class shapes (model/umlShapes.ts), offered for architecture and freeform documents. */
function umlEntries(): Entry[] {
  return UML_OPTIONS.map((option) => ({
    key: `uml-${option.name}`,
    // The node lands sized and populated as the shape needs (a class with a starter attribute and operation).
    template: option.template,
    searchText: `${option.label} ${option.name} ${option.shape} uml class diagram`.toLowerCase(),
    title: option.label,
  }));
}

/** The ER entity shapes (model/erShapes.ts), offered for architecture and freeform documents. */
function erEntries(): Entry[] {
  return ER_OPTIONS.map((option) => ({
    key: `er-${option.name}`,
    // The node lands sized and populated as the shape needs (a table with a few starter columns).
    template: option.template,
    searchText: `${option.label} ${option.name} ${option.shape} er entity table database column`.toLowerCase(),
    title: option.label,
  }));
}

function Glyph({ entry, theme, onPlace, onDragStart, onPlaceImage }: { entry: Entry; theme: DiagramTheme; onPlace: (template: NodeTemplate) => void; onDragStart: (template: NodeTemplate, pointerId: number) => void; onPlaceImage: () => void }) {
  const style = nodeKindDefault(theme, entry.template.kind === GENERIC_SHAPE_KIND ? 'default' : entry.template.kind);
  const isImage = entry.template.shape === 'image';
  const name = entry.title ?? entry.template.label;
  // A template with a size of its own (a BPMN event is a circle, a task a wider box) is drawn at that size and scaled
  // to fit the glyph, so the panel shows the shape as it will land rather than stretched into the 48 x 32 cell.
  const drawW = entry.template.size?.w ?? GLYPH_W;
  const drawH = entry.template.size?.h ?? GLYPH_H;
  const { element } = renderShape(entry.template.shape, drawW, drawH, style);
  const onPointerDown = (e: PointerEvent<HTMLButtonElement>): void => {
    if (e.pointerType === 'mouse' && e.button !== 0) {
      return;
    }
    // The Image glyph arms no template: its click opens the file picker, because the picture decides the node's size.
    if (isImage) {
      return;
    }
    onDragStart(entry.template, e.pointerId);
  };
  return (
    <button
      type="button"
      className="shape-panel-glyph"
      title={name}
      aria-label={name}
      onPointerDown={onPointerDown}
      onClick={() => {
        if (isImage) {
          onPlaceImage();
        } else {
          onPlace(entry.template);
        }
      }}
    >
      <svg viewBox={`0 0 ${String(drawW)} ${String(drawH)}`} width={GLYPH_W} height={GLYPH_H} aria-hidden="true">
        {element}
      </svg>
      <span className="shape-panel-glyph-name" aria-hidden="true">{name}</span>
    </button>
  );
}

function Group({ id, name, entries, theme, onPlace, onDragStart, onPlaceImage, forceOpen, defaultOpen = true }: { id: string; name: string; entries: Entry[]; theme: DiagramTheme; onPlace: (template: NodeTemplate) => void; onDragStart: (template: NodeTemplate, pointerId: number) => void; onPlaceImage: () => void; forceOpen: boolean; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const expanded = forceOpen || open;
  if (entries.length === 0) {
    return null;
  }
  return (
    <div className="shape-panel-group">
      <button
        type="button"
        className="shape-panel-group-header"
        aria-expanded={expanded}
        aria-controls={id}
        onClick={() => {
          setOpen((current) => !current);
        }}
      >
        {name}
      </button>
      {expanded && (
        <div id={id} className="shape-panel-grid" role="group" aria-label={name} onKeyDown={gridKeys}>
          {entries.map((entry) => (
            <Glyph key={entry.key} entry={entry} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} />
          ))}
        </div>
      )}
    </div>
  );
}

function commandsGroup(id: string, commands: readonly PanelCommand[]) {
  if (commands.length === 0) {
    return null;
  }
  return (
    <div className="shape-panel-group">
      <div className="shape-panel-group-header">
        Fragments and notes
      </div>
      <div id={id} className="shape-panel-commands" role="group" aria-label="Fragments and notes">
        {commands.map((command) => (
          <button key={command.id} type="button" className="shape-panel-command" disabled={!command.enabled} title={command.hint} onClick={command.run}>
            {command.label}
            <span className="shape-panel-command-hint">{command.hint}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

export function ShapePanel({ commands = [], documentType, theme, onPlace, onDragStart, onPlaceImage, onClose }: ShapePanelProps) {
  const id = useId();
  const [query, setQuery] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);
  // Focus goes to search whenever the panel mounts, matching "S opens the panel and focuses search"; a click to
  // open it lands here too, which is harmless (Cmd+K palettes do the same regardless of how they were opened).
  useEffect(() => {
    searchRef.current?.focus();
  }, []);
  const kinds = useMemo(() => kindEntries(documentType), [documentType]);
  // A sequence diagram has only its two kinds: a free presentation shape has nowhere to be drawn (design/atlas/sequence-editor.md).
  const shapes = useMemo(() => (documentType === 'sequence' ? [] : shapeEntries()), [documentType]);
  const basic = useMemo(() => (documentType === 'sequence' ? [] : basicEntries(documentType)), [documentType]);
  const moreFlowchart = useMemo(() => (documentType === 'flowchart' ? moreFlowchartEntries() : []), [documentType]);
  const states = useMemo(() => (documentType === 'flowchart' || documentType === 'freeform' ? stateEntries() : []), [documentType]);
  const swimlanes = useMemo(() => (documentType === 'flowchart' || documentType === 'freeform' ? swimlaneEntries() : []), [documentType]);
  const bpmn = useMemo(() => (documentType === 'flowchart' || documentType === 'freeform' ? bpmnEntries() : []), [documentType]);
  const icons = useMemo(() => (documentType === 'architecture' ? iconEntries() : []), [documentType]);
  const org = useMemo(() => (documentType === 'architecture' || documentType === 'freeform' ? orgEntries() : []), [documentType]);
  const uml = useMemo(() => (documentType === 'architecture' || documentType === 'freeform' ? umlEntries() : []), [documentType]);
  const er = useMemo(() => (documentType === 'architecture' || documentType === 'freeform' ? erEntries() : []), [documentType]);
  const q = query.trim().toLowerCase();
  const matches = (entry: Entry): boolean => q.length === 0 || entry.searchText.includes(q);
  const filteredKinds = kinds.filter(matches);
  const filteredShapes = shapes.filter(matches);
  const filteredBasic = basic.filter(matches);
  const filteredMoreFlowchart = moreFlowchart.filter(matches);
  const filteredStates = states.filter(matches);
  const filteredSwimlanes = swimlanes.filter(matches);
  const filteredBpmn = bpmn.filter(matches);
  const filteredIcons = icons.filter(matches);
  const filteredOrg = org.filter(matches);
  const filteredUml = uml.filter(matches);
  const filteredEr = er.filter(matches);
  const searching = q.length > 0;

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      onClose();
    }
  };
  const panel = useRef<HTMLDivElement>(null);
  const onSearchKeyDown = (e: KeyboardEvent<HTMLInputElement>): void => {
    if (e.key === 'ArrowDown' && !e.metaKey && !e.ctrlKey && !e.altKey && !e.shiftKey) {
      const first = panel.current?.querySelector<HTMLElement>('.shape-panel-glyph');
      if (first) {
        e.preventDefault();
        first.focus();
      }
    }
  };

  return (
    <div ref={panel} className="shape-panel" role="region" aria-label="Shapes" tabIndex={-1} data-region="shapes" onKeyDown={onKeyDown}>
      <div className="shape-panel-search">
        <input
          ref={searchRef}
          type="search"
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
          }}
          placeholder="Search shapes"
          aria-label="Search shapes"
          onKeyDown={onSearchKeyDown}
        />
      </div>
      <div className="shape-panel-scroll">
        <Group id={`${id}-kinds`} name={documentType === 'freeform' ? 'Kinds' : documentType === 'sequence' ? 'Sequence' : `${documentType[0]?.toUpperCase() ?? ''}${documentType.slice(1)} kinds`} entries={filteredKinds} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} />
        {documentType === 'sequence' && commandsGroup(`${id}-commands`, commands.filter((command) => q.length === 0 || `${command.label} ${command.hint}`.toLowerCase().includes(q)))}
        {documentType === 'flowchart' && <Group id={`${id}-more-flowchart`} name="More flowchart shapes" entries={filteredMoreFlowchart} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {(documentType === 'flowchart' || documentType === 'freeform') && <Group id={`${id}-state`} name="State" entries={filteredStates} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {(documentType === 'flowchart' || documentType === 'freeform') && <Group id={`${id}-swimlane`} name="Swimlane" entries={filteredSwimlanes} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {(documentType === 'flowchart' || documentType === 'freeform') && <Group id={`${id}-bpmn`} name="BPMN" entries={filteredBpmn} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {documentType === 'architecture' && <Group id={`${id}-icons`} name="Icons" entries={filteredIcons} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        <Group id={`${id}-org`} name="Org chart" entries={filteredOrg} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />
        {(documentType === 'architecture' || documentType === 'freeform') && <Group id={`${id}-uml`} name="UML class" entries={filteredUml} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {(documentType === 'architecture' || documentType === 'freeform') && <Group id={`${id}-er`} name="ER entity" entries={filteredEr} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        <Group id={`${id}-shapes`} name="Shapes" entries={filteredShapes} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} />
        {documentType !== 'sequence' && <Group id={`${id}-basic`} name="Basic shapes" entries={filteredBasic} theme={theme} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} forceOpen={searching} defaultOpen={false} />}
        {searching && filteredKinds.length === 0 && filteredShapes.length === 0 && filteredMoreFlowchart.length === 0 && filteredStates.length === 0 && filteredSwimlanes.length === 0 && filteredBpmn.length === 0 && filteredIcons.length === 0 && filteredOrg.length === 0 && filteredUml.length === 0 && filteredEr.length === 0 && <p className="panel-hint">No shapes match “{query}”.</p>}
      </div>
    </div>
  );
}
