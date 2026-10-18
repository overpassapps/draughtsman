/**
 * The admin organisation-themes API (docs/api.md "## Themes") and the read every signed-in user makes of the default.
 * Same style as brandingClient.ts: one small function per call, `fetchImpl` last for test injection, every mutating
 * call attaches the CSRF header. A refusal carries the server's Problem+JSON detail, and for a 422 every field-level
 * problem, so the page can say what to fix.
 */

import { csrfHeaders } from './csrf.ts';
import type { OrgDiagram, OrgTheme, PublicTheme } from '../theme/orgTheme.ts';

export interface ThemeProblem {
  field: string;
  message: string;
}

export class ThemesError extends Error {
  readonly status: number;
  readonly problems: ThemeProblem[];

  constructor(status: number, detail: string, problems: ThemeProblem[] = []) {
    super(detail);
    this.name = 'ThemesError';
    this.status = status;
    this.problems = problems;
  }
}

/** Body of GET /api/admin/themes and of every call that changes the set. */
export interface AdminThemes {
  defaultId: string | null;
  themes: OrgTheme[];
  bases: string[];
}

/** Body of GET /api/themes. */
export interface PublicThemes {
  defaultId: string | null;
  themes: PublicTheme[];
}

interface ProblemBody {
  title?: string;
  detail?: string;
  problems?: ThemeProblem[];
}

async function refusal(response: Response, fallback: string): Promise<ThemesError> {
  try {
    const problem = (await response.json()) as ProblemBody;
    return new ThemesError(response.status, problem.detail ?? problem.title ?? fallback, problem.problems ?? []);
  } catch {
    return new ThemesError(response.status, fallback);
  }
}

async function read<T>(response: Response, fallback: string): Promise<T> {
  if (!response.ok) {
    throw await refusal(response, fallback);
  }
  return (await response.json()) as T;
}

/**
 * The server omits null members from its JSON (the same convention as every other response), so an absent diagram colour
 * or font arrives as `undefined`. The page's own types say `null`, and a strict `!== null` check downstream would let
 * `undefined` through as a value, so every response is normalised here at the fetch boundary.
 */
export function normaliseDiagram(raw: Partial<OrgDiagram> & { fonts?: Partial<OrgDiagram['fonts']> }): OrgDiagram {
  return {
    base: raw.base ?? 'overpass',
    palette: raw.palette ?? {},
    canvasBackground: raw.canvasBackground ?? null,
    exportBackground: raw.exportBackground ?? null,
    gridColor: raw.gridColor ?? null,
    fonts: { label: raw.fonts?.label ?? null, mono: raw.fonts?.mono ?? null },
  };
}

function normaliseTheme(raw: OrgTheme): OrgTheme {
  return { ...raw, diagram: normaliseDiagram(raw.diagram) };
}

function normaliseAdmin(raw: AdminThemes): AdminThemes {
  return { ...raw, defaultId: raw.defaultId ?? null, themes: raw.themes.map(normaliseTheme) };
}

function normalisePublic(raw: PublicThemes): PublicThemes {
  // Not an array: a server that does not know this endpoint answered with something else, which is treated as no themes.
  return { defaultId: raw.defaultId ?? null, themes: Array.isArray(raw.themes) ? raw.themes.map((t): PublicTheme => ({ ...t, diagram: normaliseDiagram(t.diagram) })) : [] };
}

const JSON_HEADERS = { 'Content-Type': 'application/json', Accept: 'application/json' };

export async function fetchAdminThemes(fetchImpl: typeof fetch = fetch): Promise<AdminThemes> {
  return normaliseAdmin(await read<AdminThemes>(await fetchImpl('/api/admin/themes', { headers: { Accept: 'application/json' } }), 'Could not read the themes.'));
}

/** The default and every theme's diagram half, for the editor's Theme menu. Any signed-in user may read it. */
export async function fetchPublicThemes(fetchImpl: typeof fetch = fetch): Promise<PublicThemes> {
  return normalisePublic(await read<PublicThemes>(await fetchImpl('/api/themes', { headers: { Accept: 'application/json' } }), 'Could not read the themes.'));
}

/** Creates a theme from `baseId`: undefined or 'overpass' is the shipped default, else a copy of that theme. */
export async function createTheme(name: string, baseId: string | undefined, fetchImpl: typeof fetch = fetch): Promise<OrgTheme> {
  const response = await fetchImpl('/api/admin/themes', {
    method: 'POST',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify({ name, baseId: baseId ?? null }),
  });
  return normaliseTheme(await read<OrgTheme>(response, 'Could not create the theme.'));
}

export async function saveTheme(theme: OrgTheme, fetchImpl: typeof fetch = fetch): Promise<OrgTheme> {
  const response = await fetchImpl(`/api/admin/themes/${encodeURIComponent(theme.id)}`, {
    method: 'PUT',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify(theme),
  });
  return normaliseTheme(await read<OrgTheme>(response, 'Could not save the theme.'));
}

export async function duplicateTheme(id: string, fetchImpl: typeof fetch = fetch): Promise<OrgTheme> {
  const response = await fetchImpl(`/api/admin/themes/${encodeURIComponent(id)}/duplicate`, {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return normaliseTheme(await read<OrgTheme>(response, 'Could not duplicate the theme.'));
}

export async function deleteTheme(id: string, fetchImpl: typeof fetch = fetch): Promise<AdminThemes> {
  const response = await fetchImpl(`/api/admin/themes/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return normaliseAdmin(await read<AdminThemes>(response, 'Could not delete the theme.'));
}

/** Makes `id` the theme every user receives; null returns to the shipped Overpass default. */
export async function setDefaultTheme(id: string | null, fetchImpl: typeof fetch = fetch): Promise<AdminThemes> {
  const response = await fetchImpl('/api/admin/themes/default', {
    method: 'PUT',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify({ id }),
  });
  return normaliseAdmin(await read<AdminThemes>(response, 'Could not change the default theme.'));
}

/** Adds a theme from the text of a theme file (the same YAML `exportThemeUrl` downloads). */
export async function importTheme(yaml: string, fetchImpl: typeof fetch = fetch): Promise<OrgTheme> {
  const response = await fetchImpl('/api/admin/themes/import', {
    method: 'POST',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify({ yaml }),
  });
  return normaliseTheme(await read<OrgTheme>(response, 'Could not import the theme file.'));
}

/** The download link for a theme file; a plain GET the browser follows with its session cookie. */
export function exportThemeUrl(id: string): string {
  return `/api/admin/themes/${encodeURIComponent(id)}/export`;
}

/** A draft theme read from a site's CSS, and why it is what it is. Nothing is saved; see `extractTheme`. */
export interface ThemeDraft {
  draft: OrgTheme;
  /** One line per colour chosen: where it came from. */
  reasons: string[];
  /** Every colour the extractor moved to pass the contrast checks, with from, to and why. */
  adjustments: string[];
  source: 'css' | 'url';
}

/**
 * Asks the server to read a draft theme from pasted CSS (or a page's HTML), or, when the operator has enabled it, from a web address.
 * Saves nothing: the draft becomes a theme only through createTheme and saveTheme.
 */
export async function extractTheme(input: { css: string } | { url: string }, fetchImpl: typeof fetch = fetch): Promise<ThemeDraft> {
  const response = await fetchImpl('/api/admin/themes/extract', {
    method: 'POST',
    headers: { ...JSON_HEADERS, ...csrfHeaders() },
    body: JSON.stringify(input),
  });
  const raw = await read<{ draft: OrgTheme; reasons?: string[]; adjustments?: string[]; source?: string }>(response, 'Could not read colours from that.');
  return { draft: normaliseTheme(raw.draft), reasons: raw.reasons ?? [], adjustments: raw.adjustments ?? [], source: raw.source === 'url' ? 'url' : 'css' };
}

/**
 * Whether the operator has turned on fetching a page by address (themes.fetchFromUrl.enabled). The paste-CSS route never needs it. Any
 * failure reads as off, so the page offers only what always works.
 */
export async function fetchExtractOptions(fetchImpl: typeof fetch = fetch): Promise<{ url: boolean }> {
  try {
    const response = await fetchImpl('/api/admin/themes/extract/options', { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      return { url: false };
    }
    const raw = (await response.json()) as { url?: boolean };
    return { url: raw.url === true };
  } catch {
    return { url: false };
  }
}
