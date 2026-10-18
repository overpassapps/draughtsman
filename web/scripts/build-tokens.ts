/**
 * Theme token compiler.
 *
 * Reads themes/chrome.yaml and themes/diagram-*.yaml from the repository root and writes:
 *   src/generated/tokens.css         CSS custom properties for the chrome (light and dark) and every diagram theme
 *   src/generated/diagram-themes.ts  the diagram themes as typed data: per-kind node and edge defaults
 *
 * Runs before `vite`, `tsc`, `eslint` and `vitest` (see package.json) so the generated files always match the
 * YAML; they are git-ignored. Contrast failures against WCAG AA are reported as warnings and never fail the
 * build (spec section 8): a theme with poor contrast is a support conversation, not an outage.
 *
 * Runs under Node's type stripping (`node --experimental-strip-types`), so keep to erasable TypeScript: no enums,
 * no parameter properties, no namespaces.
 */

import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse as parseYaml } from 'yaml';
import type { DiagramTheme, EdgeKindStyle, NodeKindStyle } from '../src/theme/diagramTheme.ts';
import { surfaceTextColor } from '../src/theme/resolveStyle.ts';
import { chromeContrastIssues, diagramContrastIssues } from '../src/theme/themeContrast.ts';

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const themesDir = join(webRoot, '..', 'themes');
const outDir = join(webRoot, 'src', 'generated');

type ChromeMode = 'dark' | 'light' | 'system';

interface ChromeTheme {
  name: string;
  productName: string;
  /** Optional: the shipped theme names none, so the app's own mark stands in (web/public). Validated, never emitted. */
  logo?: string;
  mode: ChromeMode;
  colors: Record<string, string>;
  light?: { colors?: Record<string, string> };
  dark?: { colors?: Record<string, string> };
  radius: number;
  font: Record<string, string>;
}

const DASHES = new Set(['solid', 'dashed', 'dotted']);
const ARROWS = new Set(['none', 'arrow', 'open', 'diamond', 'circle', 'one', 'many', 'zeroOrOne', 'oneOrMany', 'zeroOrMany', 'oneAndOnlyOne', 'inheritance', 'aggregation', 'composition']);

const warnings: string[] = [];

function warn(message: string): void {
  warnings.push(message);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function requireString(source: string, record: Record<string, unknown>, key: string): string {
  const value = record[key];
  if (typeof value !== 'string') {
    throw new Error(`${source}: "${key}" must be a string`);
  }
  return value;
}

function requireNumber(source: string, record: Record<string, unknown>, key: string): number {
  const value = record[key];
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`${source}: "${key}" must be a number`);
  }
  return value;
}

function optionalString(source: string, record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  if (value !== undefined && typeof value !== 'string') {
    throw new Error(`${source}: "${key}" must be a string`);
  }
  return value;
}

function requireStringMap(source: string, value: unknown, key: string): Record<string, string> {
  if (!isRecord(value)) {
    throw new Error(`${source}: "${key}" must be a map`);
  }
  const out: Record<string, string> = {};
  for (const [name, entry] of Object.entries(value)) {
    if (typeof entry !== 'string') {
      throw new Error(`${source}: "${key}.${name}" must be a string`);
    }
    out[name] = entry;
  }
  return out;
}

/** Turns camelCase token names into kebab-case CSS property suffixes: surfaceRaised -> surface-raised. */
function kebab(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}

function cssBlock(selector: string, declarations: [string, string][], indent = ''): string {
  const lines = declarations.map(([property, value]) => `${indent}  ${property}: ${value};`);
  return `${indent}${selector} {\n${lines.join('\n')}\n${indent}}\n`;
}

// ---- chrome -------------------------------------------------------------------------------------------------

function readChrome(): ChromeTheme {
  const source = 'themes/chrome.yaml';
  const raw: unknown = parseYaml(readFileSync(join(themesDir, 'chrome.yaml'), 'utf8'));
  if (!isRecord(raw)) {
    throw new Error(`${source}: expected a map at the top level`);
  }
  const mode = requireString(source, raw, 'mode');
  if (mode !== 'dark' && mode !== 'light' && mode !== 'system') {
    throw new Error(`${source}: "mode" must be dark, light or system`);
  }
  const companion = (key: 'light' | 'dark'): { colors?: Record<string, string> } | undefined => {
    const block = raw[key];
    if (block === undefined) {
      return undefined;
    }
    if (!isRecord(block)) {
      throw new Error(`${source}: "${key}" must be a map`);
    }
    return block.colors === undefined ? {} : { colors: requireStringMap(source, block.colors, `${key}.colors`) };
  };
  const light = companion('light');
  const dark = companion('dark');
  return {
    name: requireString(source, raw, 'name'),
    productName: requireString(source, raw, 'productName'),
    ...(raw.logo === undefined ? {} : { logo: requireString(source, raw, 'logo') }),
    mode,
    colors: requireStringMap(source, raw.colors, 'colors'),
    ...(light ? { light } : {}),
    ...(dark ? { dark } : {}),
    radius: requireNumber(source, raw, 'radius'),
    font: requireStringMap(source, raw.font, 'font'),
  };
}

interface ChromeModes {
  primary: ChromeMode;
  dark: Record<string, string>;
  light: Record<string, string>;
}

/** Resolves the per-mode colour sets: `colors` is the set for `mode` (dark when system), the other comes from the companion block. */
function resolveChromeModes(chrome: ChromeTheme): ChromeModes {
  const primary: ChromeMode = chrome.mode;
  const primaryIsLight = primary === 'light';
  const companionKey = primaryIsLight ? 'dark' : 'light';
  const companion = chrome[companionKey]?.colors;
  if (!companion) {
    warn(`themes/chrome.yaml: no "${companionKey}" block; ${companionKey} mode falls back to the primary colours`);
  }
  const companionColors = { ...chrome.colors, ...(companion ?? {}) };
  for (const key of Object.keys(chrome.colors)) {
    if (companion && !(key in companion)) {
      warn(`themes/chrome.yaml: "${companionKey}.colors.${key}" missing; using the primary value`);
    }
  }
  return primaryIsLight
    ? { primary, light: chrome.colors, dark: companionColors }
    : { primary, dark: chrome.colors, light: companionColors };
}

function chromeDeclarations(colors: Record<string, string>): [string, string][] {
  return Object.entries(colors).map(([name, value]) => [`--chrome-${kebab(name)}`, value]);
}

function checkChromeContrast(label: string, colors: Record<string, string>): void {
  for (const issue of chromeContrastIssues(colors)) {
    warn(`chrome (${label}): ${issue.message}`);
  }
}

function emitChromeCss(chrome: ChromeTheme): string {
  const modes = resolveChromeModes(chrome);
  checkChromeContrast('dark', modes.dark);
  checkChromeContrast('light', modes.light);

  const primaryColors = modes.primary === 'light' ? modes.light : modes.dark;
  const shared: [string, string][] = [
    ['--chrome-radius', `${String(chrome.radius)}px`],
    ...Object.entries(chrome.font).map(([name, value]): [string, string] => [`--chrome-font-${kebab(name)}`, value]),
    ['--chrome-color-scheme', modes.primary === 'system' ? 'light dark' : modes.primary],
  ];

  let css = '';
  css += cssBlock(':root', [...shared, ...chromeDeclarations(primaryColors)]);
  css += cssBlock(':root[data-theme-mode="dark"]', [['--chrome-color-scheme', 'dark'], ...chromeDeclarations(modes.dark)]);
  css += cssBlock(':root[data-theme-mode="light"]', [['--chrome-color-scheme', 'light'], ...chromeDeclarations(modes.light)]);
  if (modes.primary === 'system') {
    css += '@media (prefers-color-scheme: light) {\n';
    css += cssBlock(':root:not([data-theme-mode])', chromeDeclarations(modes.light), '  ');
    css += '}\n';
  }
  return css;
}

// ---- diagram ------------------------------------------------------------------------------------------------

function readNodeStyle(source: string, kind: string, raw: unknown, fallback: NodeKindStyle | undefined, font: string): NodeKindStyle {
  if (!isRecord(raw)) {
    throw new Error(`${source}: nodes.${kind} must be a map`);
  }
  const pick = <T>(key: string, check: (value: unknown) => value is T, fallbackValue: T | undefined): T => {
    const value = raw[key];
    if (value === undefined) {
      if (fallbackValue === undefined) {
        throw new Error(`${source}: nodes.${kind}.${key} is required (no default to inherit)`);
      }
      return fallbackValue;
    }
    if (!check(value)) {
      throw new Error(`${source}: nodes.${kind}.${key} has the wrong type`);
    }
    return value;
  };
  const isString = (value: unknown): value is string => typeof value === 'string';
  const isNumber = (value: unknown): value is number => typeof value === 'number';
  const isDash = (value: unknown): value is NodeKindStyle['dash'] => typeof value === 'string' && DASHES.has(value);
  return {
    fill: pick('fill', isString, fallback?.fill),
    stroke: pick('stroke', isString, fallback?.stroke),
    strokeWidth: pick('strokeWidth', isNumber, fallback?.strokeWidth),
    textColor: pick('textColor', isString, fallback?.textColor),
    font: pick('font', isString, fallback?.font ?? font),
    dash: pick('dash', isDash, fallback?.dash ?? 'solid'),
  };
}

function readEdgeStyle(source: string, kind: string, raw: unknown, fallback: EdgeKindStyle | undefined): EdgeKindStyle {
  if (!isRecord(raw)) {
    throw new Error(`${source}: edges.${kind} must be a map`);
  }
  const pick = <T>(key: string, check: (value: unknown) => value is T, fallbackValue: T | undefined): T => {
    const value = raw[key];
    if (value === undefined) {
      if (fallbackValue === undefined) {
        throw new Error(`${source}: edges.${kind}.${key} is required (no default to inherit)`);
      }
      return fallbackValue;
    }
    if (!check(value)) {
      throw new Error(`${source}: edges.${kind}.${key} has the wrong type`);
    }
    return value;
  };
  const isString = (value: unknown): value is string => typeof value === 'string';
  const isNumber = (value: unknown): value is number => typeof value === 'number';
  const isDash = (value: unknown): value is EdgeKindStyle['dash'] => typeof value === 'string' && DASHES.has(value);
  const isArrow = (value: unknown): value is EdgeKindStyle['arrowEnd'] => typeof value === 'string' && ARROWS.has(value);
  return {
    stroke: pick('stroke', isString, fallback?.stroke),
    strokeWidth: pick('strokeWidth', isNumber, fallback?.strokeWidth),
    dash: pick('dash', isDash, fallback?.dash ?? 'solid'),
    arrowStart: pick('arrowStart', isArrow, fallback?.arrowStart ?? 'none'),
    arrowEnd: pick('arrowEnd', isArrow, fallback?.arrowEnd ?? 'arrow'),
  };
}

function readDiagramTheme(file: string): DiagramTheme {
  const source = `themes/${file}`;
  const raw: unknown = parseYaml(readFileSync(join(themesDir, file), 'utf8'));
  if (!isRecord(raw)) {
    throw new Error(`${source}: expected a map at the top level`);
  }
  const fonts = requireStringMap(source, raw.fonts, 'fonts');
  const label = fonts.label;
  const mono = fonts.mono;
  if (label === undefined || mono === undefined) {
    throw new Error(`${source}: fonts.label and fonts.mono are required`);
  }
  if (!isRecord(raw.grid)) {
    throw new Error(`${source}: "grid" must be a map`);
  }
  const grid = {
    size: requireNumber(source, raw.grid, 'size'),
    color: requireString(source, raw.grid, 'color'),
    visible: raw.grid.visible === true,
  };
  if (!isRecord(raw.nodes) || !isRecord(raw.edges)) {
    throw new Error(`${source}: "nodes" and "edges" must be maps`);
  }
  if (raw.nodes.default === undefined || raw.edges.default === undefined) {
    throw new Error(`${source}: nodes.default and edges.default are required`);
  }
  const nodeDefault = readNodeStyle(source, 'default', raw.nodes.default, undefined, label);
  const nodes: Record<string, NodeKindStyle> & { default: NodeKindStyle } = { default: nodeDefault };
  for (const [kind, style] of Object.entries(raw.nodes)) {
    if (kind !== 'default') {
      nodes[kind] = readNodeStyle(source, kind, style, nodeDefault, label);
    }
  }
  const edgeDefault = readEdgeStyle(source, 'default', raw.edges.default, undefined);
  const edges: Record<string, EdgeKindStyle> & { default: EdgeKindStyle } = { default: edgeDefault };
  for (const [kind, style] of Object.entries(raw.edges)) {
    if (kind !== 'default') {
      edges[kind] = readEdgeStyle(source, kind, style, edgeDefault);
    }
  }
  const canvasText = optionalString(source, raw, 'canvasText');
  const exportText = optionalString(source, raw, 'exportText');
  return {
    name: requireString(source, raw, 'name'),
    description: typeof raw.description === 'string' ? raw.description : '',
    palette: requireStringMap(source, raw.palette, 'palette'),
    fonts: { label, mono },
    grid,
    canvasBackground: requireString(source, raw, 'canvasBackground'),
    exportBackground: requireString(source, raw, 'exportBackground'),
    ...(canvasText === undefined ? {} : { canvasText }),
    ...(exportText === undefined ? {} : { exportText }),
    nodes,
    edges,
  };
}

function checkDiagramContrast(theme: DiagramTheme): void {
  for (const issue of diagramContrastIssues(theme)) {
    warn(`diagram ${theme.name}: ${issue.message}`);
  }
}

function emitDiagramCss(theme: DiagramTheme): string {
  const declarations: [string, string][] = [
    ['--diagram-canvas-background', theme.canvasBackground],
    ['--diagram-export-background', theme.exportBackground],
    ['--diagram-canvas-text', surfaceTextColor(theme, 'canvas') ?? theme.nodes.default.textColor],
    ['--diagram-export-text', surfaceTextColor(theme, 'export') ?? theme.nodes.default.textColor],
    ['--diagram-grid-size', `${String(theme.grid.size)}px`],
    ['--diagram-grid-color', theme.grid.color],
    ['--diagram-grid-visible', theme.grid.visible ? '1' : '0'],
    ['--diagram-font-label', theme.fonts.label],
    ['--diagram-font-mono', theme.fonts.mono],
  ];
  for (const [name, value] of Object.entries(theme.palette)) {
    declarations.push([`--diagram-palette-${kebab(name)}`, value]);
  }
  for (const [kind, style] of Object.entries(theme.nodes)) {
    const prefix = `--diagram-node-${kebab(kind)}`;
    declarations.push([`${prefix}-fill`, style.fill], [`${prefix}-stroke`, style.stroke], [`${prefix}-text`, style.textColor]);
  }
  for (const [kind, style] of Object.entries(theme.edges)) {
    declarations.push([`--diagram-edge-${kebab(kind)}-stroke`, style.stroke]);
  }
  return cssBlock(`[data-diagram-theme="${theme.name}"]`, declarations);
}

function emitDiagramTs(themes: DiagramTheme[]): string {
  const quoted = themes.map((theme) => JSON.stringify(theme.name));
  const entries = themes.map((theme, i) => `  ${quoted[i] ?? ''}: ${JSON.stringify(theme, null, 2).replace(/\n/g, '\n  ')},`).join('\n');
  return [
    '// Generated by web/scripts/build-tokens.ts from themes/diagram-*.yaml. Do not edit; edit the YAML.',
    '',
    "import type { DiagramTheme } from '../theme/diagramTheme.ts';",
    '',
    `export type DiagramThemeName = ${quoted.join(' | ')};`,
    '',
    `export const diagramThemeNames: readonly DiagramThemeName[] = [${quoted.join(', ')}];`,
    '',
    `export const defaultDiagramThemeName: DiagramThemeName = ${quoted[0] ?? '""'};`,
    '',
    'export const diagramThemes: Record<DiagramThemeName, DiagramTheme> = {',
    entries,
    '};',
    '',
  ].join('\n');
}

// ---- main ---------------------------------------------------------------------------------------------------

function main(): void {
  const chrome = readChrome();
  const diagramFiles = readdirSync(themesDir)
    .filter((file) => /^diagram-.*\.ya?ml$/.test(file))
    .sort();
  // The shipped default goes first so it becomes defaultDiagramThemeName.
  diagramFiles.sort((a, b) => Number(b.includes('overpass')) - Number(a.includes('overpass')));
  const themes = diagramFiles.map(readDiagramTheme);
  if (themes.length === 0) {
    throw new Error('no themes/diagram-*.yaml files found');
  }
  for (const theme of themes) {
    checkDiagramContrast(theme);
  }

  let css = `/* Generated by web/scripts/build-tokens.ts from themes/*.yaml. Do not edit; edit the YAML. */\n\n`;
  css += `/* Chrome theme "${chrome.name}" (${chrome.productName}). Mode: ${chrome.mode}. */\n`;
  css += emitChromeCss(chrome);
  for (const theme of themes) {
    css += `\n/* Diagram theme "${theme.name}": ${theme.description} */\n`;
    css += emitDiagramCss(theme);
  }

  mkdirSync(outDir, { recursive: true });
  writeFileSync(join(outDir, 'tokens.css'), css);
  writeFileSync(join(outDir, 'diagram-themes.ts'), emitDiagramTs(themes));

  for (const message of warnings) {
    console.warn(`build-tokens: warning: ${message}`);
  }
  console.log(
    `build-tokens: wrote src/generated/tokens.css and src/generated/diagram-themes.ts (${String(themes.length)} diagram themes, ${String(warnings.length)} warnings)`,
  );
}

main();
