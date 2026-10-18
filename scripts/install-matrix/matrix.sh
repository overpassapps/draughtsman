#!/bin/sh
# The install matrix: ONE scripted first journey (journey.mjs) run against every cell of cells.json, with a pass/fail table and a report.
# Needs Docker, Node 22, curl and openssl; builds the image once and an archive only when a cell needs it. Plain POSIX sh.
#
#   scripts/install-matrix.sh                       every automated cell, one at a time
#   scripts/install-matrix.sh --cells docker-direct,docker-caddy
#   scripts/install-matrix.sh --keep                leave the containers, networks and volumes for inspection (see --clean-stale)
#   scripts/install-matrix.sh --skip-build          reuse an existing draughtsman:matrix image (and do not remove it at the end)
#   scripts/install-matrix.sh --break NAME          a deliberate-breakage proof: break one thing in the cell that defines NAME and expect a FAIL
#   scripts/install-matrix.sh --list                the cells, the breaks and the manual cells
#   scripts/install-matrix.sh --clean-stale         remove leftovers of an interrupted or --keep run: containers, networks and volumes named dmx-*
#   scripts/install-matrix.sh --vm windows-vm       the MANUAL windows-archive cell: run only the journey, from this Mac, against the VM
#         [--vm-port 5180] [--vm-scheme http] [--setup-code-file FILE]
#
# Output: a table on the terminal, and artifacts/install-matrix/<timestamp>/report.md (git-ignored) with each cell's steps, statuses and evidence.
# Exit code: non-zero if any cell failed unexpectedly (an EXPECTED-FAIL is not one; a MANUAL cell is listed and never counted).
#
# What it creates is recorded in a registry as it is made, and removed from that record only: containers, networks and volumes named
# dmx-<run>-*, the image tag draughtsman:matrix (only if it built it), temp folders, and the pids it started. It never touches anything else on
# the machine, on success, on failure and on Ctrl-C.
set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/scripts/install-matrix"
CELLS="node $HERE/cells.mjs"
PREFIX="$($CELLS prefix)"
IMG="$($CELLS image draughtsman)"
NGINX_IMG="$($CELLS image nginx)"
CADDY_IMG="$($CELLS image caddy)"
UBUNTU_IMG="$($CELLS image ubuntu)"
RID="$(node -e 'console.log(require("crypto").randomBytes(2).toString("hex"))')"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$ROOT/artifacts/install-matrix/$STAMP"
SCRATCH=""
REG=""
KEEP=0
SKIP_BUILD=0
SELECTED=""
BREAK=""
BRK=""
CLEANED=0
TAB="$(printf '\t')"

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
die() { printf 'install-matrix: %s\n' "$*" >&2; exit 2; }
usage() { sed -n '2,/^set -u/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

# ---------------------------------------------------------------------------------------------- registry and cleanup
register() { # register KIND NAME [CHECK]: record a thing this run made, so cleanup removes exactly it
  case "$1" in
    container|network|volume) case "$2" in "$PREFIX"*) ;; *) die "refusing to register $1 '$2': not named $PREFIX*" ;; esac ;;
  esac
  printf '%s\t%s\t%s\n' "$1" "$2" "${3:-}" >> "$REG"
}

remove_entry() { # remove_entry KIND NAME CHECK
  case "$1" in
    container) case "$2" in "$PREFIX"*) docker rm -f "$2" >/dev/null 2>&1 ;; esac ;;
    volume) case "$2" in "$PREFIX"*) docker volume rm -f "$2" >/dev/null 2>&1 ;; esac ;;
    network) case "$2" in "$PREFIX"*) docker network rm "$2" >/dev/null 2>&1 ;; esac ;;
    image) docker rmi "$2" >/dev/null 2>&1 ;;
    pid)
      # Only ever a pid this run started, and only while it is still the program we started (never a recycled pid, never by name).
      if kill -0 "$2" 2>/dev/null && ps -o command= -p "$2" 2>/dev/null | grep -q "$3"; then
        kill "$2" 2>/dev/null
        n=0; while kill -0 "$2" 2>/dev/null && [ $n -lt 30 ]; do sleep 0.5; n=$((n + 1)); done
        if kill -0 "$2" 2>/dev/null; then kill -9 "$2" 2>/dev/null; fi
      fi ;;
    dir) case "$2" in "${TMPDIR:-/tmp}"/dmx-*|/tmp/dmx-*|/private/tmp/dmx-*|/var/folders/*/dmx-*) rm -rf "$2" ;; esac ;;
  esac
  return 0
}

cleanup_from() { # cleanup_from N: remove, newest first, everything registered after line N
  [ -f "$REG" ] || return 0
  total="$(wc -l < "$REG" | tr -d ' ')"
  [ "$total" -gt "$1" ] || return 0
  # Shorten the registry first, then work through what was cut (the last entry removes the scratch folder the registry lives in).
  tail -n "$((total - $1))" "$REG" | sed '1!G;h;$!d' > "$REG.todo"
  if [ "$1" -gt 0 ]; then head -n "$1" "$REG" > "$REG.tmp"; else : > "$REG.tmp"; fi
  mv "$REG.tmp" "$REG"
  while IFS="$TAB" read -r kind name check; do
    remove_entry "$kind" "$name" "$check"
  done < "$REG.todo"
  rm -f "$REG.todo" 2>/dev/null
  return 0
}

cleanup() {
  [ "$CLEANED" = 1 ] && return 0
  CLEANED=1
  if [ -z "$REG" ] || [ ! -f "$REG" ]; then return 0; fi
  if [ "$KEEP" = 1 ]; then
    step "--keep: leaving what this run made (remove it with scripts/install-matrix.sh --clean-stale)"
    cut -f1,2 "$REG" | grep -v '^dir' | sed 's/^/  /'
    return 0
  fi
  cleanup_from 0
  return 0
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP

clean_stale() {
  step "removing leftovers named ${PREFIX}* (nothing else is touched)"
  for c in $(docker ps -a --format '{{.Names}}' | grep "^${PREFIX}" || true); do say "container $c"; docker rm -f "$c" >/dev/null 2>&1; done
  for n in $(docker network ls --format '{{.Name}}' | grep "^${PREFIX}" || true); do say "network $n"; docker network rm "$n" >/dev/null 2>&1; done
  for v in $(docker volume ls -q | grep "^${PREFIX}" || true); do say "volume $v"; docker volume rm -f "$v" >/dev/null 2>&1; done
  say "done. (The image $IMG and temp folders under ${TMPDIR:-/tmp}/dmx-* are left: remove them yourself if you want.)"
}

# ---------------------------------------------------------------------------------------------- small helpers
free_port() { node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }

wait_health() { # wait_health URL [SECONDS [CONTAINER]]: gives up early when CONTAINER has exited (a server that refused to start never will answer)
  i=0; max="${2:-120}"
  while [ "$i" -lt "$max" ]; do
    code="$(curl -ksS -m 3 -o /dev/null -w '%{http_code}' "$1/api/health" 2>/dev/null || true)"
    [ "$code" = "200" ] && return 0
    if [ -n "${3:-}" ] && [ "$(docker inspect -f '{{.State.Running}}' "$3" 2>/dev/null)" = "false" ]; then return 1; fi
    i=$((i + 1)); sleep 1
  done
  return 1
}

redact_log() { sed -E 's/(setup code: *)[A-Za-z0-9]+/\1[redacted]/g'; }

cell_error() { # cell_error DIR MESSAGE...
  d="$1"; shift
  printf '%s\n' "$*" >> "$d/error.txt"
}

add_extra() { # add_extra DIR NAME STATUS DETAIL (newlines in DETAIL are written as \n; render.mjs turns them back)
  printf '%s\t%s\t%s\n' "$2" "$3" "$(printf '%s' "$4" | awk '{ gsub(/\\/, "\\\\"); printf "%s\\n", $0 }' | sed 's/\\n$//')" >> "$1/extras.tsv"
}

make_cert() { # make_cert DIR: a self-signed certificate for localhost, made now, valid two days
  cat > "$1/openssl.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = localhost
[v3]
subjectAltName = DNS:localhost,IP:127.0.0.1
basicConstraints = critical,CA:TRUE
CNF
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$1/key.pem" -out "$1/cert.pem" -days 2 -config "$1/openssl.cnf" >/dev/null 2>&1
}

ensure_network() { # one network per cell (created, registered and removed with the cell); sets NET and SUBNET
  NET="${PREFIX}${RID}-net"
  if ! docker network inspect "$NET" >/dev/null 2>&1; then
    docker network create "$NET" >/dev/null || return 1
    register network "$NET"
  fi
  SUBNET="$(docker network inspect "$NET" --format '{{(index .IPAM.Config 0).Subnet}}')"
  [ -n "$SUBNET" ]
}

run_journey() { # run_journey CELL DIR BASE_URL SETUP_SRC [CONTAINER CLI]   (env NODE_EXTRA_CA_CERTS honoured)
  cell="$1"; cd="$2"; url="$3"; src="$4"; container="${5:-}"; cli="${6:-draughtsman}"
  set -- --base-url "$url" --setup-code-from "$src" --json "$cd/journey.json" --cli "$cli"
  if [ -n "$container" ]; then set -- "$@" --container "$container"; fi
  while IFS= read -r line; do if [ -n "$line" ]; then set -- "$@" --expect "$line"; fi; done <<EXPECT_END
$($CELLS lines "$cell" journey.expect $BRK)
EXPECT_END
  while IFS= read -r line; do if [ -n "$line" ]; then set -- "$@" --expect-selfcheck "$line"; fi; done <<EXPECT_END
$($CELLS lines "$cell" journey.expectSelfcheck $BRK)
EXPECT_END
  node "$HERE/journey.mjs" "$@" > "$cd/journey.out" 2>&1
  rc=$?
  grep -E '^(journey:|  \[)' "$cd/journey.out" | sed 's/^/    /' | head -40
  return $rc
}

run_journey_existing() { # run_journey_existing CELL DIR URL CONTAINER EMAIL   (the instance already has users; DMX_FIXTURE_PASSWORD is exported)
  cell="$1"; cd="$2"
  set -- --base-url "$3" --setup-code-from none --json "$cd/journey.json" --container "$4" --cli draughtsman --sign-in-as "$5" --password-env DMX_FIXTURE_PASSWORD
  while IFS= read -r line; do if [ -n "$line" ]; then set -- "$@" --expect "$line"; fi; done <<EXPECT_END
$($CELLS lines "$cell" journey.expect $BRK)
EXPECT_END
  node "$HERE/journey.mjs" "$@" > "$cd/journey.out" 2>&1
  rc=$?
  grep -E '^(journey:|  \[)' "$cd/journey.out" | sed 's/^/    /' | head -40
  return $rc
}

# ---------------------------------------------------------------------------------------------- docker cells
start_app_container() { # start_app_container CELL DIR  -> sets APP VOL APORT; leaves the container running
  cell="$1"; cd="$2"
  APP="${PREFIX}${RID}-${cell}-app"; VOL="${PREFIX}${RID}-${cell}-data"
  APORT="$(free_port)"
  docker volume create "$VOL" >/dev/null || return 1
  register volume "$VOL"
  yaml="$($CELLS field "$cell" yaml $BRK | sed "s#@SUBNET@#${SUBNET:-}#g")"
  if [ -n "$yaml" ]; then
    # The image's own user (uid 10001) writes the file, so ownership is exactly what the image's entry point expects.
    printf '%s\n' "$yaml" | docker run --rm -i -v "$VOL:/data" --entrypoint sh "$IMG" -c 'cat > /data/draughtsman.yaml && chmod 600 /data/draughtsman.yaml' || return 1
  fi
  docker run -d --name "$APP" --network "$NET" -p "127.0.0.1:$APORT:5180" -v "$VOL:/data" "$IMG" >/dev/null || return 1
  register container "$APP"
  printf '%s\n' "$yaml" > "$cd/draughtsman.yaml.used"
}

break_data_root() { # the data.db root-owned break: start once so the database exists, stop, give it to root, start again
  d="$1"
  wait_health "http://127.0.0.1:$APORT" 90 "$APP" || { cell_error "$d" "break data-root: the container never became healthy first"; return 1; }
  docker stop "$APP" >/dev/null
  docker run --rm --user 0 -v "$VOL:/data" --entrypoint sh "$IMG" -c 'chown 0:0 /data/data.db* && chmod 644 /data/data.db*' || return 1
  docker start "$APP" >/dev/null
}

start_proxy() { # start_proxy CELL DIR -> sets BASE_URL and CA_FILE (empty for none)
  cell="$1"; cd="$2"
  ptype="$($CELLS field "$cell" proxy.type $BRK)"
  CA_FILE=""
  case "$ptype" in
    none) BASE_URL="http://127.0.0.1:$APORT" ;;
    nginx)
      pdir="$SCRATCH/$cell-proxy"; mkdir -p "$pdir"; make_cert "$pdir" || return 1
      proto_line=""
      if [ "$($CELLS field "$cell" proxy.forwardProto $BRK)" = "true" ]; then proto_line="proxy_set_header X-Forwarded-Proto \$scheme;"; fi
      cat > "$pdir/nginx.conf" <<NGINX
events {}
http {
  resolver 127.0.0.11 valid=2s ipv6=off;
  gzip on;
  gzip_types application/json;
  gzip_proxied any;
  gzip_min_length 20;
  server {
    listen 443 ssl;
    server_name localhost;
    ssl_certificate /certs/cert.pem;
    ssl_certificate_key /certs/key.pem;
    client_max_body_size 20m;
    location / {
      set \$upstream http://$APP:5180;
      proxy_pass \$upstream;
      proxy_http_version 1.1;
      proxy_set_header Host \$host;
      proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
      $proto_line
      proxy_set_header Accept-Encoding "";
    }
  }
}
NGINX
      PPORT="$(free_port)"; PROXY="${PREFIX}${RID}-${cell}-proxy"
      docker run -d --name "$PROXY" --network "$NET" -p "127.0.0.1:$PPORT:443" -v "$pdir/nginx.conf:/etc/nginx/nginx.conf:ro" -v "$pdir:/certs:ro" "$NGINX_IMG" >/dev/null || return 1
      register container "$PROXY"
      cp "$pdir/nginx.conf" "$cd/proxy-config.txt"
      CA_FILE="$pdir/cert.pem"; BASE_URL="https://localhost:$PPORT" ;;
    caddy)
      pdir="$SCRATCH/$cell-proxy"; mkdir -p "$pdir"
      cat > "$pdir/Caddyfile" <<CADDY
{
  skip_install_trust
  auto_https disable_redirects
}
localhost {
  tls internal
  encode {
    gzip
    minimum_length 20
  }
  reverse_proxy $APP:5180 {
    header_up Accept-Encoding identity
  }
}
CADDY
      PPORT="$(free_port)"; PROXY="${PREFIX}${RID}-${cell}-proxy"
      docker run -d --name "$PROXY" --network "$NET" -p "127.0.0.1:$PPORT:443" -v "$pdir/Caddyfile:/etc/caddy/Caddyfile:ro" "$CADDY_IMG" >/dev/null || return 1
      register container "$PROXY"
      cp "$pdir/Caddyfile" "$cd/proxy-config.txt"
      n=0
      while [ $n -lt 60 ]; do
        if docker cp "$PROXY:/data/caddy/pki/authorities/local/root.crt" "$pdir/root.crt" >/dev/null 2>&1; then break; fi
        n=$((n + 1)); sleep 1
      done
      [ -s "$pdir/root.crt" ] || { cell_error "$cd" "caddy never wrote its local CA root certificate"; return 1; }
      CA_FILE="$pdir/root.crt"; BASE_URL="https://localhost:$PPORT" ;;
    cfproxy)
      pdir="$SCRATCH/$cell-proxy"; mkdir -p "$pdir"; make_cert "$pdir" || return 1
      PPORT="$(free_port)"
      node "$HERE/cfproxy.mjs" --listen "$PPORT" --target "127.0.0.1:$APORT" --cert "$pdir/cert.pem" --key "$pdir/key.pem" --mode "$($CELLS field "$cell" proxy.mode $BRK)" > "$cd/proxy.log" 2>&1 &
      register pid "$!" cfproxy.mjs
      CA_FILE="$pdir/cert.pem"; BASE_URL="https://localhost:$PPORT" ;;
    *) cell_error "$cd" "unknown proxy type '$ptype'"; return 1 ;;
  esac
  if [ "$ptype" != none ]; then printf 'the proxy in front: %s (see proxy-config.txt or proxy.log); reached at %s\n' "$ptype" "$BASE_URL" >> "$cd/notes.txt"; fi
  return 0
}

cell_docker() {
  cell="$1"; cd="$OUT/cells/$cell"; mkdir -p "$cd"
  ensure_network || { cell_error "$cd" "could not create the Docker network"; return 0; }
  start_app_container "$cell" "$cd" || { cell_error "$cd" "could not start the container"; return 0; }
  if [ "$($CELLS field "$cell" preStartBreak $BRK)" = "data-root" ]; then break_data_root "$cd" || { cell_error "$cd" "could not apply break data-root"; return 0; }; fi
  start_proxy "$cell" "$cd" || { [ -s "$cd/error.txt" ] || cell_error "$cd" "could not start the proxy"; return 0; }
  if ! wait_health "$BASE_URL" 120 "$APP"; then
    # The server did not come up. Say how it ended, and still run the journey so the report names the first step that cannot work.
    state="$(docker inspect -f '{{.State.Status}}, exit code {{.State.ExitCode}}' "$APP" 2>/dev/null)"
    add_extra "$cd" "the instance never answered GET /api/health at $BASE_URL (container: $state)" FAIL "$(docker logs --tail 12 "$APP" 2>&1 | redact_log)"
    if [ -n "$CA_FILE" ]; then NODE_EXTRA_CA_CERTS="$CA_FILE"; export NODE_EXTRA_CA_CERTS; fi
    run_journey "$cell" "$cd" "$BASE_URL" "docker-logs:$APP" "" draughtsman
    docker logs "$APP" > "$cd/app.log" 2>&1
    unset NODE_EXTRA_CA_CERTS
    return 0
  fi
  if [ -n "$CA_FILE" ]; then NODE_EXTRA_CA_CERTS="$CA_FILE"; export NODE_EXTRA_CA_CERTS; else unset NODE_EXTRA_CA_CERTS; fi
  run_journey "$cell" "$cd" "$BASE_URL" "docker-logs:$APP" "$APP" draughtsman
  docker logs "$APP" > "$cd/app.log" 2>&1
  unset NODE_EXTRA_CA_CERTS
  return 0
}

# ---------------------------------------------------------------------------------------------- archive cells
today() { date -u +%Y-%m-%d; }

ensure_archive() { # ensure_archive RID -> prints the path of the .tar.gz, building it once per run
  # DMX_ARCHIVE_DIR (optional) is a folder of archives you built earlier, reused and never deleted; by default they are built into the run's scratch folder.
  rid="$1"; dest="${DMX_ARCHIVE_DIR:-$SCRATCH/archives}"; mkdir -p "$dest"
  tgz="$(ls "$dest"/draughtsman-*-"$rid".tar.gz 2>/dev/null | head -1)"
  if [ -z "$tgz" ]; then
    say "  building the $rid archive (scripts/publish.sh; several minutes the first time)..." >&2
    "$ROOT/scripts/publish.sh" "$rid" --build-date "$(today)" --out "$dest" > "$OUT/publish-$rid.log" 2>&1 || { say "  publish.sh failed; see $OUT/publish-$rid.log" >&2; return 1; }
    tgz="$(ls "$dest"/draughtsman-*-"$rid".tar.gz 2>/dev/null | head -1)"
  fi
  [ -n "$tgz" ] && printf '%s\n' "$tgz"
}

cell_archive_host() {
  cell="$1"; cd="$OUT/cells/$cell"; mkdir -p "$cd"
  rid="$($CELLS field "$cell" rid)"
  tgz="$(ensure_archive "$rid")" || { cell_error "$cd" "could not build the $rid archive (see publish-$rid.log in the run folder)"; return 0; }
  cs="$SCRATCH/$cell"; mkdir -p "$cs/app" "$cs/data" "$cs/home" "$cs/tmp"
  tar -xzf "$tgz" -C "$cs/app" --strip-components=1 || { cell_error "$cd" "could not unpack $tgz"; return 0; }
  ver="$(env -i PATH=/usr/bin:/bin "$cs/app/draughtsman" --version 2>&1)"; vrc=$?
  if [ $vrc -eq 0 ]; then add_extra "$cd" "draughtsman --version with a bare PATH (env -i)" PASS "$ver"; else add_extra "$cd" "draughtsman --version with a bare PATH (env -i)" FAIL "exit $vrc: $ver"; fi
  port="$(free_port)"
  # env -i: nothing of this machine's PATH, Node or .NET is visible to the server; KEY_RING_PROTECTION=none keeps a test run out of the login Keychain.
  ( cd "$cs/app" && exec env -i PATH=/usr/bin:/bin HOME="$cs/home" TMPDIR="$cs/tmp" DRAUGHTSMAN_DATA="$cs/data" DRAUGHTSMAN_KEY_RING_PROTECTION=none ./draughtsman serve "--urls=http://127.0.0.1:$port" > "$cd/app.log" 2>&1 ) &
  register pid "$!" draughtsman
  url="http://127.0.0.1:$port"
  if ! wait_health "$url" 120; then cell_error "$cd" "the archive's server did not answer at $url within 120 s. Log tail: $(tail -15 "$cd/app.log" | redact_log | tr '\n' '|')"; return 0; fi
  unset NODE_EXTRA_CA_CERTS
  run_journey "$cell" "$cd" "$url" "file:$cd/app.log"
  return 0
}

cell_archive_container() {
  cell="$1"; cd="$OUT/cells/$cell"; mkdir -p "$cd"
  arch="$(docker version --format '{{.Server.Arch}}')"
  case "$arch" in arm64|aarch64) rid=linux-arm64 ;; amd64|x86_64) rid=linux-x64 ;; *) cell_error "$cd" "no release archive for Docker architecture $arch"; return 0 ;; esac
  tgz="$(ensure_archive "$rid")" || { cell_error "$cd" "could not build the $rid archive (see publish-$rid.log in the run folder)"; return 0; }
  cs="$SCRATCH/$cell"; mkdir -p "$cs/app" "$cs/data"
  tar -xzf "$tgz" -C "$cs/app" --strip-components=1 || { cell_error "$cd" "could not unpack $tgz"; return 0; }
  # The data folder is a bind mount owned by a non-root user (the stock 'ubuntu' user, uid 1000, which a bare ubuntu:24.04 has).
  docker run --rm -v "$cs/data:/data" "$UBUNTU_IMG" sh -c 'chown 1000:1000 /data && chmod 700 /data' >/dev/null 2>&1 || { cell_error "$cd" "could not give the data folder to uid 1000"; return 0; }
  APP="${PREFIX}${RID}-${cell}-app"; APORT="$(free_port)"
  docker run -d --name "$APP" --user ubuntu -p "127.0.0.1:$APORT:5180" -v "$cs/app:/opt/draughtsman:ro" -v "$cs/data:/data" -e DRAUGHTSMAN_DATA=/data -e ASPNETCORE_URLS=http://0.0.0.0:5180 "$UBUNTU_IMG" /opt/draughtsman/draughtsman serve >/dev/null || { cell_error "$cd" "could not start the ubuntu container"; return 0; }
  register container "$APP"
  info="$(docker exec "$APP" sh -c 'head -2 /etc/os-release; id; command -v dotnet node || echo "no dotnet, no node on PATH"' 2>&1 | tr '\n' ';')"
  printf 'the clean machine: %s\n' "$info" >> "$cd/notes.txt"
  url="http://127.0.0.1:$APORT"
  if ! wait_health "$url" 120; then
    docker logs "$APP" > "$cd/app.log" 2>&1
    cell_error "$cd" "the archive did not start in a bare $UBUNTU_IMG container: $(docker logs --tail 12 "$APP" 2>&1 | redact_log | tr '\n' '|')"
    return 0
  fi
  unset NODE_EXTRA_CA_CERTS
  run_journey "$cell" "$cd" "$url" "docker-logs:$APP" "$APP" /opt/draughtsman/draughtsman
  docker logs "$APP" > "$cd/app.log" 2>&1
  return 0
}

# ---------------------------------------------------------------------------------------------- upgrade cells
cell_upgrade() {
  cell="$1"; cd="$OUT/cells/$cell"; mkdir -p "$cd"
  fx="$($CELLS fixture "$($CELLS field "$cell" fixture)")"
  label="$(basename "$fx")"
  printf 'fixture: %s (a data folder written by an earlier build, through its own API)\n' "$label" >> "$cd/notes.txt"
  ensure_network || { cell_error "$cd" "could not create the Docker network"; return 0; }
  APP="${PREFIX}${RID}-${cell}-app"; VOL="${PREFIX}${RID}-${cell}-data"; APORT="$(free_port)"
  docker volume create "$VOL" >/dev/null || return 0
  register volume "$VOL"
  docker run --rm --user 0 -v "$VOL:/data" -v "$fx/sqlite:/fx:ro" --entrypoint sh "$IMG" -c 'tar xzf /fx/data-folder.tar.gz -C /data 2>/dev/null; test -s /data/data.db && chown -R 10001:10001 /data && chmod 700 /data' || { cell_error "$cd" "could not unpack the fixture into the volume"; return 0; }
  set -- docker run -d --name "$APP" --network "$NET" -p "127.0.0.1:$APORT:5180" -v "$VOL:/data"
  while IFS= read -r kv; do if [ -n "$kv" ]; then set -- "$@" -e "$kv"; fi; done <<ENV_END
$($CELLS fixture-env "$fx")
ENV_END
  "$@" "$IMG" >/dev/null || { cell_error "$cd" "could not start the container on the fixture"; return 0; }
  register container "$APP"
  url="http://127.0.0.1:$APORT"
  if ! wait_health "$url" 180; then
    docker logs "$APP" > "$cd/app.log" 2>&1
    cell_error "$cd" "the CURRENT image did not start on the $label data folder: $(docker logs --tail 15 "$APP" 2>&1 | redact_log | tr '\n' '|')"
    return 0
  fi
  # 1. everything the older build wrote must read back (the repo's own verifier, run against the live server).
  email="$($CELLS fixture-admin "$fx" | sed -n 1p)"; DMX_FIXTURE_PASSWORD="$($CELLS fixture-admin "$fx" | sed -n 2p)"; export DMX_FIXTURE_PASSWORD
  vout="$(node "$ROOT/scripts/upgrade-fixture/verify.mjs" --base "$url" --expected "$fx/sqlite/expected.json" --secrets-readable 2>&1)"; vrc=$?
  vname="upgrade-fixture/verify.mjs: every user, token, document, version, setting and secret reads back"
  if [ $vrc -eq 0 ]; then add_extra "$cd" "$vname" PASS "$(printf '%s' "$vout" | tail -3)"; else add_extra "$cd" "$vname" FAIL "$(printf '%s' "$vout" | tail -25)"; fi
  # 2. then the whole journey, as the fixture's administrator.
  unset NODE_EXTRA_CA_CERTS
  run_journey_existing "$cell" "$cd" "$url" "$APP" "$email"
  docker logs "$APP" > "$cd/app.log" 2>&1
  unset DMX_FIXTURE_PASSWORD
  return 0
}

# ---------------------------------------------------------------------------------------------- the --vm journey
run_vm() {
  addr="$(ssh -G "$VM" 2>/dev/null | awk '/^hostname /{print $2; exit}')"
  [ -n "$addr" ] || die "could not read the address of '$VM' from ssh -G"
  url="${VM_SCHEME}://$addr:${VM_PORT}"
  say "windows-archive (manual cell): the journey, from this Mac, against $url"
  if [ -n "$VM_CODE" ]; then exec node "$HERE/journey.mjs" --base-url "$url" --setup-code-from "file:$VM_CODE"; fi
  exec node "$HERE/journey.mjs" --base-url "$url" --setup-code-from none
}

# ---------------------------------------------------------------------------------------------- main
VM=""; VM_PORT=5180; VM_SCHEME=http; VM_CODE=""; DO_LIST=0; DO_CLEAN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --cells) SELECTED="${2:-}"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --break) BREAK="${2:-}"; shift 2 ;;
    --list) DO_LIST=1; shift ;;
    --clean-stale) DO_CLEAN=1; shift ;;
    --vm) VM="${2:-}"; shift 2 ;;
    --vm-port) VM_PORT="${2:-}"; shift 2 ;;
    --vm-scheme) VM_SCHEME="${2:-}"; shift 2 ;;
    --setup-code-file) VM_CODE="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done

if [ "$DO_LIST" = 1 ]; then
  say "automated cells:"; $CELLS names | sed 's/^/  /'
  say "deliberate-breakage proofs (--break NAME):"; $CELLS breaks | sed 's/^/  /'
  say "manual cells:"; $CELLS manual | node -e 'for (const m of JSON.parse(require("fs").readFileSync(0, "utf8"))) console.log("  " + m.name + ": " + m.why)'
  exit 0
fi
command -v docker >/dev/null 2>&1 || die "docker is required"
if [ "$DO_CLEAN" = 1 ]; then clean_stale; exit 0; fi
if [ -n "$VM" ]; then run_vm; fi
command -v node >/dev/null 2>&1 || die "node is required"
command -v curl >/dev/null 2>&1 || die "curl is required"
command -v openssl >/dev/null 2>&1 || die "openssl is required"
[ "$(node -p 'process.versions.node.split(".")[0]')" -ge 22 ] || die "Node 22 or later is required (this is $(node --version))"
docker info >/dev/null 2>&1 || die "Docker is not running"

if [ -n "$BREAK" ]; then
  owner="$($CELLS breaks | awk -v b="$BREAK" '$1==b {print $2}')"
  [ -n "$owner" ] || die "no break named '$BREAK' (see --list)"
  BRK="--break $BREAK"
  [ -n "$SELECTED" ] || SELECTED="$owner"
fi
[ -n "$SELECTED" ] || SELECTED="$($CELLS names | tr '\n' ',' | sed 's/,$//')"

mkdir -p "$OUT/cells"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/dmx-run.XXXXXX")"
REG="$SCRATCH/registry"; : > "$REG"
register dir "$SCRATCH"
if [ -n "$BREAK" ]; then printf '%s\n' "$BREAK" > "$OUT/break.txt"; fi

NEED_IMAGE=0
for c in $(printf '%s' "$SELECTED" | tr ',' ' '); do
  kind="$($CELLS field "$c" kind)"
  case "$kind" in docker|upgrade) NEED_IMAGE=1 ;; esac
done

if [ "$NEED_IMAGE" = 1 ]; then
  if [ "$SKIP_BUILD" = 1 ]; then
    docker image inspect "$IMG" >/dev/null 2>&1 || die "--skip-build, but $IMG does not exist"
    step "reusing $IMG"
  else
    step "docker build -t $IMG . (once; the log is $OUT/build.log)"
    docker build -t "$IMG" "$ROOT" > "$OUT/build.log" 2>&1 || { tail -20 "$OUT/build.log"; die "the image did not build"; }
    register image "$IMG"
  fi
fi
IMAGE_ID="$(docker image inspect "$IMG" --format '{{.Id}}' 2>/dev/null | cut -c1-19)"
node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({startedAt: new Date().toISOString(), image: process.argv[2] + " (" + process.argv[3] + ")", host: process.platform + " " + process.arch + ", Docker " + process.argv[4]}))' "$OUT/run.json" "$IMG" "$IMAGE_ID" "$(docker version --format '{{.Server.Version}} {{.Server.Arch}}')"
say "run $RID: report folder $OUT"

for c in $(printf '%s' "$SELECTED" | tr ',' ' '); do
  kind="$($CELLS field "$c" kind $BRK)"
  [ -n "$kind" ] || die "no automated cell named '$c' (see --list)"
  step "cell $c ($kind)"
  mark="$(wc -l < "$REG" | tr -d ' ')"
  case "$kind" in
    docker) cell_docker "$c" ;;
    archive-host) cell_archive_host "$c" ;;
    archive-container) cell_archive_container "$c" ;;
    upgrade) cell_upgrade "$c" ;;
    *) die "cell $c has an unknown kind '$kind'" ;;
  esac
  # One cell at a time: tear its things down now (unless --keep). The network is made again by the next cell that needs one.
  if [ "$KEEP" != 1 ]; then cleanup_from "$mark"; fi
done

node "$HERE/render.mjs" "$OUT"
RC=$?
exit $RC
