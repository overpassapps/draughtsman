// @vitest-environment jsdom
/**
 * An automated accessibility check of the login and first-run cards (checklist: local sign-in), the same axe-core
 * pattern as editorAxe.test.tsx: run over the states a keyboard user passes through, including the inline error band.
 */

import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { FirstRunPage } from '../pages/FirstRunPage.tsx';
import { LoginPage } from '../pages/LoginPage.tsx';

function themeResponse(): Response {
  return new Response(JSON.stringify({ name: 'default', productName: 'Acme Diagrams', logo: null, favicon: null, mode: 'system' }), { status: 200 });
}

async function violations(root: Element): Promise<string[]> {
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

function flush(): Promise<void> {
  return act(() => Promise.resolve());
}

function requestUrl(input: RequestInfo | URL): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

describe('login and first-run accessibility (axe-core)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('finds nothing on the login card', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(themeResponse())));
    const { container } = render(<LoginPage onSignedIn={() => undefined} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the login card with the error band shown', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>((input: RequestInfo | URL) => {
        const url = requestUrl(input);
        if (url === '/api/theme') {
          return Promise.resolve(themeResponse());
        }
        return Promise.resolve(new Response(JSON.stringify({ title: 'Sign-in failed', status: 401, detail: 'Email or password is incorrect.' }), { status: 401 }));
      }),
    );
    const { container } = render(<LoginPage onSignedIn={() => undefined} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });

    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'e@x.com' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await flush();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeTruthy();
    });

    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the first-run card', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(themeResponse())));
    const { container } = render(<FirstRunPage onSignedIn={() => undefined} />);
    await waitFor(() => {
      expect(screen.getByText('Acme Diagrams')).toBeTruthy();
    });
    expect(await violations(container)).toEqual([]);
  });
});
