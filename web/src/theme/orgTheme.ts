/**
 * Organisation themes (admin Themes page; docs/api.md "## Themes"). The server stores and validates them and serves the
 * chrome half as the instance stylesheet; this module is the client half: the wire types, the pure function that applies
 * a theme's diagram override over one of the compiled diagram themes, and the inline custom properties the page's live
 * chrome preview scopes to one element. Pure and DOM-free so it is unit-testable and the preview, the editor and
 * (later) an export all resolve a theme the same way.
 */

import type { CSSProperties } from 'react';
import { diagramThemes, type DiagramThemeName } from '../generated/diagram-themes.ts';
import type { DiagramTheme } from './diagramTheme.ts';

/** The chrome tokens a theme sets in each mode, in the order the page lists them. Matches themes/chrome.yaml. */
export const CHROME_KEYS = ['brand', 'brandText', 'surface', 'surfaceRaised', 'border', 'text', 'textMuted', 'accent', 'danger'] as const;
export type ChromeKey = (typeof CHROME_KEYS)[number];
export type ChromeColours = Record<ChromeKey, string>;

export type ThemeMode = 'dark' | 'light' | 'system';

export interface OrgChrome {
  dark: ChromeColours;
  light: ChromeColours;
  radius: number;
  fonts: { ui: string; mono: string };
}

/** The diagram half: which compiled theme it starts from and what it changes. See src/Draughtsman.Server/Theming/OrgTheme.cs. */
export interface OrgDiagram {
  base: string;
  palette: Record<string, string>;
  canvasBackground: string | null;
  exportBackground: string | null;
  gridColor: string | null;
  fonts: { label: string | null; mono: string | null };
}

/** An organisation theme as the admin API returns it. */
export interface OrgTheme {
  id: string;
  name: string;
  mode: ThemeMode;
  chrome: OrgChrome;
  diagram: OrgDiagram;
  updatedAt: string;
  isDefault: boolean;
}

/** What any signed-in user may read of a theme: its name and diagram half (the editor paints diagrams with it). */
export interface PublicTheme {
  id: string;
  name: string;
  diagram: OrgDiagram;
}

export const DIAGRAM_BASES: readonly DiagramThemeName[] = ['overpass', 'neutral', 'mono'];

/** Labels the page shows for the chrome tokens. */
export const CHROME_LABELS: Record<ChromeKey, string> = {
  brand: 'Brand',
  brandText: 'Text on brand',
  surface: 'Surface',
  surfaceRaised: 'Raised surface',
  border: 'Border',
  text: 'Text',
  textMuted: 'Muted text',
  accent: 'Accent',
  danger: 'Danger',
};

const HEX = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;

/** True for a colour the server would accept: #rgb or #rrggbb. Mirrors OrgThemeValidator.NormaliseColour. */
export function isHexColour(value: string): boolean {
  return HEX.test(value.trim());
}

/** A font list the server would accept. Mirrors OrgThemeValidator.IsSafeFont: family names, optionally quoted, nothing else. */
export function isSafeFont(value: string): boolean {
  const name = `(?:[A-Za-z0-9_ -]+|"[A-Za-z0-9_ -]+"|'[A-Za-z0-9_ -]+')`;
  return value.length <= 120 && new RegExp(`^\\s*${name}(?:\\s*,\\s*${name})*\\s*$`).test(value);
}

function isBase(value: string): value is DiagramThemeName {
  return (DIAGRAM_BASES as readonly string[]).includes(value);
}

function swap(colour: string, map: ReadonlyMap<string, string>): string {
  return map.get(colour.toLowerCase()) ?? colour;
}

/**
 * The diagram theme an organisation theme produces: the compiled base theme with the override applied. A palette entry
 * replaces that palette colour wherever the base's node and edge styles use it (the compiled themes hold literal hex,
 * not references), so one edit restyles every kind sharing the colour and the base's visual hierarchy survives; a name
 * the base's palette does not have is ignored. The backgrounds, grid colour and fonts are direct, and win over a
 * palette swap that touched the same value. An unknown base falls back to the first, so a stale theme still draws.
 */
export function applyDiagramOverride(override: OrgDiagram, displayName?: string): DiagramTheme {
  const base = diagramThemes[isBase(override.base) ? override.base : 'overpass'];
  const colours = new Map<string, string>();
  const palette: Record<string, string> = { ...base.palette };
  for (const [name, replacement] of Object.entries(override.palette)) {
    const current = base.palette[name];
    if (current !== undefined && isHexColour(replacement)) {
      colours.set(current.toLowerCase(), replacement);
      palette[name] = replacement;
    }
  }

  const fontLabel = override.fonts.label ?? base.fonts.label;
  const fontMono = override.fonts.mono ?? base.fonts.mono;
  const font = (value: string): string => (value === base.fonts.label ? fontLabel : value);

  const nodes = Object.fromEntries(
    Object.entries(base.nodes).map(([kind, style]) => [
      kind,
      { ...style, fill: swap(style.fill, colours), stroke: swap(style.stroke, colours), textColor: swap(style.textColor, colours), font: font(style.font) },
    ]),
  ) as DiagramTheme['nodes'];
  const edges = Object.fromEntries(Object.entries(base.edges).map(([kind, style]) => [kind, { ...style, stroke: swap(style.stroke, colours) }])) as DiagramTheme['edges'];

  const themed: DiagramTheme = {
    ...base,
    name: displayName ?? base.name,
    palette,
    fonts: { label: fontLabel, mono: fontMono },
    grid: { ...base.grid, color: override.gridColor ?? swap(base.grid.color, colours) },
    canvasBackground: override.canvasBackground ?? swap(base.canvasBackground, colours),
    exportBackground: override.exportBackground ?? swap(base.exportBackground, colours),
    nodes,
    edges,
  };
  if (base.canvasText !== undefined) {
    themed.canvasText = swap(base.canvasText, colours);
  }
  if (base.exportText !== undefined) {
    themed.exportText = swap(base.exportText, colours);
  }
  return themed;
}

/** The kebab-cased custom property a chrome token compiles to: `surfaceRaised` -> `--chrome-surface-raised`. */
export function chromeProperty(key: ChromeKey): string {
  return `--chrome-${key.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase()}`;
}

/**
 * Inline custom properties that scope one mode of a theme's chrome to an element, so the page's preview can show a
 * draft (or a saved second theme) without touching the real app shell. Invalid colours are left out rather than
 * written, so a half-typed hex never reaches the style attribute.
 */
export function chromeStyleVars(chrome: OrgChrome, set: 'dark' | 'light'): CSSProperties {
  const vars: Record<string, string> = {};
  for (const key of CHROME_KEYS) {
    const value = chrome[set][key];
    if (isHexColour(value)) {
      vars[chromeProperty(key)] = value;
    }
  }
  vars['--chrome-radius'] = `${String(chrome.radius)}px`;
  if (isSafeFont(chrome.fonts.ui)) {
    vars['--chrome-font-ui'] = chrome.fonts.ui;
  }
  if (isSafeFont(chrome.fonts.mono)) {
    vars['--chrome-font-mono'] = chrome.fonts.mono;
  }
  return vars;
}

/** Which colour set a theme shows first: its own mode, dark for system (the compiler's primary set). */
export function primarySet(mode: ThemeMode): 'dark' | 'light' {
  return mode === 'light' ? 'light' : 'dark';
}
