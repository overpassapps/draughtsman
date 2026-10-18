/**
 * Where the editor reads a document's version from (docs/api.md "Base versions").
 *
 * The server sends the version twice: as the `ETag` and as `X-Document-Version` (the same value, unquoted). A CDN, WAF
 * or gateway that compresses a response weakens its ETag (`W/"..."`) and one that strips headers may drop it, but none
 * of them has a reason to touch a header it does not know, so the custom header is read first and the ETag is the
 * fallback for a server that predates it. Either way the result is the quoted strong form `"dv1:..."`, which is what
 * `If-Match` and `If-None-Match` carry back: the server accepts a weak tag too, but there is no reason to hand it one.
 */

/** Must match `BaseVersionHeader.VersionHeaderName` on the server. */
export const DOCUMENT_VERSION_HEADER = 'X-Document-Version';

/** `dv1:abc` or `"dv1:abc"` becomes `"dv1:abc"`. */
function quoted(tag: string): string {
  const bare = tag.length >= 2 && tag.startsWith('"') && tag.endsWith('"') ? tag.slice(1, -1) : tag;
  return `"${bare}"`;
}

/** The version a response carries, as a quoted strong entity tag, or undefined when it carries none. */
export function readDocumentVersion(response: Pick<Response, 'headers'>): string | undefined {
  const own = response.headers.get(DOCUMENT_VERSION_HEADER)?.trim();
  if (own !== undefined && own !== '') {
    return quoted(own);
  }
  const etag = response.headers.get('ETag')?.trim();
  if (etag === undefined || etag === '') {
    return undefined;
  }
  return quoted(etag.replace(/^W\//, ''));
}
