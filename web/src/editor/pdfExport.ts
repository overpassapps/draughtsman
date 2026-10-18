/**
 * Server-side PDF export (docs/pdf-export.md, `GET /api/documents/{id}/pdf`): the options the endpoint takes, the URL they
 * make, and the request. The server validates the same ranges; they are repeated here only so the dialog cannot send a
 * value that is certain to be refused.
 */

export type PdfPageSize = 'a4' | 'a3' | 'letter' | 'fit';
export type PdfOrientation = 'landscape' | 'portrait';

export interface PdfExportOptions {
  pageSize: PdfPageSize;
  /** Ignored by the server for `fit`. */
  orientation: PdfOrientation;
  /** Millimetres on every side. */
  marginMm: number;
}

export const PDF_PAGE_SIZES: readonly { value: PdfPageSize; label: string }[] = [
  { value: 'a4', label: 'A4' },
  { value: 'a3', label: 'A3' },
  { value: 'letter', label: 'Letter' },
  { value: 'fit', label: 'Fit to diagram' },
];

export const PDF_MAX_MARGIN_MM = 50;

/** What the dialog opens on (design/atlas/export-dialog.md): A4 landscape, 10 mm. */
export const DEFAULT_PDF_OPTIONS: PdfExportOptions = { pageSize: 'a4', orientation: 'landscape', marginMm: 10 };

/** A margin the server will accept: a finite number of millimetres from 0 to the maximum, else the default. */
export function clampMargin(value: number): number {
  if (!Number.isFinite(value)) {
    return DEFAULT_PDF_OPTIONS.marginMm;
  }
  return Math.min(PDF_MAX_MARGIN_MM, Math.max(0, value));
}

export function pdfExportUrl(id: string, options: PdfExportOptions): string {
  const query = new URLSearchParams({
    pageSize: options.pageSize,
    orientation: options.orientation,
    margin: String(clampMargin(options.marginMm)),
  });
  return `/api/documents/${encodeURIComponent(id)}/pdf?${query.toString()}`;
}

/** The server's own words for a refusal (an RFC 9457 problem's `detail`), else the status. */
async function refusal(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { detail?: unknown; title?: unknown };
    if (typeof body.detail === 'string' && body.detail !== '') {
      return body.detail;
    }
    if (typeof body.title === 'string' && body.title !== '') {
      return body.title;
    }
  } catch {
    // Not JSON: fall through to the status.
  }
  return `Server returned ${String(response.status)}`;
}

/** Fetches the PDF. Rejects with the server's message (for a 501 that is how to install the component) when it refuses. */
export async function requestPdf(id: string, options: PdfExportOptions, signal?: AbortSignal): Promise<Blob> {
  const response = await fetch(pdfExportUrl(id, options), { headers: { Accept: 'application/pdf' }, ...(signal === undefined ? {} : { signal }) });
  if (!response.ok) {
    throw new Error(await refusal(response));
  }
  return response.blob();
}

/** Which part of PDF export is the matter, as health reports it to an administrator (`export.pdfProblem`). */
export type PdfProblem = 'disabled' | 'driver' | 'browser';

/** Reads health's `export.pdfProblem` at the fetch boundary: anything other than the three known values is treated as not reported. */
export function pdfProblemOf(value: unknown): PdfProblem | undefined {
  return value === 'disabled' || value === 'driver' || value === 'browser' ? value : undefined;
}

/** The command inside the server's install hint (in backticks), so it can be set in monospace; undefined when the hint has none. */
export function installCommandOf(hint: string | undefined): string | undefined {
  return hint === undefined ? undefined : /`([^`]+)`/.exec(hint)?.[1];
}
