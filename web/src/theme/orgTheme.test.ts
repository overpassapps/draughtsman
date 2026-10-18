import { diagramThemes } from '../generated/diagram-themes.ts';
import { applyDiagramOverride, chromeProperty, chromeStyleVars, isHexColour, isSafeFont, type OrgChrome, type OrgDiagram } from './orgTheme.ts';

const NO_OVERRIDE: OrgDiagram = { base: 'overpass', palette: {}, canvasBackground: null, exportBackground: null, gridColor: null, fonts: { label: null, mono: null } };

describe('applyDiagramOverride', () => {
  it('leaves the base theme untouched when nothing is overridden', () => {
    const themed = applyDiagramOverride(NO_OVERRIDE);
    expect(themed.nodes).toEqual(diagramThemes.overpass.nodes);
    expect(themed.edges).toEqual(diagramThemes.overpass.edges);
    expect(themed.canvasBackground).toBe(diagramThemes.overpass.canvasBackground);
  });

  it('replaces a palette colour wherever node and edge styles use it, and nowhere else', () => {
    const themed = applyDiagramOverride({ ...NO_OVERRIDE, palette: { red: '#00aa55' } });
    const base = diagramThemes.overpass;
    // The base's service stroke and data-edge stroke were the brand red; they follow the palette entry.
    expect(base.nodes.service?.stroke.toLowerCase()).toBe('#e04832');
    expect(themed.nodes.service?.stroke).toBe('#00aa55');
    expect(themed.edges.data?.stroke).toBe('#00aa55');
    expect(themed.palette.red).toBe('#00aa55');
    // A colour the palette entry did not name is unchanged: redDeep (the terminal fill) stays.
    expect(themed.nodes.terminal?.fill.toLowerCase()).toBe('#c0392b');
    expect(themed.nodes.process?.fill).toBe(base.nodes.process?.fill);
  });

  it('ignores a palette name the base does not have and a value that is not a colour', () => {
    const themed = applyDiagramOverride({ ...NO_OVERRIDE, palette: { notAColour: '#00aa55', red: 'url(x)' } });
    expect(themed.nodes).toEqual(diagramThemes.overpass.nodes);
    expect(themed.palette.notAColour).toBeUndefined();
  });

  it('lets an explicit background win over a palette swap of the same value', () => {
    // charcoal #151e33 is the overpass canvas background; swapping it recolours the canvas until a direct value is given.
    expect(applyDiagramOverride({ ...NO_OVERRIDE, palette: { charcoal: '#222222' } }).canvasBackground).toBe('#222222');
    const direct = applyDiagramOverride({ ...NO_OVERRIDE, palette: { charcoal: '#222222' }, canvasBackground: '#abcdef', exportBackground: '#fafafa', gridColor: '#010203' });
    expect(direct.canvasBackground).toBe('#abcdef');
    expect(direct.exportBackground).toBe('#fafafa');
    expect(direct.grid.color).toBe('#010203');
  });

  it('applies a label font to the theme and to every node that used the base label font', () => {
    const themed = applyDiagramOverride({ ...NO_OVERRIDE, fonts: { label: 'Fira Sans, sans-serif', mono: null } });
    expect(themed.fonts.label).toBe('Fira Sans, sans-serif');
    expect(Object.values(themed.nodes).every((style) => style.font === 'Fira Sans, sans-serif')).toBe(true);
    expect(themed.fonts.mono).toBe(diagramThemes.overpass.fonts.mono);
  });

  it('starts from the named base, and falls back to overpass for one it does not know', () => {
    expect(applyDiagramOverride({ ...NO_OVERRIDE, base: 'mono' }).canvasBackground).toBe(diagramThemes.mono.canvasBackground);
    expect(applyDiagramOverride({ ...NO_OVERRIDE, base: 'nope' }).canvasBackground).toBe(diagramThemes.overpass.canvasBackground);
  });

  it('does not mutate the compiled base theme', () => {
    const before = JSON.stringify(diagramThemes.overpass);
    applyDiagramOverride({ ...NO_OVERRIDE, palette: { red: '#00aa55' }, canvasBackground: '#000000' });
    expect(JSON.stringify(diagramThemes.overpass)).toBe(before);
  });
});

describe('colour and font shape checks', () => {
  it.each(['#fff', '#E04832', '#e04832'])('accepts %s', (value) => {
    expect(isHexColour(value)).toBe(true);
  });
  it.each(['red', '#12345', '#gggggg', 'rgb(0,0,0)', '#fff;', '', '#e0483200'])('refuses %s', (value) => {
    expect(isHexColour(value)).toBe(false);
  });
  it.each(['Inter', 'Inter, system-ui, sans-serif', '"Source Sans 3", Inter', "'Fira Sans', sans-serif"])('accepts the font list %s', (value) => {
    expect(isSafeFont(value)).toBe(true);
  });
  it.each(['Inter;} body{x:y', 'url(x)', 'Inter</style>', '"Inter', 'Inter\\', 'a/*b*/'])('refuses the font list %s', (value) => {
    expect(isSafeFont(value)).toBe(false);
  });
});

describe('chromeStyleVars', () => {
  const chrome: OrgChrome = {
    dark: { brand: '#111111', brandText: '#ffffff', surface: '#000000', surfaceRaised: '#0a0a0a', border: '#222222', text: '#eeeeee', textMuted: '#aaaaaa', accent: '#ff0000', danger: '#ff4d4f' },
    light: { brand: '#bbbbbb', brandText: '#000000', surface: '#ffffff', surfaceRaised: '#f4f4f4', border: '#dddddd', text: '#111111', textMuted: '#555555', accent: '#cc0000', danger: '#d4380d' },
    radius: 8,
    fonts: { ui: 'Inter, sans-serif', mono: 'JetBrains Mono, monospace' },
  };

  it('scopes the chosen set as kebab-cased custom properties', () => {
    const dark = chromeStyleVars(chrome, 'dark') as Record<string, string>;
    const light = chromeStyleVars(chrome, 'light') as Record<string, string>;
    expect(chromeProperty('surfaceRaised')).toBe('--chrome-surface-raised');
    expect(dark['--chrome-brand']).toBe('#111111');
    expect(light['--chrome-brand']).toBe('#bbbbbb');
    expect(dark['--chrome-radius']).toBe('8px');
  });

  it('leaves out a half-typed colour and an unsafe font rather than writing them into a style', () => {
    const vars = chromeStyleVars({ ...chrome, dark: { ...chrome.dark, brand: '#12', text: 'red;}' }, fonts: { ui: 'Inter;}', mono: 'Mono' } }, 'dark') as Record<string, string>;
    expect(vars['--chrome-brand']).toBeUndefined();
    expect(vars['--chrome-text']).toBeUndefined();
    expect(vars['--chrome-font-ui']).toBeUndefined();
    expect(vars['--chrome-surface']).toBe('#000000');
    expect(vars['--chrome-font-mono']).toBe('Mono');
  });
});
