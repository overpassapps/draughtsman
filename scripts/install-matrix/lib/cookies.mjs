// A cookie jar that behaves like a STRICT browser, so the journey can see what a real one would: a Secure cookie (and anything named __Host- or
// __Secure-) set by a response to a plain-http address is DROPPED, not kept. Chrome makes an exception for localhost; Safari and any other
// address do not, and a sign-in "worked" (HTTP 200) and then kept no session. `strict: false` is a lenient jar (like curl) that keeps
// everything, used to carry on after a session-not-kept failure so the rest of the journey still says something. Pure; test/cookies.test.mjs.

export class CookieJar {
  constructor({ strict = true } = {}) {
    this.strict = strict;
    this.cookies = new Map(); // name -> value
    this.dropped = []; // { name, reason }: what a strict browser would have refused, for the report
  }

  /** Store the cookies one response set. `setCookieLines` is an array of raw Set-Cookie header values; `url` is the request URL. */
  store(setCookieLines, url) {
    const secureContext = new URL(url).protocol === 'https:';
    for (const line of setCookieLines ?? []) {
      const parts = String(line).split(';').map((p) => p.trim());
      const eq = parts[0].indexOf('=');
      if (eq < 1) continue;
      const name = parts[0].slice(0, eq);
      const value = parts[0].slice(eq + 1);
      const attrs = new Map(parts.slice(1).map((a) => { const i = a.indexOf('='); return i < 0 ? [a.toLowerCase(), ''] : [a.slice(0, i).toLowerCase(), a.slice(i + 1)]; }));
      const secure = attrs.has('secure');
      const prefixed = name.startsWith('__Host-') || name.startsWith('__Secure-');
      if (this.strict && !secureContext && (secure || prefixed)) {
        this.dropped.push({ name, reason: `set Secure over plain http (${new URL(url).host}); a browser refuses it` });
        continue;
      }
      if (name.startsWith('__Host-') && (!secure || attrs.get('path') !== '/' || attrs.has('domain'))) {
        this.dropped.push({ name, reason: '__Host- cookie without Secure, Path=/ or with a Domain; a browser refuses it' });
        continue;
      }
      const maxAge = attrs.get('max-age');
      const expires = attrs.get('expires');
      const gone = (maxAge !== undefined && Number(maxAge) <= 0) || (expires !== undefined && Date.parse(expires) < Date.now());
      if (gone || value === '') this.cookies.delete(name);
      else this.cookies.set(name, value);
    }
  }

  /** The Cookie request header, or undefined when the jar is empty. */
  header() {
    if (this.cookies.size === 0) return undefined;
    return [...this.cookies].map(([n, v]) => `${n}=${v}`).join('; ');
  }

  get(name) {
    return this.cookies.get(name);
  }

  clear() {
    this.cookies.clear();
  }

  names() {
    return [...this.cookies.keys()];
  }
}
