import { facesFor, withEmbeddedFonts } from './exportFonts.ts';

const svg = (text: string, family = 'Inter, system-ui, sans-serif'): string =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><text font-family="${family}">${text}</text></svg>`;

function fontFetch(calls: string[] = []): typeof fetch {
  return vi.fn<typeof fetch>((input) => {
    calls.push(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
    return Promise.resolve(new Response(new Uint8Array([1, 2, 3, 4]), { status: 200 }));
  });
}

describe('embedding the bundled fonts in an SVG that is about to be rasterised', () => {
  it('adds only the faces a drawing uses: Inter latin for plain text, nothing for a family it does not name', () => {
    expect(facesFor(svg('Order service')).map((face) => [face.family, face.range.startsWith('U+0000')])).toEqual([['Inter', true]]);
    expect(facesFor(svg('Order service', 'Georgia, serif'))).toEqual([]);
    expect(facesFor(svg('x', 'JetBrains Mono, monospace')).map((face) => face.family)).toEqual(['JetBrains Mono']);
  });

  it('adds the latin-ext face only when the drawing has text in its range', () => {
    expect(facesFor(svg('Łódź')).map((face) => face.range.startsWith('U+0100'))).toEqual([false, true]);
    expect(facesFor(svg('Plain ASCII and café')).length).toBe(1);
  });

  it('puts an @font-face per face, as a data URI, straight after the opening svg tag', async () => {
    const calls: string[] = [];

    const embedded = await withEmbeddedFonts(svg('Order service'), fontFetch(calls));

    expect(calls).toHaveLength(1);
    expect(embedded.startsWith('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><style>@font-face{font-family:\'Inter\'')).toBe(true);
    expect(embedded).toContain('src:url(data:font/woff2;base64,AQIDBA==)');
    expect(embedded).toContain('<text font-family="Inter, system-ui, sans-serif">Order service</text>');
  });

  it('leaves the SVG untouched when a font cannot be read, or none is needed', async () => {
    const plain = svg('x', 'Georgia, serif');
    expect(await withEmbeddedFonts(plain, fontFetch())).toBe(plain);

    const failing = vi.fn<typeof fetch>(() => Promise.resolve(new Response('no', { status: 404 })));
    const original = svg('Order service');
    expect(await withEmbeddedFonts(original, failing)).toBe(original);
  });
});
