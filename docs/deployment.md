# Deployment: TLS, reverse proxy, firewall and permissions

This page is for the person who puts Draughtsman on a server other people will use. It covers what the server does by
default, what changes once it is reachable from other machines, and how to put TLS in front of it. Install first
([`install.md`](install.md)); administer afterwards ([`admin-guide.md`](admin-guide.md)).

Every command and output below was run against a real server unless a line says **unverified**. Two things were not
available when this was written: Windows and Linux with systemd. Where a section depends on them it says so.

## The short version

1. Keep the server on its default loopback address (`127.0.0.1:5180`) and put a reverse proxy on the same machine in front
   of it. The proxy terminates TLS. The server itself speaks plain HTTP only.
2. In `draughtsman.yaml`: list the proxy in `server.knownProxies`, name your public host in `server.allowedHosts`, and once
   `https://` works set `server.forceHttps: true`.
3. Open only 443 (and 80 if the proxy fetches certificates) on the firewall. Never expose 5180 directly.
4. Leave the data folder owner-only. Never `chmod 777` it.
5. Schedule `draughtsman backup` and copy the result off the machine ([`upgrade-backup.md`](upgrade-backup.md)).

## What the server does by default

| Setting | Default | What it means |
| --- | --- | --- |
| Bind address | `127.0.0.1:5180` (`server.host`, `server.port`) | Reachable from this machine only. The Docker image is the exception: it sets `ASPNETCORE_URLS=http://0.0.0.0:5180` so a published port works. |
| Transport | plain HTTP | No TLS in the server. Anything that travels over a network un-proxied, including passwords, is in clear. |
| Host header | `localhost`, `127.0.0.1`, `[::1]` only while bound to loopback with no proxy listed; any host otherwise | A request under another name is refused with `400` (DNS-rebinding guard). |
| Cookies | session cookie `ds_session`; `__Host-ds_session` with `Secure` once the server sees HTTPS | The server sees HTTPS when the request arrived over TLS, when a trusted proxy said so (`X-Forwarded-Proto`), or when `server.forceHttps` is on. |
| Security headers | always sent | `Content-Security-Policy`, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options` and `Permissions-Policy`; `no-store, no-transform` on API responses. HSTS only under `server.forceHttps`. |
| Sign-in | every route except `/api/health`, the theme routes and the sign-in routes | See [`api.md`](api.md#auth). |
| First run | needs a one-time setup code printed in the log | No one can claim the administrator account without reading the log. |
| Data folder | created owner-only (`0700`, files `0600`) on Linux and macOS | See [File permissions](#file-permissions). |

`ASPNETCORE_URLS`, when set, wins over `server.host` and `server.port`; `--urls=http://...` on the command line sets it
when it is not already set. To listen on IPv6 or on several addresses use `ASPNETCORE_URLS` (for example
`http://[::]:5180`), since `server.host` takes one host name or address.

### What changes when it is exposed

Binding anything other than loopback makes the server log `server.host is 0.0.0.0, which is not loopback: the API is
reachable from other hosts...`. Nothing else changes by itself, so work through this list:

- **Clear-text sign-in.** Without TLS in front, a password or session cookie crosses the network readable. Put a proxy in
  front, or do not expose the server.
- **Cookies lose `Secure` and client addresses are the proxy's.** Unless the server trusts the proxy's forwarded headers
  (next section). Consequences: every person reaches the server from one address, so the sign-in throttle (20 failed
  attempts per address per 15 minutes) and the security log's source address are the proxy's, not the person's.
- **Any Host header is accepted** once the bind is not loopback, unless `server.allowedHosts` names the hosts.
- **Anyone who can reach the port can try to sign in.** Failures are throttled; passwords are Argon2id; nothing else is
  anonymous. A firewall still belongs in front.

## Server settings for a proxy

All three are in `draughtsman.yaml` in the data folder (annotated in [`draughtsman.example.yaml`](draughtsman.example.yaml));
restart the server after editing.

```yaml
server:
  knownProxies: ["127.0.0.1", "::1"]     # the proxy's own addresses or CIDR ranges
  allowedHosts: ["draughtsman.example.com"]
  forceHttps: true
```

### `server.knownProxies`

The addresses whose `X-Forwarded-For` and `X-Forwarded-Proto` headers the server believes. An entry is an IP address
(`10.0.0.1`, `::1`) or a CIDR range (`172.18.0.0/16`, `fd00::/8`). A host name is not understood: the server logs
`server.knownProxies entry proxy.internal (a hostname or other non-address: list the proxy's IP address or a CIDR range)
was ignored; the proxy it names is not trusted` and carries on, so read the start-up log after editing.

What trusting a proxy changes, checked on a server bound to `0.0.0.0` and called from a second address
(`192.168.1.176`) that sent `X-Forwarded-For: 203.0.113.9` and `X-Forwarded-Proto: https`:

| `knownProxies` | Security log source address | Session cookie |
| --- | --- | --- |
| empty | `192.168.1.176` (the header is ignored) | `ds_session`, no `Secure` |
| `["192.168.1.0/24"]` | `203.0.113.9` | `__Host-ds_session; ... secure` |

- A proxy on the **same machine**, connecting over IPv4 loopback (`127.0.0.1`), is trusted without being listed. List `::1`
  as well if it connects over IPv6 loopback.
- List the proxy and nothing wider. Trusting a range that clients can also reach lets any client forge its address and
  scheme, which defeats the throttle and the source address in the security log.
- A proxy that overwrites `X-Forwarded-For` (Caddy does; checked: a forged `X-Forwarded-For: 1.2.3.4` sent through Caddy was
  ignored) is the safe kind. One that appends (nginx's `$proxy_add_x_forwarded_for`) is also safe: the server takes the
  address the proxy itself added, which is the last entry.
- Naming any proxy also lifts the localhost-only default for the Host header (next section).
- In Docker, a proxy container's address changes when you redeploy; list the compose network's CIDR range instead
  (`172.30.0.0/24` in the compose file below).
- **A proxy on the Docker host, in front of a container with a published port.** The server does not see the proxy's own address (`127.0.0.1`):
  it sees the **bridge gateway**, `172.17.0.1` on Docker's default bridge (a different address on a network you created: `docker network inspect
  <network> --format '{{(index .IPAM.Config 0).Gateway}}'`). That address is not loopback, so the proxy is **not trusted** unless you list it, and
  every visitor then shares one address. List the gateway, or the whole default bridge, `172.17.0.0/16`, if the gateway can change (that also
  trusts every other container on the bridge, so prefer the gateway):

  ```yaml
  server:
    knownProxies: ["172.17.0.1"]
    forceHttps: true
  ```

- **If nothing is listed**, the server logs one warning an hour, whenever forwarding headers arrive from a peer it does not trust:
  `Forwarding headers (X-Forwarded-For and the like) arrived from 172.17.0.1, which is not listed in server.knownProxies ...`. It names the peer and
  never a header value. Behind an unlisted proxy, other people's failed sign-ins (twenty from one address in 15 minutes) can lock the owner out of a
  **correct** sign-in for 15 minutes ([`troubleshooting.md`](troubleshooting.md#i-can-reach-the-server-but-not-use-it)).
- **nginx in front, optionally behind Cloudflare.** nginx must say who it is forwarding for, and, behind Cloudflare, must first learn the visitor's
  real address, or every visitor on one Cloudflare edge shares a bucket (the server takes the **last** `X-Forwarded-For` entry, which is the address
  nginx saw). Unverified here (nginx was not run); the directives are nginx's own:

  ```nginx
  # http { } block, only when Cloudflare is in front of nginx: trust Cloudflare's ranges (https://www.cloudflare.com/ips/) and read the visitor from its header
  set_real_ip_from 173.245.48.0/20;      # ...one line per published Cloudflare range
  real_ip_header   CF-Connecting-IP;

  server {
    location / {
      proxy_pass         http://127.0.0.1:5180;
      proxy_set_header   Host $host;
      proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
      proxy_set_header   X-Forwarded-Proto $scheme;
      client_max_body_size 12m;
      proxy_read_timeout 330s;
    }
  }
  ```

### `server.allowedHosts`

The Host header values the server answers to; `*.example.com` matches subdomains and `*` anything. Left empty, the default
follows the bind: localhost names while bound to loopback with no proxy listed, anything otherwise. A request under
another name gets `400 Bad Request - Invalid Hostname` (verified on a loopback server with `Host: drafts.example.com`).

**A reverse proxy on the same machine trips over this.** The server is on loopback, the proxy forwards the public name,
and with nothing listed the server refuses it. Either list the proxy in `knownProxies` (which lifts the default to "any
host") or, better, name the public host:

```yaml
server:
  allowedHosts: ["draughtsman.example.com"]
```

With that set (verified): `Host: draughtsman.example.com` answers `200`, any other Host `400`.

### `server.forceHttps`

Makes every cookie `Secure` and sends `Strict-Transport-Security: max-age=2592000` (30 days, without `includeSubDomains`
or `preload`), whatever scheme the server itself saw. Turn it on once `https://` works through the proxy and every request
reaches the server through it. On plain HTTP it makes sign-in unusable: a browser discards a `Secure` cookie set over an
insecure connection (Chrome is lenient on `localhost`, Safari is not). Verified: with `forceHttps: true` and a request over
plain HTTP to an allowed host, the response carried `Strict-Transport-Security` and `Set-Cookie: __Host-ds_session=...;
secure`.

`forceHttps` and `knownProxies` do different jobs. `forceHttps` fixes cookies and HSTS. `knownProxies` fixes the client
address. Set both.

## What a proxy must do

Whatever you use:

| Requirement | Why |
| --- | --- |
| Terminate TLS and forward to `http://127.0.0.1:5180` | The server has no TLS. |
| Pass the original `Host` | Cookies, the Host allow-list and the `__Host-` cookie prefix depend on it. |
| Send `X-Forwarded-For` and `X-Forwarded-Proto` | Client address and scheme (and list the proxy in `knownProxies`). |
| Allow request bodies up to about 12 MB | The server accepts up to 10 MiB for a document, an export or an MCP call and `assets.maxSizeBytes` (10 MiB) for an image upload. nginx's default is 1 MB: verified, a 2 MB import through default nginx is refused by nginx with `413` and its error log says `client intended to send too large body`; with `client_max_body_size 12m` the same request reached Draughtsman (which answered `401`, not signed in). |
| Allow responses that take minutes | Generating a diagram from text waits for the AI model: the default timeout for a model call is 300 seconds (`ai.timeoutSeconds`). nginx's default read timeout is 60 seconds and IIS Application Request Routing's is 30 seconds. Set the proxy's to at least 330 seconds if you use the AI features. (The proxy defaults are from each product's documentation; not exercised end to end here.) |
| Not buffer `/mcp` | MCP over HTTP can stream. Only matters for nginx (`proxy_buffering off`). |

Draughtsman needs no WebSocket: the editor polls for changes (`GET /api/documents/{id}` with `If-None-Match`, about once a
second) and MCP uses streamable HTTP.

## Behind a CDN or WAF

Nothing in this section is a requirement. A CDN, web application firewall or gateway in front of the proxy (Cloudflare,
Azure Front Door, F5, nginx with `gzip on`, and the like) works with Draughtsman as it ships.

**What was wrong, and what the product does now.** Every response that carries a document carries its version as an
`ETag`, and a save, rename or version restore quotes that version back in `If-Match`. A CDN or gateway that compresses a
response commonly turns its strong `ETag` into a weak one (`W/"dv1:…"`) on the way out, and some remove it. Earlier
builds refused a weak tag, so saving and renaming failed with `400 Invalid If-Match header` while opening a document,
creating one and every other read worked, which looks like an application fault and is not. This is what the product
does about it now:

- The server accepts the weak spelling of its own version tag. The version is an opaque identifier the server issued,
  not a byte-exact validator, so `W/"dv1:…"` names the same version as `"dv1:…"`. A stale version is still refused
  with `412 Document has changed`, weak or strong: the protection against two writers overwriting each other is
  unchanged. A list, `*` and a missing `If-Match` are refused as before.
- The same version is sent a second time, unquoted, as `X-Document-Version`, which intermediaries have no reason to
  rewrite. The browser editor reads that header first and falls back to the `ETag`, so it works through a gateway that
  removes or rewrites the `ETag`. API clients keep using `ETag` and `If-Match` as before.
- API responses carry `Cache-Control: no-store, no-transform`. `no-transform` asks intermediaries not to alter the
  payload, but it is only a request, and some intermediaries, Cloudflare among them, ignore it: behind Cloudflare Free
  a response carrying it was still compressed and its `ETag` still weakened. The two points above, not this header,
  are what keep saving working.

**You do not need to change anything for saving to work.** A weakened tag is accepted and the editor sends
`X-Document-Version`, so no CDN, WAF or compression setting is required. Admin > Connection check shows the weakened
`ETag` and the compression it saw as warnings, not failures; both are harmless to this version.

**If you still want the `ETag` to stay strong** (say a script of yours quotes it in `If-Match`), the options below
are optional, and for Cloudflare they come with an honest caveat:

- Cloudflare: we do **not** recommend switching compression off for the application's hostname with a Compression
  Rule. In our one test, behind Cloudflare Free, that removed the `Content-Encoding` header but the browser still
  received the weakened `W/"dv1:…"` `ETag`. Cloudflare's "Respect Strong ETags" (Caching > Configuration) is the
  documented setting for keeping strong `ETag`s; **we have not tested it**, so do not rely on it without trying it.
- nginx: `gzip off;` in the `location /api/` block, since nginx weakens an `ETag` when it compresses a response
  (reported behaviour, see below).
- Other CDNs and gateways: the equivalent of "keep strong `ETag`s", where one exists.

**What was verified, and what was not.** A scratch server and a small reverse proxy written for the purpose, one that
gzip-compresses JSON and rewrites `ETag: "dv1:…"` to `ETag: W/"dv1:…"` and a second that removes the `ETag`, were put in
front of a build with this change and of a build without it. Without it, every save, rename and restore through the
weakening proxy was `400` (and `428` through the one that removed the header); with it they succeed and a stale
version is still `412`. The server's own tests do the same with a handler that rewrites the `ETag` in a response. **Not
verified by us:** Azure Front Door, F5 and nginx themselves, including that nginx weakens a strong `ETag` when it
compresses a response, and Cloudflare's "Respect Strong ETags" setting; those are reported behaviours, not something
exercised here. **Observed once, on a real deployment behind Cloudflare Free:** a save, a rename, a named version and a
restore all work through it with compression on; a stale save is `412`; Cloudflare compressed a response that carried
`no-store, no-transform` and weakened its `ETag`, while `X-Document-Version` arrived intact; and a Compression Rule set
to Off for the hostname removed `Content-Encoding` but did not keep the strong `ETag` (a single observation, made before
the weak-tag fix).

**Symptoms, if you are on a build without this change.** `400 Invalid If-Match header` ("If-Match must be a strong entity tag") on every save and rename, never on
create or read; the editor says "Not saved" and "The server refused this save (400)". Only the `ETag` is affected: the
same request sent straight to the server succeeds. **Upgrade.** A current build needs no CDN setting; the optional
settings above are not a fix for an old build, and the Cloudflare one is untested.

## Caddy

Caddy gets and renews a public certificate by itself, sets `X-Forwarded-For` and `X-Forwarded-Proto`, and passes `Host`.
Install Caddy on the same machine as Draughtsman; `/etc/caddy/Caddyfile`:

<!-- file: Caddyfile -->
```
draughtsman.example.com {
	reverse_proxy 127.0.0.1:5180
}
```

`draughtsman.yaml`:

```yaml
server:
  knownProxies: ["127.0.0.1", "::1"]
  allowedHosts: ["draughtsman.example.com"]
  forceHttps: true
```

Checked with Caddy 2 in a container, in front of a server on the host (`tls internal` and a `localhost` site instead of a
public name): `curl -k https://localhost:8443/api/health` answered `{"status":"ok"}`, the first administrator was created
through it, the session cookie came back as `__Host-ds_session`, and the security log recorded the client address Caddy saw.
Automatic public certificates need ports 80 and 443 reachable from the internet; on a private network use
`tls internal` (Caddy's own CA, which clients must trust) or supply `tls /path/cert.pem /path/key.pem`.

## nginx

nginx does not get certificates for you; use certbot or your organisation's. `/etc/nginx/conf.d/draughtsman.conf`:

<!-- file: nginx.conf -->
```
server {
    listen 443 ssl;
    http2 on;
    server_name draughtsman.example.com;

    ssl_certificate     /etc/ssl/draughtsman/fullchain.pem;
    ssl_certificate_key /etc/ssl/draughtsman/privkey.pem;

    client_max_body_size 12m;

    location / {
        proxy_pass http://127.0.0.1:5180;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 330s;
    }

    location /mcp {
        proxy_pass http://127.0.0.1:5180;
        proxy_http_version 1.1;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;
        proxy_read_timeout 330s;
    }
}

server {
    listen 80;
    server_name draughtsman.example.com;
    return 301 https://$host$request_uri;
}
```

Same `draughtsman.yaml` as for Caddy. Checked with nginx 1.27 in a container (`nginx -t` passed; requests through it
reached the server; the body-limit behaviour above). Certificate renewal and the `http2 on;` directive (nginx 1.25.1 and
later; older versions write `listen 443 ssl http2;`) are the parts to adapt.

## IIS with Application Request Routing (Windows)

**Unverified: no Windows machine was available.** This is the standard ARR reverse-proxy recipe with the settings
Draughtsman needs; check it on a test site first.

1. Install the **URL Rewrite** and **Application Request Routing** modules. In IIS Manager, at the server level, open
   **Application Request Routing Cache**, then **Server Proxy Settings**, tick **Enable proxy** and set **Time-out** to
   `330` seconds (the default is 30).
2. Create a site bound to `https` with your certificate and the host name.
3. In the site's `web.config`:

   ```xml
   <configuration>
     <system.webServer>
       <rewrite>
         <rules>
           <rule name="Draughtsman" stopProcessing="true">
             <match url="(.*)" />
             <action type="Rewrite" url="http://127.0.0.1:5180/{R:1}" />
             <serverVariables>
               <set name="HTTP_X_FORWARDED_PROTO" value="https" />
             </serverVariables>
           </rule>
         </rules>
       </rewrite>
       <security>
         <requestFiltering>
           <requestLimits maxAllowedContentLength="12582912" />
         </requestFiltering>
       </security>
     </system.webServer>
   </configuration>
   ```

4. At the server level, **URL Rewrite > View Server Variables**, add `HTTP_X_FORWARDED_PROTO` to the allowed list; without
   it IIS refuses the rule.
5. In `draughtsman.yaml` list `127.0.0.1` and `::1` in `server.knownProxies` (IIS reaches the server over loopback), name the
   host in `server.allowedHosts`, and set `server.forceHttps: true`.

Two ARR behaviours to check, because the server needs the public Host and the client address: in **Server Proxy
Settings** tick **Preserve client IP in the following header**, enter `X-Forwarded-For`, and leave **Include TCP port from
client IP** unticked; and make sure ARR forwards the original Host header (the `preserveHostHeader` attribute of the
`system.webServer/proxy` section in `applicationHost.config`; attribute names are from ARR's schema, so check them against
your version). If sign-in answers `400 Bad Request - Invalid Hostname`, the server is receiving a different Host: fix that
or name what it receives in `server.allowedHosts`.

## Docker Compose with Caddy

Draughtsman and Caddy on one network; only Caddy publishes ports. The compose network has a fixed range so
`knownProxies` can name it.

Generate the key-encryption key once and keep it outside the data volume (it protects the stored AI key; see the
[admin guide](admin-guide.md#ai-settings)). `.env` next to the compose file, mode `600`:

```sh
echo "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$(openssl rand -base64 32)" > .env && chmod 600 .env
```

<!-- file: docker-compose.yml -->
```yaml
name: draughtsman
services:
  draughtsman:
    image: draughtsman:0.1.0          # the image you built or were given; none is published yet
    restart: unless-stopped
    environment:
      DRAUGHTSMAN_KEY_ENCRYPTION_KEY: ${DRAUGHTSMAN_KEY_ENCRYPTION_KEY:?create the .env file first}
    volumes:
      - ./data:/data
    networks: [internal]

  caddy:
    image: caddy:2
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    depends_on: [draughtsman]
    networks: [internal]

volumes:
  caddy-data:

networks:
  internal:
    ipam:
      config:
        - subnet: 172.30.0.0/24
```

`Caddyfile` (the service name resolves on the compose network):

<!-- file: compose.Caddyfile -->
```
draughtsman.example.com {
	reverse_proxy draughtsman:5180
}
```

`draughtsman.yaml`, which goes inside the data folder (`./data/draughtsman.yaml`):

<!-- file: compose.draughtsman.yaml -->
```yaml
server:
  knownProxies: ["172.30.0.0/24"]
  allowedHosts: ["draughtsman.example.com"]
  forceHttps: true
```

```sh
mkdir -p data && cp draughtsman.yaml data/
sudo chown -R 10001:10001 data && sudo chmod 700 data && sudo chmod 600 data/draughtsman.yaml   # the container's uid; see below
docker compose up -d
docker compose logs draughtsman | grep "setup code"        # the one-time first-run code
```

The data folder is a bind mount, so it must belong to the container's user, uid `10001` (Docker Desktop on macOS and Windows
maps ownership for you and the `chown` is not needed there). Do not mount `draughtsman.yaml` on its own as a read-only
file: the server tightens the file's mode at start-up and logs a warning when it cannot. Back up with
`docker compose exec draughtsman draughtsman backup --out /data/backups/` and copy the bundle off the
machine; the image has no `draughtsman` command on its path, which is why the call goes through `dotnet`. The line
`Overriding HTTP_PORTS '8080'` in the log is harmless.

## Firewall

Allow inbound 443 (and 80 if the proxy fetches certificates); block 5180 from everywhere but the proxy. With the server
on loopback and the proxy on the same machine, nothing listens on the network at 5180 at all, which is the best firewall.

**Unverified (no Linux host or Windows machine here); standard commands:**

```sh
sudo ufw allow 443/tcp && sudo ufw allow 80/tcp        # Ubuntu; leave 5180 unmentioned
sudo firewall-cmd --permanent --add-service=https && sudo firewall-cmd --reload     # RHEL and relatives
```

```powershell
New-NetFirewallRule -DisplayName "Draughtsman HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
```

**Docker and ufw.** Docker writes its own iptables rules for published ports, and they bypass ufw. A container published
as `-p 5180:5180` is reachable from the network whatever `ufw status` says. Publish to loopback
(`-p 127.0.0.1:5180:5180`, checked: `lsof` showed the listener on `127.0.0.1` only) or publish nothing and let a proxy
container on the same network reach it, as in the compose file.

## File permissions

The data folder holds every document, password and token hashes, the key ring and the stored AI key. The server keeps it
private to the account that runs it:

- On Linux and macOS it sets its umask to `077` before creating anything, so the database, its `-wal` and `-shm` files,
  uploads and logs are owner-only from their first byte (files `0600`, folders `0700`), and on start-up it tightens what
  an earlier run or a restore left behind: `data.db`, `draughtsman.yaml`, `keys/`, `ai/`, `branding/`, `assets/`,
  `themes/` and `logs/`. Verified: a restored `data.db` of mode `644` was `600` after the first start.
- It never makes an existing data folder stricter itself, in case you shared it on purpose, but it says so when the folder
  is open to others: `The data folder /... is open to other users (mode 755); chmod 700 it unless that is intended.`
  Act on that warning: `chmod 700 <data folder>`.
- **Do not `chmod 777` a data folder.** An old README suggested it for Docker bind mounts. A world-writable folder lets any
  local user replace the database or the key ring. For a Docker bind mount give the folder to the container's user
  instead: `sudo chown -R 10001:10001 ./draughtsman-data && sudo chmod 700 ./draughtsman-data`. Docker Desktop on macOS and
  Windows maps ownership for you, so the `chown` is not needed there; a named volume needs nothing.
- `draughtsman.yaml` holds the Postgres password if you use Postgres. It is tightened to `0600` like the rest; keep it out
  of version control and out of tickets.
- Backups contain password hashes and, with `--include-secrets`, the key ring: they are written owner-only; keep them
  access-controlled ([`upgrade-backup.md`](upgrade-backup.md)).
- **Windows has no modes.** Put the data folder where only the service account and administrators have access. The
  service installer creates `C:\ProgramData\Draughtsman` and grants `NT AUTHORITY\LocalService` modify rights on it
  (see [`install-windows.md`](install-windows.md)); do not add `Users` or `Everyone`. **Unverified on Windows.**
- Run the server as an unprivileged account. The Docker image runs as uid `10001`; the systemd unit uses a dynamic user;
  the launchd plist runs as root unless you pass `--user`.

## Outgoing email

Outgoing email is optional. Without it everything works; the only thing it adds is the **Forgot password?** link on the sign-in
page (an administrator can always create a reset link by hand: [`admin-guide.md`](admin-guide.md#forgotten-passwords)). The server
sends a message in two cases: a reset link someone asked for, and the test buttons on **Admin, Email**, which send only to the
administrator pressing them. Nothing else is ever emailed, and no message carries a password.

Two things make "Forgot password?" appear, and **both** are needed: a mail server to send through, and the public address of this
instance (`server.publicUrl`). You can set them in either of two places, or both.

**In the browser (no restart).** **Admin, Email** has a form for the mail server, port, encryption, user name, password, From address,
optional Reply-to and the public address, ready-made starting points (Microsoft 365, Google Workspace, Amazon SES, SendGrid, Postmark,
Mailgun, your own server), a **Save** that applies at once, and a way to test the saved settings or the typed values without saving.
See [`admin-guide.md`](admin-guide.md#email). The password is stored encrypted under the key ring in `keys/`
([Protecting the key ring](status.md#protecting-the-key-ring)), in `<data>/email/settings.json`.

**In `draughtsman.yaml`** (for an operator who wants it fixed in the deployment, or a password from a secret):

```yaml
server:
  # The address people use to reach this instance. Links in emails are built on it and on nothing else.
  publicUrl: https://draughtsman.example.com

email:
  provider: smtp
  from: Draughtsman <draughtsman@example.com>
  smtp:
    host: smtp.example.com
    port: 587
    tls: starttls
    user: draughtsman@example.com
    passwordEnvironmentVariable: DRAUGHTSMAN_SMTP_PASSWORD   # the variable's name; the password itself is never in this file
```

Restart after editing the file, then open **Admin, Email**, read what it says, and send a test. The page lists anything missing in plain
sentences, and a failed test says which kind of problem it was.

**Which wins when both are used.** A value `draughtsman.yaml` sets wins, and its field is shown read-only on the page with the reason
(*Set in draughtsman.yaml*); an operator's deployment setting cannot be silently replaced from the browser. Every key the file leaves at
its default (empty text, port `587`, `starttls`) counts as not set, so it locks nothing, and the page's saved value (else the default)
applies. The password follows the same rule: when the variable named by `passwordEnvironmentVariable`, or the file named by
`passwordFile`, holds a value, it wins and the page cannot replace it; otherwise the password saved on the page is used. With
`email.provider: file` the mail server fields are locked (no server is used). A value saved on the page for a field the file later takes
over stays underneath and applies again if the file's key is removed. **Remove saved settings** on the page returns to the file alone.

**Why `server.publicUrl` is a setting.** The link in an email must point at your instance. Taking the address from the request
would let anyone who can send a request with a `Host:` header of their choosing make the server email a victim a link to a site
of their own, with a working reset token in it. So the address is configured, never read from a request. It must be an `http` or
`https` address with no user name, query or fragment.

### The SMTP options

| Setting | Default | Meaning |
| --- | --- | --- |
| `email.provider` | empty | `smtp` sends through a mail server. `file` writes each message to a file and sends nothing (development and demos). Empty: no email. |
| `email.from` | empty | Who messages come from: `name@example.com` or `Name <name@example.com>`. Required. |
| `email.replyTo` | empty | Optional: where a reply should go, if not to `email.from`. |
| `email.smtp.host`, `email.smtp.port` | empty, `587` | The mail server. |
| `email.smtp.tls` | `starttls` | `starttls` upgrades the connection before any password is sent, **and refuses to go on if the server will not or its certificate is not trusted**. `none` sends in the clear and is accepted only without a user name, or to a server on this machine. |
| `email.smtp.user` | empty | The account to sign in to the server with. Empty: no sign-in (an open relay on a private network). |
| `email.smtp.passwordEnvironmentVariable` | `DRAUGHTSMAN_SMTP_PASSWORD` | The **name** of the environment variable that holds the password. |
| `email.smtp.passwordFile` | empty | A file holding the password (a Docker or Kubernetes secret, or a root-only file), relative to the data folder or absolute. When set it is used instead of the variable; one trailing newline is ignored. |
| `email.smtp.timeoutSeconds` | `20` | How long to wait for the mail server before giving up and saying so. |
| `email.file.folder` | `outbox` | Where the `file` provider writes, relative to the data folder. Each file holds its link in the clear: development only. |
| `auth.passwordReset.selfService` | `true` | Turn **Forgot password?** off even with email working. |
| `auth.passwordReset.emailedLinkMinutes` | `60` | How long an emailed link works (5 to 1440). |
| `auth.passwordReset.adminLinkHours` | `24` | How long an administrator-created link works (1 to 168). |

The password is read at the moment of sending and is never logged, never shown (the Email page names where it comes from, not
what it is) and is never in an error message: a failure is reported as a short class of problem, and the mail server's own reply text
is not repeated at all. Notes on what this client does and does not do:

- It speaks **STARTTLS**, normally on port 587. It does **not** do implicit TLS on port 465 (the framework's mail client has no
  such mode): use 587, or a relay on this machine that accepts 465 and forwards on 587. A server that offers no STARTTLS is
  refused when `tls: starttls`, before anything is sent.
- The server's certificate is checked against the operating system's trusted roots. A mail server with a self-signed
  certificate fails with *The secure connection (STARTTLS) … failed: The remote certificate is invalid*; install the issuing
  certificate in the system trust store of the host that runs Draughtsman. There is deliberately no setting to skip the check.
- **The server connects to whatever host and port an administrator enters.** For an outgoing-mail setting that is the point, so the
  setting can be changed only by an administrator on a browser session (not an Editor, and not an API token), the test message goes
  only to that administrator, and an unusable value is refused before anything is sent: the host must be a plain host name or address
  (no web address, user name, path or port in it), and no field accepts a line break. If the host this server can reach includes
  internal services you do not want a person with an administrator account to be able to probe, firewall the server's outbound
  connections; Draughtsman does not filter destinations.
- A sign-in the server refuses is, to this client, followed by the first command that needs one, so a wrong password usually reads
  *did not accept the user name or password* from that command.
- The message is plain text, UTF-8 (quoted-printable), one recipient: the address on the account, which must be a plain `name@host`
  (an account whose address is anything else is never emailed).
- The mail is handed over **after** the person has been answered, in the background, so the page and its timing are the same
  whether or not an account has the address. A failure to send is therefore not shown to the person who asked; it is a *Reset
  email could not be sent* event in the security log (with the reason), and the link made for it is ended.

**Behind a firewall.** The server needs to reach the mail server on its port (587). Nothing else about email is inbound.
**Docker:** pass the password with a secret or `-e DRAUGHTSMAN_SMTP_PASSWORD=...`, or mount a file and set
`email.smtp.passwordFile`. **Windows service / systemd / launchd:** set the variable in the service's environment, or use the
file. What was run: a running server delivering a test email and a reset email to a local SMTP sink on a free port (no TLS), and the
same server refusing a sink that offered STARTTLS with a self-signed certificate (it began STARTTLS, rejected the certificate,
and sent no password and no message); the full STARTTLS path (upgrade, then sign-in, then the message, in that order, over TLS)
is exercised by automated tests against an in-process server whose certificate the test trusts. No real mail provider (Office 365,
Gmail, SES) was used, so provider-specific settings (an app password, an SMTP relay user) are **unverified** here.

## Postgres

With `storage.provider: postgres` the database lives on your Postgres server, everything else in the data folder.
The connection string goes in `draughtsman.yaml` (owner-only, see above):

```yaml
storage:
  provider: postgres
  connectionString: "Host=db.internal;Port=5432;Database=diagrams;Username=drafter;Password=...;SSL Mode=Require"
```

Use a dedicated database and role, turn TLS on between the two (`SSL Mode=Require` is an Npgsql connection-string
option), and do not expose Postgres beyond the two machines. The role needs the right to create tables (migrations run at
start-up). Back up with `pg_dump`; see [`upgrade-backup.md`](upgrade-backup.md#postgres).

## Upgrading an instance

The procedure, and the evidence behind it, is [`upgrade-backup.md`](upgrade-backup.md): read it once before the first upgrade, and keep its checklist. For a deployed instance behind a proxy it comes
to this:

1. **Read the release notes** ([`CHANGELOG.md`](../CHANGELOG.md)): the *Breaking changes* section, and whether the release has migrations. A patch release (`0.1.x`) needs nothing beyond these steps.
2. **Back up**: `draughtsman backup --out <folder on another disk>` (SQLite; the server may keep running), or the `pg_dump` the server prints for Postgres. Copy it off the machine.
3. **Check, with the NEW binary and the service's own account and environment**, before stopping anything:
   `sudo -u <service account> /opt/draughtsman-new/draughtsman config check --DRAUGHTSMAN_DATA=/var/lib/draughtsman`, with the same environment variables the service has (the key-encryption key, if you use one). It changes nothing; it exits `0` when
   the new version would start and `1` (or `3` for a database newer than this build) when it would refuse, and says why.
4. **Stop, replace, start.** Stop the service (or the container). Replace the program **in the same folder** (a Docker image: change the tag; the program is always `/app`) and keep the data folder, which belongs outside the program
   folder. Start it. The proxy returns `502` for the few seconds the migration takes; nothing else changes (hosts, proxies, TLS and `draughtsman.yaml` are untouched).
5. **Check**: `curl -s https://draughtsman.example.com/api/health`, sign in, open a diagram, look at **Status**, and read the log for warnings (a setting the new version no longer reads is one).
6. **Going back** is restoring the pre-upgrade copy (SQLite) or the dump (Postgres) and starting the older program: [`upgrade-backup.md`](upgrade-backup.md#downgrade-going-back). Do it before people have saved anything new; anything saved after the
   upgrade is lost by going back.

On **Postgres** the server cannot take the safety copy or undo a failed migration (it logs a notice naming `pg_dump` and the pending migrations first), so step 2 is not optional there.

## Check your deployment

From another machine, with the real host name:

```sh
curl -sI https://draughtsman.example.com/api/health          # 200, Strict-Transport-Security present when forceHttps is on
curl -s  https://draughtsman.example.com/api/health          # {"status":"ok"}
curl -sI http://draughtsman.example.com:5180/api/health      # must fail to connect from outside
curl -s -o /dev/null -w '%{http_code}\n' -H 'Host: other.example.com' https://draughtsman.example.com/api/health
```

Sign in, then open **Security** in the admin bar: the source address on your own sign-in must be your machine's address,
not the proxy's (if it is the proxy's, `knownProxies` does not list the address the server actually sees: connect, look
at the address in the log, and list it or its range). In the browser's developer tools the session cookie should be
`__Host-ds_session` with `Secure` ticked.
