import { applyDiagramOverride, CHROME_KEYS, type OrgDiagram } from './orgTheme.ts';
import { chromeContrastIssues, diagramContrastIssues } from './themeContrast.ts';

/**
 * The drafts the server's colour extractor gives for its fixture stylesheets (recorded by
 * `tests/Draughtsman.Server.Tests/ThemeExtraction/ThemeExtractionGoldenTests.cs`), run through this page's own contrast checks.
 * The C# side holds a mirror of the chrome checks; the diagram checks exist only here, so this is what proves that the diagram
 * palette the extractor picks does not add a single contrast warning to the base theme it starts from.
 */
const recorded = import.meta.glob<Draft>('../../../tests/Draughtsman.Server.Tests/Fixtures/ThemeExtraction/*.draft.json', { eager: true, import: 'default' });

interface Draft {
  mode: string;
  chrome: { dark: Record<string, string>; light: Record<string, string> };
  diagram: { base: string; palette: Record<string, string> };
}

const files: [string, Draft][] = Object.entries(recorded)
  .map(([path, draft]): [string, Draft] => [path.slice(path.lastIndexOf('/') + 1), draft])
  .sort(([a], [b]) => a.localeCompare(b));

function diagram(draft: Draft): OrgDiagram {
  return { base: draft.diagram.base, palette: draft.diagram.palette, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } };
}

describe('drafts the extractor gives for its fixtures', () => {
  it('has the fixtures it is meant to check', () => {
    expect(files.length).toBeGreaterThanOrEqual(7);
    expect(files.map(([name]) => name)).toContain('dark-brand-site.draft.json');
  });

  it.each(files)('%s: every chrome colour is present and both colour sets pass the Themes page contrast checks', (_file, draft) => {
    for (const set of ['dark', 'light'] as const) {
      expect(Object.keys(draft.chrome[set]).sort()).toEqual([...CHROME_KEYS].sort());
      expect(chromeContrastIssues(draft.chrome[set])).toEqual([]);
    }
  });

  it.each(files)('%s: the diagram palette adds no contrast warning to the base theme it starts from', (_file, draft) => {
    const baseline = diagramContrastIssues(applyDiagramOverride({ ...diagram(draft), palette: {} })).map((issue) => issue.message);
    const themed = diagramContrastIssues(applyDiagramOverride(diagram(draft))).map((issue) => issue.message);
    expect(themed).toEqual(baseline);
  });

  it('can fail: a palette colour that does not read on both backgrounds is caught by the same check', () => {
    const draft = files.find(([name]) => name === 'dark-brand-site.draft.json')?.[1];
    if (draft === undefined) {
      throw new Error('dark-brand-site.draft.json was not found');
    }
    expect(draft.diagram.base).toBe('overpass');
    const broken = applyDiagramOverride({ ...diagram(draft), palette: { ...draft.diagram.palette, red: '#ffee88' } });
    expect(diagramContrastIssues(broken).length).toBeGreaterThan(0);
    // And chrome: muted text a shade from the surface fails.
    expect(chromeContrastIssues({ ...draft.chrome.light, textMuted: '#f0f0f0' }).length).toBeGreaterThan(0);
  });
});
