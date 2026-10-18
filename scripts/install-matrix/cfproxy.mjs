#!/usr/bin/env node
// A small "cloudflare-like" reverse proxy for the install matrix (no dependency). It terminates TLS with a self-signed certificate, adds
// X-Forwarded-Proto: https, X-Forwarded-For and CF-Connecting-IP, gzips JSON, and does one of these to the ETag (see lib/proxyRules.mjs):
//   --mode weak         rewrite a strong ETag to W/"..." (what compression by a CDN does; issue 63)
//   --mode noetag       remove the ETag entirely
//   --mode strip-both   remove the ETag AND X-Document-Version (a save then has nothing to quote; used by the breakage proof)
//   --mode pass         change nothing but the forwarded headers
//
//   node cfproxy.mjs --listen 8443 --target 127.0.0.1:5180 --cert cert.pem --key key.pem --mode weak
//
// Prints `cfproxy listening on <port>` once ready; stop it with SIGTERM (matrix.sh stops it by its own pid). It buffers whole responses, which is
// fine for the journey's small bodies and keeps the rules simple. It is a test stand-in, not a proxy to put in front of anything real.
import { readFileSync } from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import { gzipSync } from 'node:zlib';
import { forwardedRequestHeaders, shouldGzip, transformResponseHeaders } from './lib/proxyRules.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []));
for (const required of ['listen', 'target', 'cert', 'key']) if (!args[required]) { console.error(`cfproxy: --${required} is required`); process.exit(2); }
const mode = args.mode ?? 'weak';
const [targetHost, targetPort] = args.target.split(':');
const agent = new http.Agent({ keepAlive: true, maxSockets: 16 });

const server = https.createServer({ key: readFileSync(args.key), cert: readFileSync(args.cert) }, (req, res) => {
  const clientIp = (req.socket.remoteAddress ?? '').replace(/^::ffff:/, '');
  const upstream = http.request({ host: targetHost, port: Number(targetPort), method: req.method, path: req.url, headers: forwardedRequestHeaders(req.headers, clientIp), agent }, (up) => {
    const chunks = [];
    up.on('data', (c) => chunks.push(c));
    up.on('end', () => {
      let body = Buffer.concat(chunks);
      const gzipped = shouldGzip(up.headers, req.headers) && mode !== 'pass';
      if (gzipped) body = gzipSync(body);
      const headers = transformResponseHeaders(mode, up.headers, { gzipped });
      if (!gzipped) headers['content-length'] = String(body.length);
      // Node joins repeated Set-Cookie lines into an array on the way in; writeHead takes that array as is, so every cookie survives.
      res.writeHead(up.statusCode ?? 502, headers);
      res.end(req.method === 'HEAD' ? undefined : body);
    });
  });
  upstream.on('error', (error) => { res.writeHead(502, { 'content-type': 'text/plain' }); res.end(`cfproxy: upstream ${args.target}: ${error.code ?? error.message}`); });
  req.pipe(upstream);
});
server.listen(Number(args.listen), '127.0.0.1', () => console.log(`cfproxy listening on ${args.listen} (mode ${mode}, target ${args.target})`));
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => server.close(() => process.exit(0)));
