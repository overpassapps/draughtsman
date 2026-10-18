// A small HTTP client for the upgrade-fixture scripts (populate.mjs, verify.mjs): a cookie jar, the CSRF header a
// browser session needs, and JSON helpers. No dependencies: Node 22's fetch only. Nothing here is Draughtsman-version
// specific; it speaks the documented REST contract (docs/api.md), so the same code drives a build from any era.
import { createHash } from 'node:crypto';

export class Client {
  /** @param {string} base e.g. http://127.0.0.1:5180 */
  constructor(base, label = 'anonymous') {
    this.base = base.replace(/\/$/, '');
    this.label = label;
    this.jar = new Map();
    this.bearer = null;
    this.actor = null;
  }

  static withBearer(base, token, label = 'token', actor = null) {
    const c = new Client(base, label);
    c.bearer = token;
    c.actor = actor;
    return c;
  }

  cookieHeader() {
    return [...this.jar.entries()].map(([k, v]) => `${k}=${v}`).join('; ');
  }

  absorb(res) {
    const lines = typeof res.headers.getSetCookie === 'function' ? res.headers.getSetCookie() : [];
    for (const line of lines) {
      const pair = line.split(';')[0];
      const eq = pair.indexOf('=');
      if (eq < 1) continue;
      const name = pair.slice(0, eq).trim();
      const value = pair.slice(eq + 1).trim();
      const expired = /;\s*expires=Thu, 01 Jan 1970/i.test(line) || /;\s*max-age=0/i.test(line);
      if (expired || value === '') this.jar.delete(name);
      else this.jar.set(name, value);
    }
  }

  /** The antiforgery request token the SPA echoes back, readable from the XSRF-TOKEN cookie. */
  get xsrf() {
    const v = this.jar.get('XSRF-TOKEN');
    return v ? decodeURIComponent(v) : null;
  }

  /**
   * @param {string} method
   * @param {string} path
   * @param {{json?: any, body?: any, headers?: Record<string,string>, raw?: boolean}} [opts]
   * @returns {Promise<{status:number, headers:Headers, text:string, json:any, bytes:Buffer}>}
   */
  async request(method, path, opts = {}) {
    const headers = { Accept: 'application/json', ...(opts.headers ?? {}) };
    let body;
    if (opts.json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(opts.json);
    } else if (opts.body !== undefined) {
      body = opts.body;
    }
    if (this.bearer) {
      headers.Authorization = `Bearer ${this.bearer}`;
      if (this.actor) headers['X-Draughtsman-Actor'] = this.actor;
    } else {
      const cookie = this.cookieHeader();
      if (cookie) headers.Cookie = cookie;
      if (method !== 'GET' && method !== 'HEAD' && this.xsrf) headers['X-XSRF-TOKEN'] = this.xsrf;
    }
    const res = await fetch(this.base + path, { method, headers, body, redirect: 'manual' });
    this.absorb(res);
    const bytes = Buffer.from(await res.arrayBuffer());
    const text = bytes.toString('utf8');
    let json = null;
    const type = res.headers.get('content-type') ?? '';
    if (/json/.test(type) && text.length > 0) {
      try { json = JSON.parse(text); } catch { json = null; }
    }
    return { status: res.status, headers: res.headers, text, json, bytes };
  }

  get(path, opts) { return this.request('GET', path, opts); }
  post(path, json, opts = {}) { return this.request('POST', path, { ...opts, json }); }
  put(path, json, opts = {}) { return this.request('PUT', path, { ...opts, json }); }
  patch(path, json, opts = {}) { return this.request('PATCH', path, { ...opts, json }); }
  delete(path, opts) { return this.request('DELETE', path, opts); }
}

/** Sorted-key JSON, so two serialisations of the same value hash alike. */
export function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonicalJson(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

export function sha256(data) {
  return createHash('sha256').update(data).digest('hex');
}

export function hashJson(value) {
  return sha256(canonicalJson(value));
}

/** Waits for GET /api/health to answer, or throws. */
export async function waitForHealth(base, timeoutMs = 120000) {
  const deadline = Date.now() + timeoutMs;
  let last = '';
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`${base}/api/health`);
      if (res.ok) return await res.json();
      last = `status ${res.status}`;
    } catch (error) {
      last = String(error.cause?.code ?? error.message);
    }
    await new Promise((r) => setTimeout(r, 400));
  }
  throw new Error(`The server at ${base} did not answer /api/health within ${timeoutMs} ms (${last}).`);
}

/** A 16x16 solid-colour PNG, built by hand so the scripts need no image library. */
export async function tinyPng(rgb = [200, 40, 60], size = 16) {
  const { deflateSync } = await import('node:zlib');
  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: size }, () => rgb).flat())]);
  const raw = Buffer.concat(Array.from({ length: size }, () => row));
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc(body));
    return Buffer.concat([length, body, sum]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // colour type: RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** A multipart/form-data body with one file part, for the upload endpoints. */
export function multipartFile(fieldName, fileName, contentType, bytes) {
  const boundary = `----fixture${Math.random().toString(16).slice(2)}`;
  const head = Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${fieldName}"; filename="${fileName}"\r\nContent-Type: ${contentType}\r\n\r\n`);
  const tail = Buffer.from(`\r\n--${boundary}--\r\n`);
  return { body: Buffer.concat([head, bytes, tail]), headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` } };
}
