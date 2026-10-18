// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import type { EditorAction } from './actions.ts';
import { CommandPalette } from './CommandPalette.tsx';
import { isApplePlatform, modifierName, platformShortcut } from './platform.ts';

describe('platformShortcut (P6)', () => {
  it('names Cmd and Option on a Mac and Ctrl and Alt everywhere else', () => {
    expect(platformShortcut('Cmd/Ctrl+Z', true)).toBe('Cmd+Z');
    expect(platformShortcut('Cmd/Ctrl+Alt+]', true)).toBe('Cmd+Option+]');
    expect(platformShortcut('Cmd/Ctrl+Shift+Z', false)).toBe('Ctrl+Shift+Z');
    expect(platformShortcut('Cmd/Ctrl+Alt+[', false)).toBe('Ctrl+Alt+[');
    // Keys that are not modifier-based are left alone.
    expect(platformShortcut('Shift+Tab', true)).toBe('Shift+Tab');
    expect(platformShortcut('L', false)).toBe('L');
    expect(modifierName(true)).toBe('Cmd');
    expect(modifierName(false)).toBe('Ctrl');
  });

  it('reads the platform from the client hints, then navigator.platform, then the user agent', () => {
    expect(isApplePlatform({ userAgentData: { platform: 'macOS' }, platform: 'Win32' })).toBe(true);
    expect(isApplePlatform({ platform: 'MacIntel' })).toBe(true);
    expect(isApplePlatform({ platform: 'iPad' })).toBe(true);
    expect(isApplePlatform({ platform: 'Win32' })).toBe(false);
    expect(isApplePlatform({ platform: '', userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)' })).toBe(true);
    expect(isApplePlatform({ platform: 'Linux x86_64' })).toBe(false);
    expect(isApplePlatform(undefined)).toBe(false);
  });
});

describe('shortcuts drawn for the platform', () => {
  const actions: EditorAction[] = [{ id: 'undo', label: 'Undo', group: 'Edit', shortcut: { label: 'Cmd/Ctrl+Alt+Z', matches: () => false }, enabled: () => true, run: () => undefined }];
  const palette = () => render(<CommandPalette actions={actions} onRun={() => undefined} onClose={() => undefined} />);

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the palette\'s shortcut with Cmd and Option on a Mac', () => {
    vi.stubGlobal('navigator', { platform: 'MacIntel', userAgent: 'Mozilla/5.0 (Macintosh)' });
    palette();
    expect(screen.getByRole('option', { name: 'Undo, Cmd+Option+Z' }).querySelector('kbd')?.textContent).toBe('Cmd+Option+Z');
  });

  it('shows Ctrl and Alt elsewhere', () => {
    vi.stubGlobal('navigator', { platform: 'Win32', userAgent: 'Mozilla/5.0 (Windows NT 10.0)' });
    palette();
    expect(screen.getByRole('option', { name: 'Undo, Ctrl+Alt+Z' }).querySelector('kbd')?.textContent).toBe('Ctrl+Alt+Z');
  });
});
