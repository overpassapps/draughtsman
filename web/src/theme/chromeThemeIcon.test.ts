// @vitest-environment jsdom
/**
 * The favicon: web/index.html ships the Draughtsman icons, and they stay until the organisation supplies its own (a favicon,
 * or failing that a logo), which then replaces all of them - not just the first link, or the browser would still pick a
 * Draughtsman fallback - and goes back when the organisation removes it.
 */
import type { ChromeThemeManifest } from './chromeTheme.ts';

const PRODUCT_ICONS = `
  <link rel="icon" href="/favicon.svg" type="image/svg+xml" sizes="any" />
  <link rel="icon" href="/favicon-32.png" type="image/png" sizes="32x32" />
  <link rel="icon" href="/favicon-16.png" type="image/png" sizes="16x16" />
  <link rel="apple-touch-icon" href="/apple-touch-icon.png" />`;

function serve(theme: Partial<ChromeThemeManifest>): typeof fetch {
  // Like the server, which leaves out every member that is null: a theme with no logo or favicon sends neither key.
  const body = { name: 'T', productName: 'Draughtsman', mode: 'dark', ...theme };
  return vi.fn<typeof fetch>((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    return Promise.resolve(url === '/api/theme' ? new Response(JSON.stringify(body), { status: 200 }) : new Response('', { status: 404 }));
  });
}

function iconHrefs(): string[] {
  return [...document.head.querySelectorAll('link[rel="icon"], link[rel="apple-touch-icon"]')].map((link) => link.getAttribute('href') ?? '');
}

describe('the favicon', () => {
  beforeEach(() => {
    vi.resetModules();
    document.head.innerHTML = PRODUCT_ICONS;
  });

  it('stays the Draughtsman icon set when the organisation supplies none', async () => {
    const { applyChromeTheme } = await import('./chromeTheme.ts');
    await applyChromeTheme(serve({}));

    expect(iconHrefs()).toEqual(['/favicon.svg', '/favicon-32.png', '/favicon-16.png', '/apple-touch-icon.png']);
  });

  it('is replaced entirely by the organisation’s favicon, leaving no Draughtsman icon behind', async () => {
    const { applyChromeTheme } = await import('./chromeTheme.ts');
    await applyChromeTheme(serve({ favicon: '/api/theme/favicon' }));

    expect(iconHrefs()).toEqual(['/api/theme/favicon']);
    const link = document.head.querySelector('link[rel="icon"]');
    // None of the Draughtsman SVG's type or sizes carries over to an uploaded PNG or ICO.
    expect(link?.hasAttribute('type')).toBe(false);
    expect(link?.hasAttribute('sizes')).toBe(false);
  });

  it('falls back to the organisation’s logo when it has a logo but no favicon, as it always has', async () => {
    const { applyChromeTheme } = await import('./chromeTheme.ts');
    await applyChromeTheme(serve({ logo: '/api/theme/logo', logoLight: '/api/theme/logo-light' }));

    expect(iconHrefs()).toEqual(['/api/theme/logo']);
  });

  it('puts the Draughtsman icons back when the organisation removes its favicon', async () => {
    const { applyChromeTheme, refreshChromeTheme } = await import('./chromeTheme.ts');
    await applyChromeTheme(serve({ favicon: '/api/theme/favicon' }));
    expect(iconHrefs()).toEqual(['/api/theme/favicon']);

    await refreshChromeTheme(serve({}));

    expect(iconHrefs().sort()).toEqual(['/apple-touch-icon.png', '/favicon-16.png', '/favicon-32.png', '/favicon.svg']);
    expect(document.head.querySelector('link[data-organisation-icon]')).toBeNull();
  });

  it('changes the organisation’s favicon in place when it is replaced, without stacking links', async () => {
    const { applyChromeTheme, refreshChromeTheme } = await import('./chromeTheme.ts');
    await applyChromeTheme(serve({ favicon: '/api/theme/favicon' }));
    await refreshChromeTheme(serve({ favicon: '/api/theme/favicon' }));

    expect(document.head.querySelectorAll('link[rel="icon"]')).toHaveLength(1);
  });
});
