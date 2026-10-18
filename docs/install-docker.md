# Install with Docker

The container image runs the server as a non-root user (uid `10001`) on a small ASP.NET Core runtime image with Node 22
added for the layout sidecar. The data folder is a volume at `/data`. Everything here was run with Docker Desktop (server
20.10.14) on an Apple-silicon Mac, which runs `linux/arm64` containers; commands marked **unverified** were not run.

## Get an image

**No image is published yet.** Build one from the source repository (needs the internet for the base images and the Node and
npm packages), or load one someone exported for you:

```sh
docker build -t draughtsman:0.1.0 .                       # from the repository root; verified
docker run --rm draughtsman:0.1.0 --version               # Draughtsman 0.1.0 (build <date>)
```

For an air-gapped host, export on a connected machine and load on the other
([`install.md`](install.md#installing-without-internet-access), and all of it in [`air-gapped.md`](air-gapped.md)): `scripts/docker-export.sh draughtsman:0.1.0 --out ./offline`
produces a compressed image (304 MB here) with its checksum, then `docker load -i draughtsman-0.1.0-image.tar.gz`.

PDF from the editor needs nothing in the image: it prints from the user's own browser. For server-side PDF (scripts and agents) build with the optional browser, which adds several hundred megabytes (`docker build --build-arg
INSTALL_CHROMIUM=true -t draughtsman:0.1.0-chromium .`; the first start then writes `export.chromium.enabled: true` into a
data folder that has no `draughtsman.yaml` yet and never overwrites one that does). See [`pdf-export.md`](pdf-export.md).

## Run it

Start with the port published to loopback only, and put a reverse proxy in front for anyone else
([`deployment.md`](deployment.md)):

```sh
mkdir -p draughtsman-data && sudo chown -R 10001:10001 draughtsman-data && sudo chmod 700 draughtsman-data   # Linux; see below
docker run -d --name draughtsman --restart unless-stopped \
  -p 127.0.0.1:5180:5180 \
  -v "$(pwd)/draughtsman-data:/data" \
  draughtsman:0.1.0
curl -s http://127.0.0.1:5180/api/health       # {"status":"ok"}
docker logs draughtsman 2>&1 | grep "setup code"
```

Open <http://localhost:5180> and enter the setup code on the first-run page ([`admin-guide.md`](admin-guide.md#first-run-and-the-setup-code)).

- **The container binds every interface** (`ASPNETCORE_URLS=http://0.0.0.0:5180`); what limits who can reach it is the
  `-p` you give. `-p 127.0.0.1:5180:5180` is loopback only (checked with `lsof`); `-p 5180:5180` is every interface of the host
  and, on Linux, bypasses `ufw` ([`deployment.md`](deployment.md#firewall)).
- **Data folder ownership.** On Linux a bind mount must belong to uid `10001` (`chown -R 10001:10001`, then `chmod 700`);
  Docker never rewrites a bind mount's ownership. Docker Desktop on macOS and Windows maps it for you. **Never `chmod 777`.**
  A **named volume** (`-v draughtsman-data:/data`) needs no `chown`: the image creates `/data` with mode `700`, owned by uid
  `10001`, and a new volume copies both. A volume created by an older image (mode `755`) makes the server log `The data folder
  /data is open to other users (mode 755); chmod 700 it`; fix that once with `docker exec draughtsman chmod 700 /data`.
- **Settings**: `draughtsman.yaml` goes in the data folder (`draughtsman-data/draughtsman.yaml`; `chmod 600`, owned by uid
  `10001`). Restart the container after editing. [`draughtsman.example.yaml`](draughtsman.example.yaml) lists everything. Do
  not mount the file alone as read-only: the server tightens the file's mode at start and warns when it cannot.
- **Log lines to expect.** `Key ring protection: The key ring is not protected ...` on Linux until you supply a
  key-encryption key (below), and `Overriding HTTP_PORTS '8080' and HTTPS_PORTS ''. Binding to values defined by URLS` (harmless).

### Protect the stored AI key

On Linux the key ring that encrypts the stored AI API key is left as plain files unless you give it a key from outside the data
volume ([`admin-guide.md`](admin-guide.md#the-api-key-and-the-key-ring)):

```sh
openssl rand -base64 32 > draughtsman-kek.txt && chmod 600 draughtsman-kek.txt
docker run -d --name draughtsman -p 127.0.0.1:5180:5180 \
  -v "$(pwd)/draughtsman-data:/data" \
  -e DRAUGHTSMAN_KEY_ENCRYPTION_KEY="$(cat draughtsman-kek.txt)" \
  draughtsman:0.1.0
```

The log then says `Key ring protection: key-encryption key from the environment variable DRAUGHTSMAN_KEY_ENCRYPTION_KEY` (checked). Keep
the key file somewhere the data volume's backups do not reach. Or mount it as a secret file and set `keyRing.keyFile` in
`draughtsman.yaml`.

## nginx or another proxy on the Docker host

With the port published to loopback (`-p 127.0.0.1:5180:5180`) and nginx on the same machine, the container does **not** see nginx as `127.0.0.1`: it
sees Docker's bridge gateway, `172.17.0.1` on the default bridge, which is not loopback, so the server does not believe the proxy's
`X-Forwarded-For` or `X-Forwarded-Proto` until you list it. Until then every visitor shares one address, which makes the sign-in throttle (twenty
failed attempts per address in 15 minutes) everyone's, and the log carries the warning `Forwarding headers (X-Forwarded-For and the like) arrived
from 172.17.0.1, which is not listed in server.knownProxies`. In `draughtsman-data/draughtsman.yaml` (owned by uid `10001`, mode `600`), then
`docker restart draughtsman`:

```yaml
server:
  knownProxies: ["172.17.0.1"]    # the bridge gateway; "172.17.0.0/16" if it can change (it also trusts every container on the bridge)
  forceHttps: true                # once https:// works through the proxy; browse only over https:// afterwards
```

and in nginx, `proxy_set_header Host $host; proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; proxy_set_header X-Forwarded-Proto $scheme;`
(with Cloudflare in front, nginx also needs `set_real_ip_from` and `real_ip_header CF-Connecting-IP`; the full block is in
[`deployment.md`](deployment.md#serverknownproxies)). Check the gateway on your host with
`docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}'`. Docker Desktop's published ports reach the container from the same
`172.17.0.1`.

## Commands inside the container

The image's entry point passes its arguments to the server. For everything else, `draughtsman` is on the path inside the
container (a one-line wrapper over `dotnet /app/draughtsman.dll`, which still works the same way):

| Task | Command |
| --- | --- |
| Version | `docker run --rm draughtsman:0.1.0 --version` |
| Back up | `docker exec draughtsman draughtsman backup --out /data/backups/` |
| Restore into an empty folder | `docker run --rm -v "$(pwd)/new-data:/data" -v "$(pwd)/backups:/backups:ro" draughtsman:0.1.0 restore /backups/<bundle>.zip` |
| Lost administrator | `docker exec draughtsman draughtsman reset-admin admin@example.com --generate` |
| The setup code | `docker logs draughtsman 2>&1 \| grep "setup code"` |
| What is wrong with this install (works with the server stopped) | `docker exec draughtsman draughtsman doctor` |
| A support bundle to send us | `docker exec draughtsman draughtsman support-bundle --output /data/support-bundle.zip`, then `docker cp draughtsman:/data/support-bundle.zip .`, or `docker exec draughtsman cat /data/support-bundle.zip > support-bundle.zip`. Write it under `/data`: `docker cp` cannot read from `/tmp` in a read-only container |

Run `doctor` as the image's own user, which is what `docker exec` does by default; `docker exec -u 0` hides a database the image's user cannot write, because root can open anything.
`doctor` exits `0` with no problems, `1` with warnings and `2` with failures; the support bundle holds no document, secret, token or key, and its `README.txt` lists exactly what it does hold
([`troubleshooting.md`](troubleshooting.md#troubleshooting-an-install)). Behind nginx on the same host, Docker's published port makes the proxy's address the bridge gateway (usually `172.17.0.1`), which the
server does not trust until it is listed: `server.knownProxies: ["172.17.0.0/16"]` in `/data/draughtsman.yaml` ([`deployment.md`](deployment.md#serverknownproxies)); **Admin > Connection check** shows the exact address.
To keep a log file in the volume for the bundle, set `logging.file.enabled: true` in `/data/draughtsman.yaml` and restart.

(An image built before the wrapper was added has no `draughtsman` on the path: there, use `dotnet /app/draughtsman.dll backup ...`.) A backup written to
`/data/backups/` is on the same volume as the data: copy it off, or mount a second folder for it
([`upgrade-backup.md`](upgrade-backup.md)).

## A local model on the host

Inside a container `localhost` is the container. Checked on Docker Desktop: from the image, `http://host.docker.internal:11434`
reached the host's Ollama and `http://localhost:11434` did not. Use `http://host.docker.internal:11434` as the Ollama endpoint
on **AI** in the admin pages. On Linux add `--add-host=host.docker.internal:host-gateway` to `docker run` (unverified) and make
Ollama listen on an address the container can reach.

## Postgres

Run Postgres wherever you like and put the connection string in `draughtsman.yaml` (`storage.provider: postgres`,
[`deployment.md`](deployment.md#postgres)). From the container, name the database host as the container's network sees
it (a compose service name, or `host.docker.internal`). Verified with Postgres 17: the server migrated an empty database
(6 migrations), first-run and a restart worked, and `pg_dump` and `pg_restore` of that database round-tripped.

## Compose, TLS and a reverse proxy

[`deployment.md`](deployment.md#docker-compose-with-caddy) has a compose file with Caddy in front, checked end to end.

## Upgrade

Build or load the new image under a new tag, `docker stop draughtsman && docker rm draughtsman`, and run the new tag with the
**same** `-v`. Back up first, and check the new image against the volume without starting it:
`docker run --rm -v draughtsman-data:/data draughtsman:0.2.0 config check`. The database migrates itself at start; a SQLite database is copied to
`data.db.pre-migration-<stamp>` first and put back if the upgrade fails (exit code `4`). Going back means restoring that copy or your backup: an older image refuses a newer database and
exits with code `3`, writing nothing ([`upgrade-backup.md`](upgrade-backup.md#downgrade-going-back); checked across two images and one volume).

## If the data folder is not writable

If the account in the container (uid `10001`) cannot write the data folder, or the database file inside it, the server stops at once with **one line**
and exit code `5`, and serves nothing:

```
Draughtsman cannot start: it cannot write to the data folder /data, running as draughtsman (uid 10001). Give that account the folder and everything in it and start again, for example: chown -R 10001 /data (a Docker bind mount must be owned by the container's own uid, 10001 in the published image). Nothing was changed. See docs/troubleshooting.md.
```

On the host, `sudo chown -R 10001:10001 draughtsman-data && sudo chmod 700 draughtsman-data`, then `docker start draughtsman`. The usual cause is a
`data.db` restored or copied in as root (`docker cp`, `cp` under `sudo`); a bind mount owned by root has the same effect. Once the server is up, a
database that stops accepting writes shows as `503 This instance cannot save changes` on the first request that writes (a disk that fills, checked in a
container with a full `tmpfs` data folder: every correct sign-in answered `503`, and the log named `SQLite Error 13: 'database or disk is full'`), and, when it is
refused outright (a read-only file or mount), as `"status": "degraded"` on `/api/health`. Correct the cause and restart the container.

## If it does not work

[`troubleshooting.md`](troubleshooting.md#troubleshooting-an-install): open **Admin > Status > Connection check**, run `docker exec draughtsman draughtsman doctor`, and send us a `support-bundle`. Useful first: `docker logs draughtsman`, `docker inspect --format '{{.State.ExitCode}}'
draughtsman` (`3` means a newer database), and `curl -s http://127.0.0.1:5180/api/health`.
