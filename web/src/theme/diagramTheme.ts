/**
 * The shape of a compiled diagram theme (themes/diagram-*.yaml). web/scripts/build-tokens.ts emits
 * src/generated/diagram-themes.ts against these types; nothing else should construct them by hand.
 */

import type { ArrowHead, Dash } from '../model/types.ts';

/** Fully resolved defaults for one node kind. Every field is present after compilation. */
export interface NodeKindStyle {
  fill: string;
  stroke: string;
  strokeWidth: number;
  textColor: string;
  font: string;
  dash: Dash;
  /** Label size in canvas pixels; absent from the theme, resolved from the element override or the default (14). */
  fontSize?: number;
  /** Bold label; absent from the theme, resolved from the element override or the default (false). */
  bold?: boolean;
}

/** Fully resolved defaults for one edge kind. */
export interface EdgeKindStyle {
  stroke: string;
  strokeWidth: number;
  dash: Dash;
  arrowStart: ArrowHead;
  arrowEnd: ArrowHead;
}

export interface DiagramGrid {
  size: number;
  color: string;
  visible: boolean;
}

export interface DiagramFonts {
  label: string;
  mono: string;
}

/** Which of the theme's two backgrounds a render is drawn on: the editor canvas or the export paper. */
export type Surface = 'canvas' | 'export';

export interface DiagramTheme {
  name: string;
  description: string;
  palette: Record<string, string>;
  fonts: DiagramFonts;
  grid: DiagramGrid;
  /** Painted behind the drawing in the editor. */
  canvasBackground: string;
  /** Painted behind the drawing on export; never the chrome surface. */
  exportBackground: string;
  /**
   * Colour of text drawn straight on the canvas (edge labels, and nodes whose fill is transparent). When absent it
   * is derived from `canvasBackground`: light text on a dark surface, dark text on a light one. See
   * `surfaceTextColor` in resolveStyle.ts.
   */
  canvasText?: string;
  /** The same for text drawn straight on the export paper, derived from `exportBackground` when absent. */
  exportText?: string;
  /** Keyed by node kind; `default` is used for kinds the theme does not name. */
  nodes: Record<string, NodeKindStyle> & { default: NodeKindStyle };
  /** Keyed by edge kind; `default` is used for kinds the theme does not name. */
  edges: Record<string, EdgeKindStyle> & { default: EdgeKindStyle };
}
