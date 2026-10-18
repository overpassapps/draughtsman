import { diagramThemes } from '../generated/diagram-themes.ts';
import type { DiagramTheme } from './diagramTheme.ts';
import { AA_TEXT, contrastRatio } from './contrast.ts';
import { applyDocumentOverrides, effectiveTheme, resolveEdgeStyle, resolveNodeStyle, surfaceTextColor } from './resolveStyle.ts';

const theme: DiagramTheme = {
  name: 'test',
  description: '',
  palette: { red: '#D71920' },
  fonts: { label: 'Inter', mono: 'Mono' },
  grid: { size: 20, color: '#EEEEEE', visible: true },
  canvasBackground: '#FFFFFF',
  exportBackground: '#FFFFFF',
  nodes: {
    default: { fill: '#FFFFFF', stroke: '#111111', strokeWidth: 1.5, textColor: '#111111', font: 'Inter', dash: 'solid' },
    service: { fill: '#DCE8F8', stroke: '#1F5FBF', strokeWidth: 1.5, textColor: '#1F2937', font: 'Inter', dash: 'solid' },
  },
  edges: {
    default: { stroke: '#111111', strokeWidth: 1.5, dash: 'solid', arrowStart: 'none', arrowEnd: 'arrow' },
    data: { stroke: '#1F5FBF', strokeWidth: 1.5, dash: 'solid', arrowStart: 'none', arrowEnd: 'open' },
  },
};

/** Names every object inherits, which a free-form kind or token name may collide with. */
const PROTOTYPE_NAMES = ['constructor', 'toString', 'valueOf', 'hasOwnProperty'];

describe('resolveNodeStyle', () => {
  it('uses the kind default when the node has no style', () => {
    expect(resolveNodeStyle(theme, { kind: 'service' })).toEqual(theme.nodes.service);
  });

  it('falls back to the theme default for an unknown kind, Object.prototype member names included', () => {
    expect(resolveNodeStyle(theme, { kind: 'mainframe' })).toEqual(theme.nodes.default);
    for (const kind of PROTOTYPE_NAMES) {
      expect(resolveNodeStyle(theme, { kind })).toEqual(theme.nodes.default);
    }
  });

  it('lets the sparse node style override only the fields it names', () => {
    const resolved = resolveNodeStyle(theme, { kind: 'service', style: { fill: '#E8EEF8', dash: 'dashed' } });
    expect(resolved).toEqual({ ...theme.nodes.service, fill: '#E8EEF8', dash: 'dashed' });
  });

  it('follows a named fill with readable text when it names no text colour (QA-06), changing nothing else', () => {
    const resolved = resolveNodeStyle(theme, { kind: 'service', style: { fill: '#000000', dash: 'dashed' } });
    expect(resolved).toEqual({ ...theme.nodes.service, fill: '#000000', dash: 'dashed', textColor: '#ffffff' });
  });

  it('resolves a token: colour reference through the palette and passes a literal through', () => {
    const resolved = resolveNodeStyle(theme, { kind: 'service', style: { fill: 'token:red', stroke: 'rgb(1, 2, 3)', textColor: 'token:red' } });
    expect(resolved).toEqual({ ...theme.nodes.service, fill: theme.palette.red, stroke: 'rgb(1, 2, 3)', textColor: theme.palette.red });
  });

  it('keeps the inherited colour when the palette has no such token', () => {
    const resolved = resolveNodeStyle(theme, { kind: 'service', style: { fill: 'token:nosuch' } });
    expect(resolved).toEqual(theme.nodes.service);
  });

  it('treats an Object.prototype member name as an unknown token', () => {
    for (const name of PROTOTYPE_NAMES) {
      expect(resolveNodeStyle(theme, { kind: 'service', style: { fill: `token:${name}` } })).toEqual(theme.nodes.service);
    }
  });
});

describe('resolveEdgeStyle', () => {
  it('uses the kind default and honours sparse overrides', () => {
    expect(resolveEdgeStyle(theme, { kind: 'data' })).toEqual(theme.edges.data);
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { arrowStart: 'arrow' } })).toEqual({ ...theme.edges.data, arrowStart: 'arrow' });
    expect(resolveEdgeStyle(theme, { kind: 'unknown' })).toEqual(theme.edges.default);
  });

  it('falls back to the edge default for a kind that names an Object.prototype member', () => {
    for (const kind of PROTOTYPE_NAMES) {
      expect(resolveEdgeStyle(theme, { kind })).toEqual(theme.edges.default);
    }
  });

  it('resolves a token: stroke through the palette, keeping literals and unknown tokens honest', () => {
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { stroke: 'token:red' } })).toEqual({ ...theme.edges.data, stroke: theme.palette.red });
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { stroke: '#ABCDEF' } })).toEqual({ ...theme.edges.data, stroke: '#ABCDEF' });
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { stroke: 'token:nosuch' } })).toEqual(theme.edges.data);
  });

  it.each(['one', 'many', 'zeroOrOne', 'oneOrMany', 'zeroOrMany', 'oneAndOnlyOne', 'inheritance', 'aggregation', 'composition'] as const)('honours the %s arrowhead override at either end', (head) => {
    expect(resolveEdgeStyle(theme, { kind: 'flow', style: { arrowStart: head } }).arrowStart).toBe(head);
    expect(resolveEdgeStyle(theme, { kind: 'flow', style: { arrowEnd: head } }).arrowEnd).toBe(head);
  });

  it('drops an unknown arrowhead rather than passing it through', () => {
    const bogus = 'not-an-arrowhead' as never;
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { arrowStart: bogus } })).toEqual(theme.edges.data);
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { arrowEnd: bogus } })).toEqual(theme.edges.data);
  });

  it('drops an unknown dash rather than passing it through', () => {
    const bogus = 'wavy' as never;
    expect(resolveEdgeStyle(theme, { kind: 'data', style: { dash: bogus } })).toEqual(theme.edges.data);
    expect(resolveNodeStyle(theme, { kind: 'service', style: { dash: bogus } })).toEqual(theme.nodes.service);
  });
});

describe('applyDocumentOverrides', () => {
  it('returns the same theme when there is nothing to override', () => {
    expect(applyDocumentOverrides(theme, undefined)).toBe(theme);
    expect(applyDocumentOverrides(theme, {})).toBe(theme);
  });

  it('overrides dotted token paths without mutating the source theme', () => {
    const overridden = applyDocumentOverrides(theme, {
      'nodes.service.fill': '#FF0000',
      'edges.flow.stroke': '#00FF00',
      'grid.size': '40',
      'grid.visible': 'false',
      canvasBackground: '#000000',
      'palette.red': '#AA0000',
      'no.such.path': 'ignored',
    });
    expect(overridden.nodes.service?.fill).toBe('#FF0000');
    expect(overridden.edges.flow).toBeUndefined();
    expect(overridden.grid).toEqual({ size: 40, color: '#EEEEEE', visible: false });
    expect(overridden.canvasBackground).toBe('#000000');
    expect(overridden.palette.red).toBe('#AA0000');
    expect(theme.nodes.service?.fill).toBe('#DCE8F8');
    expect(theme.grid.size).toBe(20);
  });

  it('ignores a path that stops on a group of tokens rather than one token', () => {
    const overridden = applyDocumentOverrides(theme, {
      'nodes.default': '#2A2A2E',
      grid: 'off',
      // A kind is a group whether or not the theme names it, so neither of these may become a string.
      'nodes.badge': '#2A2A2E',
      'edges.custom': '#FF0000',
      // Identity, not paint: a document overrides how a theme paints, never which theme it is.
      name: 'mono',
      'nodes.service.stroke': '#00FF00',
    });
    // Each group keeps its tokens, so a node or edge of that kind still resolves a full style; only the leaf applies.
    expect(overridden.nodes.default).toEqual(theme.nodes.default);
    expect(overridden.grid).toEqual(theme.grid);
    expect(resolveNodeStyle(overridden, { kind: 'mainframe' })).toEqual(theme.nodes.default);
    expect(resolveNodeStyle(overridden, { kind: 'badge' })).toEqual(theme.nodes.default);
    expect(resolveEdgeStyle(overridden, { kind: 'custom' })).toEqual(theme.edges.default);
    expect(overridden.name).toBe(theme.name);
    expect(overridden.nodes.service?.stroke).toBe('#00FF00');
  });

  it('adds a token the theme omits, so a document may name one the theme leaves out', () => {
    const overridden = applyDocumentOverrides(theme, { canvasText: '#ABCDEF', 'palette.gold': '#E0A526' });
    expect(overridden.canvasText).toBe('#ABCDEF');
    expect(resolveNodeStyle(overridden, { kind: 'service', style: { fill: 'token:gold' } }).fill).toBe('#E0A526');
  });

  it('takes a token: colour in an override, as an element style does', () => {
    const overridden = applyDocumentOverrides(theme, {
      'nodes.service.fill': 'token:red',
      'nodes.service.stroke': 'token:nosuch',
      'nodes.service.textColor': '#123456',
      'edges.data.stroke': 'token:red',
      'edges.default.stroke': 'token:nosuch',
    });
    // A known token resolves through the palette, a literal passes through, and an unknown name overrides nothing.
    expect(resolveNodeStyle(overridden, { kind: 'service' })).toEqual({ ...theme.nodes.service, fill: theme.palette.red, textColor: '#123456' });
    expect(resolveEdgeStyle(overridden, { kind: 'data' })).toEqual({ ...theme.edges.data, stroke: theme.palette.red });
    expect(resolveEdgeStyle(overridden, { kind: 'default' })).toEqual(theme.edges.default);
  });

  it('settles the document palette before the references that read it, whatever order they are written in', () => {
    // The reference is written before the palette entry it names, the order that decides nothing if order matters.
    const overridden = applyDocumentOverrides(theme, { 'nodes.service.fill': 'token:red', 'palette.red': '#000000', 'edges.data.stroke': 'token:brand', 'palette.brand': '#00FF00' });
    expect(resolveNodeStyle(overridden, { kind: 'service' }).fill).toBe('#000000');
    expect(resolveEdgeStyle(overridden, { kind: 'data' }).stroke).toBe('#00FF00');
  });

  it('reads a palette override as a colour literal, so either key order gives the same picture', () => {
    // A palette entry naming another would resolve differently depending on which key the document writes first.
    const aliasFirst = applyDocumentOverrides(theme, { 'palette.brand': 'token:red', 'palette.red': '#000000', 'nodes.service.fill': 'token:brand' });
    const aliasLast = applyDocumentOverrides(theme, { 'palette.red': '#000000', 'palette.brand': 'token:red', 'nodes.service.fill': 'token:brand' });
    expect(aliasFirst.palette.brand).toBeUndefined();
    expect(aliasFirst).toEqual(aliasLast);
    // The reference to the dropped entry names nothing, so the kind keeps the fill the theme gave it.
    expect(resolveNodeStyle(aliasFirst, { kind: 'service' }).fill).toBe(theme.nodes.service?.fill);
  });

  it('ignores prototype-polluting paths', () => {
    const overridden = applyDocumentOverrides(theme, { '__proto__.polluted': 'yes', 'constructor.prototype.x': 'y' });
    expect(({} as Record<string, unknown>).polluted).toBeUndefined();
    expect(overridden.name).toBe('test');
  });
});

describe('precedence', () => {
  it('is theme < document overrides < element style', () => {
    const doc = { theme: { 'nodes.service.fill': '#FF0000', 'nodes.service.stroke': '#00FF00' } };
    const effective = effectiveTheme(theme, doc);
    const withoutStyle = resolveNodeStyle(effective, { kind: 'service' });
    expect(withoutStyle.fill).toBe('#FF0000');
    expect(withoutStyle.stroke).toBe('#00FF00');
    const withStyle = resolveNodeStyle(effective, { kind: 'service', style: { fill: '#0000FF' } });
    expect(withStyle.fill).toBe('#0000FF');
    expect(withStyle.stroke).toBe('#00FF00');
  });
});

describe('surfaceTextColor', () => {
  const legible = (fg: string | undefined, bg: string): boolean => (contrastRatio(fg ?? '', bg) ?? 0) >= AA_TEXT;

  it('derives dark text on a light surface and light text on a dark one', () => {
    const twoSurfaces = { ...theme, canvasBackground: '#1A1A1C', exportBackground: '#FFFFFF' };
    const onCanvas = surfaceTextColor(twoSurfaces, 'canvas');
    const onExport = surfaceTextColor(twoSurfaces, 'export');
    expect(legible(onCanvas, '#1A1A1C')).toBe(true);
    expect(legible(onExport, '#FFFFFF')).toBe(true);
    expect(onCanvas).not.toBe(onExport);
  });

  it('prefers the theme tokens when present', () => {
    const explicit = { ...theme, canvasText: '#123456', exportText: '#654321' };
    expect(surfaceTextColor(explicit, 'canvas')).toBe('#123456');
    expect(surfaceTextColor(explicit, 'export')).toBe('#654321');
  });

  it('follows a document override of the surface background', () => {
    const dark = effectiveTheme(theme, { theme: { canvasBackground: '#000000' } });
    expect(legible(surfaceTextColor(dark, 'canvas'), '#000000')).toBe(true);
    const named = effectiveTheme(theme, { theme: { canvasText: '#ABCDEF' } });
    expect(surfaceTextColor(named, 'canvas')).toBe('#ABCDEF');
  });

  it('names no colour at all when the background is not a hex colour, rather than guessing one', () => {
    expect(surfaceTextColor({ ...theme, canvasBackground: 'rgb(255, 255, 255)' }, 'canvas')).toBeUndefined();
    expect(surfaceTextColor({ ...theme, canvasBackground: 'FFFFFF' }, 'canvas')).toBeUndefined();
    expect(surfaceTextColor({ ...theme, exportBackground: 'transparent' }, 'export')).toBeUndefined();
  });
});

describe('the shipped diagram themes and the selection colour', () => {
  // The selection outline is the chrome accent (brand red, deeper on light). A participant that draws in the same red looks
  // selected when it is not, so no shipped theme may give participants either accent as their stroke.
  it.each(Object.entries(diagramThemes))('%s draws a participant in neither chrome accent red', (_name, shipped) => {
    const stroke = shipped.nodes.participant?.stroke.toLowerCase();
    expect(stroke).toBeDefined();
    expect(['#e04832', '#c0392b']).not.toContain(stroke);
  });
});

describe('resolveNodeStyle text colour on a chosen fill (QA-06)', () => {
  // The Overpass diagram theme draws near-white text, which is unreadable on a light swatch.
  const overpass = diagramThemes.overpass;
  const base = overpass.nodes.service ?? overpass.nodes.default;

  it('turns the theme\'s light text dark on a yellow fill, and it then reads', () => {
    const style = resolveNodeStyle(overpass, { kind: 'service', style: { fill: '#E5B83A' } });
    expect(style.textColor).toBe('#0f172a');
    expect(contrastRatio(style.textColor, '#E5B83A') ?? 0).toBeGreaterThanOrEqual(AA_TEXT);
    // The defect: the theme's own text colour does not read there.
    expect(contrastRatio(base.textColor, '#E5B83A') ?? 99).toBeLessThan(AA_TEXT);
  });

  it('turns dark theme text white on a dark fill', () => {
    const style = resolveNodeStyle(theme, { kind: 'service', style: { fill: '#0B1F44' } });
    expect(style.textColor).toBe('#ffffff');
  });

  it('keeps the theme\'s text colour where it already reads on the fill', () => {
    expect(resolveNodeStyle(theme, { kind: 'service', style: { fill: '#FFFFFF' } }).textColor).toBe('#1F2937');
  });

  it('keeps a text colour the document names, however poorly it reads', () => {
    expect(resolveNodeStyle(overpass, { kind: 'service', style: { fill: '#E5B83A', textColor: '#ffffff' } }).textColor).toBe('#ffffff');
  });

  it('keeps a palette token as the text colour, resolved, and leaves a see-through fill alone', () => {
    expect(resolveNodeStyle(theme, { kind: 'service', style: { fill: '#FFFFFF', textColor: 'token:red' } }).textColor).toBe('#D71920');
    expect(resolveNodeStyle(overpass, { kind: 'service', style: { fill: 'transparent' } }).textColor).toBe(base.textColor);
  });
});
