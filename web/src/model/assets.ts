/**
 * Uploading and addressing assets (build-spec-v0.1.md section 7). An image node stores the asset id in
 * `props.assetId`; the picture itself is fetched from `GET /api/assets/{id}` on the canvas and inlined as a data URI
 * for export. Nothing here names a colour or a layout; it is the one place the SPA knows the asset wire format, so
 * the canvas, the panel and the exporter all resolve an id the same way.
 */

import { csrfHeaders } from './csrf.ts';
import type { NodeTemplate } from './create.ts';
import { createNode } from './create.ts';
import type { Document, Id, Node, Point } from './types.ts';

/** The longest side a newly placed image node is allowed to be, in canvas units. */
export const IMAGE_MAX_LONG_SIDE = 240;

/** The accepted upload types, matching the server's content sniffing (src/Draughtsman.Core/Media/AssetTypes.cs). */
export const ACCEPTED_IMAGE_TYPES = 'image/png,image/jpeg,image/gif,image/webp,image/svg+xml';

export interface UploadedAsset {
  id: Id;
  contentType: string;
  size: number;
}

/** A refused upload, carrying the server's status and detail so the UI can say what was wrong. */
export class AssetUploadError extends Error {
  constructor(
    message: string,
    public readonly status: number,
  ) {
    super(message);
    this.name = 'AssetUploadError';
  }
}

/** The asset id an image node points at, or undefined when it has none (so it draws the placeholder frame). */
export function assetIdOf(node: Pick<Node, 'props'>): Id | undefined {
  const value = node.props?.assetId;
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

/** The URL an asset is served from. */
export function assetUrl(id: Id): string {
  return `/api/assets/${encodeURIComponent(id)}`;
}

/** A node size for an image, its aspect ratio kept and neither side longer than `IMAGE_MAX_LONG_SIDE`. */
export function fitImageSize(width: number, height: number): { w: number; h: number } {
  if (!(width > 0) || !(height > 0)) {
    return { w: IMAGE_MAX_LONG_SIDE, h: IMAGE_MAX_LONG_SIDE };
  }

  const scale = Math.min(1, IMAGE_MAX_LONG_SIDE / Math.max(width, height));
  return { w: Math.max(1, Math.round(width * scale)), h: Math.max(1, Math.round(height * scale)) };
}

/** The picture's intrinsic size, loaded from an object URL; a format with no intrinsic size falls back to square. */
export async function imageDimensions(file: Blob): Promise<{ w: number; h: number }> {
  const url = URL.createObjectURL(file);
  try {
    const image = await loadImage(url);
    return image.naturalWidth > 0 && image.naturalHeight > 0 ? { w: image.naturalWidth, h: image.naturalHeight } : { w: IMAGE_MAX_LONG_SIDE, h: IMAGE_MAX_LONG_SIDE };
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Uploads a file to `POST /api/assets` and returns its id. The server decides the type from the content and the size
 * limit, so a refused upload surfaces the server's own status and detail rather than a client-side guess.
 */
export async function uploadAsset(file: Blob, fileName: string): Promise<UploadedAsset> {
  const form = new FormData();
  form.append('file', file, fileName);
  const response = await fetch('/api/assets', { method: 'POST', headers: csrfHeaders(), body: form });
  if (!response.ok) {
    throw new AssetUploadError(await problemDetail(response), response.status);
  }

  return (await response.json()) as UploadedAsset;
}

/**
 * An image node centred on `centre`, sized to `size`, carrying the asset id (and the file's name as a prop for later
 * reference). The node is created through `createNode` so it takes a fresh id and steps off any node already at that
 * spot; its own geometry then re-centres the image's box on the same point.
 */
export function createImageNode(document: Document, centre: Point, step: number, assetId: Id, size: { w: number; h: number }, fileName?: string): Node {
  const template: NodeTemplate = { kind: 'image', shape: 'image', label: '' };
  const node = createNode(document, centre, step, undefined, template);
  return {
    ...node,
    geometry: {
      x: Math.round(centre.x - size.w / 2),
      y: Math.round(centre.y - size.h / 2),
      w: size.w,
      h: size.h,
    },
    props: fileName === undefined ? { assetId } : { assetId, fileName },
  };
}

/** Every image node's asset data URI, keyed by asset id, for a self-contained SVG export. */
export async function preloadAssetDataUrls(document: Document): Promise<Map<Id, string>> {
  const ids = new Set<Id>();
  for (const node of document.nodes) {
    if (node.shape === 'image') {
      const id = assetIdOf(node);
      if (id !== undefined) {
        ids.add(id);
      }
    }
  }

  const loaded = await Promise.all([...ids].map(async (id): Promise<[Id, string] | undefined> => {
    const dataUrl = await fetchAssetDataUrl(id);
    return dataUrl === undefined ? undefined : [id, dataUrl];
  }));
  return new Map(loaded.filter((entry): entry is [Id, string] => entry !== undefined));
}

/** One asset as a data URI, or undefined when it cannot be read; a missing asset leaves the placeholder frame. */
export async function fetchAssetDataUrl(id: Id): Promise<string | undefined> {
  try {
    const response = await fetch(assetUrl(id));
    if (!response.ok) {
      return undefined;
    }

    return await blobToDataUrl(await response.blob());
  } catch {
    return undefined;
  }
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => {
      resolve(image);
    };
    image.onerror = () => {
      reject(new Error('The image could not be read.'));
    };
    image.src = url;
  });
}

function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      if (typeof reader.result === 'string') {
        resolve(reader.result);
      } else {
        reject(new Error('The image could not be read.'));
      }
    };
    reader.onerror = () => {
      reject(new Error('The image could not be read.'));
    };
    reader.readAsDataURL(blob);
  });
}

/** The problem detail from a refused upload, falling back to the status text. */
async function problemDetail(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { detail?: unknown; title?: unknown };
    if (typeof body.detail === 'string' && body.detail.length > 0) {
      return body.detail;
    }

    if (typeof body.title === 'string' && body.title.length > 0) {
      return body.title;
    }
  } catch {
    // Not JSON; fall through to the status text.
  }

  return `Upload failed (${String(response.status)})`;
}
