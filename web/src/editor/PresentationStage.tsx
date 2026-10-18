/**
 * Present (design/atlas/flythrough.md): a full-window, read-only overlay that plays the document's flythrough. It draws
 * the diagram with the same `DiagramSvg` the canvas uses, from a snapshot of the document taken when Present opened, so
 * another writer's live-synced change never moves a show under way, and drives its viewport frame by frame from the
 * camera path (canvas/camera.ts, editor/usePresentation.ts). Nothing on it edits anything.
 *
 * The controls are one bar at the bottom (previous, play or pause, next, a scrubber with a tick per keyframe, "3 / 7",
 * fullscreen, close) and the current keyframe's caption above it. While the tour is playing, or the overlay is full
 * screen, the bar and the cursor fade after CHROME_IDLE_MS without a pointer move or key; they never fade while a
 * windowed tour is paused, and any move, key or focus brings them back. Reduced motion turns every transition into a cut
 * and the fade into an instant change.
 *
 * Keys (the overlay keeps every key from the editor behind it, as the dialogs do): Right, Down, PageDown, Space and
 * Enter go on; Left, Up, PageUp and Backspace go back; Home and End go to the ends; 1 to 9 go to that keyframe; P plays
 * and pauses; F toggles fullscreen; Escape leaves full screen first and the presentation second. A presentation clicker sends PageUp and PageDown, so it needs no
 * setup. A key a focused control uses itself (Enter or Space on a button, an arrow on the scrubber) stays the control's.
 */

import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { DiagramSvg } from '../canvas/DiagramSvg.tsx';
import { indexDocument } from '../canvas/documentIndex.ts';
import { pathEnd, viewportAtProgress } from '../canvas/camera.ts';
import type { LineBreaker, TextMeasurer } from '../canvas/text.ts';
import { defaultRoutingBackend, useEdgeRoutes, type RoutingBackend } from '../canvas/useEdgeRoutes.ts';
import type { Size } from '../canvas/viewport.ts';
import type { Document, Flythrough } from '../model/types.ts';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { usePresentation, useReducedMotion, type FrameSource } from './usePresentation.ts';

/** How long the controls stay after the last pointer move or key, when they are allowed to fade. */
export const CHROME_IDLE_MS = 2500;

export interface PresentationStageProps {
  /** The document to draw, as it was when Present opened. */
  document: Document;
  flythrough: Flythrough;
  theme: DiagramTheme;
  measure?: TextMeasurer | undefined;
  breakLines?: LineBreaker | undefined;
  /** The keyframe to start on; the first by default. */
  startIndex?: number | undefined;
  /** Start playing at once. */
  autoplay?: boolean | undefined;
  /** Leaves Present: Escape, the close button, or the parent ending it. */
  onClose: () => void;
  /** A fixed window size, for tests and export; measured from the stage otherwise. */
  size?: Size | undefined;
  /** Creates the routing worker the stage owns; the real worker by default. Tests pass an in-process one. */
  routingBackend?: (() => RoutingBackend | undefined) | undefined;
  /** The clock and frame source; the real ones by default. */
  frames?: FrameSource | undefined;
}

const ICON_PROPS = { viewBox: '0 0 24 24', width: 20, height: 20, fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': true } as const;

function PreviousIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="m15 18-6-6 6-6" />
    </svg>
  );
}

function NextIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="m9 18 6-6-6-6" />
    </svg>
  );
}

function PlayIcon() {
  return (
    <svg {...ICON_PROPS}>
      <polygon points="6 4 20 12 6 20 6 4" />
    </svg>
  );
}

function PauseIcon() {
  return (
    <svg {...ICON_PROPS}>
      <rect x="6" y="4" width="4" height="16" />
      <rect x="14" y="4" width="4" height="16" />
    </svg>
  );
}

function ReplayIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
      <path d="M3 3v5h5" />
    </svg>
  );
}

function FullscreenIcon({ active }: { active: boolean }) {
  return (
    <svg {...ICON_PROPS}>
      {active ? (
        <>
          <path d="M8 3v3a2 2 0 0 1-2 2H3" />
          <path d="M21 8h-3a2 2 0 0 1-2-2V3" />
          <path d="M3 16h3a2 2 0 0 1 2 2v3" />
          <path d="M16 21v-3a2 2 0 0 1 2-2h3" />
        </>
      ) : (
        <>
          <path d="M8 3H5a2 2 0 0 0-2 2v3" />
          <path d="M21 8V5a2 2 0 0 0-2-2h-3" />
          <path d="M3 16v3a2 2 0 0 0 2 2h3" />
          <path d="M16 21h3a2 2 0 0 0 2-2v-3" />
        </>
      )}
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M18 6 6 18" />
      <path d="m6 6 12 12" />
    </svg>
  );
}

/** The element in full screen, or null: where the Fullscreen API is missing the property is undefined, which is none. */
function fullscreenElementNow(): Element | null {
  return globalThis.document.fullscreenElement ?? null;
}

export function PresentationStage({ document: snapshot, flythrough, theme, measure, breakLines, startIndex, autoplay, onClose, size: fixedSize, routingBackend = defaultRoutingBackend, frames }: PresentationStageProps) {
  const root = useRef<HTMLDivElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const reducedMotion = useReducedMotion();
  const presentation = usePresentation(flythrough, { startIndex, autoplay, reducedMotion, frames });
  const { view } = presentation;
  const count = flythrough.keyframes.length;
  const end = pathEnd(flythrough);

  const [measured, setMeasured] = useState<Size>({ width: 0, height: 0 });
  const size = fixedSize ?? measured;
  useLayoutEffect(() => {
    if (fixedSize !== undefined) {
      return;
    }
    const element = stage.current;
    if (element === null) {
      return;
    }
    const read = (): void => {
      const rect = element.getBoundingClientRect();
      setMeasured((current) => (current.width === rect.width && current.height === rect.height ? current : { width: rect.width, height: rect.height }));
    };
    read();
    if (typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver(read);
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, [fixedSize]);

  const index = useMemo(() => indexDocument(snapshot), [snapshot]);
  const routes = useEdgeRoutes(index, routingBackend);
  const viewport = useMemo(() => viewportAtProgress(flythrough, view.p, size), [flythrough, view.p, size]);

  // Focus moves in on open and back to where it was on close: the editor's canvas, or the control that opened Present. The
  // page, which had no focus to give back, is left to the caller.
  useEffect(() => {
    const before = globalThis.document.activeElement;
    root.current?.focus({ preventScroll: true });
    return () => {
      if (before instanceof HTMLElement && before !== globalThis.document.body && before.isConnected) {
        before.focus({ preventScroll: true });
      }
    };
  }, []);

  // Everything else in the page is inert while the overlay is open: no focus, no pointer, nothing for a screen reader to
  // wander into. The overlay's own siblings are the editor behind it, whatever bars and dialogs that holds.
  useEffect(() => {
    const overlay = root.current;
    const inerted = [...(overlay?.parentElement?.children ?? [])].filter((sibling) => sibling !== overlay && !sibling.hasAttribute('inert'));
    for (const sibling of inerted) {
      sibling.setAttribute('inert', '');
    }
    return () => {
      for (const sibling of inerted) {
        sibling.removeAttribute('inert');
      }
    };
  }, []);

  const [fullscreen, setFullscreen] = useState(false);
  useEffect(() => {
    const onChange = (): void => {
      const element = fullscreenElementNow();
      setFullscreen(element !== null && element === root.current);
    };
    globalThis.document.addEventListener('fullscreenchange', onChange);
    return () => {
      globalThis.document.removeEventListener('fullscreenchange', onChange);
    };
  }, []);
  const canFullscreen = typeof HTMLElement !== 'undefined' && typeof HTMLElement.prototype.requestFullscreen === 'function';
  const toggleFullscreen = useCallback(() => {
    if (fullscreenElementNow() !== null) {
      void globalThis.document.exitFullscreen().catch(() => undefined);
    } else {
      void root.current?.requestFullscreen().catch(() => undefined);
    }
  }, []);
  // Leaving Present while full screen must not strand the browser there.
  useEffect(
    () => () => {
      if (fullscreenElementNow() !== null) {
        void globalThis.document.exitFullscreen().catch(() => undefined);
      }
    },
    [],
  );

  // The controls fade only when they are in the way: a playing tour, or full screen. Any move, key or focus shows them
  // and starts the countdown again; a countdown only runs while they may fade.
  const [chromeShown, setChromeShown] = useState(true);
  const idleTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const mayFade = view.playing || fullscreen;
  const reveal = useCallback(() => {
    setChromeShown(true);
    clearTimeout(idleTimer.current);
    if (mayFade) {
      idleTimer.current = setTimeout(() => {
        setChromeShown(false);
      }, CHROME_IDLE_MS);
    }
  }, [mayFade]);
  useEffect(() => {
    if (mayFade) {
      idleTimer.current = setTimeout(() => {
        setChromeShown(false);
      }, CHROME_IDLE_MS);
    }
    return () => {
      clearTimeout(idleTimer.current);
    };
  }, [mayFade]);
  const shown = chromeShown || !mayFade;

  const onKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>): void => {
    // Nothing reaches the editor behind: its window-level shortcuts would act on a canvas nobody can see.
    e.stopPropagation();
    reveal();
    if (e.ctrlKey || e.metaKey || e.altKey) {
      return;
    }
    const target = e.target;
    const onButton = target instanceof HTMLButtonElement;
    const onRange = target instanceof HTMLInputElement;
    const handled = ((): boolean => {
      switch (e.key) {
        case 'Escape':
          // Full screen first, presentation second: a browser that hands the page this key before (or instead of) leaving
          // full screen itself must not also close the show with the one press.
          if (fullscreenElementNow() === null) {
            onClose();
          } else {
            void globalThis.document.exitFullscreen().catch(() => undefined);
          }
          return true;
        case 'ArrowRight':
        case 'ArrowDown':
          if (onRange) {
            return false;
          }
          presentation.next();
          return true;
        case 'ArrowLeft':
        case 'ArrowUp':
          if (onRange) {
            return false;
          }
          presentation.previous();
          return true;
        case 'PageDown':
          presentation.next();
          return true;
        case 'PageUp':
        case 'Backspace':
          presentation.previous();
          return true;
        case ' ':
        case 'Enter':
          if (onButton || onRange) {
            return false;
          }
          presentation.next();
          return true;
        case 'Home':
          if (onRange) {
            return false;
          }
          presentation.jumpTo(0);
          return true;
        case 'End':
          if (onRange) {
            return false;
          }
          presentation.jumpTo(count - 1);
          return true;
        case 'p':
        case 'P':
          presentation.toggle();
          return true;
        case 'f':
        case 'F':
          toggleFullscreen();
          return true;
        default:
          if (/^[1-9]$/.test(e.key) && Number(e.key) <= count) {
            presentation.jumpTo(Number(e.key) - 1);
            return true;
          }
          return false;
      }
    })();
    if (handled) {
      e.preventDefault();
    }
  };

  const caption = flythrough.keyframes[view.index]?.caption;
  const atStart = end === 0 || (view.p <= 0 && flythrough.loop !== true);
  const atEnd = end === 0 || (view.finished && flythrough.loop !== true);
  const playLabel = view.playing ? 'Pause' : view.finished ? 'Replay' : 'Play';

  return (
    <div
      ref={root}
      className="present"
      role="dialog"
      aria-modal="true"
      aria-label="Presentation"
      tabIndex={-1}
      data-chrome={shown ? 'shown' : 'hidden'}
      data-reduced-motion={reducedMotion || undefined}
      onKeyDown={onKeyDown}
      onPointerMove={reveal}
      onPointerDown={reveal}
      onFocus={reveal}
    >
      <div ref={stage} className="present-stage">
        <DiagramSvg document={snapshot} theme={theme} viewport={viewport} size={size} idPrefix="present-" index={index} routes={routes} measure={measure} breakLines={breakLines} showGrid={false} pixelRatio={typeof window === 'undefined' ? 1 : window.devicePixelRatio} />
      </div>
      <p className="present-caption" aria-live="polite" data-empty={caption === undefined || undefined}>
        {caption}
      </p>
      <div className="present-chrome">
        <button type="button" className="present-close" aria-label="Exit presentation" title="Exit (Esc)" onClick={onClose}>
          <CloseIcon />
        </button>
        <div className="present-bar" role="toolbar" aria-label="Presentation controls">
          <button
            type="button"
            aria-label="Previous keyframe"
            title="Previous (Left, PageUp)"
            aria-disabled={atStart}
            onClick={() => {
              if (!atStart) {
                presentation.previous();
              }
            }}
          >
            <PreviousIcon />
          </button>
          <button type="button" className="present-play" aria-label={playLabel} title={`${playLabel} (P)`} onClick={presentation.toggle}>
            {view.playing ? <PauseIcon /> : view.finished ? <ReplayIcon /> : <PlayIcon />}
          </button>
          <button
            type="button"
            aria-label="Next keyframe"
            title="Next (Right, PageDown, Space)"
            aria-disabled={atEnd}
            onClick={() => {
              if (!atEnd) {
                presentation.next();
              }
            }}
          >
            <NextIcon />
          </button>
          <div className="present-scrubber">
            <input
              type="range"
              aria-label="Position in the tour"
              aria-valuetext={`Keyframe ${String(view.index + 1)} of ${String(count)}`}
              min={0}
              max={end}
              step="any"
              value={view.p}
              disabled={end === 0}
              onChange={(e) => {
                presentation.pause();
                presentation.seek(e.currentTarget.valueAsNumber);
              }}
            />
            <div className="present-ticks" aria-hidden="true">
              {flythrough.keyframes.map((keyframe, n) => (
                <span key={`${keyframe.id}:${String(n)}`} className="present-tick" data-current={n === view.index || undefined} style={{ left: end === 0 ? '0%' : `${String((n / end) * 100)}%` }} />
              ))}
            </div>
          </div>
          <span className="present-counter">
            {view.index + 1} / {count}
          </span>
          {canFullscreen && (
            <button type="button" aria-label={fullscreen ? 'Exit full screen' : 'Full screen'} title="Full screen (F)" aria-pressed={fullscreen} onClick={toggleFullscreen}>
              <FullscreenIcon active={fullscreen} />
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
