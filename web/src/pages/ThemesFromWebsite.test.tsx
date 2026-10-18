// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import type { OrgTheme } from '../theme/orgTheme.ts';
import { ThemesPage } from './ThemesPage.tsx';

const admin: AuthUser = { id: 'usr_admin', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };

const DARK = { brand: '#ff6a3d', brandText: '#0f172a', surface: '#0b1020', surfaceRaised: '#121a30', border: '#263150', text: '#e6e9f2', textMuted: '#98a2bd', accent: '#ff6a3d', danger: '#ff5c5c' };
const LIGHT = { brand: '#ff6a3d', brandText: '#0f172a', surface: '#ffffff', surfaceRaised: '#f5f6fa', border: '#dcdfe8', text: '#0b1020', textMuted: '#555d73', accent: '#b8400f', danger: '#d4380d' };

const DRAFT: OrgTheme = {
  id: 'theme-from-pasted-css',
  name: 'Theme from pasted CSS',
  mode: 'dark',
  chrome: { dark: DARK, light: LIGHT, radius: 6, fonts: { ui: 'Inter, system-ui, sans-serif', mono: 'JetBrains Mono, monospace' } },
  diagram: { base: 'overpass', palette: { red: '#e04832' }, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } },
  updatedAt: '1970-01-01T00:00:00.000Z',
  isDefault: false,
};

interface Call { method: string; url: string; body: unknown }

function address(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

const branding = { themeName: 'Overpass', themeProductName: 'Draughtsman', productName: 'Draughtsman', mode: 'dark', logo: null, logoLight: null, favicon: null, overridden: { productName: false, logo: false, logoLight: false, favicon: false } };

interface Options {
  extractFromUrl: boolean;
  /** What the server answers to a save of the draft. */
  saveStatus?: number;
  extractStatus?: number;
}

function stub(calls: Call[], options: Options): typeof fetch {
  let themes: OrgTheme[] = [{ ...DRAFT, id: 'acme', name: 'Acme' }];
  const list = (): Response => new Response(JSON.stringify({ defaultId: null, themes, bases: ['overpass', 'neutral', 'mono'] }));
  return (input, init) => {
    const target = address(input);
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
      return Promise.resolve(list());
    }
    if (target === '/api/admin/themes/extract/options') {
      return Promise.resolve(new Response(JSON.stringify({ url: options.extractFromUrl })));
    }
    if (target === '/api/admin/themes/extract') {
      if (options.extractStatus !== undefined) {
        return Promise.resolve(new Response(JSON.stringify({ title: 'No colours found', detail: 'No colours could be read from that CSS. Paste the stylesheet itself, or a page with its styles inline.' }), { status: options.extractStatus }));
      }
      const source = (body as { url?: string }).url === undefined ? 'css' : 'url';
      return Promise.resolve(new Response(JSON.stringify({
        draft: DRAFT,
        reasons: ['Dark surface #0b1020: the page background.', 'Dark brand #ff6a3d: a button background.'],
        adjustments: ['light accent #ff6a3d became #b8400f: it read at 2.9:1 against the surface, below the 4.5:1 WCAG AA asks for.'],
        source,
      })));
    }
    if (target === '/api/admin/themes' && method === 'POST') {
      const created = { ...DRAFT, id: 'from-a-site', name: (body as { name: string }).name };
      themes = [...themes, created];
      return Promise.resolve(new Response(JSON.stringify(created), { status: 201 }));
    }
    if (target === '/api/admin/themes/from-a-site' && method === 'PUT') {
      if (options.saveStatus !== undefined && options.saveStatus !== 200) {
        return Promise.resolve(new Response(JSON.stringify({ title: 'Invalid theme', detail: 'The theme was refused.', problems: [{ field: 'dark.brand', message: 'brand (dark) must be a hex colour such as #e04832.' }] }), { status: options.saveStatus }));
      }
      const saved = { ...(body as OrgTheme), updatedAt: '2026-10-05T10:00:00.000Z' };
      themes = themes.map((t) => (t.id === saved.id ? saved : t));
      return Promise.resolve(new Response(JSON.stringify(saved)));
    }
    if (target === '/api/admin/themes/from-a-site' && method === 'DELETE') {
      themes = themes.filter((t) => t.id !== 'from-a-site');
      return Promise.resolve(list());
    }
    return Promise.resolve(new Response('{}', { status: 404 }));
  };
}

async function open(options: Options, calls: Call[]): Promise<void> {
  vi.stubGlobal('fetch', vi.fn<typeof fetch>(stub(calls, options)));
  render(<ThemesPage currentUser={admin} />);
  await screen.findByRole('region', { name: 'From a website' });
  await waitFor(() => { expect(calls.some((c) => c.url === '/api/admin/themes/extract/options')).toBe(true); });
  await act(async () => { await Promise.resolve(); });
}

async function readPasted(): Promise<void> {
  fireEvent.change(screen.getByLabelText('Paste CSS or a page’s HTML'), { target: { value: 'body { background: #0b1020 }' } });
  fireEvent.click(screen.getByRole('button', { name: 'Read colours from this CSS' }));
  await screen.findByRole('region', { name: 'Draft theme' });
}

describe('Themes page: From a website', () => {
  let calls: Call[];

  beforeEach(() => {
    calls = [];
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('offers only the paste box, with a line saying the address box is off, when the server has not enabled fetching', async () => {
    await open({ extractFromUrl: false }, calls);

    expect(screen.getByLabelText('Paste CSS or a page’s HTML')).toBeTruthy();
    expect(screen.queryByLabelText('Web address')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Fetch and read colours' })).toBeNull();
    expect(screen.getByText(/switched off on this server/)).toBeTruthy();
  });

  it('shows the web address box, and the plain statement of what the server will do, when the server has enabled fetching', async () => {
    await open({ extractFromUrl: true }, calls);

    expect(screen.getByLabelText('Web address')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Fetch and read colours' })).toBeTruthy();
    expect(screen.getByText(/fetch this page and up to 8 of its stylesheets/)).toBeTruthy();
    expect(screen.queryByText(/switched off on this server/)).toBeNull();
  });

  it('reads pasted CSS into an unsaved draft with how it was chosen and what was adjusted, and saves nothing', async () => {
    await open({ extractFromUrl: false }, calls);
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Read colours from this CSS' }).disabled).toBe(true);

    await readPasted();

    const extract = calls.find((c) => c.url === '/api/admin/themes/extract');
    expect(extract?.method).toBe('POST');
    expect(extract?.body).toEqual({ css: 'body { background: #0b1020 }' });
    const region = screen.getByRole('region', { name: 'Draft theme' });
    expect(within(within(region).getByRole('list', { name: 'How this was chosen' })).getAllByRole('listitem')).toHaveLength(2);
    expect(within(region).getByText(/light accent #ff6a3d became #b8400f/)).toBeTruthy();
    // The existing editor, with its contrast table, holds the draft; Save is the create button and is available at once.
    expect(within(region).getByRole('form', { name: 'Edit theme Theme from pasted CSS' })).toBeTruthy();
    expect(within(region).getByRole('table', { name: 'Contrast report, dark colours' })).toBeTruthy();
    expect(within(region).getByRole<HTMLButtonElement>('button', { name: 'Create theme from draft' }).disabled).toBe(false);
    // Never auto-saved: the only calls so far are the page's reads and the extract.
    expect(calls.filter((c) => c.method !== 'GET').map((c) => `${c.method} ${c.url}`)).toEqual(['POST /api/admin/themes/extract']);
  });

  it('sends the web address when one is given', async () => {
    await open({ extractFromUrl: true }, calls);

    fireEvent.change(screen.getByLabelText('Web address'), { target: { value: ' https://example.com ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Fetch and read colours' }));
    await screen.findByRole('region', { name: 'Draft theme' });

    expect(calls.find((c) => c.url === '/api/admin/themes/extract')?.body).toEqual({ url: 'https://example.com' });
    expect(screen.getByText('Draft from a web address')).toBeTruthy();
  });

  it('creates the theme only when asked, through the existing create and save, with any token the admin changed', async () => {
    await open({ extractFromUrl: false }, calls);
    await readPasted();

    fireEvent.change(screen.getByLabelText('Brand (dark)', { selector: 'input[type=text]' }), { target: { value: '#112233' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create theme from draft' }));

    expect(await screen.findByText(/Created Theme from pasted CSS from a website/)).toBeTruthy();
    const writes = calls.filter((c) => c.method !== 'GET' && c.url !== '/api/admin/themes/extract');
    expect(writes.map((c) => `${c.method} ${c.url}`)).toEqual(['POST /api/admin/themes', 'PUT /api/admin/themes/from-a-site']);
    expect(writes[0]?.body).toEqual({ name: 'Theme from pasted CSS', baseId: null });
    const saved = writes[1]?.body as OrgTheme;
    expect(saved.id).toBe('from-a-site');
    expect(saved.chrome.dark.brand).toBe('#112233');
    expect(saved.chrome.dark.surface).toBe('#0b1020');
    expect(saved.diagram.palette).toEqual({ red: '#e04832' });
    // The draft is gone, and the new theme is open in the ordinary editor.
    expect(screen.queryByRole('region', { name: 'Draft theme' })).toBeNull();
    expect(await screen.findByRole('form', { name: 'Edit theme Theme from pasted CSS' })).toBeTruthy();
  });

  it('removes the theme it created when the server refuses the draft, so a refusal leaves nothing behind', async () => {
    await open({ extractFromUrl: false, saveStatus: 422 }, calls);
    await readPasted();

    fireEvent.click(screen.getByRole('button', { name: 'Create theme from draft' }));

    await waitFor(() => { expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/admin/themes/from-a-site')).toBe(true); });
    expect(screen.getAllByRole('alert').map((a) => a.textContent).join(' ')).toContain('The theme was refused.');
    expect(screen.getByRole('region', { name: 'Draft theme' })).toBeTruthy();
  });

  it('shows the server’s refusal when no colours could be read, and offers no draft', async () => {
    await open({ extractFromUrl: false, extractStatus: 422 }, calls);

    fireEvent.change(screen.getByLabelText('Paste CSS or a page’s HTML'), { target: { value: 'p { margin: 0 }' } });
    fireEvent.click(screen.getByRole('button', { name: 'Read colours from this CSS' }));

    expect((await screen.findByRole('alert')).textContent).toContain('No colours could be read');
    expect(screen.queryByRole('region', { name: 'Draft theme' })).toBeNull();
  });

  it('discards a draft on request and when another theme is opened for editing', async () => {
    await open({ extractFromUrl: false }, calls);
    await readPasted();
    fireEvent.click(screen.getByRole('button', { name: 'Discard this draft' }));
    expect(screen.queryByRole('region', { name: 'Draft theme' })).toBeNull();

    await readPasted();
    fireEvent.click(screen.getByRole('button', { name: 'Edit Acme' }));
    expect(screen.queryByRole('region', { name: 'Draft theme' })).toBeNull();
    // One editor at a time, so no field id appears twice.
    expect(screen.getAllByRole('form', { name: /Edit theme/ })).toHaveLength(1);
    expect(screen.getByRole('form', { name: 'Edit theme Acme' })).toBeTruthy();
  });
});
