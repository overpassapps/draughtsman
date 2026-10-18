// What the "cloudflare-like" proxy does to a request and a response, as pure functions so the rules are unit-tested (test/proxyRules.test.mjs).
// This is a stand-in for what a CDN does to a JSON API response: compress it, and in doing so weaken the strong ETag (issue 63), or drop it.
// It is NOT Cloudflare; it reproduces the one behaviour that broke a real install, so a regression in the server's answer to it is caught.

export const MODES = ['weak', 'noetag', 'strip-both', 'pass'];

/** Headers to add to the request the proxy forwards. `clientIp` is the address the proxy saw. */
export function forwardedRequestHeaders(requestHeaders, clientIp) {
  const out = { ...requestHeaders };
  const prior = out['x-forwarded-for'];
  out['x-forwarded-for'] = prior ? `${prior}, ${clientIp}` : clientIp;
  out['x-forwarded-proto'] = 'https';
  out['cf-connecting-ip'] = clientIp;
  // Cloudflare speaks gzip/br to the visitor; the origin is asked for identity so the proxy compresses (and re-tags) it itself.
  delete out['accept-encoding'];
  delete out.connection;
  return out;
}

/** `"abc"` -> `W/"abc"`; an already weak or missing tag is returned as it is. */
export function weakenEtag(etag) {
  if (typeof etag !== 'string' || etag === '' || /^W\//i.test(etag)) return etag;
  return `W/${etag}`;
}

/** Whether the proxy compresses this response: JSON, not already encoded, and the visitor accepts gzip. */
export function shouldGzip(responseHeaders, requestHeaders) {
  const type = String(responseHeaders['content-type'] ?? '');
  const encoded = responseHeaders['content-encoding'];
  const accepts = /\bgzip\b/i.test(String(requestHeaders['accept-encoding'] ?? ''));
  return accepts && !encoded && /json/i.test(type);
}

/**
 * The response headers the proxy returns for `mode`. `gzipped` says the body was compressed (so length and encoding change).
 *   weak        compress, ETag -> W/ (what a CDN does when it compresses)
 *   noetag      compress, ETag removed entirely
 *   strip-both  ETag AND X-Document-Version removed (so a save has nothing to quote: the journey must FAIL step f)
 *   pass        a plain pass-through
 */
export function transformResponseHeaders(mode, responseHeaders, { gzipped }) {
  if (!MODES.includes(mode)) throw new Error(`unknown proxy mode ${mode}`);
  const out = { ...responseHeaders };
  delete out.connection;
  delete out['transfer-encoding'];
  if (gzipped) {
    out['content-encoding'] = 'gzip';
    out.vary = out.vary ? `${out.vary}, Accept-Encoding` : 'Accept-Encoding';
    delete out['content-length'];
  }
  if (mode === 'weak' && out.etag) out.etag = weakenEtag(out.etag);
  if (mode === 'noetag') delete out.etag;
  if (mode === 'strip-both') { delete out.etag; delete out['x-document-version']; }
  return out;
}
