/**
 * Runtime chrome theme (spec section 8). The server compiles the configured chrome theme YAML to CSS custom
 * properties at startup and serves them at `/api/theme/chrome.css` with a small manifest at `/api/theme`, so an
 * instance admin can re-skin the shell without rebuilding `web/`. The product name, logo, light-on-dark logo and
 * favicon can additionally be overridden from the admin branding page (M3); the admin page dispatches
 * {@link BRANDING_CHANGED_EVENT} after a save so the shell picks them up without a reload. The build-time tokens in
 * `src/generated/tokens.css` (compiled from the repository's `themes/chrome.yaml` by web/scripts/build-tokens.ts)
 * stay the development default; a failed fetch leaves them in place, so the app never depends on the theme endpoint
 * being reachable.
 */

/** Body of GET /api/theme; see src/Draughtsman.Server/Endpoints/ChromeThemeEndpoints.cs. */
export interface ChromeThemeManifest {
  name: string;
  productName: string;
  logo: string | null;
  logoLight: string | null;
  /** The theme's header-only simplified mark (plain and light-on-dark); absent or null when it names none or an admin uploaded a logo. */
  logoCompact?: string | null;
  logoCompactLight?: string | null;
  favicon: string | null;
  mode: 'dark' | 'light' | 'system';
}

/** A window event the branding admin page fires after a change, so mounted brand marks refetch without a reload. */
export const BRANDING_CHANGED_EVENT = 'draughtsman:branding-changed';

let manifest: Promise<ChromeThemeManifest | undefined> | undefined;

/**
 * Bumped by every {@link refreshChromeTheme}. The server sends the brand images `no-store` under fixed paths, but an
 * `<img>` whose `src` string has not changed is not refetched, so a re-uploaded logo would keep its old picture until
 * a reload; once branding has changed in this page load, every image path in the manifest carries `?v=<revision>`.
 */
let revision = 0;

function withRevision(theme: ChromeThemeManifest): ChromeThemeManifest {
  // The server omits every member that is null (docs/api.md), so a theme with no logo or favicon sends no such keys: they are
  // null from here on, as the type says, or `theme.favicon ?? theme.logo` would be undefined and an icon `href` "undefined".
  const base: ChromeThemeManifest = { ...theme, logo: theme.logo ?? null, logoLight: theme.logoLight ?? null, favicon: theme.favicon ?? null };
  if (revision === 0) {
    return base;
  }
  const bust = (path: string | null | undefined): string | null | undefined => (path ? `${path}?v=${String(revision)}` : path);
  const next: ChromeThemeManifest = { ...base, logo: bust(base.logo) ?? null, logoLight: bust(base.logoLight) ?? null, favicon: bust(base.favicon) ?? null };
  // The compact marks are optional: set them only when the server sent the key.
  if (theme.logoCompact !== undefined) {
    next.logoCompact = bust(theme.logoCompact) ?? null;
  }
  if (theme.logoCompactLight !== undefined) {
    next.logoCompactLight = bust(theme.logoCompactLight) ?? null;
  }
  return next;
}

/** Fetches the manifest once per page load; resolves undefined when the server is unreachable or answers with an error. */
function loadChromeThemeManifest(fetchImpl: typeof fetch): Promise<ChromeThemeManifest | undefined> {
  manifest ??= fetchImpl('/api/theme', { headers: { Accept: 'application/json' } })
    .then(async (response) => (response.ok ? withRevision((await response.json()) as ChromeThemeManifest) : undefined))
    .catch(() => undefined);
  return manifest;
}

/**
 * A fresh (uncached) read of the manifest, for a page that renders the instance's own brand directly rather than
 * relying on the once-per-load cache above - the login and first-run cards (design/atlas/login-admin.md: "the
 * instance logo and product name from the chrome theme"). Anonymous endpoint, so this is safe to call before sign-in.
 */
export async function fetchChromeThemeManifest(fetchImpl: typeof fetch = fetch): Promise<ChromeThemeManifest | undefined> {
  try {
    const response = await fetchImpl('/api/theme', { headers: { Accept: 'application/json' } });
    return response.ok ? withRevision((await response.json()) as ChromeThemeManifest) : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The logo to draw for the theme's active surface. A dark surface (the theme's mode is `dark`, or `system`, whose
 * primary set is dark - see ChromeThemeCss) uses the light-on-dark variant, falling back to the single logo when
 * none was uploaded; a light surface uses the plain logo.
 */
export function brandLogo(manifest: ChromeThemeManifest): string | null {
  const dark = manifest.mode !== 'light';
  return (dark ? manifest.logoLight ?? manifest.logo : manifest.logo) ?? null;
}

/**
 * The logo for the app header bar, where a wide wordmark's hairlines vanish at 24 to 32 CSS px. It is the theme's
 * compact mark for the active surface when the theme provides one, else exactly {@link brandLogo}. The server drops
 * the compact marks whenever an admin has uploaded a logo, so an uploaded logo is drawn as it is. The login,
 * first-run and About pages keep {@link brandLogo}.
 */
export function brandHeaderLogo(manifest: ChromeThemeManifest): string | null {
  const dark = manifest.mode !== 'light';
  const compact = dark ? manifest.logoCompactLight ?? manifest.logoCompact : manifest.logoCompact;
  return compact ?? brandLogo(manifest);
}

/** Overrides the build-time chrome tokens with the server's, then applies the product name and favicon. */
export async function applyChromeTheme(fetchImpl: typeof fetch = fetch): Promise<void> {
  try {
    const response = await fetchImpl('/api/theme/chrome.css', { headers: { Accept: 'text/css' } });
    if (response.ok) {
      document.querySelector('style[data-chrome-theme="runtime"]')?.remove();
      const style = document.createElement('style');
      style.dataset.chromeTheme = 'runtime';
      style.textContent = await response.text();
      document.head.append(style);
    }
  } catch {
    // Keep the build-time tokens; a theme endpoint that is down is not a broken app.
  }

  const theme = await loadChromeThemeManifest(fetchImpl);
  if (theme === undefined) {
    return;
  }

  document.title = theme.productName;
  const icon = theme.favicon ?? theme.logo;
  if (icon !== null) {
    applyOrganisationIcon(icon);
  } else {
    restoreProductIcons();
  }
}

const ORGANISATION_ICON_ATTRIBUTE = 'data-organisation-icon';

/** The icons web/index.html ships for the product itself, taken out of the head while an organisation's favicon is in force. */
let productIcons: HTMLLinkElement[] | undefined;

/**
 * Swaps the product's own icons (web/index.html: the Draughtsman SVG, its 16 and 32 px PNG fallbacks and the apple
 * touch icon) for the organisation's favicon. One `<link rel="icon">` remains, with none of the old `type`/`sizes`,
 * or the browser would still pick a Draughtsman fallback; the apple touch icon goes too, so a home-screen shortcut
 * does not carry the product's mark over an organisation's own brand. A theme with no favicon and no logo never gets
 * here, so the product's icons stay in place by default.
 */
function applyOrganisationIcon(icon: string): void {
  productIcons ??= [...document.querySelectorAll<HTMLLinkElement>('link[rel="icon"], link[rel="apple-touch-icon"]')].filter(
    (link) => !link.hasAttribute(ORGANISATION_ICON_ATTRIBUTE),
  );
  productIcons.forEach((link) => {
    link.remove();
  });
  let link = document.querySelector<HTMLLinkElement>(`link[${ORGANISATION_ICON_ATTRIBUTE}]`);
  if (link === null) {
    link = document.createElement('link');
    link.rel = 'icon';
    link.setAttribute(ORGANISATION_ICON_ATTRIBUTE, '');
    document.head.append(link);
  }
  link.href = icon;
}

/** Puts the product's icons back after the organisation's favicon was removed (Branding, "Reset"), without a reload. */
function restoreProductIcons(): void {
  if (productIcons === undefined) {
    return;
  }
  document.querySelector(`link[${ORGANISATION_ICON_ATTRIBUTE}]`)?.remove();
  for (const link of productIcons) {
    if (!link.isConnected) {
      document.head.append(link);
    }
  }
}

/**
 * Drops the once-per-load manifest cache and re-applies it, for after the branding admin page saves. Mounted brand
 * marks (the app shell, the login card) also listen for {@link BRANDING_CHANGED_EVENT} and refetch themselves.
 */
export async function refreshChromeTheme(fetchImpl: typeof fetch = fetch): Promise<void> {
  manifest = undefined;
  revision += 1;
  await applyChromeTheme(fetchImpl);
  window.dispatchEvent(new Event(BRANDING_CHANGED_EVENT));
}
