// @vitest-environment jsdom
/**
 * An automated accessibility check of the places the Draughtsman mark and lockup are drawn (the shell header, the sign-in and
 * first-run cards, the About page), the same axe-core pattern as loginAxe.test.tsx. Colour contrast is off in jsdom; the mark's
 * colours against every chrome surface are asserted in scripts/brandAssets.test.ts instead.
 */
import { render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import { AppShell } from '../app/AppShell.tsx';
import { AboutPage } from '../pages/AboutPage.tsx';
import { FirstRunPage } from '../pages/FirstRunPage.tsx';
import { LoginPage } from '../pages/LoginPage.tsx';

const USER = { id: 'usr_1', email: 'e@x.com', displayName: 'E', role: 'Admin', createdAt: '2026-01-01T00:00:00.000Z', active: true } as const;
const THEMES = {
  default: { name: 'Overpass', productName: 'Draughtsman', mode: 'dark' },
  organisation: { name: 'Acme', productName: 'Acme Diagrams', logo: '/api/theme/logo', logoLight: '/api/theme/logo-light', mode: 'light' },
} as const;

function serve(theme: (typeof THEMES)[keyof typeof THEMES]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const body: unknown =
        url === '/api/theme'
          ? theme
          : url === '/api/about'
            ? { version: '0.1.0', buildDate: '2026-09-30', supportContact: 'help@example.com', noticesUrl: '/THIRD-PARTY-NOTICES.txt' }
            : {};
      return Promise.resolve(new Response(JSON.stringify(body), { status: 200 }));
    }),
  );
}

async function violations(root: Element): Promise<string[]> {
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false }, region: { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

describe.each(['default', 'organisation'] as const)('brand accessibility with the %s branding (axe-core)', (which) => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('finds nothing on the sign-in card', async () => {
    serve(THEMES[which]);
    const { container } = render(<LoginPage onSignedIn={() => undefined} />);
    await screen.findByText(THEMES[which].productName);
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the first-run card', async () => {
    serve(THEMES[which]);
    const { container } = render(<FirstRunPage onSignedIn={() => undefined} />);
    await screen.findByText(THEMES[which].productName);
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in the shell header', async () => {
    serve(THEMES[which]);
    const { container } = render(<AppShell user={USER} onSignedOut={() => undefined}>{null}</AppShell>);
    await waitFor(() => {
      expect(container.querySelector('.shell-brand')?.textContent).toBe(THEMES[which].productName);
    });
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing on the About page, and names every picture on it', async () => {
    serve(THEMES[which]);
    const { container } = render(<AboutPage currentUser={USER} />);
    await screen.findByText('0.1.0');
    expect(await violations(container)).toEqual([]);
    // The lockup carries its own words ("by Overpass") only as a picture, so it must have a name.
    expect(screen.getByRole('img', { name: 'Draughtsman by Overpass' })).toBeTruthy();
    for (const image of container.querySelectorAll('img')) {
      expect(image.getAttribute('alt')).not.toBeNull();
    }
  });
});
