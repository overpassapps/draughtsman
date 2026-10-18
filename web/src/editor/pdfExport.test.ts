import { DEFAULT_PDF_OPTIONS, clampMargin, pdfExportUrl, requestPdf } from './pdfExport.ts';

describe('pdfExportUrl', () => {
  it('carries the page size, orientation and margin, and encodes the id', () => {
    expect(pdfExportUrl('doc a/b', { pageSize: 'a3', orientation: 'portrait', marginMm: 12.5 })).toBe('/api/documents/doc%20a%2Fb/pdf?pageSize=a3&orientation=portrait&margin=12.5');
  });

  it('opens on A4 landscape with a 10 mm margin', () => {
    expect(DEFAULT_PDF_OPTIONS).toEqual({ pageSize: 'a4', orientation: 'landscape', marginMm: 10 });
  });

  it('never sends a margin the server would refuse', () => {
    expect(clampMargin(-5)).toBe(0);
    expect(clampMargin(80)).toBe(50);
    expect(clampMargin(Number.NaN)).toBe(10);
    expect(pdfExportUrl('d', { pageSize: 'fit', orientation: 'landscape', marginMm: 999 })).toContain('margin=50');
  });
});

describe('requestPdf', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('returns the PDF bytes on success', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('%PDF-1.4', { status: 200 }))));
    const blob = await requestPdf('d', DEFAULT_PDF_OPTIONS);
    expect(await blob.text()).toBe('%PDF-1.4');
  });

  it("rejects with the server's own words when it answers 501, so the install command reaches the user", async () => {
    const problem = { title: 'PDF export is not available', detail: 'Install the browser with `npx playwright@1.63.0 install chromium`.' };
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(problem), { status: 501, headers: { 'Content-Type': 'application/problem+json' } }))));
    await expect(requestPdf('d', DEFAULT_PDF_OPTIONS)).rejects.toThrow('npx playwright@1.63.0 install chromium');
  });

  it('falls back to the status when the refusal is not a problem document', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response('boom', { status: 500 }))));
    await expect(requestPdf('d', DEFAULT_PDF_OPTIONS)).rejects.toThrow('Server returned 500');
  });
});
