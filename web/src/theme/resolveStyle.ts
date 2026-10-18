/**
 * Style resolution: theme default for the kind, overridden by the document's theme token overrides, overridden by
 * the element's own sparse style.
 *
 * The document's `theme` map overrides *tokens* of the diagram theme (keys are dotted paths such as
 * `nodes.service.fill`, `edges.flow.stroke`, `palette.red`, `grid.color`, `canvasBackground`), so together with the
 * theme it forms the effective theme every element inherits from. An element's own style is the deliberate deviation
 * a document stores, so it always wins; a document-wide override that beat an explicit per-node style would make
 * "documents store only deviations" (spec section 4) meaningless.
 */

import type { ArrowHead, Dash, Document, Edge, Node } from '../model/types.ts';
import { parseHex, readableTextOn, relativeLuminance } from './contrast.ts';
import type { DiagramTheme, EdgeKindStyle, NodeKindStyle, Surface } from './diagramTheme.ts';

/** The dotted-path prefix of the theme's own colour names, which a `token:<name>` reference reads. */
const PALETTE_PREFIX = 'palette.';

/** Applies a document's dotted-path token overrides to a theme, returning a new theme. Unknown paths are ignored. */
export function applyDocumentOverrides(theme: DiagramTheme, overrides: Record<string, string> | undefined): DiagramTheme {
  if (!overrides || Object.keys(overrides).length === 0) {
    return theme;
  }
  const result = structuredClone(theme);
  const entries = Object.entries(overrides);
  // The palette is the base every `token:` reference names, so it settles first and carries colour literals only:
  // an entry naming another entry would make the picture depend on the order the document happens to list its keys.
  for (const [path, value] of entries.filter(([key]) => key.startsWith(PALETTE_PREFIX))) {
    if (isLiteral(value)) {
      setPath(result, path.split('.'), value);
    }
  }
  for (const [path, value] of entries.filter(([key]) => !key.startsWith(PALETTE_PREFIX))) {
    const colour = paint(result.palette, value);
    if (colour !== undefined) {
      setPath(result, path.split('.'), colour);
    }
  }
  return result;
}

/** Theme containers keyed by kind: every entry is a whole style, so a kind name never names a token. */
const GROUP_MAPS = new Set(['nodes', 'edges']);

/** Top-level fields that say which theme this is rather than how it paints: a document overrides tokens, not these. */
const IDENTITY_FIELDS = new Set(['name', 'description']);

function unsafeSegment(segment: string): boolean {
  return segment === '__proto__' || segment === 'constructor' || segment === 'prototype';
}

/**
 * True when `key` names one token inside `container`: the position holds a token today, or it is a token the theme
 * omits and a document may add (`canvasText`, a new `palette` entry). A position inside a kind map holds a whole
 * style, never a token, so a kind name fails this test whether or not the theme names that kind, and so do the
 * theme's own identity fields.
 */
function isTokenPosition(container: Record<string, unknown>, containerName: string | undefined, key: string): boolean {
  if (containerName === undefined ? IDENTITY_FIELDS.has(key) : GROUP_MAPS.has(containerName)) {
    return false;
  }
  const existing = Object.hasOwn(container, key) ? container[key] : undefined;
  return typeof existing !== 'object' || existing === null;
}

function setPath(theme: DiagramTheme, segments: string[], value: string): void {
  const key = segments[segments.length - 1];
  if (key === undefined || segments.some(unsafeSegment)) {
    return;
  }
  let container = theme as unknown as Record<string, unknown>;
  let containerName: string | undefined;
  for (const segment of segments.slice(0, -1)) {
    const next = Object.hasOwn(container, segment) ? container[segment] : undefined;
    // A step the theme does not carry is not a route to a token, so the path addresses nothing.
    if (typeof next !== 'object' || next === null) {
      return;
    }
    container = next as Record<string, unknown>;
    containerName = segment;
  }
  if (!isTokenPosition(container, containerName, key)) {
    return;
  }
  const existing = container[key];
  // Keep numeric tokens numeric (strokeWidth, grid.size) and booleans boolean (grid.visible).
  if (typeof existing === 'number') {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      container[key] = parsed;
    }
  } else if (typeof existing === 'boolean') {
    container[key] = value === 'true';
  } else {
    container[key] = value;
  }
}

/**
 * A theme entry by name, as an own property only. Kinds and token names come from the document, so a name such as
 * `constructor` or `toString` must read as "the theme does not carry it" rather than yielding an inherited member.
 */
function entry<T>(entries: Record<string, T>, name: string): T | undefined {
  return Object.hasOwn(entries, name) ? entries[name] : undefined;
}

/** The theme's default for a node kind, falling back to `default` for kinds the theme does not name. */
export function nodeKindDefault(theme: DiagramTheme, kind: string): NodeKindStyle {
  return entry(theme.nodes, kind) ?? theme.nodes.default;
}

/** The theme's default for an edge kind, falling back to `default` for kinds the theme does not name. */
export function edgeKindDefault(theme: DiagramTheme, kind: string): EdgeKindStyle {
  return entry(theme.edges, kind) ?? theme.edges.default;
}

/** Prefix of a theme token reference in a document's colour field, per `$defs/colour` in the document schema. */
const TOKEN_PREFIX = 'token:';

/** True when a colour is written out rather than naming a palette entry. */
function isLiteral(colour: string): boolean {
  return !colour.startsWith(TOKEN_PREFIX);
}

/** The dashed line styles a document may store, the only values the schema admits (see document.schema.json `dash`). */
const DASHES = new Set(['solid', 'dashed', 'dotted']);

/** The arrowhead names the schema admits; anything else is a document bug and must not pass through to the render. */
const ARROW_HEADS = new Set([
  'none', 'arrow', 'open', 'diamond', 'circle',
  'one', 'many', 'zeroOrOne', 'oneOrMany', 'zeroOrMany', 'oneAndOnlyOne',
  'inheritance', 'aggregation', 'composition',
]);

/** A value that the `dash` enum accepts; anything else falls back to the inherited dash rather than passing through. */
function validDash(value: string | undefined): Dash | undefined {
  return value !== undefined && DASHES.has(value) ? (value as Dash) : undefined;
}

/** A value that the `arrowHead` enum accepts; anything else is dropped in favour of the inherited head. */
function validArrowHead(value: string | undefined): ArrowHead | undefined {
  return value !== undefined && ARROW_HEADS.has(value) ? (value as ArrowHead) : undefined;
}

/**
 * A colour a document stores, wherever it stores it: a CSS colour literal, used as written, or `token:<name>`,
 * looked up in the palette so re-skinning moves it. Undefined when there is no colour to use — none was given, or
 * the palette carries no such name — so the caller keeps what it would have used anyway.
 */
function paint(palette: Record<string, string>, colour: string | undefined): string | undefined {
  if (colour === undefined || isLiteral(colour)) {
    return colour;
  }
  return entry(palette, colour.slice(TOKEN_PREFIX.length));
}

/** Fully resolved style for a node under an effective theme (see applyDocumentOverrides). */
export function resolveNodeStyle(theme: DiagramTheme, node: Pick<Node, 'kind' | 'style'>): NodeKindStyle {
  const base = nodeKindDefault(theme, node.kind);
  const style = node.style;
  if (!style) {
    return base;
  }
  const fontSize = style.fontSize ?? base.fontSize;
  const bold = style.bold ?? base.bold;
  const fill = paint(theme.palette, style.fill) ?? base.fill;
  return {
    fill,
    stroke: paint(theme.palette, style.stroke) ?? base.stroke,
    strokeWidth: style.strokeWidth ?? base.strokeWidth,
    // A text colour the document names is kept as written; otherwise the theme's, unless it cannot be read on this fill (QA-06).
    textColor: paint(theme.palette, style.textColor) ?? readableTextOn(base.textColor, fill),
    font: style.font ?? base.font,
    dash: validDash(style.dash) ?? base.dash,
    ...(fontSize === undefined ? {} : { fontSize }),
    ...(bold === undefined ? {} : { bold }),
  };
}

/** Fully resolved style for an edge under an effective theme (see applyDocumentOverrides). */
export function resolveEdgeStyle(theme: DiagramTheme, edge: Pick<Edge, 'kind' | 'style'>): EdgeKindStyle {
  const base = edgeKindDefault(theme, edge.kind);
  const style = edge.style;
  if (!style) {
    return base;
  }
  return {
    stroke: paint(theme.palette, style.stroke) ?? base.stroke,
    strokeWidth: style.strokeWidth ?? base.strokeWidth,
    dash: validDash(style.dash) ?? base.dash,
    arrowStart: validArrowHead(style.arrowStart) ?? base.arrowStart,
    arrowEnd: validArrowHead(style.arrowEnd) ?? base.arrowEnd,
  };
}

/** The theme a document renders with: the chosen diagram theme plus the document's token overrides. */
export function effectiveTheme(theme: DiagramTheme, document: Pick<Document, 'theme'>): DiagramTheme {
  return applyDocumentOverrides(theme, document.theme);
}

/** The background a surface paints behind the drawing. */
export function surfaceBackground(theme: DiagramTheme, surface: Surface): string {
  return surface === 'export' ? theme.exportBackground : theme.canvasBackground;
}

/**
 * Relative luminance above which a surface counts as light. It is the point where black and white text swap
 * places on the WCAG contrast scale, so whichever side the surface falls, the derived text reads against it.
 */
const LIGHT_SURFACE_LUMINANCE = 0.179;
const TEXT_ON_LIGHT = '#111111';
const TEXT_ON_DARK = '#F2F2F2';

/**
 * Colour for text drawn straight on a surface rather than on a node's fill: edge labels and transparent-fill
 * nodes. The theme's `canvasText` / `exportText` win when set; otherwise it is derived from the surface
 * background's luminance. Undefined when that background is not a hex colour, because a surface that cannot be
 * read cannot be contrasted against: the caller keeps the theme's own colours instead of guessing.
 */
export function surfaceTextColor(theme: DiagramTheme, surface: Surface): string | undefined {
  const explicit = surface === 'export' ? theme.exportText : theme.canvasText;
  if (explicit !== undefined) {
    return explicit;
  }
  const rgb = parseHex(surfaceBackground(theme, surface));
  if (!rgb) {
    return undefined;
  }
  return relativeLuminance(rgb) > LIGHT_SURFACE_LUMINANCE ? TEXT_ON_LIGHT : TEXT_ON_DARK;
}
