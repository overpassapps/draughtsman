/**
 * The Tour view of the editor's right-hand panel (design/atlas/flythrough.md): where the camera path is authored. The
 * canvas's current view is captured as a keyframe ("Add keyframe"), or a first tour is generated from the diagram's
 * containers; each keyframe can be selected (the canvas camera cuts to it, so it can be checked without presenting),
 * reordered, duplicated, deleted, given a caption and told how long to take to arrive, how long to stay and how to ease.
 *
 * Every change goes through `onEdit`, which writes it as one undo step (useDocumentEditor `editFlythrough`), and a field
 * commits on blur or Enter, never per keystroke, so typing a caption is one step. Reordering is by the Move buttons or
 * Alt+ArrowUp/Down on a focused row: no drag library. Times are shown in seconds and stored in milliseconds; an empty
 * field clears the setting back to the player's default, which its placeholder shows.
 */

import { useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import {
  addKeyframe,
  DEFAULT_DURATION_MS,
  DEFAULT_EASING,
  DEFAULT_HOLD_MS,
  duplicateKeyframe,
  durationOf,
  EASING_LABELS,
  EASINGS,
  easingOf,
  holdOf,
  loops,
  MAX_KEYFRAMES,
  moveKeyframe,
  removeKeyframe,
  setLoop,
  tourFromShots,
  updateKeyframe,
  type KeyframePatch,
  type Shot,
} from '../model/flythrough.ts';
import type { Flythrough, Id, Keyframe, KeyframeEasing, ViewRect } from '../model/types.ts';
import { platformShortcut } from './platform.ts';

export interface FlythroughPanelProps {
  flythrough: Flythrough | undefined;
  /** The canvas's current view, for "Add keyframe" and "Use current view"; undefined while the canvas has no size. */
  capture: () => ViewRect | undefined;
  /** Moves the canvas camera to a keyframe's view, for checking it. */
  onShow: (view: ViewRect) => void;
  /** Whether the diagram might have containers to generate from: a cheap check, true on every edit-worthy frame. */
  canGenerate: boolean;
  /** The shots "Generate from containers" makes, worked out when the button is pressed; empty when there are none. */
  generate: () => readonly Shot[];
  /** Writes an edit of the tour as one undo step; answers whether anything changed. */
  onEdit: (edit: (current: Flythrough | undefined) => Flythrough | undefined) => boolean;
  /** Opens Present, on the keyframe at this index. */
  onPresent: (index: number) => void;
}

const SECONDS = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 3 });

function seconds(ms: number): string {
  return SECONDS.format(ms / 1000);
}

function label(keyframe: Keyframe, position: number): string {
  const caption = keyframe.caption?.trim();
  return caption === undefined || caption === '' ? `Keyframe ${String(position + 1)}` : caption;
}

/** A field's text in seconds as milliseconds: null for an empty field (the default), undefined for text that is not a time. */
function parseSeconds(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === '') {
    return null;
  }
  // A decimal comma is as much a time as a point.
  const value = Number(trimmed.replace(',', '.'));
  return Number.isFinite(value) && value >= 0 ? value * 1000 : undefined;
}

export function FlythroughPanel({ flythrough, capture, onShow, canGenerate, generate, onEdit, onPresent }: FlythroughPanelProps) {
  const [selectedId, setSelectedId] = useState<Id | undefined>(undefined);
  const [notice, setNotice] = useState('');
  const keyframes = flythrough?.keyframes ?? [];
  const selectedIndex = keyframes.findIndex((keyframe) => keyframe.id === selectedId);
  const full = keyframes.length >= MAX_KEYFRAMES;

  const add = (): void => {
    const view = capture();
    if (view === undefined) {
      setNotice('The canvas has no size yet, so there is no view to capture.');
      return;
    }
    // After the selected keyframe when there is one, else at the end.
    const at = selectedIndex >= 0 ? selectedIndex + 1 : keyframes.length;
    let addedId: Id | undefined;
    onEdit((current) => {
      const added = addKeyframe(current, view, at);
      addedId = added?.id;
      return added?.flythrough ?? current;
    });
    if (addedId !== undefined) {
      setSelectedId(addedId);
      setNotice(`Added keyframe ${String(at + 1)} from the current view.`);
    }
  };

  const generateTour = (): void => {
    const shots = generate();
    if (shots.length === 0) {
      setNotice('Nothing to generate: no container has a position yet.');
      return;
    }
    onEdit((current) => tourFromShots(shots, current) ?? current);
    setSelectedId(undefined);
    setNotice(`${keyframes.length > 0 ? 'Replaced the tour with' : 'Generated'} ${String(shots.length)} keyframes. Undo brings back what was there.`);
  };

  const select = (keyframe: Keyframe): void => {
    setSelectedId(keyframe.id);
    onShow(keyframe.view);
  };

  const move = (keyframe: Keyframe, to: number): void => {
    onEdit((current) => (current === undefined ? current : moveKeyframe(current, keyframe.id, to)));
    setNotice(`Moved to position ${String(Math.min(keyframes.length, Math.max(1, to + 1)))} of ${String(keyframes.length)}.`);
  };

  const patch = (keyframe: Keyframe, change: KeyframePatch): void => {
    onEdit((current) => (current === undefined ? current : updateKeyframe(current, keyframe.id, change)));
  };

  const remove = (keyframe: Keyframe): void => {
    const position = keyframes.findIndex((candidate) => candidate.id === keyframe.id);
    onEdit((current) => (current === undefined ? current : removeKeyframe(current, keyframe.id)));
    const following = keyframes[position + 1] ?? keyframes[position - 1];
    setSelectedId(following?.id);
    setNotice(keyframes.length === 1 ? 'Deleted the last keyframe, so there is no tour.' : `Deleted ${label(keyframe, position)}.`);
  };

  const duplicate = (keyframe: Keyframe): void => {
    let copyId: Id | undefined;
    onEdit((current) => {
      const copy = current === undefined ? undefined : duplicateKeyframe(current, keyframe.id);
      copyId = copy?.id;
      return copy?.flythrough ?? current;
    });
    if (copyId !== undefined) {
      setSelectedId(copyId);
      setNotice('Duplicated the keyframe.');
    }
  };

  const onListKeyDown = (e: ReactKeyboardEvent<HTMLOListElement>): void => {
    // Read now: the event's currentTarget is gone by the time a frame callback runs.
    const list = e.currentTarget;
    const row = e.target instanceof HTMLElement ? e.target.closest<HTMLElement>('[data-keyframe]') : null;
    const id = row?.dataset.keyframe;
    const position = keyframes.findIndex((keyframe) => keyframe.id === id);
    const keyframe = keyframes[position];
    if (keyframe === undefined || !(e.target instanceof HTMLButtonElement) || !e.target.classList.contains('tour-row-main')) {
      return;
    }
    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      e.preventDefault();
      if (e.altKey) {
        move(keyframe, position + (e.key === 'ArrowUp' ? -1 : 1));
        // The row keeps its button, so keyboard focus follows it to its new place.
        requestAnimationFrame(() => {
          list.querySelector<HTMLElement>(`[data-keyframe="${keyframe.id}"] .tour-row-main`)?.focus();
        });
        return;
      }
      const mains = [...list.querySelectorAll<HTMLElement>('.tour-row-main')];
      mains[position + (e.key === 'ArrowUp' ? -1 : 1)]?.focus();
    }
  };

  return (
    <section className="tour-panel" aria-labelledby="tour-heading">
      <h2 id="tour-heading" className="panel-heading">
        Tour
      </h2>
      <p className="panel-hint">Frame the diagram the way you want it shown, then add a keyframe. Present plays them in order.</p>
      <div className="tour-actions" role="group" aria-label="Tour actions">
        <button type="button" disabled={full} onClick={add} title="Capture the canvas's current view as a keyframe">
          Add keyframe
        </button>
        <button
          type="button"
          disabled={!canGenerate}
          onClick={generateTour}
          title={canGenerate ? 'An overview, then each container, then everything else' : 'Needs a diagram with at least one container'}
        >
          {keyframes.length > 0 ? 'Regenerate from containers' : 'Generate from containers'}
        </button>
        <button type="button" className="tour-present" disabled={keyframes.length === 0} onClick={() => { onPresent(Math.max(0, selectedIndex)); }} title={`Play the tour (${platformShortcut('Cmd/Ctrl+Alt+P')})`}>
          Present
        </button>
      </div>
      {flythrough !== undefined && keyframes.length > 1 && (
        <label className="tour-loop">
          <input
            type="checkbox"
            checked={loops(flythrough)}
            onChange={(e) => {
              const loop = e.currentTarget.checked;
              onEdit((current) => (current === undefined ? current : setLoop(current, loop)));
            }}
          />
          Loop back to the first keyframe
        </label>
      )}
      {keyframes.length === 0 ? (
        <p className="panel-hint">No keyframes yet.</p>
      ) : (
        <ol className="tour-list" aria-label="Keyframes" onKeyDown={onListKeyDown}>
          {keyframes.map((keyframe, position) => {
            const isSelected = keyframe.id === selectedId;
            return (
              <li key={`${keyframe.id}:${String(position)}`} className="tour-row" data-keyframe={keyframe.id} data-selected={isSelected || undefined}>
                <div className="tour-row-head">
                  <button
                    type="button"
                    className="tour-row-main"
                    aria-current={isSelected || undefined}
                    title="Go to this keyframe (Alt+Up or Down moves it)"
                    onClick={() => {
                      select(keyframe);
                    }}
                  >
                    <span className="tour-row-number">{position + 1}</span>
                    <span className="tour-row-caption">{label(keyframe, position)}</span>
                  </button>
                  <div className="tour-row-actions" role="group" aria-label={`Keyframe ${String(position + 1)}`}>
                    <button type="button" aria-label="Move up" title="Move up" disabled={position === 0} onClick={() => { move(keyframe, position - 1); }}>
                      ↑
                    </button>
                    <button type="button" aria-label="Move down" title="Move down" disabled={position === keyframes.length - 1} onClick={() => { move(keyframe, position + 1); }}>
                      ↓
                    </button>
                    <button type="button" aria-label="Duplicate" title="Duplicate" disabled={full} onClick={() => { duplicate(keyframe); }}>
                      ⧉
                    </button>
                    <button type="button" aria-label="Delete" title="Delete" onClick={() => { remove(keyframe); }}>
                      ✕
                    </button>
                  </div>
                </div>
                {isSelected && (
                  <KeyframeFields
                    keyframe={keyframe}
                    position={position}
                    looping={flythrough !== undefined && loops(flythrough)}
                    onPatch={(change) => {
                      patch(keyframe, change);
                    }}
                    onRecapture={() => {
                      const view = capture();
                      if (view !== undefined) {
                        patch(keyframe, { view });
                        setNotice('Updated the keyframe to the current view.');
                      }
                    }}
                  />
                )}
              </li>
            );
          })}
        </ol>
      )}
      <p className="visually-hidden" role="status">
        {notice}
      </p>
    </section>
  );
}

interface KeyframeFieldsProps {
  keyframe: Keyframe;
  position: number;
  /** Whether the tour loops, which is what gives the first keyframe an arrival time. */
  looping: boolean;
  onPatch: (change: KeyframePatch) => void;
  onRecapture: () => void;
}

function KeyframeFields({ keyframe, position, looping, onPatch, onRecapture }: KeyframeFieldsProps) {
  const first = position === 0;
  const arrivalUsed = !first || looping;
  const caption = keyframe.caption ?? '';

  // A field commits on blur or Enter; the keys are keyed to the stored value so an undo or a move shows it again. Text
  // that is not a time is refused and the field goes back to the stored one, rather than clearing the setting.
  const commitSeconds = (input: HTMLInputElement, field: 'durationMs' | 'holdMs', current: number | undefined): void => {
    const parsed = parseSeconds(input.value);
    if (parsed === undefined) {
      input.value = current === undefined ? '' : seconds(current);
      return;
    }
    if (parsed !== current && (parsed !== null || current !== undefined)) {
      onPatch({ [field]: parsed });
    }
  };

  return (
    <div className="tour-fields">
      <div className="tour-field">
        <label htmlFor={`tour-caption-${keyframe.id}`}>Caption</label>
        <textarea
          id={`tour-caption-${keyframe.id}`}
          key={`caption:${caption}`}
          rows={2}
          defaultValue={caption}
          onBlur={(e) => {
            if (e.currentTarget.value !== caption) {
              onPatch({ caption: e.currentTarget.value });
            }
          }}
          onKeyDown={(e) => {
            // Escape discards what was typed: the field goes back to the stored caption before focus leaves it.
            if (e.key === 'Escape') {
              e.currentTarget.value = caption;
            }
            // Enter commits (Shift+Enter is a line break), as a label editor does.
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault();
              e.currentTarget.blur();
            }
          }}
        />
      </div>
      <div className="tour-field-row">
        <div className="tour-field">
          <label htmlFor={`tour-duration-${keyframe.id}`}>Arrive (s)</label>
          <input
            id={`tour-duration-${keyframe.id}`}
            key={`duration:${String(keyframe.durationMs)}`}
            type="text"
            inputMode="decimal"
            disabled={!arrivalUsed || easingOf(keyframe) === 'cut'}
            placeholder={seconds(DEFAULT_DURATION_MS)}
            defaultValue={keyframe.durationMs === undefined ? '' : seconds(durationOf(keyframe))}
            title={first ? 'Only used when the tour loops: how long the trip back from the last keyframe takes' : 'How long the camera takes to get here from the previous keyframe'}
            onBlur={(e) => {
              commitSeconds(e.currentTarget, 'durationMs', keyframe.durationMs);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.currentTarget.blur();
              }
            }}
          />
          {first && <span className="tour-field-note">Used only when the tour loops: the trip back from the last keyframe.</span>}
        </div>
        <div className="tour-field">
          <label htmlFor={`tour-hold-${keyframe.id}`}>Hold (s)</label>
          <input
            id={`tour-hold-${keyframe.id}`}
            key={`hold:${String(keyframe.holdMs)}`}
            type="text"
            inputMode="decimal"
            placeholder={seconds(DEFAULT_HOLD_MS)}
            defaultValue={keyframe.holdMs === undefined ? '' : seconds(holdOf(keyframe))}
            title="How long an autoplaying tour stays here"
            onBlur={(e) => {
              commitSeconds(e.currentTarget, 'holdMs', keyframe.holdMs);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.currentTarget.blur();
              }
            }}
          />
        </div>
      </div>
      <div className="tour-field">
        <label htmlFor={`tour-easing-${keyframe.id}`}>Easing</label>
        <select
          id={`tour-easing-${keyframe.id}`}
          value={easingOf(keyframe)}
          onChange={(e) => {
            const easing = e.currentTarget.value as KeyframeEasing;
            // The default is the absence of a setting, so choosing it puts the keyframe back to sparse.
            onPatch({ easing: easing === DEFAULT_EASING ? null : easing });
          }}
        >
          {EASINGS.map((easing) => (
            <option key={easing} value={easing}>
              {EASING_LABELS[easing]}
            </option>
          ))}
        </select>
      </div>
      <button type="button" className="tour-recapture" onClick={onRecapture} title="Replace this keyframe's view with what the canvas shows now">
        Use current view
      </button>
    </div>
  );
}
