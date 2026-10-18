/**
 * Where a document's thumbnail is served (docs/api.md `GET /api/documents/{id}/thumbnail`): an SVG that fits a fixed 320 by 200
 * box, shown through `<img src>` and never inlined, so anything inside it is inert. The `v` query value is the document's
 * last-modified time. The server ignores it; it makes an edited document's thumbnail a different address, so a page that is
 * still open (or a browser that kept the image in memory for the session) asks again instead of showing the old picture.
 */

/** The thumbnail's own size, which the card reserves before the image arrives so nothing moves when it does. */
export const THUMBNAIL_WIDTH = 320;
export const THUMBNAIL_HEIGHT = 200;

export function thumbnailUrl(id: string, updatedAt: string): string {
  return `/api/documents/${encodeURIComponent(id)}/thumbnail?v=${encodeURIComponent(updatedAt)}`;
}
