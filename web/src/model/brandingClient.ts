/**
 * The admin branding settings (M3; docs/api.md "## Branding"). Same style as authClient.ts: one small function per
 * call, `fetchImpl` last for test injection, and every mutating call attaches the CSRF header because the SPA is
 * cookie-authenticated. The public read the shell uses is `GET /api/theme` (theme/chromeTheme.ts); these are the
 * admin-only writes and the state they return.
 */

import { csrfHeaders } from './csrf.ts';

export type BrandingSlot = 'logo' | 'logo-light' | 'favicon';

/** Which parts of the branding the admin has overridden, so the page can offer "reset to the theme default". */
export interface BrandingOverridden {
  productName: boolean;
  logo: boolean;
  logoLight: boolean;
  favicon: boolean;
}

/** Body of every branding response; see src/Draughtsman.Server/Endpoints/BrandingEndpoints.cs. */
export interface BrandingState {
  themeName: string;
  themeProductName: string;
  productName: string;
  mode: 'dark' | 'light' | 'system';
  logo: string | null;
  logoLight: string | null;
  favicon: string | null;
  overridden: BrandingOverridden;
}

/** Raised when the server refused a branding call with a Problem+JSON detail worth showing (400/413/415). */
export class BrandingError extends Error {
  readonly status: number;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = 'BrandingError';
    this.status = status;
  }
}

interface ProblemBody {
  title?: string;
  detail?: string;
}

async function problemDetail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as ProblemBody;
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

async function readState(response: Response, fallback: string): Promise<BrandingState> {
  if (!response.ok) {
    throw new BrandingError(response.status, await problemDetail(response, fallback));
  }
  return (await response.json()) as BrandingState;
}

/** The current branding and which parts are overridden (admin only). */
export async function fetchBranding(fetchImpl: typeof fetch = fetch): Promise<BrandingState> {
  const response = await fetchImpl('/api/admin/branding', { headers: { Accept: 'application/json' } });
  return readState(response, 'Could not read the branding settings.');
}

/** Stores the product name; blank resets it to the theme's own. */
export async function setProductName(productName: string, fetchImpl: typeof fetch = fetch): Promise<BrandingState> {
  const response = await fetchImpl('/api/admin/branding', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', ...csrfHeaders() },
    body: JSON.stringify({ productName }),
  });
  return readState(response, 'Could not save the product name.');
}

/** Uploads a PNG/SVG logo (or PNG/SVG/ICO favicon); the server decides the type by content. */
export async function uploadBrandingAsset(slot: BrandingSlot, file: File, fetchImpl: typeof fetch = fetch): Promise<BrandingState> {
  const form = new FormData();
  form.append('file', file);
  const response = await fetchImpl(`/api/admin/branding/${slot}`, {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
    body: form,
  });
  return readState(response, 'Could not upload the image.');
}

/** Removes one override, so it falls back to the theme file. */
export async function deleteBrandingAsset(slot: BrandingSlot, fetchImpl: typeof fetch = fetch): Promise<BrandingState> {
  const response = await fetchImpl(`/api/admin/branding/${slot}`, {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return readState(response, 'Could not remove the image.');
}

/** Removes every override, returning to the theme's own branding. */
export async function resetBranding(fetchImpl: typeof fetch = fetch): Promise<BrandingState> {
  const response = await fetchImpl('/api/admin/branding/reset', {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  return readState(response, 'Could not reset the branding.');
}
