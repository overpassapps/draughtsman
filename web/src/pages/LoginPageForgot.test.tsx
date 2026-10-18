// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { LoginPage } from './LoginPage.tsx';

const SENTENCE = 'If an account with that address can be reset, we have emailed it a link. The email can take a few minutes to arrive.';

function stubServer(reset: (body: unknown) => Response): { resets: unknown[] } {
  const resets: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url === '/api/theme') {
        return Promise.resolve(new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 }));
      }
      if (url === '/api/auth/password-reset/request') {
        const body: unknown = JSON.parse(init?.body as string);
        resets.push(body);
        return Promise.resolve(reset(body));
      }
      throw new Error(`unexpected fetch: ${url}`);
    }),
  );
  return { resets };
}

describe('LoginPage password recovery', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('offers "Forgot password?" only when emailed links are set up', () => {
    stubServer(() => new Response('{}', { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);

    expect(screen.getByRole('button', { name: 'Forgot password?' })).toBeTruthy();
    expect(screen.queryByTestId('forgot-hint')).toBeNull();
  });

  it('otherwise says to ask an administrator, as plain text with nothing to click', () => {
    stubServer(() => new Response('{}', { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: false }} />);

    expect(screen.queryByRole('button', { name: 'Forgot password?' })).toBeNull();
    expect(screen.getByTestId('forgot-hint').textContent).toBe('Forgot your password? Ask your administrator for a reset link.');
    expect(screen.getByTestId('forgot-hint').querySelector('a, button')).toBeNull();
  });

  it('also says it when the status carried no answer at all', () => {
    stubServer(() => new Response('{}', { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} />);

    expect(screen.getByTestId('forgot-hint')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Forgot password?' })).toBeNull();
  });

  it('asks for the address, sends it, and shows the server\'s one sentence', async () => {
    const { resets } = stubServer(() => new Response(JSON.stringify({ message: SENTENCE }), { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'erin@acme.example' } });

    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));

    expect(screen.getByRole('heading', { name: 'Reset your password' })).toBeTruthy();
    const field = screen.getByLabelText<HTMLInputElement>('Email');
    expect(field.value).toBe('erin@acme.example');
    await waitFor(() => {
      expect(document.activeElement).toBe(field);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }));

    expect((await screen.findByTestId('reset-requested')).textContent).toBe(SENTENCE);
    expect(resets).toEqual([{ email: 'erin@acme.example' }]);
    expect(screen.getByRole('heading', { name: 'Check your email' })).toBeTruthy();
  });

  it('shows the same sentence for any address: the page has nothing of its own to say about accounts', async () => {
    stubServer(() => new Response(JSON.stringify({ message: SENTENCE }), { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'nobody-at-all@acme.example' } });
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }));

    expect((await screen.findByTestId('reset-requested')).textContent).toBe(SENTENCE);
  });

  it('keeps the form and says so when the requester is throttled', async () => {
    stubServer(() => new Response(JSON.stringify({ title: 'Too many attempts', detail: 'Too many attempts from this network address; try again in 15 minutes.' }), { status: 429 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'erin@acme.example' } });
    fireEvent.click(screen.getByRole('button', { name: 'Email me a link' }));

    expect((await screen.findByRole('alert')).textContent).toContain('try again in 15 minutes');
    expect(screen.getByRole('button', { name: 'Email me a link' })).toBeTruthy();
    expect(screen.queryByTestId('reset-requested')).toBeNull();
  });

  it('sends nothing for an empty address and goes back to sign-in with Back or Escape', () => {
    const { resets } = stubServer(() => new Response('{}', { status: 202 }));
    render(<LoginPage onSignedIn={() => undefined} passwordReset={{ selfService: true }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));

    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Email me a link' }).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Back to sign in' }));
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Forgot password?' }));
    fireEvent.keyDown(screen.getByLabelText('Email'), { key: 'Escape' });
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeTruthy();
    expect(resets).toEqual([]);
  });
});
