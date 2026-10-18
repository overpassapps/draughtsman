/**
 * Export fly-through (docs/flythrough.md): the document and its camera path as one self-contained HTML file that plays
 * in any browser, with no server, no network and no library. The diagram is the export surface's SVG
 * (editor/exportDiagram.tsx `documentToSvg`: full detail, theme applied, images inlined, orthogonal connectors routed),
 * so the file draws what the editor draws; the player (editor/flythroughPlayer.ts) is a few kilobytes of plain JavaScript
 * that sizes the SVG to the window and sets the world's transform for the camera each frame.
 *
 * The file plays on open, paused on the first keyframe; adding `#play` to its URL starts it playing, for a kiosk or a
 * page that embeds it in an iframe. Every key and control is the Present overlay's (editor/PresentationStage.tsx).
 */

import { DEFAULT_DURATION_MS, DEFAULT_EASING, DEFAULT_HOLD_MS, canonicalFlythrough, loops } from '../model/flythrough.ts';
import type { Document, Flythrough } from '../model/types.ts';
import { effectiveTheme, surfaceBackground } from '../theme/resolveStyle.ts';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { documentToSvg, type ExportSvgOptions } from './exportDiagram.tsx';
import { PLAYER_CSS, PLAYER_SCRIPT } from './flythroughPlayer.ts';

function escapeHtml(text: string): string {
  return text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
}

/** JSON safe inside a `<script>`: nothing in it can close the tag or start a comment, and U+2028/2029 cannot break the line. */
function scriptJson(value: unknown): string {
  const json = JSON.stringify(value);
  return json
    .replaceAll('<', '\\u003c')
    .replaceAll(String.fromCharCode(0x2028), '\\u2028')
    .replaceAll(String.fromCharCode(0x2029), '\\u2029');
}

const ICON = 'viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"';

/**
 * The standalone viewer for a tour, as one HTML string. The tour is written in canonical form with its defaults beside
 * it, so the player's numbers are the editor's. Throws when the document has no tour: there is nothing to play.
 */
export function flythroughToHtml(document: Document, flythrough: Flythrough, theme: DiagramTheme, options: ExportSvgOptions = {}): string {
  const tour = canonicalFlythrough(flythrough);
  if (tour === undefined) {
    throw new Error('The diagram has no fly-through to export.');
  }
  const { svg } = documentToSvg(document, theme, options);
  const resolved = effectiveTheme(theme, document);
  const background = surfaceBackground(resolved, 'export');
  const end = loops(tour) ? tour.keyframes.length : Math.max(0, tour.keyframes.length - 1);
  const data = { keyframes: tour.keyframes, loop: loops(tour), defaults: { duration: DEFAULT_DURATION_MS, hold: DEFAULT_HOLD_MS, easing: DEFAULT_EASING } };
  const ticks = tour.keyframes.map((_, n) => `<span class="ft-tick" style="left:${end === 0 ? '0' : String((n / end) * 100)}%"></span>`).join('');
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(document.title === '' ? 'Fly-through' : document.title)}</title>
<style>${PLAYER_CSS}body { background: ${escapeHtml(background)}; }</style>
</head>
<body>
<div id="flythrough" data-chrome="shown" role="region" aria-label="Fly-through of ${escapeHtml(document.title)}">
<div id="stage">${svg}</div>
<noscript><p>This fly-through needs JavaScript to play; the diagram is shown as it is on the first keyframe.</p></noscript>
<p id="ft-caption" aria-live="polite" hidden></p>
<div id="ft-chrome">
<div id="ft-bar" role="toolbar" aria-label="Fly-through controls">
<button type="button" id="ft-prev" aria-label="Previous keyframe" title="Previous (Left, PageUp)"><svg ${ICON}><path d="m15 18-6-6 6-6"/></svg></button>
<button type="button" id="ft-play" aria-label="Play" title="Play (P)" data-state="paused"><svg data-icon="play" ${ICON}><polygon points="6 4 20 12 6 20 6 4"/></svg><svg data-icon="pause" ${ICON}><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg><svg data-icon="replay" ${ICON}><path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/></svg></button>
<button type="button" id="ft-next" aria-label="Next keyframe" title="Next (Right, PageDown, Space)"><svg ${ICON}><path d="m9 18 6-6-6-6"/></svg></button>
<div id="ft-scrubber"><input type="range" id="ft-range" aria-label="Position in the tour" min="0" max="${String(end)}" step="any" value="0"${end === 0 ? ' disabled' : ''}><div id="ft-ticks" aria-hidden="true">${ticks}</div></div>
<span id="ft-counter">1 / ${String(tour.keyframes.length)}</span>
<button type="button" id="ft-full" aria-label="Full screen" title="Full screen (F)" aria-pressed="false"><svg ${ICON}><path d="M8 3H5a2 2 0 0 0-2 2v3"/><path d="M21 8V5a2 2 0 0 0-2-2h-3"/><path d="M3 16v3a2 2 0 0 0 2 2h3"/><path d="M16 21h3a2 2 0 0 0 2-2v-3"/></svg></button>
</div>
</div>
</div>
<script type="application/json" id="flythrough-data">${scriptJson(data)}</script>
<script>${PLAYER_SCRIPT}</script>
</body>
</html>
`;
}
