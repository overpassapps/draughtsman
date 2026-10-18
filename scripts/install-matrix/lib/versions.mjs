// How a document's version arrived (docs/api.md, "Base versions"): the server sends it twice, as the ETag and as the unquoted
// X-Document-Version, so that a CDN which weakens or drops the ETag cannot break a save. This module is the comparison the journey reports.
// Pure; covered by test/versions.test.mjs.

/** `W/"dv1:abc"`, `"dv1:abc"` and `dv1:abc` all name the version `dv1:abc`. Returns null for nothing usable. */
export function bareVersion(value) {
  if (value === undefined || value === null) return null;
  let v = String(value).trim();
  if (v === '') return null;
  v = v.replace(/^W\//i, '');
  if (v.length >= 2 && v.startsWith('"') && v.endsWith('"')) v = v.slice(1, -1);
  return v === '' ? null : v;
}

/** The quoted strong form an If-Match carries when built from X-Document-Version, as the web editor does. */
export function quotedStrong(value) {
  const bare = bareVersion(value);
  return bare === null ? null : `"${bare}"`;
}

/**
 * Compare what a read carried.
 *   etagState: 'strong' | 'weak' | 'missing'
 *   xdvState:  'present' | 'missing'
 *   agree:     true when both name the same version, false when they differ, null when one is missing
 *   usable:    whether any version arrived at all (when false, no save can be made)
 *   version:   the bare version, preferring X-Document-Version as the editor does
 *   notes:     human sentences, for a report
 */
export function compareVersionHeaders({ etag, xDocumentVersion }) {
  const etagText = etag === undefined || etag === null ? '' : String(etag).trim();
  const xdvBare = bareVersion(xDocumentVersion);
  const etagBare = bareVersion(etagText);
  const etagState = etagBare === null ? 'missing' : /^W\//i.test(etagText) ? 'weak' : 'strong';
  const xdvState = xdvBare === null ? 'missing' : 'present';
  const agree = etagBare !== null && xdvBare !== null ? etagBare === xdvBare : null;
  const notes = [];
  if (etagState === 'weak') notes.push('the ETag arrived weakened (W/): an intermediary compressed or re-tagged the response');
  if (etagState === 'missing') notes.push('the ETag header did not arrive: an intermediary removed it');
  if (xdvState === 'missing') notes.push('X-Document-Version did not arrive: an intermediary removed it (this build always sends it)');
  if (agree === false) notes.push(`the ETag (${etagBare}) and X-Document-Version (${xdvBare}) name different versions`);
  const usable = etagBare !== null || xdvBare !== null;
  if (!usable) notes.push('no version header arrived at all: a save has nothing to quote in If-Match');
  return { etagState, xdvState, agree, usable, version: xdvBare ?? etagBare, notes };
}
