/**
 * Turning the current document into a downloadable picture: the one place the editor serialises what it draws. Export
 * re-renders the document through the same `DiagramSvg` and active theme the canvas uses, on its `export` surface, so
 * a download carries the same shapes, labels and colours: it is always in full detail, and never the grid or the
 * selection chrome (DiagramSvg.tsx). The render is a pure function of the document and theme, so it does not read the
 * on-screen SVG, which is virtualised to the viewport and would omit anything off screen. Orthogonal connectors draw
 * from the document's stored waypoints, as the canvas draws them before a fresh route lands (a route is derived at
 * render time and never stored, decision D4 a).
 *
 * The picture is cropped to `DocumentIndex.contentBounds` with `EXPORT_MARGIN` world units on every side and rendered
 * at one canvas unit per pixel (the canvas's viewport is not export state). PNG is that SVG rasterised through an
 * `Image` and a `canvas`; PDF would need a client-side library and is not built here.
 *
 * Background rule: SVG and PNG are the same drawing, so both are painted with the theme's `exportBackground` (paper) as an
 * opaque rectangle behind it, and the text that sits straight on it takes the export text colour. There is no transparent
 * option. A theme's fills must therefore read on paper as well as on the canvas: the `container` kind is a see-through
 * outline for that reason (a fill equal to the canvas colour was invisible on screen and a dark slab on export).
 */

import { renderToStaticMarkup } from 'react-dom/server';
import { indexDocument } from '../canvas/documentIndex.ts';
import { indexWithRoutes } from '../canvas/routeDocument.ts';
import { DiagramSvg } from '../canvas/DiagramSvg.tsx';
import type { LineBreaker, TextMeasurer } from '../canvas/text.ts';
import type { Viewport } from '../canvas/viewport.ts';
import type { DiagramTheme } from '../theme/diagramTheme.ts';
import { withEmbeddedFonts } from './exportFonts.ts';
import type { Document } from '../model/types.ts';

/** World units left blank on every side of the drawing, at the export's one unit per pixel. */
export const EXPORT_MARGIN = 24;

/** Device pixels per canvas unit in an exported PNG; 2 keeps text crisp on a high-density screen. */
export const PNG_EXPORT_SCALE = 2;

export interface ExportSvgOptions {
  measure?: TextMeasurer | undefined;
  /** Breaks node labels where the label editor does, so the export matches the canvas (DiagramSvg.tsx). */
  breakLines?: LineBreaker | undefined;
  margin?: number | undefined;
  /**
   * Resolves an image node's asset id to an inlined data URI, so the export is self-contained (model/assets.ts
   * `preloadAssetDataUrls`). Without one, an image node exports as its placeholder frame.
   */
  assetHref?: ((assetId: string) => string | undefined) | undefined;
}

export interface ExportedSvg {
  svg: string;
  width: number;
  height: number;
}

/**
 * The document as a standalone SVG string, cropped to its content with a margin and carrying the active theme. The
 * result is padded to a whole-pixel size so the PNG raster never resamples on a fractional edge.
 */
export function documentToSvg(document: Document, theme: DiagramTheme, options: ExportSvgOptions = {}): ExportedSvg {
  const margin = options.margin ?? EXPORT_MARGIN;
  const { index, routes } = indexWithRoutes(indexDocument(document));
  const bounds = index.contentBounds;
  const width = Math.max(1, Math.ceil((bounds?.w ?? 0) + margin * 2));
  const height = Math.max(1, Math.ceil((bounds?.h ?? 0) + margin * 2));
  const viewport: Viewport = { zoom: 1, x: margin - (bounds?.x ?? 0), y: margin - (bounds?.y ?? 0) };
  const diagram = <DiagramSvg document={document} theme={theme} viewport={viewport} size={{ width, height }} surface="export" routes={routes} idPrefix="export-" measure={options.measure} breakLines={options.breakLines} assetHref={options.assetHref} />;
  return { svg: renderToStaticMarkup(diagram), width, height };
}

/**
 * A file name for the document title: lower case, with every run of characters that is not a letter or digit folded to
 * a single hyphen, so a title names the download without needing the filesystem's rules. A title that folds to nothing
 * (empty, or only punctuation) still names a file.
 */
export function exportFileName(title: string, extension: string): string {
  const slug = title
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return `${slug === '' ? 'diagram' : slug}.${extension}`;
}

/** Hands the browser a blob under `filename`; the object URL is released once the download has started. */
export function downloadBlob(filename: string, blob: Blob): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 0);
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => {
      resolve(image);
    };
    image.onerror = () => {
      reject(new Error('The diagram could not be rasterised.'));
    };
    image.src = url;
  });
}

/** Rasterises an exported SVG string to a PNG blob, drawn at `scale` device pixels per canvas unit. */
export async function svgToPngBlob(svg: string, width: number, height: number, scale = PNG_EXPORT_SCALE): Promise<Blob> {
  // The bundled fonts go inside the SVG first: an SVG drawn as an image cannot reach the page's own @font-face (exportFonts.ts).
  const url = URL.createObjectURL(new Blob([await withEmbeddedFonts(svg)], { type: 'image/svg+xml;charset=utf-8' }));
  try {
    const image = await loadImage(url);
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const context = canvas.getContext('2d');
    if (context === null) {
      throw new Error('The diagram could not be rasterised.');
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob((blob) => {
        if (blob === null) {
          reject(new Error('The diagram could not be rasterised.'));
        } else {
          resolve(blob);
        }
      }, 'image/png');
    });
  } finally {
    URL.revokeObjectURL(url);
  }
}
