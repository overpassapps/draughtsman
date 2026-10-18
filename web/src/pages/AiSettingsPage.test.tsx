// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { AiKeyState, AiSettingsState, KeyRingState } from '../model/aiSettingsClient.ts';
import type { AuthUser } from '../model/authClient.ts';
import { AiSettingsPage } from './AiSettingsPage.tsx';

const admin: AuthUser = {
  id: 'usr_admin',
  email: 'admin@example.com',
  displayName: 'Admin',
  role: 'Admin',
  createdAt: '2026-01-01T00:00:00.000Z',
  active: true,
};

const editor: AuthUser = { ...admin, id: 'usr_editor', role: 'Editor' };

const SECRET = 'sk-typed-secret-0123456789';

function state(partial: { key?: Partial<AiKeyState>; provider?: string | null; enabled?: boolean } = {}): AiSettingsState {
  const provider = partial.provider === undefined ? 'openai' : partial.provider;
  return {
    enabled: partial.enabled ?? provider !== null,
    problem: provider === null ? 'No AI provider is configured.' : null,
    effective: { provider, model: provider === null ? null : 'gpt-4o-mini', endpoint: 'https://api.openai.com/v1', apiVersion: null },
    file: { provider: null, model: null, endpoint: null, apiVersion: null },
    overrides: { provider, model: null, endpoint: null, apiVersion: null },
    key: {
      source: 'none',
      environmentVariable: 'DRAUGHTSMAN_AI_KEY',
      environmentSet: false,
      storedSet: false,
      storedUnreadable: false,
      required: true,
      ...partial.key,
    },
    providers: ['ollama', 'openai', 'anthropic', 'azure-openai'],
  };
}

const PROTECTED: KeyRingState = {
  state: 'protected',
  provider: 'key',
  message: 'The key ring is protected by key-encryption key from the environment variable DRAUGHTSMAN_KEY_ENCRYPTION_KEY.',
  unreadableKeys: 0,
  migration: 'none',
};

const UNPROTECTED: KeyRingState = {
  state: 'unprotected',
  provider: 'none',
  reason: 'no-provider',
  message: 'The key ring is not protected: anyone who can read this server’s data folder can decrypt the stored AI API key. Set DRAUGHTSMAN_KEY_ENCRYPTION_KEY to a key held outside the data folder.',
  unreadableKeys: 0,
  migration: 'none',
};

function withKeyRing(keyRing: KeyRingState | undefined, key: Partial<AiKeyState> = {}): AiSettingsState {
  return { ...state({ key }), ...(keyRing === undefined ? {} : { keyRing }) };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200 });
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

describe('AiSettingsPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('saves the provider settings', async () => {
    const fetchMock = vi.fn<typeof fetch>((input, init) => {
      const url = requestUrl(input);
      if (url === '/api/admin/ai' && (init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(json(state({ provider: null })));
      }
      if (url === '/api/admin/ai' && init?.method === 'PUT') {
        return Promise.resolve(json(state()));
      }
      throw new Error(`unexpected fetch: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<AiSettingsPage currentUser={admin} />);

    await waitFor(() => { expect(screen.getByLabelText('Provider')).toBeTruthy(); });
    expect(screen.getByText(/AI is off: No AI provider is configured/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Provider'), { target: { value: 'openai' } });
    fireEvent.change(screen.getByLabelText('Model'), { target: { value: 'gpt-4o-mini' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/admin/ai',
        expect.objectContaining({ method: 'PUT', body: JSON.stringify({ provider: 'openai', model: 'gpt-4o-mini', endpoint: '', apiVersion: '' }) }),
      );
    });
    await waitFor(() => { expect(screen.getByRole('status').textContent).toBe('AI settings saved.'); });
    expect(screen.getByText(/AI is on: openai/)).toBeTruthy();
  });

  it('sends the key once, never shows it, and clears the field', async () => {
    const fetchMock = vi.fn<typeof fetch>((input, init) => {
      const url = requestUrl(input);
      if (url === '/api/admin/ai/key' && init?.method === 'PUT') {
        return Promise.resolve(json(state({ key: { source: 'stored', storedSet: true } })));
      }
      return Promise.resolve(json(state()));
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<AiSettingsPage currentUser={admin} />);
    await waitFor(() => { expect(screen.getByLabelText('API key')).toBeTruthy(); });

    const input = screen.getByLabelText('API key');
    expect(input).toHaveProperty('type', 'password');
    fireEvent.change(input, { target: { value: SECRET } });
    fireEvent.click(screen.getByRole('button', { name: 'Save key' }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith('/api/admin/ai/key', expect.objectContaining({ method: 'PUT', body: JSON.stringify({ apiKey: SECRET }) }));
    });
    await waitFor(() => { expect(screen.getByText(/A key is set \(stored encrypted/)).toBeTruthy(); });
    // The typed value is gone from the field and appears nowhere in the page.
    expect(screen.getByLabelText('Replace API key')).toHaveProperty('value', '');
    expect(document.body.innerHTML).not.toContain(SECRET);
    expect(screen.getByRole('button', { name: 'Clear key' })).toBeTruthy();
  });

  it.each([['an omitted key (how the server writes a null)', undefined], ['a null', null], ['whitespace', '  ']])('reads a file provider that is %s as none, never as empty parentheses', async (_name, provider) => {
    const loaded = state({});
    const file: Record<string, unknown> = { ...loaded.file };
    delete file.provider;
    const body = provider === undefined ? { ...loaded, file } : { ...loaded, file: { ...file, provider } };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(body))));
    render(<AiSettingsPage currentUser={admin} />);
    await waitFor(() => { expect(screen.getByLabelText('Provider')).toBeTruthy(); });
    expect(screen.getByRole('option', { name: 'Use draughtsman.yaml (none)' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: /\(\)/ })).toBeNull();
  });

  it('asks in the page before resetting, saying it removes the stored key, and reads an empty file provider as none', async () => {
    const fetchMock = vi.fn<typeof fetch>((input, init) => {
      const url = requestUrl(input);
      if (url === '/api/admin/ai/reset' && init?.method === 'POST') {
        return Promise.resolve(json(state({ provider: null })));
      }
      const loaded = state({ key: { source: 'stored', storedSet: true } });
      return Promise.resolve(json({ ...loaded, file: { ...loaded.file, provider: '' } }));
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<AiSettingsPage currentUser={admin} />);
    await waitFor(() => { expect(screen.getByLabelText('Provider')).toBeTruthy(); });
    expect(screen.getByRole('option', { name: 'Use draughtsman.yaml (none)' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Reset to draughtsman.yaml' }));

    expect(fetchMock).not.toHaveBeenCalledWith('/api/admin/ai/reset', expect.anything());
    expect(screen.getByRole('group', { name: 'Confirm reset' }).textContent).toContain('removes the stored API key');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(fetchMock).not.toHaveBeenCalledWith('/api/admin/ai/reset', expect.anything());

    fireEvent.click(screen.getByRole('button', { name: 'Reset to draughtsman.yaml' }));
    fireEvent.click(screen.getByRole('button', { name: 'Yes, reset and remove the key' }));
    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith('/api/admin/ai/reset', expect.objectContaining({ method: 'POST' }));
    });
  });

  it('says when the environment variable is the key in effect', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(state({ key: { source: 'environment', environmentSet: true, storedSet: true } })))));
    render(<AiSettingsPage currentUser={admin} />);

    await waitFor(() => {
      expect(screen.getByText(/set by the environment variable DRAUGHTSMAN_AI_KEY and is in effect/)).toBeTruthy();
    });
  });

  it('does not ask for a key when the provider is a local Ollama', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(state({ provider: 'ollama', key: { required: false } })))));
    render(<AiSettingsPage currentUser={admin} />);

    await waitFor(() => { expect(screen.getByText(/No key is needed for a local model/)).toBeTruthy(); });
  });

  it('shows a failed connection test as an alert without any key text', async () => {
    const fetchMock = vi.fn<typeof fetch>((input) => {
      if (requestUrl(input) === '/api/admin/ai/test') {
        return Promise.resolve(json({ ok: false, message: 'The provider rejected the API key. Check it is valid for this provider and can use the model.' }));
      }
      return Promise.resolve(json(state({ key: { source: 'stored', storedSet: true } })));
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<AiSettingsPage currentUser={admin} />);
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Test connection' })).toBeTruthy(); });

    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    await waitFor(() => { expect(screen.getByRole('alert').textContent).toContain('rejected the API key'); });
  });

  describe('key ring protection notice', () => {
    it('warns, with what to set, while the ring is unprotected', async () => {
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(UNPROTECTED)))));
      render(<AiSettingsPage currentUser={admin} />);

      const notice = await screen.findByRole('note', { name: 'Key ring protection' });
      expect(notice.getAttribute('data-state')).toBe('unprotected');
      expect(notice.getAttribute('data-reason')).toBe('no-provider');
      expect(notice.textContent).toContain('anyone who can read this server’s data folder can decrypt the stored AI API key');
      expect(notice.textContent).toContain('DRAUGHTSMAN_KEY_ENCRYPTION_KEY');
      // The warning style, not the quiet one.
      expect(notice.querySelector('.admin-banner')).not.toBeNull();
    });

    it('is quiet, and says what protects the ring, once it is protected', async () => {
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(PROTECTED)))));
      render(<AiSettingsPage currentUser={admin} />);

      const notice = await screen.findByRole('note', { name: 'Key ring protection' });
      expect(notice.getAttribute('data-state')).toBe('protected');
      expect(notice.textContent).toContain('The key ring is protected by key-encryption key');
      expect(notice.querySelector('.admin-banner')).toBeNull();
      expect(notice.querySelector('.admin-notice')).toBeNull();
    });

    it('keeps warning about a pre-migration backup, protected or not, and names the folder', async () => {
      const keyRing: KeyRingState = { ...PROTECTED, migration: 'migrated', backup: 'backups/key-ring-20260930T200022Z' };
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(keyRing)))));
      render(<AiSettingsPage currentUser={admin} />);

      const notice = await screen.findByRole('note', { name: 'Key ring protection' });
      expect(notice.querySelector('.admin-notice')?.textContent).toContain('backups/key-ring-20260930T200022Z');
      expect(notice.querySelector('.admin-notice')?.textContent).toContain('delete that folder');
      // A protected ring with a leftover backup is still not shown as a failure.
      expect(notice.querySelector('.admin-banner')).toBeNull();
    });

    it('says why a key that exists is not being sent to this endpoint, and says nothing of the kind when none is held back', async () => {
      const withheld = 'The environment variable DRAUGHTSMAN_AI_KEY is set, but it is sent only to the host in draughtsman.yaml, and this endpoint is another host.';
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(state({ key: { source: 'none', environmentSet: true, withheld } })))));
      const first = render(<AiSettingsPage currentUser={admin} />);
      const summary = await screen.findByText(/No key is set\./);
      expect(summary.textContent).toContain('sent only to the host in draughtsman.yaml');
      first.unmount();

      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(state({ key: { source: 'none', environmentSet: false } })))));
      render(<AiSettingsPage currentUser={admin} />);
      expect((await screen.findByText(/No key is set\./)).textContent).not.toContain('sent only to the host');
    });

    it('warns when keys in the ring cannot be decrypted even though protection is on, and tells the admin to paste the key again', async () => {
      const keyRing: KeyRingState = { ...PROTECTED, unreadableKeys: 1, message: `${PROTECTED.message} 1 key(s) in the ring cannot be decrypted with the protection available now.` };
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(keyRing, { storedUnreadable: true })))));
      render(<AiSettingsPage currentUser={admin} />);

      const notice = await screen.findByRole('note', { name: 'Key ring protection' });
      expect(notice.querySelector('.admin-banner')?.textContent).toContain('cannot be decrypted');
      expect(screen.getByText(/A stored key could not be decrypted .* Paste it again\./).textContent).toContain('protection is not available');
    });

    it('shows the server’s reason and what to do for a stored key it cannot read, and notes one it moved off the install folder', async () => {
      const unreadable = 'The stored AI API key cannot be read. Name the old install folder in keyRing.legacyInstallPaths and restart, or enter it again in Admin > AI.';
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing({ ...PROTECTED, storedSecrets: [{ name: 'AI API key', file: 'ai/settings.json', state: 'unreadable', message: unreadable }] }, { storedUnreadable: true })))));
      const first = render(<AiSettingsPage currentUser={admin} />);
      expect((await screen.findByText(/keyRing\.legacyInstallPaths/)).textContent).toContain('enter it again in Admin > AI');
      first.unmount();

      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing({ ...PROTECTED, storedSecrets: [{ name: 'AI API key', file: 'ai/settings.json', state: 'reprotected', message: 'The stored AI API key was re-protected.' }] })))));
      render(<AiSettingsPage currentUser={admin} />);
      expect((await screen.findByTestId('ai-stored-secret')).textContent).toContain('re-protected');
    });

    it('shows nothing for a server that predates key ring protection', async () => {
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(undefined)))));
      render(<AiSettingsPage currentUser={admin} />);

      await waitFor(() => { expect(screen.getByLabelText('Provider')).toBeTruthy(); });
      expect(screen.queryByRole('note', { name: 'Key ring protection' })).toBeNull();
    });

    it('renders the server’s message as text, never as markup', async () => {
      const keyRing: KeyRingState = { ...UNPROTECTED, message: '<img src=x onerror=alert(1)> not protected' };
      vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(json(withKeyRing(keyRing)))));
      render(<AiSettingsPage currentUser={admin} />);

      const notice = await screen.findByRole('note', { name: 'Key ring protection' });
      expect(notice.querySelector('img')).toBeNull();
      expect(notice.textContent).toContain('<img src=x onerror=alert(1)>');
    });
  });

  it('refuses an editor with an alert and no form, and makes no request', () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(json(state())));
    vi.stubGlobal('fetch', fetchMock);
    render(<AiSettingsPage currentUser={editor} />);

    expect(screen.getByRole('alert').textContent).toBe('You need to be an administrator to view this page.');
    expect(screen.queryByLabelText('Provider')).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
