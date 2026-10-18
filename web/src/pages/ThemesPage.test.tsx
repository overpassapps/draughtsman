// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import type { OrgTheme } from '../theme/orgTheme.ts';
import { ThemesPage } from './ThemesPage.tsx';

const admin: AuthUser = { id: 'usr_admin', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const editor: AuthUser = { ...admin, id: 'usr_editor', role: 'Editor' };

const DARK = { brand: '#c0392b', brandText: '#ffffff', surface: '#0a0e1a', surfaceRaised: '#151e33', border: '#202a42', text: '#f2f2f2', textMuted: '#9aa4b8', accent: '#e04832', danger: '#ff4d4f' };
const LIGHT = { brand: '#c0392b', brandText: '#ffffff', surface: '#ffffff', surfaceRaised: '#f4f6fa', border: '#d6dae4', text: '#151e33', textMuted: '#616b7b', accent: '#c0392b', danger: '#d4380d' };

function theme(overrides: Partial<OrgTheme> = {}): OrgTheme {
  return {
    id: 'acme',
    name: 'Acme',
    mode: 'dark',
    chrome: { dark: DARK, light: LIGHT, radius: 6, fonts: { ui: 'Inter, system-ui, sans-serif', mono: 'JetBrains Mono, monospace' } },
    diagram: { base: 'overpass', palette: {}, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } },
    updatedAt: '2026-09-30T10:00:00.000Z',
    isDefault: false,
    ...overrides,
  };
}

function url(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

const branding = { themeName: 'Overpass', themeProductName: 'Draughtsman', productName: 'Draughtsman', mode: 'dark', logo: null, logoLight: null, favicon: null, overridden: { productName: false, logo: false, logoLight: false, favicon: false } };

function stub(initial: OrgTheme[], calls: { method: string; url: string; body: unknown }[]): typeof fetch {
  let themes = initial;
  let defaultId: string | null = null;
  const state = (): Response => new Response(JSON.stringify({ defaultId, themes: themes.map((t) => ({ ...t, isDefault: t.id === defaultId })), bases: ['overpass', 'neutral', 'mono'] }));
  return (input, init) => {
    const target = url(input);
    const method = init?.method ?? 'GET';
    const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
    calls.push({ method, url: target, body });
    if (target === '/api/theme') {
      return Promise.resolve(new Response(JSON.stringify({ name: 'Overpass', productName: 'Draughtsman', logo: null, logoLight: null, favicon: null, mode: 'dark' })));
    }
    if (target === '/api/theme/chrome.css') {
      return Promise.resolve(new Response('/* css */'));
    }
    if (target === '/api/admin/branding') {
      return Promise.resolve(new Response(JSON.stringify(branding)));
    }
    if (target === '/api/admin/themes' && method === 'GET') {
      return Promise.resolve(state());
    }
    if (target === '/api/admin/themes' && method === 'POST') {
      const created = theme({ id: 'new-one', name: (body as { name: string }).name });
      themes = [...themes, created];
      return Promise.resolve(new Response(JSON.stringify(created), { status: 201 }));
    }
    if (target === '/api/admin/themes/default') {
      defaultId = (body as { id: string | null }).id;
      return Promise.resolve(state());
    }
    if (target.startsWith('/api/admin/themes/') && method === 'PUT') {
      const saved = { ...(body as OrgTheme), updatedAt: '2026-09-30T11:00:00.000Z' };
      themes = themes.map((t) => (t.id === saved.id ? saved : t));
      return Promise.resolve(new Response(JSON.stringify(saved)));
    }
    return Promise.resolve(new Response('{}', { status: 404 }));
  };
}

describe('ThemesPage', () => {
  let calls: { method: string; url: string; body: unknown }[];

  beforeEach(() => {
    calls = [];
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('shows a non-admin a message and asks the server for nothing', () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme()], calls)));
    render(<ThemesPage currentUser={editor} />);
    expect(screen.getByRole('alert').textContent).toContain('administrator');
    expect(calls).toEqual([]);
  });

  it('lists the shipped default and the organisation themes, with the default badged', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme(), theme({ id: 'globex', name: 'Globex', mode: 'light' })], calls)));
    render(<ThemesPage currentUser={admin} />);
    const list = await screen.findByRole('list', { name: 'Themes' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    // With no default chosen the shipped theme is the default: it carries the badge and the others offer Make default.
    expect(within(screen.getByTestId('theme-card-shipped')).getByText('Default')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Make default: Acme' })).toBeTruthy();
    expect(screen.getByText(/Light · diagrams start from/)).toBeTruthy();
  });

  it('creates a theme from a base and opens it for editing', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme()], calls)));
    render(<ThemesPage currentUser={admin} />);
    await screen.findByRole('list', { name: 'Themes' });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Create theme' }).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('New theme name'), { target: { value: 'Globex' } });
    fireEvent.change(screen.getByLabelText('Start from'), { target: { value: 'acme' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create theme' }));
    expect(await screen.findByRole('form', { name: 'Edit theme Globex' })).toBeTruthy();
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Globex', baseId: 'acme' });
  });

  it('warns inline about low contrast without blocking the save, then saves the draft', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme()], calls)));
    render(<ThemesPage currentUser={admin} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Acme' }));
    const save = await screen.findByRole('button', { name: 'Save theme' });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    // No warning about text on the surface while the colours are the shipped ones.
    expect(screen.queryByText(/Contrast: text on surface/)).toBeNull();
    fireEvent.change(screen.getByLabelText('Text (dark)'), { target: { value: '#2a3550' } });
    expect((await screen.findAllByText(/Contrast: text on surface is \d\.\d\d:1, below WCAG AA 4\.5:1/)).length).toBeGreaterThan(0);
    // The previews show the draft, not the saved value.
    expect(screen.getByTestId('chrome-preview').getAttribute('style')).toContain('--chrome-text: #2a3550');

    // Poor contrast is a warning: Save stays enabled and sends the draft.
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save theme' }).disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Save theme' }));
    await waitFor(() => { expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/admin/themes/acme')).toBe(true); });
    const put = calls.find((c) => c.method === 'PUT');
    expect((put?.body as OrgTheme).chrome.dark.text).toBe('#2a3550');
    expect(await screen.findByText('Saved Acme.')).toBeTruthy();
  });

  it('blocks Save for a colour that is not hex and says which field', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme()], calls)));
    render(<ThemesPage currentUser={admin} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Acme' }));
    await screen.findByRole('button', { name: 'Save theme' });
    fireEvent.change(screen.getByLabelText('Accent (dark)'), { target: { value: 'red' } });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Save theme' }).disabled).toBe(true);
    expect(screen.getAllByText('Use a hex colour such as #e04832.').length).toBeGreaterThan(0);
    // Putting a valid colour back (and only that) makes the draft valid again... but it is now the original, so nothing is dirty.
    fireEvent.change(screen.getByLabelText('Accent (dark)'), { target: { value: '#e04832' } });
    expect(screen.queryByText('Use a hex colour such as #e04832.')).toBeNull();
    expect(calls.some((c) => c.method === 'PUT')).toBe(false);
  });

  it('switches the colour set and previews the light set', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub([theme()], calls)));
    render(<ThemesPage currentUser={admin} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Edit Acme' }));
    await screen.findByRole('button', { name: 'Save theme' });
    expect(screen.getByTestId('chrome-preview').getAttribute('data-set')).toBe('dark');
    fireEvent.click(screen.getByLabelText('Light set'));
    expect(screen.getByTestId('chrome-preview').getAttribute('data-set')).toBe('light');
    expect(screen.getByTestId('chrome-preview').getAttribute('style')).toContain('--chrome-surface: #ffffff');
  });

  it('makes a theme the default and restyles the app, and can return to the shipped default', async () => {
    const css = vi.fn();
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((input, init) => { if (url(input) === '/api/theme/chrome.css') { css(); } return stub([theme()], calls)(input, init); }));
    render(<ThemesPage currentUser={admin} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Make default: Acme' }));
    expect(await screen.findByText('Everyone now receives Acme.')).toBeTruthy();
    expect(calls.find((c) => c.url === '/api/admin/themes/default')?.body).toEqual({ id: 'acme' });
    // The stylesheet is re-read so the shell restyles without a reload.
    await waitFor(() => { expect(css).toHaveBeenCalled(); });

    fireEvent.click(await screen.findByRole('button', { name: 'Use Overpass default' }));
    await act(async () => { await Promise.resolve(); });
    await waitFor(() => { expect(calls.filter((c) => c.url === '/api/admin/themes/default').at(-1)?.body).toEqual({ id: null }); });
  });
});
