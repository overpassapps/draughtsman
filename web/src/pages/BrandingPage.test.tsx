// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import { BrandingPage } from './BrandingPage.tsx';

const admin: AuthUser = {
  id: 'usr_admin',
  email: 'admin@example.com',
  displayName: 'Admin',
  role: 'Admin',
  createdAt: '2026-01-01T00:00:00.000Z',
  active: true,
};

const editor: AuthUser = { ...admin, id: 'usr_editor', role: 'Editor' };

interface StateOverrides {
  productName?: string;
  overridden?: Partial<Record<'productName' | 'logo' | 'logoLight' | 'favicon', boolean>>;
}

function stateBody(overrides: StateOverrides = {}): Response {
  return new Response(
    JSON.stringify({
      themeName: 'Overpass',
      themeProductName: 'Draughtsman',
      productName: overrides.productName ?? 'Draughtsman',
      mode: 'dark',
      logo: '/api/theme/logo',
      logoLight: '/api/theme/logo-light',
      favicon: '/api/theme/favicon',
      overridden: { productName: false, logo: false, logoLight: false, favicon: false, ...overrides.overridden },
    }),
    { status: 200 },
  );
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

function route(): typeof fetch {
  return (input: RequestInfo | URL, init?: RequestInit) => {
    const url = requestUrl(input);
    const method = init?.method ?? 'GET';
    if (url === '/api/theme/chrome.css') {
      return Promise.resolve(new Response('/* tokens */', { headers: { 'Content-Type': 'text/css' } }));
    }
    if (url === '/api/theme') {
      return Promise.resolve(new Response(JSON.stringify({ name: 'Overpass', productName: 'Draughtsman', logo: null, logoLight: null, favicon: null, mode: 'dark' }), { status: 200 }));
    }
    if (url === '/api/admin/branding' && method === 'GET') {
      return Promise.resolve(stateBody());
    }
    if (url === '/api/admin/branding' && method === 'PUT') {
      return Promise.resolve(stateBody({ productName: 'Acme', overridden: { productName: true } }));
    }
    if (url === '/api/admin/branding/logo') {
      return Promise.resolve(stateBody({ overridden: { logo: true } }));
    }
    throw new Error(`unexpected fetch: ${method} ${url}`);
  };
}

describe('BrandingPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('loads the branding and saves the product name', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(route()));
    render(<BrandingPage currentUser={admin} />);

    await waitFor(() => {
      expect(screen.getByLabelText('Product name')).toHaveProperty('value', 'Draughtsman');
    });

    fireEvent.change(screen.getByLabelText('Product name'), { target: { value: 'Acme' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() => {
      expect(vi.mocked(fetch)).toHaveBeenCalledWith(
        '/api/admin/branding',
        expect.objectContaining({ method: 'PUT', body: JSON.stringify({ productName: 'Acme' }) }),
      );
    });
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveProperty('textContent', 'Product name saved.');
    });
  });

  it('uploads a logo as multipart and marks it overridden', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(route()));
    render(<BrandingPage currentUser={admin} />);
    await waitFor(() => {
      expect(screen.getByLabelText('Product name')).toBeTruthy();
    });

    const file = new File([new Uint8Array([1, 2, 3])], 'logo.png', { type: 'image/png' });
    await act(async () => {
      fireEvent.change(screen.getByLabelText('Upload Logo'), { target: { files: [file] } });
      await Promise.resolve();
    });

    await waitFor(() => {
      const call = vi.mocked(fetch).mock.calls.find(([url]) => url === '/api/admin/branding/logo');
      expect(call).toBeDefined();
      expect(call?.[1]?.method).toBe('POST');
      expect(call?.[1]?.body).toBeInstanceOf(FormData);
    });
    await waitFor(() => {
      expect(screen.getAllByText('Overridden').length).toBeGreaterThan(0);
    });
  });

  it('asks in the page before resetting all branding, and resets only once confirmed', async () => {
    const base = route();
    const fetchMock = vi.fn<typeof fetch>((input, init) => {
      const url = requestUrl(input);
      if (url === '/api/admin/branding' && (init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(stateBody({ overridden: { logo: true } }));
      }
      if (url === '/api/admin/branding/reset') {
        return Promise.resolve(stateBody());
      }
      return base(input, init);
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<BrandingPage currentUser={admin} />);
    await waitFor(() => {
      expect(screen.getByLabelText('Product name')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Reset all branding' }));

    expect(screen.getByRole('group', { name: 'Confirm reset' })).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalledWith('/api/admin/branding/reset', expect.anything());
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('group', { name: 'Confirm reset' })).toBeNull();
    expect(fetchMock).not.toHaveBeenCalledWith('/api/admin/branding/reset', expect.anything());

    fireEvent.click(screen.getByRole('button', { name: 'Reset all branding' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes, reset all branding' }));
    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith('/api/admin/branding/reset', expect.objectContaining({ method: 'POST' }));
    });
  });

  it('asks in the page before removing one uploaded image, and deletes only once confirmed', async () => {
    const base = route();
    const fetchMock = vi.fn<typeof fetch>((input, init) => {
      const url = requestUrl(input);
      const method = init?.method ?? 'GET';
      if (url === '/api/admin/branding' && method === 'GET') {
        return Promise.resolve(stateBody({ overridden: { logo: true } }));
      }
      if (url === '/api/admin/branding/logo' && method === 'DELETE') {
        return Promise.resolve(stateBody());
      }
      return base(input, init);
    });
    vi.stubGlobal('fetch', fetchMock);
    const deleted = (): boolean => fetchMock.mock.calls.some(([url, init]) => requestUrl(url) === '/api/admin/branding/logo' && init?.method === 'DELETE');
    render(<BrandingPage currentUser={admin} />);
    await waitFor(() => {
      expect(screen.getByLabelText('Product name')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Remove Logo' }));
    expect(screen.getByRole('group', { name: 'Confirm remove Logo' }).textContent).toContain('deletes the uploaded file');
    expect(deleted()).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('group', { name: 'Confirm remove Logo' })).toBeNull();
    expect(deleted()).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Remove Logo' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes, remove Logo' }));
    await waitFor(() => {
      expect(deleted()).toBe(true);
    });
  });

  it('refuses an editor with an alert and no form', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(route()));
    render(<BrandingPage currentUser={editor} />);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveProperty('textContent', 'You need to be an administrator to view this page.');
    });
    expect(screen.queryByLabelText('Product name')).toBeNull();
  });
});
