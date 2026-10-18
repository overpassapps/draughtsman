// @vitest-environment jsdom
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { AuthUser } from '../model/authClient.ts';
import { UsersPage } from './UsersPage.tsx';

const ADMIN: AuthUser = { id: 'usr_a', email: 'ada@acme.example', displayName: 'Ada Admin', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const EDITOR_ROLE: AuthUser = { id: 'usr_x', email: 'xena@acme.example', displayName: 'Xena Editor', role: 'Editor', createdAt: '2026-01-01T00:00:00.000Z', active: true };
const ERIN: AuthUser = { id: 'usr_e', email: 'erin@acme.example', displayName: 'Erin Editor', role: 'Editor', createdAt: '2026-01-02T00:00:00.000Z', active: true };
const GONE: AuthUser = { id: 'usr_g', email: 'gone@acme.example', displayName: 'Gone Away', role: 'Editor', createdAt: '2026-01-03T00:00:00.000Z', active: false };

const CREATED = { token: 'dpr_secretsecretsecret', expiresAt: '2030-01-02T09:00:00.000Z', expiresInHours: 24, userEmail: 'erin@acme.example' };

function serve(created: Response | (() => Response) = new Response(JSON.stringify(CREATED), { status: 201 })): { posts: string[] } {
  const posts: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if ((init?.method ?? 'GET') === 'POST' && url.endsWith('/password-reset-link')) {
        posts.push(url);
        return Promise.resolve(typeof created === 'function' ? created() : created.clone());
      }
      return Promise.resolve(new Response(JSON.stringify([ADMIN, ERIN, GONE]), { status: 200 }));
    }),
  );
  return { posts };
}

function firstOf<T>(items: T[]): T {
  const [item] = items;
  if (item === undefined) {
    throw new Error('Expected at least one match.');
  }
  return item;
}

async function openDialog(): Promise<void> {
  render(<UsersPage currentUser={ADMIN} />);
  await screen.findByText('Erin Editor');
  fireEvent.click(firstOf(screen.getAllByRole('button', { name: 'Create reset link' })));
}

describe('the Create reset link dialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('is offered for active people other than the administrator themselves, and for nobody who is deactivated', async () => {
    serve();
    render(<UsersPage currentUser={ADMIN} />);
    await screen.findByText('Erin Editor');

    expect(screen.getAllByRole('button', { name: 'Create reset link' })).toHaveLength(1);
    const adaRow = screen.getByText('Ada Admin').closest('tr') as HTMLElement;
    const goneRow = screen.getByText('Gone Away').closest('tr') as HTMLElement;
    expect(adaRow.textContent).not.toContain('Create reset link');
    expect(goneRow.textContent).not.toContain('Create reset link');
  });

  it('says what creating the link does before it creates one, and creates nothing until asked', async () => {
    const { posts } = serve();
    await openDialog();

    const dialog = screen.getByRole('dialog', { name: 'Create a reset link for Erin Editor?' });
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.textContent).toContain('You never see or choose it.');
    expect(dialog.textContent).toContain('Creating it ends any earlier reset link');
    expect(dialog.textContent).toContain('signs Erin Editor out everywhere else');
    expect(posts).toEqual([]);
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Create link' }));
    });
  });

  it('shows the link once with its expiry, a copy button and the warning, and posts to this person\'s endpoint', async () => {
    const { posts } = serve();
    await openDialog();

    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));

    const field = await screen.findByLabelText<HTMLInputElement>('Reset link');
    expect(field.readOnly).toBe(true);
    expect(field.value).toBe(`${window.location.origin}/reset-password#token=dpr_secretsecretsecret`);
    expect(posts).toEqual(['/api/users/usr_e/password-reset-link']);
    expect(screen.getByRole('alert').textContent).toContain('Anyone who has this link can set Erin Editor’s password until it expires.');
    expect(screen.getByRole('alert').textContent).toContain('It is shown once and cannot be shown again.');
    expect(screen.getByText(/24 hours from now/)).toBeTruthy();
    expect(screen.getByRole('dialog').textContent).toContain('2 Jan 2030');
  });

  it('prefers the address the server built from server.publicUrl', async () => {
    serve(new Response(JSON.stringify({ ...CREATED, link: 'https://draughtsman.example.com/reset-password#token=dpr_secretsecretsecret' }), { status: 201 }));
    await openDialog();

    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));

    expect((await screen.findByLabelText<HTMLInputElement>('Reset link')).value).toBe('https://draughtsman.example.com/reset-password#token=dpr_secretsecretsecret');
  });

  it('copies the link and says so, then goes back to "Copy link"', async () => {
    serve();
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    await openDialog();
    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));
    await screen.findByLabelText('Reset link');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy link' }));
      await Promise.resolve();
    });

    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/reset-password#token=dpr_secretsecretsecret`);
    expect(screen.getByRole('button', { name: 'Copied' })).toBeTruthy();
    expect(screen.getByText('Link copied to the clipboard.')).toBeTruthy();
  });

  it('selects the link and says how to copy it by hand when the clipboard is not available', async () => {
    serve();
    vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new Error('denied')) } });
    await openDialog();
    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));
    await screen.findByLabelText('Reset link');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy link' }));
      await Promise.resolve();
    });

    expect(await screen.findByText(/press Ctrl\+C \(Cmd\+C on a Mac\)/)).toBeTruthy();
    const field = screen.getByLabelText<HTMLInputElement>('Reset link');
    expect(document.activeElement).toBe(field);
    expect(field.selectionStart).toBe(0);
    expect(field.selectionEnd).toBe(field.value.length);
  });

  it('closes with Close, Cancel or Escape, gives focus back to its button, and does not keep the link anywhere on the page', async () => {
    serve();
    await openDialog();
    const opener = firstOf(screen.getAllByRole('button', { name: 'Create reset link' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));
    await screen.findByLabelText('Reset link');

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.body.textContent).not.toContain('dpr_secretsecretsecret');
    expect(document.activeElement).toBe(opener);

    fireEvent.click(opener);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('keeps Tab inside the dialog', async () => {
    serve();
    await openDialog();
    const create = screen.getByRole('button', { name: 'Create link' });
    const cancel = screen.getByRole('button', { name: 'Cancel' });
    create.focus();

    fireEvent.keyDown(create, { key: 'Tab' });
    expect(document.activeElement).toBe(cancel);
    cancel.focus();
    fireEvent.keyDown(cancel, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(create);
  });

  it('reports the server\'s reason and stays on the question when it cannot create a link', async () => {
    serve(new Response(JSON.stringify({ title: 'Account deactivated', detail: 'erin@acme.example is deactivated. Reactivate the account first.' }), { status: 409 }));
    await openDialog();

    fireEvent.click(screen.getByRole('button', { name: 'Create link' }));

    expect((await screen.findByRole('alert')).textContent).toContain('is deactivated');
    expect(screen.getByRole('button', { name: 'Create link' })).toBeTruthy();
    expect(screen.queryByLabelText('Reset link')).toBeNull();
  });

  it('is not there at all for an Editor, who is told the page is not theirs and sends no request', () => {
    const { posts } = serve();
    render(<UsersPage currentUser={EDITOR_ROLE} />);

    expect(screen.queryByRole('button', { name: 'Create reset link' })).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
    expect(posts).toEqual([]);
    expect(vi.mocked(fetch)).not.toHaveBeenCalled();
  });
});
