// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { CANVAS_HINT_KEY } from './canvasHint.ts';
import { EmptyCanvasHint } from './EmptyCanvasHint.tsx';

describe('EmptyCanvasHint', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    window.localStorage.clear();
  });

  it('shows the one line on an empty canvas and names the keys', () => {
    render(<EmptyCanvasHint hasNodes={false} />);
    expect(screen.getByRole('note').textContent).toContain('Press S for shapes, C for connectors, or Ctrl+K for everything.');
  });

  it('says Cmd on a Mac', () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)');
    render(<EmptyCanvasHint hasNodes={false} />);
    expect(screen.getByRole('note').textContent).toContain('Cmd+K');
  });

  it('is used up by drawing the first node, and stays away afterwards', () => {
    const view = render(<EmptyCanvasHint hasNodes={false} />);
    view.rerender(<EmptyCanvasHint hasNodes />);
    expect(screen.queryByRole('note')).toBeNull();
    expect(window.localStorage.getItem(CANVAS_HINT_KEY)).toBe('1');
    view.rerender(<EmptyCanvasHint hasNodes={false} />);
    expect(screen.queryByRole('note')).toBeNull();
  });

  it('is not used up by opening a document that already has nodes', () => {
    render(<EmptyCanvasHint hasNodes />);
    expect(screen.queryByRole('note')).toBeNull();
    expect(window.localStorage.getItem(CANVAS_HINT_KEY)).toBeNull();
  });

  it('is dismissed by its close button, for good', () => {
    const view = render(<EmptyCanvasHint hasNodes={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss this hint' }));
    expect(screen.queryByRole('note')).toBeNull();
    expect(window.localStorage.getItem(CANVAS_HINT_KEY)).toBe('1');
    view.unmount();
    render(<EmptyCanvasHint hasNodes={false} />);
    expect(screen.queryByRole('note')).toBeNull();
  });
});
