// @vitest-environment jsdom
import { fetchChromeThemeManifest, refreshChromeTheme } from './chromeTheme.ts';

const body = { name: 'T', productName: 'P', logo: '/api/theme/logo', logoLight: '/api/theme/logo-light', favicon: null, mode: 'dark' };

describe('brand image paths after a branding change', () => {
  const fetchStub = vi.fn<typeof fetch>((url) =>
    Promise.resolve((typeof url === 'string' ? url : '').endsWith('chrome.css') ? new Response('', { status: 200 }) : new Response(JSON.stringify(body), { status: 200 })),
  );

  it('are untouched before any change, then carry a new ?v= after each refresh so an <img> refetches', async () => {
    expect((await fetchChromeThemeManifest(fetchStub))?.logoLight).toBe('/api/theme/logo-light');

    await refreshChromeTheme(fetchStub);
    const first = await fetchChromeThemeManifest(fetchStub);
    expect(first?.logoLight).toBe('/api/theme/logo-light?v=1');
    expect(first?.logo).toBe('/api/theme/logo?v=1');
    expect(first?.favicon).toBeNull();

    await refreshChromeTheme(fetchStub);
    expect((await fetchChromeThemeManifest(fetchStub))?.logoLight).toBe('/api/theme/logo-light?v=2');
  });
});
