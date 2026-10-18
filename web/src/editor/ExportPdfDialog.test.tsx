// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ExportPdfDialog } from './ExportPdfDialog.tsx';
import { installCommandOf, pdfProblemOf, type PdfExportOptions } from './pdfExport.ts';

const HINT = 'server-side PDF export needs the optional Chromium component. Install the browser with `npx playwright@1.63.0 install chromium`, then set export.chromium.enabled to true.';

function renderDialog(props: Partial<Parameters<typeof ExportPdfDialog>[0]> = {}) {
  const onPdf = vi.fn();
  const onPrint = vi.fn<(options: PdfExportOptions) => Promise<void>>(() => Promise.resolve());
  const onClose = vi.fn();
  render(<ExportPdfDialog available hint={undefined} documentId="doc_1" onPdf={onPdf} onPrint={onPrint} onClose={onClose} {...props} />);
  return { onPdf, onPrint, onClose };
}

describe('ExportPdfDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('takes the install command out of the server hint', () => {
    expect(installCommandOf(HINT)).toBe('npx playwright@1.63.0 install chromium');
    expect(installCommandOf('no command here')).toBeUndefined();
    expect(installCommandOf(undefined)).toBeUndefined();
  });

  it('shows the options form opened on A4 landscape with a 10 mm margin when the component is available', () => {
    renderDialog();
    expect((screen.getByLabelText<HTMLSelectElement>('Page size')).value).toBe('a4');
    expect((screen.getByLabelText<HTMLSelectElement>('Orientation')).value).toBe('landscape');
    expect((screen.getByLabelText<HTMLInputElement>('Margin (mm)')).value).toBe('10');
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('disables orientation for Fit, where the page takes the drawing’s shape', () => {
    renderDialog();
    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('Page size'), { target: { value: 'fit' } });
    expect((screen.getByLabelText<HTMLSelectElement>('Orientation')).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('Page size'), { target: { value: 'a3' } });
    expect((screen.getByLabelText<HTMLSelectElement>('Orientation')).disabled).toBe(false);
  });

  it('refuses a margin outside 0 to 50 mm before asking the server', () => {
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);
    renderDialog();
    fireEvent.change(screen.getByLabelText<HTMLInputElement>('Margin (mm)'), { target: { value: '80' } });
    expect(screen.getByRole('alert').textContent).toContain('between 0 and 50');
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Export PDF' }).disabled).toBe(true);
    fireEvent.change(screen.getByLabelText<HTMLInputElement>('Margin (mm)'), { target: { value: '0' } });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Export PDF' }).disabled).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('hands the PDF to the caller, asking the server with the chosen options', async () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(new Response('%PDF-1.4', { status: 200 })));
    vi.stubGlobal('fetch', fetchMock);
    const { onPdf } = renderDialog();
    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('Page size'), { target: { value: 'a3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Export PDF' }));

    await waitFor(() => { expect(onPdf).toHaveBeenCalledTimes(1); });
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/documents/doc_1/pdf?pageSize=a3&orientation=landscape&margin=10');
  });

  it('keeps the dialog open and shows the server’s refusal inline', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ detail: HINT }), { status: 501 }))));
    const { onPdf, onClose } = renderDialog();
    fireEvent.click(screen.getByRole('button', { name: 'Export PDF' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('npx playwright@1.63.0 install chromium');
    expect(onPdf).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('with no server PDF there is no dead end: the same form, one line saying what it does, and a button that prints', () => {
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);
    renderDialog({ available: false, hint: HINT });

    const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
    expect(within(dialog).getByText('Opens your browser’s print dialog: choose Save as PDF.')).not.toBeNull();
    expect((within(dialog).getByLabelText<HTMLSelectElement>('Page size')).value).toBe('a4');
    expect(within(dialog).getByRole('button', { name: 'Open print dialog' })).not.toBeNull();
    // Nothing about a missing install, no status notice, no server route, no install command.
    expect(within(dialog).queryByRole('status')).toBeNull();
    expect(dialog.textContent).not.toContain('not installed');
    expect(dialog.textContent).not.toContain('unavailable');
    expect(within(dialog).queryByText(/npx playwright/)).toBeNull();
    expect(within(dialog).queryByRole('button', { name: 'Export PDF' })).toBeNull();
    expect(within(dialog).queryByRole('button', { name: 'Print in browser…' })).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('with no server PDF, prints with the chosen page size, orientation and margin, then closes', async () => {
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);
    const { onPrint, onClose } = renderDialog({ available: false, hint: undefined });
    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('Page size'), { target: { value: 'a3' } });
    fireEvent.change(screen.getByLabelText<HTMLSelectElement>('Orientation'), { target: { value: 'portrait' } });
    fireEvent.change(screen.getByLabelText<HTMLInputElement>('Margin (mm)'), { target: { value: '15' } });
    fireEvent.click(screen.getByRole('button', { name: 'Open print dialog' }));

    await waitFor(() => { expect(onClose).toHaveBeenCalledTimes(1); });
    expect(onPrint).toHaveBeenCalledTimes(1);
    expect(onPrint).toHaveBeenCalledWith({ pageSize: 'a3', orientation: 'portrait', marginMm: 15 });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('keeps the dialog open and says so when the print view could not be made', async () => {
    const onPrint = vi.fn<(options: PdfExportOptions) => Promise<void>>(() => Promise.reject(new Error('The print view could not be opened.')));
    const { onClose } = renderDialog({ available: false, onPrint });
    fireEvent.click(screen.getByRole('button', { name: 'Open print dialog' }));

    expect((await screen.findByRole('alert')).textContent).toContain('print view could not be opened');
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Open print dialog' }).disabled).toBe(false);
  });

  it('refuses a margin outside 0 to 50 mm before printing, too', () => {
    const { onPrint } = renderDialog({ available: false });
    fireEvent.change(screen.getByLabelText<HTMLInputElement>('Margin (mm)'), { target: { value: '80' } });
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Open print dialog' }).disabled).toBe(true);
    expect(onPrint).not.toHaveBeenCalled();
  });

  it('with server PDF it stays the primary route and browser print is offered beside it', async () => {
    const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(new Response('%PDF-1.4', { status: 200 })));
    vi.stubGlobal('fetch', fetchMock);
    const { onPdf, onPrint, onClose } = renderDialog();

    const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
    expect(within(dialog).getByRole('button', { name: 'Export PDF' }).className).toContain('sql-import-create');
    expect(within(dialog).queryByRole('button', { name: 'Open print dialog' })).toBeNull();
    expect(within(dialog).getByText(/choose Save as PDF/)).not.toBeNull();

    fireEvent.change(within(dialog).getByLabelText('Page size'), { target: { value: 'letter' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Print in browser…' }));
    await waitFor(() => { expect(onClose).toHaveBeenCalledTimes(1); });
    expect(onPrint).toHaveBeenCalledWith({ pageSize: 'letter', orientation: 'landscape', marginMm: 10 });
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onPdf).not.toHaveBeenCalled();
  });

  it('for a document the server does not hold there is no server route even when the component works', () => {
    renderDialog({ documentId: undefined });
    expect(screen.queryByRole('button', { name: 'Export PDF' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Open print dialog' })).not.toBeNull();
  });

  it('tells an administrator that a switched-on component is not working, with the server’s sentence and no install command for a driver', () => {
    // QA B1: the notice used to send an admin to install Chromium when the .playwright folder was what was missing.
    const driverHint = 'server-side PDF export is enabled, but Playwright’s driver is not usable on this server: the playwright package /srv/draughtsman/.playwright/package/cli.js is missing. This is not a browser problem, so installing Chromium will not fix it.';
    renderDialog({ available: false, hint: driverHint, problem: 'driver' });

    const dialog = screen.getByRole('dialog', { name: 'Export PDF' });
    const note = within(dialog).getByRole('status');
    expect(note.textContent).toContain('switched on but not working');
    expect(note.textContent).toContain('package/cli.js is missing');
    expect(within(dialog).queryByText(/npx playwright/)).toBeNull();
    // It is a note beside a working form, not a replacement for it.
    expect(within(dialog).getByRole('button', { name: 'Open print dialog' })).not.toBeNull();
  });

  it('an enabled component with no browser names the command or the path setting, and does not tell the admin to enable it', () => {
    renderDialog({ available: false, hint: HINT, problem: 'browser' });

    const status = within(screen.getByRole('dialog', { name: 'Export PDF' })).getByRole('status');
    expect(within(status).getByText('npx playwright@1.63.0 install chromium')).not.toBeNull();
    expect(status.textContent).not.toContain('then set');
    expect(status.textContent).toContain('export.chromium.path');
  });

  it('says nothing about the server to someone whose server has simply not enabled PDF', () => {
    renderDialog({ available: false, hint: HINT, problem: 'disabled' });
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('reads the problem at the boundary: only the three known values count', () => {
    expect(pdfProblemOf('driver')).toBe('driver');
    expect(pdfProblemOf('browser')).toBe('browser');
    expect(pdfProblemOf('disabled')).toBe('disabled');
    expect(pdfProblemOf('whatever')).toBeUndefined();
    expect(pdfProblemOf(undefined)).toBeUndefined();
  });

  it('closes on Escape and keeps the key from the canvas behind', () => {
    const { onClose } = renderDialog();
    const behind = vi.fn();
    window.addEventListener('keydown', behind);
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    window.removeEventListener('keydown', behind);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(behind).not.toHaveBeenCalled();
  });
});
