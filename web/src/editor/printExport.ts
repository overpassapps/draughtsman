/**
 * Client-side "Print to PDF" (docs/pdf-export.md, design/atlas/export-dialog.md "Browser print"): the default PDF route, which
 * needs nothing on the server. It prints the SVG that `exportDiagram.tsx` already draws for SVG and PNG export (one renderer,
 * so the colours, fonts and theme match by construction) on a page chosen by the same three options the server route takes, and hands it to the browser's print dialog,
 * where the user chooses Save as PDF.
 *
 * Page maths mirrors `src/Draughtsman.Server/Export/PdfPage.cs`: a fixed paper size scales the drawing down to sit inside its
 * margins, never up, centred, on one sheet; Fit cuts the page to the drawing plus its margins. The margin is padding inside
 * the sheet and `@page` margin is 0, so the browser has no margin area to print its own header and footer (page URL, date) into.
 *
 * The print document is built in a window opened synchronously from the click (a blocker lets that through, and an async open
 * after the render would lose the user's gesture), and when the browser refuses the window it prints from a hidden iframe in
 * the same page. The document holds no script: the opener calls `print()`.
 */

import type { PdfExportOptions } from './pdfExport.ts';

/** CSS pixels per millimetre (96 per inch). */
const PX_PER_MM = 96 / 25.4;

/** A hair of slack under the sheet's height so rounding can never push the content onto a second page (as PdfPage.cs does). */
const SLACK_MM = 0.3;

/** The paper in millimetres, after orientation. A4, A3 and Letter match `PdfPage.PaperMm`; `fit` has no paper of its own. */
export function paperMm(size: Exclude<PdfExportOptions['pageSize'], 'fit'>, orientation: PdfExportOptions['orientation']): { width: number; height: number } {
  const [w, h] = size === 'a3' ? [297, 420] : size === 'letter' ? [215.9, 279.4] : [210, 297];
  return orientation === 'landscape' ? { width: h, height: w } : { width: w, height: h };
}

export interface PrintLayout {
  /** The page the browser is asked for, in CSS pixels. */
  pageWidthPx: number;
  pageHeightPx: number;
  /** The `@page size` value, in millimetres for a paper size and pixels for Fit. */
  pageSize: string;
  marginMm: number;
  /** How big the drawing is printed, in CSS pixels: its own size, scaled down if it would not fit inside the margins. */
  drawingWidthPx: number;
  drawingHeightPx: number;
  /** The scale applied to the drawing: 1 unless it had to shrink, and never above 1. */
  scale: number;
}

const num = (value: number): string => String(Math.round(value * 100) / 100);

/** Where the drawing goes on the page for `options`. Pure, so it is tested without a browser. */
export function printLayout(drawingWidth: number, drawingHeight: number, options: PdfExportOptions): PrintLayout {
  const marginMm = options.marginMm;
  const marginPx = marginMm * PX_PER_MM;
  if (options.pageSize === 'fit') {
    const pageWidthPx = drawingWidth + 2 * marginPx;
    const pageHeightPx = drawingHeight + 2 * marginPx;
    return { pageWidthPx, pageHeightPx, pageSize: `${num(pageWidthPx)}px ${num(pageHeightPx)}px`, marginMm, drawingWidthPx: drawingWidth, drawingHeightPx: drawingHeight, scale: 1 };
  }
  const paper = paperMm(options.pageSize, options.orientation);
  const availableWidth = Math.max(paper.width - 2 * marginMm, 1) * PX_PER_MM;
  const availableHeight = Math.max(paper.height - 2 * marginMm - SLACK_MM, 1) * PX_PER_MM;
  const scale = Math.min(1, availableWidth / drawingWidth, availableHeight / drawingHeight);
  return {
    pageWidthPx: paper.width * PX_PER_MM,
    pageHeightPx: paper.height * PX_PER_MM,
    pageSize: `${num(paper.width)}mm ${num(paper.height)}mm`,
    marginMm,
    // Floor to a hundredth of a pixel so rounding can only make the drawing smaller than the space, never larger.
    drawingWidthPx: Math.floor(drawingWidth * scale * 100) / 100,
    drawingHeightPx: Math.floor(drawingHeight * scale * 100) / 100,
    scale,
  };
}

/** The drawing's own paper colour (its background rectangle, the theme's `exportBackground`), white if it is not a plain colour. */
export function paperOf(svg: string): string {
  const match = /<rect\b[^>]*\bclass="canvas-background"[^>]*\bfill="(#[0-9a-fA-F]{3,8})"/.exec(svg);
  return match?.[1] ?? '#ffffff';
}

const escapeHtml = (text: string): string => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/**
 * The HTML document the browser prints: `svg` (as `documentToSvg` produced it, fonts embedded) on a sheet of the chosen
 * size. `title` becomes the document title, which Chrome offers as the file name when it saves the PDF.
 */
export function printPageHtml(svg: string, drawingWidth: number, drawingHeight: number, options: PdfExportOptions, title: string): string {
  const layout = printLayout(drawingWidth, drawingHeight, options);
  const fit = options.pageSize === 'fit';
  const paper = paperOf(svg);
  const sheetWidth = fit ? `${num(layout.pageWidthPx)}px` : `${num(paperMm(options.pageSize as Exclude<typeof options.pageSize, 'fit'>, options.orientation).width)}mm`;
  const sheetHeight = fit ? `${num(layout.pageHeightPx)}px` : `${num(paperMm(options.pageSize as Exclude<typeof options.pageSize, 'fit'>, options.orientation).height - SLACK_MM)}mm`;
  const css =
    `@page{size:${layout.pageSize};margin:0}` +
    `*{box-sizing:border-box;-webkit-print-color-adjust:exact;print-color-adjust:exact}` +
    `html,body{margin:0;padding:0;background:${paper}}` +
    `.sheet{width:${sheetWidth};height:${sheetHeight};padding:${num(layout.marginMm)}mm;display:flex;align-items:center;justify-content:center;overflow:hidden;break-inside:avoid}` +
    `.sheet svg{display:block;flex:none;width:${num(layout.drawingWidthPx)}px;height:${num(layout.drawingHeightPx)}px}`;
  return `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>${escapeHtml(title)}</title><style>${css}</style></head><body><div class="sheet">${svg}</div></body></html>`;
}

/** What the print target needs from the browser; the default is the real one, a test passes its own. */
export interface PrintEnvironment {
  /** `window.open('', '_blank')`: null when a popup blocker refuses it. */
  open: () => Window | null;
  document: Document;
}

export interface PrintTarget {
  /** Where the view is shown: its own window, or a hidden frame in this page when the browser refused the window. */
  kind: 'window' | 'frame';
  /** Fills the target with `html`, waits for it (and its fonts) to be ready, then opens the browser's print dialog. */
  print: (html: string) => Promise<void>;
  /** Throws the target away without printing, for a failure before there was anything to print. */
  dispose: () => void;
}

/** A wait that gives up, so a document that never fires `load` cannot hold the print dialog back for ever. */
function settled(wait: Promise<unknown>, ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, ms);
    void wait.then(
      () => {
        clearTimeout(timer);
        resolve();
      },
      () => {
        clearTimeout(timer);
        resolve();
      },
    );
  });
}

/** When the document's fonts are ready; a document with no font loading API (jsdom) is ready at once. */
function fontsReady(doc: Document): Promise<unknown> {
  return (doc as unknown as { fonts?: { ready: Promise<unknown> } }).fonts?.ready ?? Promise.resolve();
}

function loaded(target: Window): Promise<void> {
  return new Promise<void>((resolve) => {
    if (target.document.readyState === 'complete') {
      resolve();
      return;
    }
    target.addEventListener('load', () => { resolve(); }, { once: true });
  });
}

const FRAME_ATTRIBUTE = 'data-print-frame';

/**
 * Opens where the print view will go. Call it straight from the click that asked for it, before any `await`: a popup blocker
 * lets a window opened by a user gesture through, and it blocks one opened after the render has finished.
 */
export function openPrintTarget(environment: PrintEnvironment = browserEnvironment()): PrintTarget {
  const popup = environment.open();
  if (popup !== null) {
    let closed = false;
    const close = (): void => {
      if (!closed) {
        closed = true;
        popup.close();
      }
    };
    return {
      kind: 'window',
      print: async (html) => {
        popup.document.open();
        // The one way to put a whole document, <title> and <style> included, into a window that is still blank.
        // eslint-disable-next-line @typescript-eslint/no-deprecated
        popup.document.write(html);
        popup.document.close();
        await settled(loaded(popup), 3000);
        await settled(fontsReady(popup.document), 3000);
        // Print and Cancel both end in `afterprint`: the view has done its job either way.
        popup.addEventListener('afterprint', close, { once: true });
        popup.focus();
        popup.print();
      },
      dispose: close,
    };
  }

  // Popups are blocked: print from an iframe in this page. It stays the size of a pixel and out of the way; the browser lays the
  // document out on the page it asks for, not at the frame's size.
  const doc = environment.document;
  for (const stale of Array.from(doc.querySelectorAll(`iframe[${FRAME_ATTRIBUTE}]`))) {
    stale.remove();
  }
  const frame = doc.createElement('iframe');
  frame.setAttribute(FRAME_ATTRIBUTE, '');
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  frame.title = 'Print view';
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;pointer-events:none';
  doc.body.append(frame);
  return {
    kind: 'frame',
    print: async (html) => {
      const ready = new Promise<void>((resolve) => {
        frame.addEventListener('load', () => { resolve(); }, { once: true });
      });
      frame.srcdoc = html;
      await settled(ready, 3000);
      const target = frame.contentWindow;
      if (target === null) {
        frame.remove();
        throw new Error('The print view could not be opened.');
      }
      await settled(fontsReady(target.document), 3000);
      target.addEventListener('afterprint', () => { frame.remove(); }, { once: true });
      target.focus();
      target.print();
    },
    dispose: () => {
      frame.remove();
    },
  };
}

function browserEnvironment(): PrintEnvironment {
  return { open: () => window.open('', '_blank'), document };
}
