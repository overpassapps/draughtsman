/**
 * File > Export PDF (design/atlas/export-dialog.md, "What is built: the PDF export dialog" and "Browser print is the default").
 * One options form (page size, orientation, margin) for two routes. The default route needs nothing on the server: it opens the
 * browser's print dialog on the themed export SVG (`printExport.ts`) and the user chooses Save as PDF. When the server's optional
 * Chromium component has proved it can launch (`GET /api/health` `export.pdf`), "Export PDF" asks it for a file as before and
 * browser print is offered beside it. There is no unavailable state: an administrator whose switched-on component is failing
 * sees the server's reason as a note, and nothing else changes. It reuses the Import SQL dialog's chrome (`sql-import-*`) and rules: modal, Tab kept inside, every key kept from the
 * canvas behind it, Escape closes.
 */

import { useEffect, useId, useRef, useState } from 'react';
import {
  DEFAULT_PDF_OPTIONS,
  PDF_MAX_MARGIN_MM,
  PDF_PAGE_SIZES,
  clampMargin,
  installCommandOf,
  requestPdf,
  type PdfExportOptions,
  type PdfProblem,
  type PdfOrientation,
  type PdfPageSize,
} from './pdfExport.ts';

export interface ExportPdfDialogProps {
  /** True only when the server has proved a browser launch (health's `export.pdf`): then it is the primary route. */
  available: boolean;
  /** The server's own words about why it cannot make PDFs (health's `export.pdfHint`), shown to an administrator as a note. */
  hint: string | undefined;
  /** Which part is the matter (health's `export.pdfProblem`): a missing driver is not cured by installing a browser. */
  problem?: PdfProblem | undefined;
  /** Undefined for a document the server does not hold (a preview): there is no server route for it, only browser print. */
  documentId: string | undefined;
  /** Called with the PDF once fetched; the caller downloads it. */
  onPdf: (blob: Blob) => void;
  /**
   * Opens the browser's print dialog on the diagram laid out for `options`. It is called straight from the click, so a window it
   * opens is not taken for a popup; it resolves once the print dialog is up, or rejects when the print view could not be made.
   */
  onPrint: (options: PdfExportOptions) => Promise<void>;
  onClose: () => void;
}

export function ExportPdfDialog({ available, hint, problem, documentId, onPdf, onPrint, onClose }: ExportPdfDialogProps) {
  const headingId = useId();
  const sizeId = useId();
  const orientationId = useId();
  const marginId = useId();
  const [options, setOptions] = useState<PdfExportOptions>(DEFAULT_PDF_OPTIONS);
  const [marginText, setMarginText] = useState(String(DEFAULT_PDF_OPTIONS.marginMm));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const sizeField = useRef<HTMLSelectElement>(null);
  const command = installCommandOf(hint);

  useEffect(() => {
    sizeField.current?.focus();
  }, []);

  const marginValid = marginText.trim() !== '' && Number.isFinite(Number(marginText)) && Number(marginText) >= 0 && Number(marginText) <= PDF_MAX_MARGIN_MM;

  const serverRoute = available && documentId !== undefined;

  const printInBrowser = (): void => {
    setBusy(true);
    setError(undefined);
    onPrint({ ...options, marginMm: clampMargin(Number(marginText)) })
      .then(() => {
        setBusy(false);
        onClose();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'The print view could not be opened.');
      });
  };

  const exportPdf = (): void => {
    if (documentId === undefined) {
      return;
    }
    setBusy(true);
    setError(undefined);
    requestPdf(documentId, { ...options, marginMm: clampMargin(Number(marginText)) })
      .then((blob) => {
        setBusy(false);
        onPdf(blob);
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'The PDF could not be made.');
      });
  };

  return (
    <div
      className="sql-import-backdrop"
      role="presentation"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && !busy) {
          onClose();
        }
      }}
    >
      <div
        className="sql-import"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === 'Escape' && !busy) {
            onClose();
          }
          if (e.key === 'Tab') {
            const focusable = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('input, select, button:not(:disabled)'));
            const firstField = focusable[0];
            const last = focusable[focusable.length - 1];
            if (e.shiftKey && document.activeElement === firstField) {
              e.preventDefault();
              last?.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
              e.preventDefault();
              firstField?.focus();
            }
          }
        }}
      >
        <h2 id={headingId} className="sql-import-title">Export PDF</h2>

        <p className="sql-import-help">
          {serverRoute
            ? 'Export PDF makes a single-page, vector PDF on the server. Print in browser opens your browser’s print dialog: choose Save as PDF.'
            : 'Opens your browser’s print dialog: choose Save as PDF.'}
        </p>
        <div className="sql-import-row">
          <div className="sql-import-field">
            <label htmlFor={sizeId}>Page size</label>
            <select
              id={sizeId}
              ref={sizeField}
              value={options.pageSize}
              onChange={(e) => {
                setOptions({ ...options, pageSize: e.target.value as PdfPageSize });
              }}
            >
              {PDF_PAGE_SIZES.map((size) => (
                <option key={size.value} value={size.value}>{size.label}</option>
              ))}
            </select>
          </div>
          <div className="sql-import-field">
            <label htmlFor={orientationId}>Orientation</label>
            <select
              id={orientationId}
              value={options.orientation}
              disabled={options.pageSize === 'fit'}
              onChange={(e) => {
                setOptions({ ...options, orientation: e.target.value as PdfOrientation });
              }}
            >
              <option value="landscape">Landscape</option>
              <option value="portrait">Portrait</option>
            </select>
          </div>
          <div className="sql-import-field">
            <label htmlFor={marginId}>Margin (mm)</label>
            <input
              id={marginId}
              type="number"
              inputMode="decimal"
              min={0}
              max={PDF_MAX_MARGIN_MM}
              step={1}
              value={marginText}
              aria-invalid={!marginValid}
              onChange={(e) => {
                setMarginText(e.target.value);
              }}
            />
          </div>
        </div>
        <p className="sql-import-help">
          {options.pageSize === 'fit'
            ? 'The page is cut to the diagram, so nothing is scaled.'
            : 'The diagram is scaled down to fit inside the margins, never up.'}{' '}
          {serverRoute ? ' Print in browser follows the diagram theme, as SVG and PNG export do; Export PDF follows your organisation’s default.' : ' Colours follow the diagram theme, as in SVG and PNG export.'}
        </p>
        {!available && (problem === 'driver' || problem === 'browser') && (
          <p className="sql-import-help" role="status">
            Server-side PDF is switched on but not working.{' '}
            {problem === 'browser' && command !== undefined ? (
              <>
                On the server, run <code>{command}</code> (or set <code>export.chromium.path</code> in <code>draughtsman.yaml</code> to an existing Chrome or Chromium); the server checks again within thirty seconds.
              </>
            ) : (
              (hint ?? 'Check the server log (docs/pdf-export.md).')
            )}
          </p>
        )}
        {!marginValid && <p className="sql-import-error" role="alert">The margin must be between 0 and {String(PDF_MAX_MARGIN_MM)} mm.</p>}
        {error !== undefined && <p className="sql-import-error" role="alert">{error}</p>}
        <div className="sql-import-actions">
          <button type="button" onClick={onClose} disabled={busy}>Cancel</button>
          {serverRoute ? (
            <>
              <button type="button" onClick={printInBrowser} disabled={busy || !marginValid}>Print in browser…</button>
              <button type="button" className="sql-import-create" onClick={exportPdf} disabled={busy || !marginValid}>
                {busy ? 'Making the PDF…' : 'Export PDF'}
              </button>
            </>
          ) : (
            <button type="button" className="sql-import-create" onClick={printInBrowser} disabled={busy || !marginValid}>
              {busy ? 'Opening the print dialog…' : 'Open print dialog'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
