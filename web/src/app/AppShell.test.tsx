// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { AppShell } from './AppShell.tsx';

const USER = { id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true } as const;

function stubTheme(theme: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(() =>
      Promise.resolve(new Response(JSON.stringify({ name: 'T', productName: 'Acme', favicon: null, mode: 'dark', ...theme }), { status: 200 })),
    ),
  );
}

async function headerLogoSrc(): Promise<string | null> {
  const { container } = render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
  await waitFor(() => {
    expect(container.querySelector('.shell-brand-logo')).not.toBeNull();
  });
  return container.querySelector('.shell-brand-logo')?.getAttribute('src') ?? null;
}

describe('AppShell header logo', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('draws the theme compact mark when the theme provides one', async () => {
    stubTheme({ logo: '/api/theme/logo', logoLight: '/api/theme/logo-light', logoCompact: '/api/theme/logo-compact', logoCompactLight: '/api/theme/logo-compact-light' });
    expect(await headerLogoSrc()).toBe('/api/theme/logo-compact-light');
  });

  it('draws the normal logo when the theme has no compact mark (a client theme, or an uploaded logo)', async () => {
    stubTheme({ logo: '/api/theme/logo', logoLight: '/api/theme/logo-light', logoCompact: null, logoCompactLight: null });
    expect(await headerLogoSrc()).toBe('/api/theme/logo-light');
  });
});

describe('AppShell', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, '', '/');
  });

  it('asks for no licence and shows no notice bar, to an admin or anyone else', async () => {
    for (const role of ['Admin', 'Editor'] as const) {
      const calls: string[] = [];
      vi.stubGlobal(
        'fetch',
        vi.fn<typeof fetch>((input) => {
          calls.push(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
          return Promise.resolve(new Response(JSON.stringify({ name: 'T', productName: 'Acme', favicon: null, mode: 'dark' }), { status: 200 }));
        }),
      );
      const { unmount } = render(<AppShell user={{ ...USER, role }} onSignedOut={() => undefined}>{null}</AppShell>);
      await waitFor(() => { expect(calls.length).toBeGreaterThan(0); });
      expect(calls.some((url) => url.includes('licence'))).toBe(false);
      expect(screen.queryByRole('status')).toBeNull();
      unmount();
    }
  });
});

const ADMIN_LABELS = ['Users', 'Branding', 'Themes', 'AI settings', 'Email', 'Security', 'Status', 'Connection check'] as const;
const EDITOR = { ...USER, role: 'Editor' } as const;

function openAdminMenu(): HTMLElement {
  fireEvent.click(screen.getByRole('button', { name: 'Admin' }));
  const menu = document.getElementById('shell-admin-links');
  if (menu === null) {
    throw new Error('the Admin menu did not open');
  }
  return menu;
}

describe('AppShell Admin menu (QA P18)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('folds the admin pages into one Admin button, closed until asked', () => {
    stubTheme({});
    render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    const button = screen.getByRole('button', { name: 'Admin' });
    expect(button.getAttribute('aria-expanded')).toBe('false');
    for (const label of ADMIN_LABELS) {
      expect(screen.queryByRole('link', { name: label })).toBeNull();
    }

    const menu = openAdminMenu();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(within(menu).getAllByRole('link').map((link) => link.textContent)).toEqual([...ADMIN_LABELS]);
    expect(screen.getByRole('link', { name: 'Security' }).getAttribute('href')).toBe('/admin/security');
    expect(screen.getByRole('link', { name: 'AI settings' }).getAttribute('href')).toBe('/admin/ai');
    expect(screen.getByRole('link', { name: 'Email' }).getAttribute('href')).toBe('/admin/email');
  });

  it('closes on Escape and hands focus back to the button', () => {
    stubTheme({});
    render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    openAdminMenu();
    screen.getByRole('link', { name: 'Users' }).focus();
    fireEvent.keyDown(screen.getByRole('link', { name: 'Users' }), { key: 'Escape' });
    expect(screen.queryByRole('link', { name: 'Users' })).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Admin' }));
  });

  it('keeps Tokens and About on the bar for everyone, outside the Admin menu', () => {
    stubTheme({});
    render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.getByRole('link', { name: 'Tokens' }).getAttribute('href')).toBe('/admin/tokens');
    expect(screen.getByRole('link', { name: 'About' }).getAttribute('href')).toBe('/about');
    expect(screen.getByRole('link', { name: 'Documents' })).toBeTruthy();
  });

  it('shows an Editor no admin item anywhere: no Admin menu, none of its pages', () => {
    stubTheme({});
    render(<AppShell user={EDITOR} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.queryByRole('button', { name: 'Admin' })).toBeNull();
    for (const label of ADMIN_LABELS) {
      expect(screen.queryByRole('link', { name: label })).toBeNull();
    }
    expect(screen.getByRole('link', { name: 'Tokens' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'About' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'E' }).getAttribute('href')).toBe('/account');
  });
});

function stubNarrowViewport(): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({ matches: true, media: query, addEventListener: () => undefined, removeEventListener: () => undefined })),
  );
}

describe('AppShell compact layout (QA S3)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('folds every link into one Menu button under the breakpoint, and opens them as a list', () => {
    stubTheme({});
    stubNarrowViewport();
    render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    const toggle = screen.getByRole('button', { name: 'Menu' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('link', { name: 'Documents' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Sign out' })).toBeNull();

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    // One structure: the admin pages are listed inline under an "Admin" heading, not behind a second button.
    for (const label of ADMIN_LABELS) {
      expect(screen.getByRole('link', { name: label })).toBeTruthy();
    }
    expect(screen.queryByRole('button', { name: 'Admin' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeTruthy();
  });

  it('offers an Editor the same Menu without a single admin page', () => {
    stubTheme({});
    stubNarrowViewport();
    render(<AppShell user={EDITOR} onSignedOut={() => undefined}>{null}</AppShell>);
    fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
    for (const label of ADMIN_LABELS) {
      expect(screen.queryByRole('link', { name: label })).toBeNull();
    }
    expect(screen.queryByText('Admin')).toBeNull();
    expect(screen.getByRole('link', { name: 'Tokens' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'About' })).toBeTruthy();
  });
});

describe('AppShell skip link (QA S5)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('is the first link on the page and moves focus to the main landmark', () => {
    stubTheme({});
    render(
      <AppShell user={USER} onSignedOut={() => undefined}>
        <h1>Page</h1>
      </AppShell>,
    );
    const links = screen.getAllByRole('link');
    expect(links[0]?.textContent).toBe('Skip to main content');
    const skip = links[0];
    if (skip === undefined) {
      throw new Error('no links');
    }
    fireEvent.click(skip);
    // The shell supplies the one main landmark (pages and the editor render none of their own), and the link lands on it.
    expect(screen.getAllByRole('main')).toHaveLength(1);
    expect(document.activeElement).toBe(screen.getByRole('main'));
  });
});

describe('AppShell account and security links', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('links the signed-in name to the account page and offers the security log to admins only', () => {
    stubTheme({});
    const admin = render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.getByRole('link', { name: 'E' }).getAttribute('href')).toBe('/account');
    openAdminMenu();
    expect(screen.getByRole('link', { name: 'Security' }).getAttribute('href')).toBe('/admin/security');
    admin.unmount();

    render(<AppShell user={EDITOR} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.getByRole('link', { name: 'E' }).getAttribute('href')).toBe('/account');
    expect(screen.queryByRole('link', { name: 'Security' })).toBeNull();
  });
});

describe('AppShell Help links (PR-04, PR-05)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('gives everyone About, and only an administrator the Status page', () => {
    stubTheme({});
    const admin = render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.getByRole('link', { name: 'About' }).getAttribute('href')).toBe('/about');
    openAdminMenu();
    expect(screen.getByRole('link', { name: 'Status' }).getAttribute('href')).toBe('/admin/status');
    admin.unmount();

    render(<AppShell user={EDITOR} onSignedOut={() => undefined}>{null}</AppShell>);
    expect(screen.getByRole('link', { name: 'About' }).getAttribute('href')).toBe('/about');
    expect(screen.queryByRole('link', { name: 'Status' })).toBeNull();
  });
});
