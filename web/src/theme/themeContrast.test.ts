import { diagramThemes } from '../generated/diagram-themes.ts';
import { applyDiagramOverride } from './orgTheme.ts';
import { chromeContrastIssues, controlBorder, diagramContrastIssues } from './themeContrast.ts';

const OVERPASS_DARK = { brand: '#c0392b', brandText: '#FFFFFF', surface: '#0a0e1a', surfaceRaised: '#151e33', border: '#202a42', text: '#F2F2F2', textMuted: '#9aa4b8', accent: '#e04832', danger: '#FF4D4F' };

describe('chromeContrastIssues', () => {
  it('raises nothing for the shipped dark colours, and their control outline clears 3:1 (QA-03)', () => {
    expect(chromeContrastIssues(OVERPASS_DARK)).toEqual([]);
    expect(controlBorder(OVERPASS_DARK.text, OVERPASS_DARK.surface)).toBe('#83858a');
  });

  it('raises nothing for the shipped light colours either', () => {
    const light = { brand: '#c0392b', brandText: '#FFFFFF', surface: '#FFFFFF', surfaceRaised: '#f4f6fa', border: '#d6dae4', text: '#151e33', textMuted: '#616b7b', accent: '#c0392b', danger: '#D4380D' };
    expect(chromeContrastIssues(light)).toEqual([]);
  });

  it('flags a theme whose text is too close to its surface for the outline mixed from them to read', () => {
    // A mid-grey text on a slightly lighter surface: the 52% mix is nearly the surface itself.
    const issues = chromeContrastIssues({ ...OVERPASS_DARK, text: '#3a4256' });
    const outline = issues.find((issue) => issue.subject === 'controlBorder/surface');
    expect(outline?.minimum).toBe(3);
    expect(outline?.ratio).toBeLessThan(3);
    expect(outline?.message).toMatch(/^control outline \(52% text over surface\) on surface is \d\.\d\d:1, below WCAG AA 3:1$/);
  });

  it('flags text that does not clear 4.5:1 and names both tokens and the measured ratio', () => {
    const issues = chromeContrastIssues({ ...OVERPASS_DARK, text: '#2a3550' });
    const onSurface = issues.find((issue) => issue.subject === 'text/surface');
    expect(onSurface?.minimum).toBe(4.5);
    expect(onSurface?.ratio).toBeLessThan(4.5);
    expect(onSurface?.message).toMatch(/^text on surface is \d\.\d\d:1, below WCAG AA 4\.5:1$/);
    // The fix clears it again.
    expect(chromeContrastIssues({ ...OVERPASS_DARK, text: '#ffffff' }).some((issue) => issue.subject === 'text/surface')).toBe(false);
  });

  it('holds graphics (accent, danger, border) to 3:1, not 4.5:1', () => {
    // #767676 on white is 4.54:1: fine for text and graphics; #959595 is 2.9:1: below 3:1.
    const base = { ...OVERPASS_DARK, surface: '#ffffff', surfaceRaised: '#ffffff', text: '#000000', textMuted: '#000000', brand: '#000000', brandText: '#ffffff', border: '#000000' };
    expect(chromeContrastIssues({ ...base, accent: '#767676' })).toEqual([]);
    expect(chromeContrastIssues({ ...base, accent: '#959595' }).map((issue) => issue.subject)).toEqual(['accent/surface']);
  });

  it('skips a colour that is not hex instead of guessing', () => {
    expect(chromeContrastIssues({ ...OVERPASS_DARK, text: 'red' }).some((issue) => issue.subject === 'text/surface')).toBe(false);
  });
});

describe('diagramContrastIssues', () => {
  it('raises nothing for any shipped diagram theme: the neutral theme\'s container, lane, external and note strokes clear 3:1 (QA-03)', () => {
    expect(diagramContrastIssues(diagramThemes.overpass)).toEqual([]);
    expect(diagramContrastIssues(diagramThemes.neutral)).toEqual([]);
    expect(diagramContrastIssues(diagramThemes.mono)).toEqual([]);
  });

  it('still warns when a stroke is light grey on white, as the old neutral values were', () => {
    const container = diagramThemes.neutral.nodes.container;
    if (container === undefined) {
      throw new Error('the neutral theme has no container kind');
    }
    const faded = { ...diagramThemes.neutral, nodes: { ...diagramThemes.neutral.nodes, container: { ...container, stroke: '#9CA3AF' } } };
    expect(diagramContrastIssues(faded).map((issue) => issue.subject)).toEqual(['nodes.container']);
  });

  it('turns a palette edit into a warning on the kinds that use the colour', () => {
    // Paint the brand red the same as the canvas: every red stroke vanishes into it.
    const themed = applyDiagramOverride({ base: 'overpass', palette: { red: '#151e33' }, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } });
    const subjects = diagramContrastIssues(themed).map((issue) => issue.subject);
    expect(subjects).toContain('nodes.service');
    expect(subjects).toContain('edges.data');
  });

  it('says so when a background is not a hex colour', () => {
    const issues = diagramContrastIssues({ ...diagramThemes.overpass, canvasBackground: 'navy' });
    expect(issues.some((issue) => issue.subject === 'canvas' && issue.ratio === undefined)).toBe(true);
  });
});
