// @vitest-environment jsdom
import { openPrintTarget, paperMm, paperOf, printLayout, printPageHtml, type PrintEnvironment } from './printExport.ts';
import type { PdfExportOptions } from './pdfExport.ts';

const MM = 96 / 25.4;
const svgOn = (paper: string, width = 400, height = 300): string =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="${String(width)}" height="${String(height)}" viewBox="0 0 ${String(width)} ${String(height)}" style="display:block"><rect class="canvas-background" width="${String(width)}" height="${String(height)}" fill="${paper}"/><g class="world"><text>Order service</text></g></svg>`;
const options = (pageSize: PdfExportOptions['pageSize'], orientation: PdfExportOptions['orientation'], marginMm: number): PdfExportOptions => ({ pageSize, orientation, marginMm });

/** The one `@page` rule's `size` and `margin`, read back out of the printed document. */
const pageRule = (html: string): { size: string; margin: string } => {
  const match = /@page\{size:([^;]+);margin:([^}]+)\}/.exec(html);
  if (match === null) {
    throw new Error('no @page rule');
  }
  return { size: match[1] ?? '', margin: match[2] ?? '' };
};

describe('printLayout', () => {
  it('knows the papers in millimetres, turned for landscape', () => {
    expect(paperMm('a4', 'portrait')).toEqual({ width: 210, height: 297 });
    expect(paperMm('a4', 'landscape')).toEqual({ width: 297, height: 210 });
    expect(paperMm('a3', 'landscape')).toEqual({ width: 420, height: 297 });
    expect(paperMm('letter', 'portrait')).toEqual({ width: 215.9, height: 279.4 });
  });

  it('shrinks a drawing that is too big to fit inside the margins, keeping its shape', () => {
    // A4 landscape with 10 mm margins leaves 277 x 189.7 mm, i.e. 1046.9 x 716.9 px; 2000 x 1000 is limited by its width.
    const layout = printLayout(2000, 1000, options('a4', 'landscape', 10));
    expect(layout.scale).toBeCloseTo(1046.93 / 2000, 3);
    expect(layout.drawingWidthPx).toBeCloseTo(1046.9, 0);
    expect(layout.drawingWidthPx).toBeLessThanOrEqual(277 * MM);
    expect(layout.drawingHeightPx / layout.drawingWidthPx).toBeCloseTo(0.5, 3);
    // And one limited by its height instead: 800 x 2000 portrait-shaped on a landscape page.
    const tall = printLayout(800, 2000, options('a4', 'landscape', 10));
    expect(tall.drawingHeightPx).toBeLessThanOrEqual((210 - 20 - 0.3) * MM);
    expect(tall.drawingHeightPx).toBeCloseTo(716.9, 0);
  });

  it('never scales a small drawing up', () => {
    const layout = printLayout(400, 300, options('a3', 'landscape', 10));
    expect(layout.scale).toBe(1);
    expect(layout.drawingWidthPx).toBe(400);
    expect(layout.drawingHeightPx).toBe(300);
  });

  it('cuts the page to the drawing for Fit, margins added on every side, nothing scaled', () => {
    const layout = printLayout(400, 300, options('fit', 'portrait', 10));
    expect(layout.scale).toBe(1);
    expect(layout.pageWidthPx).toBeCloseTo(400 + 20 * MM, 6);
    expect(layout.pageHeightPx).toBeCloseTo(300 + 20 * MM, 6);
    expect(layout.pageSize).toBe('475.59px 375.59px');
  });

  it('gives a margin so large that nothing is left a floor rather than a zero or negative box', () => {
    const layout = printLayout(400, 300, options('a4', 'portrait', 50));
    expect(layout.drawingWidthPx).toBeGreaterThan(0);
    expect(layout.drawingHeightPx).toBeGreaterThan(0);
  });
});

describe('printPageHtml', () => {
  it.each([
    ['a4', 'landscape', '297mm 210mm'],
    ['a4', 'portrait', '210mm 297mm'],
    ['a3', 'landscape', '420mm 297mm'],
    ['a3', 'portrait', '297mm 420mm'],
    ['letter', 'portrait', '215.9mm 279.4mm'],
    ['letter', 'landscape', '279.4mm 215.9mm'],
  ] as const)('prints %s %s on a %s page', (size, orientation, expected) => {
    const html = printPageHtml(svgOn('#ffffff'), 400, 300, options(size, orientation, 10), 'Order service');
    expect(pageRule(html).size).toBe(expected);
  });

  it('prints Fit on a page cut to the drawing', () => {
    const html = printPageHtml(svgOn('#ffffff'), 400, 300, options('fit', 'landscape', 10), 'Order service');
    expect(pageRule(html).size).toBe('475.59px 375.59px');
    expect(html).toContain('.sheet{width:475.59px;height:375.59px;');
  });

  it('puts the chosen margin round the drawing and leaves the browser no margin of its own to print a header or footer in', () => {
    const fifteen = printPageHtml(svgOn('#ffffff'), 400, 300, options('a4', 'landscape', 15), 'd');
    const none = printPageHtml(svgOn('#ffffff'), 400, 300, options('a4', 'landscape', 0), 'd');
    expect(fifteen).toContain('padding:15mm');
    expect(none).toContain('padding:0mm');
    expect(fifteen).not.toContain('padding:0mm');
    expect(pageRule(fifteen).margin).toBe('0');
    // The sheet is the paper less a sliver, so the content can never spill onto a second page.
    expect(fifteen).toContain('.sheet{width:297mm;height:209.7mm;');
  });

  it('scales a big drawing down to the margins, and a small one not at all, in the svg’s own size', () => {
    const big = printPageHtml(svgOn('#ffffff', 2000, 1000), 2000, 1000, options('a4', 'landscape', 10), 'd');
    expect(big).toMatch(/\.sheet svg\{display:block;flex:none;width:1046\.9\d?px;height:523\.4\d?px\}/);
    const small = printPageHtml(svgOn('#ffffff'), 400, 300, options('a4', 'landscape', 10), 'd');
    expect(small).toContain('.sheet svg{display:block;flex:none;width:400px;height:300px}');
  });

  it('asks the browser to print backgrounds and paints the page the drawing’s own paper colour', () => {
    const dark = printPageHtml(svgOn('#1b1f24'), 400, 300, options('a4', 'landscape', 10), 'd');
    expect(dark).toContain('print-color-adjust:exact');
    expect(dark).toContain('-webkit-print-color-adjust:exact');
    expect(dark).toContain('html,body{margin:0;padding:0;background:#1b1f24}');
    const white = printPageHtml(svgOn('#ffffff'), 400, 300, options('a4', 'landscape', 10), 'd');
    expect(white).toContain('background:#ffffff}');
    expect(white).not.toContain('#1b1f24');
  });

  it('reads the paper only from a plain colour, falling back to white', () => {
    expect(paperOf(svgOn('#fafafa'))).toBe('#fafafa');
    expect(paperOf(svgOn('url(javascript:alert(1))'))).toBe('#ffffff');
    expect(paperOf('<svg></svg>')).toBe('#ffffff');
  });

  it('carries the exported svg whole, so anything added to the export prints, and names the file after the title safely', () => {
    const svg = svgOn('#ffffff');
    const html = printPageHtml(svg, 400, 300, options('a4', 'landscape', 10), 'Q3 <script>alert(1)</script> & "plan"');
    expect(html).toContain(`<div class="sheet">${svg}</div>`);
    expect(html).toContain('<title>Q3 &lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;plan&quot;</title>');
    expect(html).not.toContain('<script>');
  });

  it('holds no script of its own: the opener prints', () => {
    expect(printPageHtml(svgOn('#ffffff'), 400, 300, options('a4', 'landscape', 10), 'd')).not.toMatch(/<script/i);
  });
});

describe('openPrintTarget', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    document.body.innerHTML = '';
  });

  const fakePopup = () => {
    const listeners = new Map<string, () => void>();
    const written: string[] = [];
    const popup = {
      document: { readyState: 'complete', open: vi.fn(), write: (html: string) => written.push(html), close: vi.fn(), fonts: undefined },
      addEventListener: (type: string, listener: () => void) => listeners.set(type, listener),
      focus: vi.fn(),
      print: vi.fn(),
      close: vi.fn(),
    };
    return { popup, listeners, written };
  };

  it('prints from a window of its own, and closes it when the print dialog is done', async () => {
    const { popup, listeners, written } = fakePopup();
    const environment: PrintEnvironment = { open: () => popup as unknown as Window, document };
    const target = openPrintTarget(environment);
    expect(target.kind).toBe('window');

    await target.print('<html>view</html>');
    expect(written).toEqual(['<html>view</html>']);
    expect(popup.print).toHaveBeenCalledTimes(1);
    expect(document.querySelector('iframe')).toBeNull();
    expect(popup.close).not.toHaveBeenCalled();

    listeners.get('afterprint')?.();
    expect(popup.close).toHaveBeenCalledTimes(1);
  });

  it('opens its window before anything is awaited, because that is the moment the click still counts', () => {
    const open = vi.fn(() => fakePopup().popup as unknown as Window);
    openPrintTarget({ open, document });
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('falls back to a hidden frame in the page when the popup is blocked, and prints from that', async () => {
    const printed = vi.fn();
    const frameWindow = { document: { fonts: undefined }, addEventListener: vi.fn(), focus: vi.fn(), print: printed };
    vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockReturnValue(frameWindow as unknown as Window);

    const target = openPrintTarget({ open: () => null, document });
    expect(target.kind).toBe('frame');
    const frame = document.querySelector<HTMLIFrameElement>('iframe[data-print-frame]');
    expect(frame).not.toBeNull();
    expect(frame?.getAttribute('aria-hidden')).toBe('true');

    const done = target.print('<html>view</html>');
    frame?.dispatchEvent(new Event('load'));
    await done;
    expect(frame?.srcdoc).toBe('<html>view</html>');
    expect(printed).toHaveBeenCalledTimes(1);
  });

  it('removes the frame it made once the print dialog is done, and a stale one before making another', async () => {
    let afterprint: (() => void) | undefined;
    const frameWindow = { document: { fonts: undefined }, addEventListener: (_type: string, listener: () => void) => { afterprint = listener; }, focus: vi.fn(), print: vi.fn() };
    vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockReturnValue(frameWindow as unknown as Window);

    const first = openPrintTarget({ open: () => null, document });
    const second = openPrintTarget({ open: () => null, document });
    expect(document.querySelectorAll('iframe[data-print-frame]')).toHaveLength(1);

    const frame = document.querySelector<HTMLIFrameElement>('iframe[data-print-frame]');
    const done = second.print('x');
    frame?.dispatchEvent(new Event('load'));
    await done;
    afterprint?.();
    expect(document.querySelector('iframe[data-print-frame]')).toBeNull();
    first.dispose();
  });

  it('throws its frame away when asked to, without printing', () => {
    const printed = vi.fn();
    vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockReturnValue({ print: printed } as unknown as Window);
    const target = openPrintTarget({ open: () => null, document });
    expect(document.querySelector('iframe[data-print-frame]')).not.toBeNull();
    target.dispose();
    expect(document.querySelector('iframe[data-print-frame]')).toBeNull();
    expect(printed).not.toHaveBeenCalled();
  });
});
