/**
 * The Style section of the right properties panel: fill, stroke, stroke width and dash for a node selection (plus
 * font, font size and bold for shapes), and stroke, stroke width and dash for an edge selection (arrowheads are
 * owned by the Connector section, another lane). Every control writes its one property across the whole selection as
 * one undo step, and a "reset" affordance on each property clears the override so it inherits the diagram theme.
 *
 * A colour is chosen from a swatch strip built from the active theme's palette, or typed as a hex value; there is no
 * colour-picker dependency. On a multi-selection a property whose elements disagree shows a "Mixed" state and the
 * first interaction replaces it with the chosen value; a shared value edits in place. Values are read from the
 * resolved element style (the theme default plus the element's sparse override), so the control always shows what the
 * canvas draws.
 */

import { useId, useState, type CSSProperties, type MouseEvent } from 'react';
import type { Edge, Node } from '../model/types.ts';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { LABEL_FONT_SIZE } from '../canvas/text.ts';
import { resolveEdgeStyle, resolveNodeStyle } from '../theme/resolveStyle.ts';
import { DASH_OPTIONS, MIN_FONT_SIZE, sharedValue, swatchName, type StylableField } from './styleState.ts';

interface StylePanelProps {
  /** Selected elements this panel styles; all nodes, or all edges, never a mix. */
  target: 'node' | 'edge';
  nodes: Node[];
  edges: Edge[];
  /** The effective diagram theme (the document's token overrides already applied). */
  theme: DiagramTheme;
  onStyle: (field: StylableField, value: string | number | boolean | undefined, target: 'node' | 'edge') => void;
  onMouseDown: (e: MouseEvent<HTMLElement>) => void;
}

/** The fields the panel shows for the current selection. */
function visibleFields(target: 'node' | 'edge'): StylableField[] {
  return target === 'node' ? ['fill', 'stroke', 'strokeWidth', 'dash', 'font', 'fontSize', 'bold'] : ['stroke', 'strokeWidth', 'dash'];
}

/** The resolved value of one field across every selected element, from the element's own override only. */
function resolvedFieldValues(target: 'node' | 'edge', nodes: Node[], edges: Edge[], theme: DiagramTheme, field: StylableField): unknown[] {
  if (target === 'node') {
    return nodes.map((node) => {
      const base = resolveNodeStyle(theme, node);
      switch (field) {
        case 'fill':
          return node.style?.fill ?? base.fill;
        case 'stroke':
          return node.style?.stroke ?? base.stroke;
        case 'strokeWidth':
          return node.style?.strokeWidth ?? base.strokeWidth;
        case 'dash':
          return node.style?.dash ?? base.dash;
        case 'font':
          return node.style?.font ?? base.font;
        case 'fontSize':
          return node.style?.fontSize ?? base.fontSize ?? LABEL_FONT_SIZE;
        case 'bold':
          return node.style?.bold ?? false;
      }
    });
  }
  return edges.map((edge) => {
    switch (field) {
      case 'stroke':
        return edge.style?.stroke ?? resolveEdgeStyle(theme, edge).stroke;
      case 'strokeWidth':
        return edge.style?.strokeWidth ?? resolveEdgeStyle(theme, edge).strokeWidth;
      case 'dash':
        return edge.style?.dash ?? resolveEdgeStyle(theme, edge).dash;
      default:
        return undefined;
    }
  });
}

/** The colour swatches: the theme palette's entries in declaration order, named as a person would. */
function swatches(theme: DiagramTheme): { name: string; value: string }[] {
  return Object.entries(theme.palette).map(([name, value]) => ({ name: swatchName(name), value }));
}

/** A text draft that resets after each commit, so a control never re-syncs to the model through an effect. */
function useDraft(): readonly [string | undefined, (next: string | undefined) => void] {
  const [draft, setDraft] = useState<string | undefined>(undefined);
  return [draft, setDraft];
}

function ColourField({ label, value, mixed, swatchList, onValue, onReset, onMouseDown, id }: {
  label: string;
  value: string | undefined;
  mixed: boolean;
  swatchList: { name: string; value: string }[];
  onValue: (value: string | undefined) => void;
  onReset: () => void;
  onMouseDown: (e: MouseEvent<HTMLElement>) => void;
  id: string;
}) {
  const [draft, setDraft] = useDraft();
  const shown = draft ?? (mixed ? '' : value ?? '');
  const apply = (next: string): void => {
    const trimmed = next.trim();
    onValue(trimmed.length === 0 ? undefined : trimmed);
    setDraft(undefined);
  };
  return (
    <div className="panel-style-row panel-style-row-colour">
      <span className="panel-style-label">{label}</span>
      <div className="panel-style-swatches" role="group" aria-label={`${label} colour`}>
        {swatchList.map((swatch) => (
          <button
            key={swatch.name}
            type="button"
            className="panel-style-swatch"
            title={`${swatch.name} (${swatch.value.toLowerCase()})`}
            aria-label={`${label} ${swatch.name}`}
            aria-pressed={!mixed && value?.toLowerCase() === swatch.value.toLowerCase()}
            style={{ '--swatch': swatch.value } as CSSProperties}
            onMouseDown={onMouseDown}
            onClick={() => {
              onValue(swatch.value);
            }}
          />
        ))}
      </div>
      <div className="panel-style-input">
        <span className="panel-style-colour-name">{mixed ? 'Mixed' : (swatchList.find((swatch) => swatch.value.toLowerCase() === value?.toLowerCase())?.name ?? (value === undefined ? '' : 'Custom'))}</span>
        <input
          id={`${id}-hex`}
          type="text"
          value={shown}
          placeholder={mixed ? 'Mixed' : value}
          spellCheck={false}
          aria-label={`${label} hex value`}
          onBlur={(e) => {
            apply(e.target.value);
          }}
          onChange={(e) => {
            setDraft(e.target.value);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              apply(e.currentTarget.value);
              e.currentTarget.blur();
            }
          }}
        />
      </div>
      <button type="button" className="panel-style-reset" title={`Reset ${label.toLowerCase()} to theme default`} aria-label={`Reset ${label.toLowerCase()}`} onMouseDown={onMouseDown} onClick={onReset}>
        ×
      </button>
    </div>
  );
}

function NumberField({ label, value, mixed, onValue, onReset, onMouseDown, id, min, step }: {
  label: string;
  value: number | undefined;
  mixed: boolean;
  onValue: (value: number | undefined) => void;
  onReset: () => void;
  onMouseDown: (e: MouseEvent<HTMLElement>) => void;
  id: string;
  min?: number;
  step?: number;
}) {
  const [draft, setDraft] = useDraft();
  const shown = draft ?? (mixed ? '' : value === undefined ? '' : String(value));
  const apply = (next: string): void => {
    const parsed = Number(next);
    onValue(next.trim().length === 0 ? undefined : Number.isFinite(parsed) ? parsed : value);
    setDraft(undefined);
  };
  return (
    <div className="panel-style-row">
      <span className="panel-style-label">{label}</span>
      <div className="panel-style-input panel-style-input-number">
        <input
          id={`${id}-num`}
          type="number"
          value={shown}
          min={min}
          step={step}
          placeholder={mixed ? 'Mixed' : value === undefined ? '' : String(value)}
          aria-label={label}
          onBlur={(e) => {
            apply(e.target.value);
          }}
          onChange={(e) => {
            setDraft(e.target.value);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              apply(e.currentTarget.value);
              e.currentTarget.blur();
            }
          }}
        />
      </div>
      <button type="button" className="panel-style-reset" title={`Reset ${label.toLowerCase()} to theme default`} aria-label={`Reset ${label.toLowerCase()}`} onMouseDown={onMouseDown} onClick={onReset}>
        ×
      </button>
    </div>
  );
}

function FontField({ label, value, mixed, onValue, onReset, onMouseDown, id }: {
  label: string;
  value: string | undefined;
  mixed: boolean;
  onValue: (value: string | undefined) => void;
  onReset: () => void;
  onMouseDown: (e: MouseEvent<HTMLElement>) => void;
  id: string;
}) {
  const [draft, setDraft] = useDraft();
  const shown = draft ?? (mixed ? '' : value ?? '');
  return (
    <div className="panel-style-row">
      <span className="panel-style-label">{label}</span>
      <div className="panel-style-input">
        <input
          id={id}
          type="text"
          value={shown}
          placeholder={mixed ? 'Mixed' : undefined}
          aria-label={label}
          spellCheck={false}
          onChange={(e) => {
            setDraft(e.target.value);
          }}
          onBlur={(e) => {
            const trimmed = e.target.value.trim();
            onValue(trimmed.length === 0 ? undefined : trimmed);
            setDraft(undefined);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              const trimmed = e.currentTarget.value.trim();
              onValue(trimmed.length === 0 ? undefined : trimmed);
              e.currentTarget.blur();
            }
          }}
        />
      </div>
      <button
        type="button"
        className="panel-style-reset"
        title={`Reset ${label.toLowerCase()} to theme default`}
        aria-label={`Reset ${label.toLowerCase()}`}
        onMouseDown={onMouseDown}
        onClick={onReset}
      >
        ×
      </button>
    </div>
  );
}

export function StylePanel({ target, nodes, edges, theme, onStyle, onMouseDown }: StylePanelProps) {
  const id = useId();
  if (target === 'node' ? nodes.length === 0 : edges.length === 0) {
    return null;
  }

  const fieldDefs: Record<StylableField, { label: string; kind: 'colour' | 'number' | 'dash' | 'font' | 'bold' }> = {
    fill: { label: 'Fill', kind: 'colour' },
    stroke: { label: 'Stroke', kind: 'colour' },
    strokeWidth: { label: 'Stroke width', kind: 'number' },
    dash: { label: 'Dash', kind: 'dash' },
    font: { label: 'Font', kind: 'font' },
    fontSize: { label: 'Font size', kind: 'number' },
    bold: { label: 'Bold', kind: 'bold' },
  };

  return (
    <section className="panel-section" aria-labelledby={`${id}-heading`}>
      <h2 id={`${id}-heading`} className="panel-heading">
        Style
      </h2>
      {visibleFields(target).map((field) => {
        const values = resolvedFieldValues(target, nodes, edges, theme, field);
        const { value, mixed } = sharedValue(values);
        const def = fieldDefs[field];
        const key = `${target}-${field}`;
        if (def.kind === 'colour') {
          return (
            <ColourField
              key={key}
              id={`${id}-${field}`}
              label={def.label}
              value={value as string | undefined}
              mixed={mixed}
              swatchList={swatches(theme)}
              onValue={(next) => {
                onStyle(field, next, target);
              }}
              onReset={() => {
                onStyle(field, undefined, target);
              }}
              onMouseDown={onMouseDown}
            />
          );
        }
        if (def.kind === 'number') {
          const numeric = typeof value === 'number' ? value : undefined;
          return (
            <NumberField
              key={key}
              id={`${id}-${field}`}
              label={def.label}
              value={numeric}
              mixed={mixed}
              min={field === 'fontSize' ? MIN_FONT_SIZE : 0}
              step={field === 'fontSize' ? 1 : 0.5}
              onValue={(next) => {
                onStyle(field, next, target);
              }}
              onReset={() => {
                onStyle(field, undefined, target);
              }}
              onMouseDown={onMouseDown}
            />
          );
        }
        if (def.kind === 'dash') {
          return (
            <div className="panel-style-row" key={key}>
              <span className="panel-style-label">{def.label}</span>
              <div className="panel-row panel-row-pairs">
                {DASH_OPTIONS.map((option) => (
                  <button
                    key={option}
                    type="button"
                    aria-pressed={!mixed && value === option}
                    onMouseDown={onMouseDown}
                    onClick={() => {
                      onStyle(field, option, target);
                    }}
                  >
                    {option}
                  </button>
                ))}
              </div>
              <button
                type="button"
                className="panel-style-reset"
                title={`Reset ${def.label.toLowerCase()} to theme default`}
                aria-label={`Reset ${def.label.toLowerCase()}`}
                onMouseDown={onMouseDown}
                onClick={() => {
                  onStyle(field, undefined, target);
                }}
              >
                ×
              </button>
            </div>
          );
        }
        if (def.kind === 'font') {
          return (
            <FontField
              key={key}
              id={`${id}-${field}`}
              label={def.label}
              value={value as string | undefined}
              mixed={mixed}
              onValue={(next) => {
                onStyle(field, next, target);
              }}
              onReset={() => {
                onStyle(field, undefined, target);
              }}
              onMouseDown={onMouseDown}
            />
          );
        }
        // Bold: a press-on/press-off toggle where off clears the override (back to "not bold").
        const boldOn = value === true;
        return (
          <div className="panel-style-row" key={key}>
            <span className="panel-style-label">{def.label}</span>
            <button
              type="button"
              className="panel-style-bold"
              aria-pressed={boldOn}
              aria-label="Bold"
              onMouseDown={onMouseDown}
              onClick={() => {
                onStyle(field, boldOn ? undefined : true, target);
              }}
            >
              B
            </button>
            <button
              type="button"
              className="panel-style-reset"
              title={`Reset ${def.label.toLowerCase()} to theme default`}
              aria-label={`Reset ${def.label.toLowerCase()}`}
              onMouseDown={onMouseDown}
              onClick={() => {
                onStyle(field, undefined, target);
              }}
            >
              ×
            </button>
          </div>
        );
      })}
    </section>
  );
}
