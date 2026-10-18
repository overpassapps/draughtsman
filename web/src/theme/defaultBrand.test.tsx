// @vitest-environment jsdom
/**
 * The Draughtsman mark is only the DEFAULT: it shows where an organisation has set nothing, and where it has set a logo or a
 * product name the organisation's own brand is all that is drawn - never the mark in its place and never beside it. Each
 * surface is tested in both directions (default shows; organisation set, it does not).
 */
import { render, screen, waitFor } from '@testing-library/react';
import { AppShell } from '../app/AppShell.tsx';
import { AboutPage } from '../pages/AboutPage.tsx';
import { FirstRunPage } from '../pages/FirstRunPage.tsx';
import { LoginPage } from '../pages/LoginPage.tsx';
import { ResetPasswordPage } from '../pages/ResetPasswordPage.tsx';
import { forgetResetToken } from '../model/resetToken.ts';
import type { ChromeThemeManifest } from './chromeTheme.ts';
import { usesDefaultBrand } from './defaultBrand.ts';

const USER = { id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Editor', createdAt: '2026-01-01T00:00:00.000Z', active: true } as const;
const ABOUT = { version: '0.1.0', buildDate: '2026-09-30', supportContact: 'draughtsman@overpass.co.uk', noticesUrl: '/THIRD-PARTY-NOTICES.txt' };

/**
 * What the server sends when the organisation has set nothing (the shipped theme names no logo, and the product name is the
 * default). The server omits every member that is null, so there are no `logo` or `favicon` keys at all.
 */
const DEFAULT_THEME = { name: 'Overpass', productName: 'Draughtsman', mode: 'dark' } as const;
/** What it sends once an organisation has uploaded a logo, or named its own product. */
const ORG_LOGO_THEME = { ...DEFAULT_THEME, productName: 'Acme Diagrams', logo: '/api/theme/logo', logoLight: '/api/theme/logo-light' } as const;
const ORG_NAME_ONLY_THEME = { ...DEFAULT_THEME, productName: 'Acme Diagrams' } as const;
const ORG_LOGO_ONLY_THEME = { ...DEFAULT_THEME, logo: '/api/theme/logo', logoLight: '/api/theme/logo-light' } as const;

function stubServer(theme: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url === '/api/theme') {
        return Promise.resolve(new Response(JSON.stringify(theme), { status: 200 }));
      }
      if (url === '/api/about') {
        return Promise.resolve(new Response(JSON.stringify(ABOUT), { status: 200 }));
      }
      return Promise.resolve(new Response('{}', { status: 200 }));
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  forgetResetToken();
  window.history.replaceState(null, '', '/');
});

describe('usesDefaultBrand', () => {
  const manifest = (overrides: Partial<ChromeThemeManifest>): ChromeThemeManifest => ({ logo: null, logoLight: null, favicon: null, ...DEFAULT_THEME, ...overrides });

  it('is true only when there is no logo and the product name is the default', () => {
    expect(usesDefaultBrand(manifest({}))).toBe(true);
  });

  it('is false for an organisation logo, an organisation product name, or both', () => {
    expect(usesDefaultBrand(manifest({ logo: '/api/theme/logo' }))).toBe(false);
    expect(usesDefaultBrand(manifest({ productName: 'Acme Diagrams' }))).toBe(false);
    expect(usesDefaultBrand(manifest({ logo: '/api/theme/logo', productName: 'Acme Diagrams' }))).toBe(false);
  });

  it('is false while the theme has not loaded, so the mark never flashes up before an organisation’s own logo', () => {
    expect(usesDefaultBrand(undefined)).toBe(false);
  });
});

describe('the app shell header', () => {
  const mount = () => render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);

  it('draws the Draughtsman mark beside the product name when the organisation has set nothing', async () => {
    stubServer(DEFAULT_THEME);
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('.shell-brand-logo')).not.toBeNull();
    });
    const mark = container.querySelector('.shell-brand-logo');
    expect(mark?.getAttribute('src')).toBe('/brand/mark-on-dark.svg');
    // Decorative: the product name beside it is the accessible name, so a screen reader says it once.
    expect(mark?.getAttribute('alt')).toBe('');
    expect(container.querySelector('.shell-brand')?.textContent).toBe('Draughtsman');
  });

  it('draws only the organisation’s own logo and name when one is set', async () => {
    stubServer(ORG_LOGO_THEME);
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('.shell-brand-logo')?.getAttribute('src')).toBe('/api/theme/logo-light');
    });
    expect(container.querySelectorAll('.shell-brand img')).toHaveLength(1);
    expect(container.querySelector('img[src^="/brand/"]')).toBeNull();
    expect(container.querySelector('.shell-brand')?.textContent).toBe('Acme Diagrams');
  });

  it('draws no mark beside an organisation’s product name, with or without a logo of its own', async () => {
    stubServer(ORG_NAME_ONLY_THEME);
    const named = mount();
    await waitFor(() => {
      expect(named.container.querySelector('.shell-brand')?.textContent).toBe('Acme Diagrams');
    });
    expect(named.container.querySelector('img')).toBeNull();
    named.unmount();

    stubServer(ORG_LOGO_ONLY_THEME);
    const logoOnly = mount();
    await waitFor(() => {
      expect(logoOnly.container.querySelector('.shell-brand-logo')?.getAttribute('src')).toBe('/api/theme/logo-light');
    });
    expect(logoOnly.container.querySelector('img[src^="/brand/"]')).toBeNull();
  });

  it('uses the navy-on-light mark on a light theme', async () => {
    stubServer({ ...DEFAULT_THEME, mode: 'light' });
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('.shell-brand-logo')?.getAttribute('src')).toBe('/brand/mark.svg');
    });
  });

  it('lets the browser choose between the two on a system theme, as the theme’s own colours do', async () => {
    stubServer({ ...DEFAULT_THEME, mode: 'system' });
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('picture source')).not.toBeNull();
    });
    expect(container.querySelector('picture source')?.getAttribute('media')).toBe('(prefers-color-scheme: light)');
    expect(container.querySelector('picture source')?.getAttribute('srcset')).toBe('/brand/mark.svg');
    expect(container.querySelector('picture img')?.getAttribute('src')).toBe('/brand/mark-on-dark.svg');
  });
});

describe.each([
  ['sign-in page', () => render(<LoginPage onSignedIn={() => undefined} />)],
  ['first-run page', () => render(<FirstRunPage onSignedIn={() => undefined} />)],
  [
    'password-reset page',
    () => {
      window.history.replaceState(null, '', '/reset-password#token=dpr_the_secret');
      return render(<ResetPasswordPage onFinished={() => undefined} />);
    },
  ],
])('the %s', (_name, mount) => {
  it('draws the Draughtsman mark above the product name when the organisation has set nothing', async () => {
    stubServer(DEFAULT_THEME);
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('.auth-logo')).not.toBeNull();
    });
    expect(container.querySelector('.auth-logo')?.getAttribute('src')).toBe('/brand/mark-on-dark.svg');
    expect(container.querySelector('.auth-logo')?.getAttribute('alt')).toBe('');
    expect(screen.getByText('Draughtsman', { selector: '.auth-product' })).toBeTruthy();
  });

  it('shows only the organisation’s own logo and name when one is set', async () => {
    stubServer(ORG_LOGO_THEME);
    const { container } = mount();

    await waitFor(() => {
      expect(container.querySelector('.auth-logo')?.getAttribute('src')).toBe('/api/theme/logo-light');
    });
    expect(container.querySelectorAll('img')).toHaveLength(1);
    expect(container.querySelector('img[src^="/brand/"]')).toBeNull();
    expect(screen.getByText('Acme Diagrams', { selector: '.auth-product' })).toBeTruthy();
  });

  it('shows no mark beside an organisation’s product name when it has no logo of its own', async () => {
    stubServer(ORG_NAME_ONLY_THEME);
    const { container } = mount();

    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams', { selector: '.auth-product' })).toBeTruthy();
    });
    expect(container.querySelector('img')).toBeNull();
  });
});

describe('the About page', () => {
  const mount = () => render(<AboutPage currentUser={USER} />);

  it('shows the lockup as the product’s identity, with the plain-text "by Overpass" line, and no organisation block, when nothing is set', async () => {
    stubServer(DEFAULT_THEME);
    mount();

    const lockup = await screen.findByRole('img', { name: 'Draughtsman by Overpass' });
    expect(lockup.getAttribute('src')).toBe('/brand/lockup-dark.svg');
    expect(screen.getByRole('region', { name: 'The product' })).toBeTruthy();
    expect(screen.queryByRole('region', { name: /branding/ })).toBeNull();
  });

  it('shows the organisation’s branding in a block of its own, apart from the lockup, when it has set a logo and name', async () => {
    stubServer(ORG_LOGO_THEME);
    mount();

    const product = await screen.findByRole('region', { name: 'The product' });
    const organisation = screen.getByRole('region', { name: /branding/ });
    expect(product).not.toBe(organisation);
    expect(product.contains(organisation)).toBe(false);
    expect(product.querySelector('img')?.getAttribute('src')).toBe('/brand/lockup-dark.svg');
    expect(organisation.querySelector('img')?.getAttribute('src')).toBe('/api/theme/logo-light');
    expect(organisation.querySelector('img')?.getAttribute('alt')).toBe('Acme Diagrams logo');
    expect(organisation.textContent).toContain('Acme Diagrams');
    // The lockup is not in the organisation's block, and the organisation's logo is not in the product's.
    expect(organisation.querySelector('img[src^="/brand/"]')).toBeNull();
    expect(product.querySelector('img[src^="/api/"]')).toBeNull();
  });

  it('shows the organisation block for a product name alone, without a picture', async () => {
    stubServer(ORG_NAME_ONLY_THEME);
    mount();

    await screen.findByRole('region', { name: 'The product' });
    const organisation = screen.getByRole('region', { name: /branding/ });
    expect(organisation.querySelector('img')).toBeNull();
    expect(organisation.textContent).toContain('Acme Diagrams');
  });

  it('uses the navy lockup on a light theme', async () => {
    stubServer({ ...DEFAULT_THEME, mode: 'light' });
    mount();

    expect((await screen.findByRole('img', { name: 'Draughtsman by Overpass' })).getAttribute('src')).toBe('/brand/lockup-light.svg');
  });
});
