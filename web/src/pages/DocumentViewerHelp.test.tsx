// @vitest-environment jsdom
/**
 * Onboarding through the whole editor (design/atlas/help-about-status.md, empty-loading-error-states.md): the Help menu's
 * shortcut sheet, Documentation and About; `?`; the empty-canvas hint; the properties panel's "nothing selected" line; and
 * the AI tab's "AI is off" line, which only an administrator sees.
 */
import './loadTolerance.ts';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { CurrentUserContext } from '../app/currentUser.ts';
import { CANVAS_HINT_KEY } from '../editor/canvasHint.ts';
import { must } from '../model/sequenceFixture.ts';
import { parseDocument } from '../model/parse.ts';
import type { AuthUser } from '../model/authClient.ts';
import type { Document } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const populated = parseDocument(JSON.parse(JSON.stringify(orderService)));
const empty: Document = { ...structuredClone(populated), nodes: [], edges: [] };

const user = (role: 'Admin' | 'Editor'): AuthUser => ({ id: 'usr_1', email: 'e@x.com', displayName: 'E', role, createdAt: '2026-01-01T00:00:00.000Z', active: true });

function stubServer(options: { ai?: boolean; docsUrl?: string } = {}): void {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url === '/api/health') {
        return Promise.resolve(new Response(JSON.stringify({ export: { pdf: false }, ai: { enabled: options.ai === true } }), { status: 200 }));
      }
      if (url === '/api/about') {
        return Promise.resolve(new Response(JSON.stringify({ version: '0.1.0', ...(options.docsUrl === undefined ? {} : { docsUrl: options.docsUrl }) }), { status: 200 }));
      }
      return Promise.resolve(new Response('{}', { status: 200 }));
    }),
  );
}

async function mount(document: Document, role?: 'Admin' | 'Editor') {
  const tree = <DocumentViewer id={document.id} document={document} etag={'"dv1:loaded"'} />;
  const view = render(role === undefined ? tree : <CurrentUserContext.Provider value={user(role)}>{tree}</CurrentUserContext.Provider>);
  const canvas = must(view.container.querySelector<HTMLElement>('.diagram-canvas'));
  canvas.setPointerCapture = () => undefined;
  canvas.releasePointerCapture = () => undefined;
  canvas.hasPointerCapture = () => false;
  await act(() => Promise.resolve());
  return { ...view, canvas };
}

const openHelp = (): HTMLElement => {
  fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
  return screen.getByRole('group', { name: 'Help' });
};

describe('DocumentViewer onboarding', () => {
  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    window.localStorage.clear();
    window.history.replaceState(null, '', '/d/doc_order_service');
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    window.localStorage.clear();
    window.history.replaceState(null, '', '/');
  });

  describe('Help menu and the shortcut sheet', () => {
    it('lists the palette, Keyboard shortcuts and About under Help, and Documentation only when the instance configures a docs URL', async () => {
      stubServer();
      await mount(populated);
      const without = within(openHelp()).getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'));
      expect(without).toEqual(['Command palette, Ctrl+K', 'Keyboard shortcuts, ?', 'About']);
      cleanup();

      stubServer({ docsUrl: 'https://docs.example.com/d' });
      await mount(populated);
      const withDocs = within(openHelp()).getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'));
      expect(withDocs).toEqual(['Command palette, Ctrl+K', 'Keyboard shortcuts, ?', 'Documentation', 'About']);
    });

    it('opens the sheet from Help, built from the registry: grouped, with key caps, and only actions that have a shortcut', async () => {
      stubServer();
      await mount(populated);
      fireEvent.click(within(openHelp()).getByRole('menuitem', { name: 'Keyboard shortcuts, ?' }));

      const dialog = screen.getByRole('dialog', { name: 'Keyboard shortcuts' });
      const sections = within(dialog).getAllByRole('region').map((section) => section.getAttribute('aria-label'));
      expect(sections).toEqual(expect.arrayContaining(['Edit', 'View', 'Arrange']));
      const undo = must(within(dialog).getByText('Undo').closest('li'));
      expect(Array.from(undo.querySelectorAll('kbd')).map((key) => key.textContent)).toEqual(['Ctrl', 'Z']);
      // A registered action with no shortcut is not a row.
      expect(within(dialog).queryByText('Tidy layout (Layered)')).toBeNull();
      expect(within(dialog).queryByText('Rename diagram')).toBeNull();
    });

    it('opens on a bare ? and closes on Escape, but a ? typed in a text field, or with Ctrl held, is nobody\'s shortcut', async () => {
      stubServer();
      await mount(populated);

      fireEvent.keyDown(window, { key: '?', ctrlKey: true });
      expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).toBeNull();
      const title = screen.getByRole('button', { name: /Order service|Rename/i });
      fireEvent.keyDown(title, { key: '?' });
      fireEvent.keyDown(window, { key: '?' });
      const dialog = screen.getByRole('dialog', { name: 'Keyboard shortcuts' });

      fireEvent.keyDown(dialog, { key: 'Escape' });
      expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).toBeNull();

      // Typing ? into the shapes search box must not open the sheet.
      fireEvent.keyDown(window, { key: 's' });
      const search = screen.getByRole('searchbox', { name: 'Search shapes' });
      fireEvent.keyDown(search, { key: '?' });
      expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).toBeNull();
    });

    it('opens the documentation in a new tab without an opener, and About in the app', async () => {
      stubServer({ docsUrl: 'https://docs.example.com/d' });
      const open = vi.spyOn(window, 'open').mockReturnValue(null);
      await mount(populated);

      fireEvent.click(within(openHelp()).getByRole('menuitem', { name: 'Documentation' }));
      expect(open).toHaveBeenCalledWith('https://docs.example.com/d', '_blank', 'noopener,noreferrer');

      fireEvent.click(within(openHelp()).getByRole('menuitem', { name: 'About' }));
      expect(window.location.pathname).toBe('/about');
    });
  });

  describe('the empty canvas hint', () => {
    it('shows the one line on an empty document, names the palette key for the platform, and is out of the way of clicks', async () => {
      stubServer();
      const { container } = await mount(empty);

      const hint = must(container.querySelector('.canvas-hint'));
      expect(hint.textContent).toContain('Press S for shapes, C for connectors, or Ctrl+K for everything.');
    });

    it('is not shown on a document that already has nodes, but merely opening one does not use the hint up', async () => {
      stubServer();
      const { container } = await mount(populated);

      expect(container.querySelector('.canvas-hint')).toBeNull();
      expect(window.localStorage.getItem(CANVAS_HINT_KEY)).toBeNull();
    });

    it('stays away after it has been dismissed, in a later session on an empty document', async () => {
      stubServer();
      const first = await mount(empty);
      fireEvent.click(within(first.container).getByRole('button', { name: 'Dismiss this hint' }));
      expect(first.container.querySelector('.canvas-hint')).toBeNull();
      expect(window.localStorage.getItem(CANVAS_HINT_KEY)).toBe('1');
      first.unmount();

      const second = await mount(empty);
      expect(second.container.querySelector('.canvas-hint')).toBeNull();
    });

    it('does not break when the browser blocks storage: the hint simply shows', async () => {
      stubServer();
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new Error('blocked');
      });
      const { container } = await mount(empty);

      expect(container.querySelector('.canvas-hint')).not.toBeNull();
    });
  });

  describe('the properties panel', () => {
    it('says what to do while nothing is selected, and stops once something is', async () => {
      stubServer();
      const { container, canvas } = await mount(populated);
      expect(screen.getByText('Select a node or connector to edit it.')).toBeTruthy();

      const node = must(container.querySelector('g[data-id="n_web"]'));
      fireEvent.pointerDown(canvas, { button: 0, pointerId: 1, clientX: 0, clientY: 0 });
      fireEvent.click(node);
      fireEvent.keyDown(canvas, { key: 'Tab' });
      expect(screen.queryByText('Select a node or connector to edit it.')).toBeNull();
    });
  });

  describe('the AI tab while no provider is configured', () => {
    it('shows an administrator the tab, with the line that says AI is off and links to AI settings', async () => {
      stubServer({ ai: false });
      await mount(populated, 'Admin');

      fireEvent.click(screen.getByRole('button', { name: 'AI' }));
      const note = screen.getByTestId('ai-off');
      expect(note.textContent).toMatch(/AI is off\./);
      expect(within(note).getByRole('link', { name: 'AI settings' }).getAttribute('href')).toBe('/admin/ai');
      expect(screen.queryByRole('tab', { name: 'From text' })).toBeNull();
    });

    it('hides the tab from an Editor, who can do nothing about it', async () => {
      stubServer({ ai: false });
      await mount(populated, 'Editor');

      expect(screen.queryByRole('button', { name: 'AI' })).toBeNull();
      expect(screen.queryByTestId('ai-off')).toBeNull();
    });

    it('shows the real panel, not the off line, once a provider is configured', async () => {
      stubServer({ ai: true });
      await mount(populated, 'Admin');

      fireEvent.click(screen.getByRole('button', { name: 'AI' }));
      expect(screen.queryByTestId('ai-off')).toBeNull();
    });
  });
});
