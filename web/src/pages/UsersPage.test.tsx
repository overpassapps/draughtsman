// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import { UsersPage } from './UsersPage.tsx';

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', lastSignInAt: '2026-10-01T02:35:07.000Z', active: true };
const ERIN: AuthUser = { id: 'usr_e', email: 'erin@acme.example', displayName: 'Erin Editor', role: 'Editor', createdAt: '2026-01-02T00:00:00.000Z', active: true };

function serve(): { calls: { method: string; url: string; body: unknown }[] } {
  const calls: { method: string; url: string; body: unknown }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const method = init?.method ?? 'GET';
      calls.push({ method, url, body: init?.body === undefined ? undefined : JSON.parse(init.body as string) });
      if (method === 'GET') {
        return Promise.resolve(new Response(JSON.stringify([ADMIN, ERIN]), { status: 200 }));
      }
      return Promise.resolve(new Response(JSON.stringify(ERIN), { status: method === 'POST' ? 201 : 200 }));
    }),
  );
  return { calls };
}

const writes = (calls: { method: string }[]) => calls.filter((call) => call.method !== 'GET');

async function mount() {
  const served = serve();
  const view = render(<UsersPage currentUser={ADMIN} />);
  await screen.findByText('Erin Editor');
  return { ...served, ...view };
}

function fillAddForm(password: string): void {
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'new@acme.example' } });
  fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'New Person' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: password } });
}

describe('UsersPage Add user (QA S8)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the password rule beside the field, tied to it for a screen reader', async () => {
    await mount();
    const field = screen.getByLabelText('Password');
    const hint = screen.getByText('At least 12 characters.');
    expect(field.getAttribute('aria-describedby')).toBe(hint.id);
  });

  it('does not silently disable Add user for a short password: it says what is wrong, in the server’s words, and sends nothing', async () => {
    const { calls } = await mount();
    fillAddForm('short');
    const button = screen.getByRole<HTMLButtonElement>('button', { name: 'Add user' });
    expect(button.disabled).toBe(false);

    fireEvent.click(button);

    expect((await screen.findByRole('alert')).textContent).toBe('Password must be at least 12 characters.');
    expect(writes(calls)).toEqual([]);
  });

  it('adds the user once the password is long enough (the other direction)', async () => {
    const { calls } = await mount();
    fillAddForm('a long enough password');

    fireEvent.click(screen.getByRole('button', { name: 'Add user' }));

    await waitFor(() => { expect(writes(calls)).toHaveLength(1); });
    expect(writes(calls)[0]).toMatchObject({ method: 'POST', url: '/api/users', body: { email: 'new@acme.example', password: 'a long enough password' } });
  });

  it('keeps each label with its own input, so a long label wraps inside its column instead of dropping its input to another row', async () => {
    await mount();
    for (const label of ['Email', 'Display name', 'Role', 'Password']) {
      const field = screen.getByLabelText(label).closest('.admin-field');
      expect(field, label).not.toBeNull();
      expect(within(field as HTMLElement).getByText(label).tagName).toBe('LABEL');
    }
  });
});

describe('UsersPage Reset password (QA S8)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  async function openReset(name: string) {
    const view = await mount();
    const row = screen.getByText(name).closest('tr') as HTMLElement;
    fireEvent.click(within(row).getByRole('button', { name: 'Reset password' }));
    return { ...view, row };
  }

  it('has a visible label and the rule, not a bare box', async () => {
    const { row } = await openReset('Erin Editor');
    const label = within(row).getByText('New password for Erin Editor');
    expect(label.tagName).toBe('LABEL');
    expect(label.classList.contains('visually-hidden')).toBe(false);
    const field = within(row).getByLabelText('New password for Erin Editor');
    expect(within(row).getByText(/At least 12 characters\./).id).toBe(field.getAttribute('aria-describedby'));
    // It also says what a reset does, so an administrator is not surprised that the person is signed out.
    expect(within(row).getByText(/signed out everywhere/)).toBeTruthy();
  });

  it('answers a short password with the real message beside the field and sends nothing', async () => {
    const { row, calls } = await openReset('Erin Editor');
    fireEvent.change(within(row).getByLabelText('New password for Erin Editor'), { target: { value: 'short' } });

    fireEvent.click(within(row).getByRole('button', { name: 'Save' }));

    expect(within(row).getByRole('alert').textContent).toBe('Password must be at least 12 characters.');
    expect(within(row).getByLabelText('New password for Erin Editor').getAttribute('aria-invalid')).toBe('true');
    expect(writes(calls)).toEqual([]);
  });

  it('resets with a long enough one (the other direction)', async () => {
    const { row, calls } = await openReset('Erin Editor');
    fireEvent.change(within(row).getByLabelText('New password for Erin Editor'), { target: { value: 'a brand new long one' } });

    fireEvent.click(within(row).getByRole('button', { name: 'Save' }));

    await waitFor(() => { expect(writes(calls)).toHaveLength(1); });
    expect(writes(calls)[0]).toMatchObject({ method: 'PATCH', url: '/api/users/usr_e', body: { password: 'a brand new long one' } });
  });
});

describe('UsersPage dates (QA P14)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the last sign-in as day, month name and 24-hour time, with the zone on hover, and Never for none', async () => {
    await mount();
    const adminRow = screen.getByText('Ada Admin').closest('tr') as HTMLElement;
    const time = adminRow.querySelector('time');
    expect(time?.textContent).toMatch(/^\d{1,2} [A-Z][a-z]{2} \d{4}, \d{2}:\d{2}$/);
    expect(time?.getAttribute('datetime')).toBe('2026-10-01T02:35:07.000Z');
    expect(time?.getAttribute('title')).toMatch(/:\d{2}/);
    expect(within(screen.getByText('Erin Editor').closest('tr') as HTMLElement).getByText('Never')).toBeTruthy();
  });
});

describe('UsersPage for an Editor', () => {
  it('shows the not-allowed state with a heading and a way back, and asks for no users', async () => {
    const { calls } = serve();
    render(<UsersPage currentUser={{ ...ERIN }} />);
    await act(() => Promise.resolve());
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Administrators only');
    expect(screen.getByRole('alert').textContent).toBe('You need to be an administrator to view this page.');
    expect(screen.getByRole('link', { name: 'Back to documents' }).getAttribute('href')).toBe('/');
    expect(screen.queryByText('Add user')).toBeNull();
    expect(calls.filter((call) => call.method !== 'GET')).toEqual([]);
    vi.unstubAllGlobals();
  });
});
