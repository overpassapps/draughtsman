// @vitest-environment jsdom
import { loadTextPanePrefs, saveTextPanePrefs, TEXT_PANE_KEY } from './textPanePrefs.ts';

afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

describe('what the Text pane remembers', () => {
  it('starts closed on the DSL', () => {
    expect(loadTextPanePrefs()).toEqual({ open: false, format: 'dsl' });
  });

  it('remembers open and format separately, each keeping the other', () => {
    saveTextPanePrefs({ open: true });
    saveTextPanePrefs({ format: 'mermaid' });
    expect(loadTextPanePrefs()).toEqual({ open: true, format: 'mermaid' });
    saveTextPanePrefs({ open: false });
    expect(loadTextPanePrefs()).toEqual({ open: false, format: 'mermaid' });
    expect(JSON.parse(localStorage.getItem(TEXT_PANE_KEY) ?? 'null')).toEqual({ open: false, format: 'mermaid' });
  });

  it.each(['', 'not json', 'null', '[]', '{"open":"yes","format":"yaml"}', '{"open":1}'])('treats %j in the store as nothing remembered', (value) => {
    localStorage.setItem(TEXT_PANE_KEY, value);
    expect(loadTextPanePrefs()).toEqual({ open: false, format: 'dsl' });
  });

  it('survives a store that refuses to be read or written', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('blocked', 'SecurityError'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('full', 'QuotaExceededError'); });
    expect(loadTextPanePrefs()).toEqual({ open: false, format: 'dsl' });
    expect(() => { saveTextPanePrefs({ open: true }); }).not.toThrow();
  });
});
