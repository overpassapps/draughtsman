// @vitest-environment jsdom
import { createTheme, deleteTheme, fetchAdminThemes, fetchPublicThemes, importTheme, saveTheme, setDefaultTheme, ThemesError } from './themesClient.ts';
import type { OrgTheme } from '../theme/orgTheme.ts';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

// What the server sends: null members are omitted from its JSON.
const wire = {
  id: 'acme',
  name: 'Acme',
  mode: 'dark',
  chrome: { dark: {}, light: {}, radius: 6, fonts: { ui: 'Inter', mono: 'Mono' } },
  diagram: { base: 'overpass', palette: {}, fonts: {} },
  updatedAt: '2026-09-30T10:00:00.000Z',
  isDefault: false,
};

describe('themesClient', () => {
  beforeEach(() => {
    document.cookie = 'XSRF-TOKEN=tok123';
  });

  it('restores the members the server omitted as null, so strict checks downstream see null, not undefined', async () => {
    const fetchStub = vi.fn<typeof fetch>(() => Promise.resolve(json({ themes: [wire] })));
    const state = await fetchAdminThemes(fetchStub);
    expect(state.defaultId).toBeNull();
    expect(state.themes[0]?.diagram.canvasBackground).toBeNull();
    expect(state.themes[0]?.diagram.fonts).toEqual({ label: null, mono: null });
  });

  it('treats an answer without a themes array as no themes (an older server), and keeps real ones', async () => {
    expect((await fetchPublicThemes(() => Promise.resolve(json({ id: 'doc_1' })))).themes).toEqual([]);
    const real = await fetchPublicThemes(() => Promise.resolve(json({ defaultId: 'acme', themes: [{ id: 'acme', name: 'Acme', diagram: wire.diagram }] })));
    expect(real.defaultId).toBe('acme');
    expect(real.themes.map((t) => t.name)).toEqual(['Acme']);
  });

  it('attaches the CSRF header to every write and none is sent to a read', async () => {
    const fetchStub = vi.fn<typeof fetch>(() => Promise.resolve(json({ ...wire })));
    await createTheme('Acme', undefined, fetchStub);
    await saveTheme(wire as unknown as OrgTheme, fetchStub);
    await importTheme('name: x', fetchStub);
    const writes = fetchStub.mock.calls.map(([, init]) => new Headers(init?.headers).get('X-XSRF-TOKEN'));
    expect(writes).toEqual(['tok123', 'tok123', 'tok123']);

    const readStub = vi.fn<typeof fetch>(() => Promise.resolve(json({ themes: [] })));
    await fetchAdminThemes(readStub);
    expect(new Headers(readStub.mock.calls[0]?.[1]?.headers).get('X-XSRF-TOKEN')).toBeNull();
  });

  it('sends the default as {id} and null to clear it', async () => {
    const fetchStub = vi.fn<typeof fetch>(() => Promise.resolve(json({ themes: [] })));
    await setDefaultTheme('acme', fetchStub);
    await setDefaultTheme(null, fetchStub);
    expect(fetchStub.mock.calls.map(([url, init]) => [url, init?.method, init?.body])).toEqual([
      ['/api/admin/themes/default', 'PUT', '{"id":"acme"}'],
      ['/api/admin/themes/default', 'PUT', '{"id":null}'],
    ]);
  });

  it('raises a ThemesError carrying the server detail and every field problem for a 422', async () => {
    const refused = json({ title: 'Invalid theme', detail: 'Bad colour', problems: [{ field: 'dark.accent', message: 'accent (dark) must be a hex colour' }] }, 422);
    await expect(saveTheme(wire as unknown as OrgTheme, () => Promise.resolve(refused))).rejects.toMatchObject({
      status: 422,
      message: 'Bad colour',
      problems: [{ field: 'dark.accent', message: 'accent (dark) must be a hex colour' }],
    });
    await expect(deleteTheme('x', () => Promise.resolve(new Response('nope', { status: 500 })))).rejects.toBeInstanceOf(ThemesError);
  });

  it('encodes the id in the path', async () => {
    const fetchStub = vi.fn<typeof fetch>(() => Promise.resolve(json({ themes: [] })));
    await deleteTheme('a/b', fetchStub);
    expect(fetchStub.mock.calls[0]?.[0]).toBe('/api/admin/themes/a%2Fb');
  });
});
