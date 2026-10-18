// @vitest-environment jsdom
import './loadTolerance.ts';
import { fireEvent, render, screen } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import type { PublicThemes } from '../model/themesClient.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

const document = parseDocument(orderService);

const themes: PublicThemes = {
  defaultId: 'acme',
  themes: [
    { id: 'acme', name: 'Acme', diagram: { base: 'overpass', palette: {}, canvasBackground: '#123456', exportBackground: null, gridColor: null, fonts: { label: null, mono: null } } },
    { id: 'globex', name: 'Globex', diagram: { base: 'mono', palette: {}, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } } },
  ],
};

function themeName(container: HTMLElement): string | null {
  return container.querySelector('svg[data-diagram-theme]')?.getAttribute('data-diagram-theme') ?? null;
}

describe('DocumentViewer organisation themes', () => {
  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    // jsdom lays nothing out; give the canvas a real box so the SVG renders.
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('opens in the organisation default and draws with its colours', () => {
    const { container } = render(<DocumentViewer document={document} orgThemes={themes} />);
    const select = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Theme' });
    expect(select.value).toBe('org:acme');
    expect(themeName(container)).toBe('Acme');
    expect(container.innerHTML).toContain('#123456');
  });

  it('lists the organisation themes after the built-in ones and lets the user pick either kind', () => {
    const { container } = render(<DocumentViewer document={document} orgThemes={themes} />);
    const select = screen.getByRole<HTMLSelectElement>('combobox', { name: 'Theme' });
    expect(Array.from(select.options).map((o) => o.textContent)).toEqual(['overpass', 'mono', 'neutral', 'Acme (organisation)', 'Globex (organisation)']);

    fireEvent.change(select, { target: { value: 'org:globex' } });
    expect(themeName(container)).toBe('Globex');
    expect(container.innerHTML).not.toContain('#123456');

    fireEvent.change(select, { target: { value: 'neutral' } });
    expect(themeName(container)).toBe('neutral');
  });

  it('uses the shipped default when there is no organisation default, or it names a theme that no longer exists', () => {
    const none = render(<DocumentViewer document={document} orgThemes={{ ...themes, defaultId: null }} />);
    expect(themeName(none.container)).toBe('overpass');
    none.unmount();
    const stale = render(<DocumentViewer document={document} orgThemes={{ ...themes, defaultId: 'deleted' }} />);
    expect(themeName(stale.container)).toBe('overpass');
    stale.unmount();
    const absent = render(<DocumentViewer document={document} />);
    expect(themeName(absent.container)).toBe('overpass');
  });

  it('applies a default that arrives after the editor opened, until the user chooses', () => {
    const { container, rerender } = render(<DocumentViewer document={document} />);
    expect(themeName(container)).toBe('overpass');
    rerender(<DocumentViewer document={document} orgThemes={themes} />);
    expect(themeName(container)).toBe('Acme');

    fireEvent.change(screen.getByRole('combobox', { name: 'Theme' }), { target: { value: 'mono' } });
    rerender(<DocumentViewer document={document} orgThemes={{ ...themes, defaultId: 'globex' }} />);
    expect(themeName(container)).toBe('mono');
  });
});
