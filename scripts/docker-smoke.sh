#!/usr/bin/env bash
# Builds the release image and proves it runs: server up, health answers, a document is created through the API, the
# sidecar (ELK layout in the image's Node) works, the data folder survives a container restart, the process is
# not root, a fresh named volume starts owner-only (mode 700, uid 10001) with no permissions warning, and
# `draughtsman` is on PATH. Needs Docker and python3. Not part of verify-local.sh (an image build takes minutes).
#
#   scripts/docker-smoke.sh            build draughtsman:smoke, then run the checks
#   SKIP_BUILD=1 scripts/docker-smoke.sh   reuse the existing image
#   IMAGE=name:tag scripts/docker-smoke.sh use another tag
#
# Only its own container and scratch folder are created and removed (by id / path); nothing else is touched.
set -euo pipefail

cd "$(dirname "$0")/.."
IMAGE="${IMAGE:-draughtsman:smoke}"
DATA="$(mktemp -d)"
CID=""
VCID=""
VOL=""
JAR="$DATA.cookies"

cleanup() {
  if [ -n "$CID" ]; then docker rm -f "$CID" >/dev/null 2>&1 || true; fi
  if [ -n "$VCID" ]; then docker rm -f "$VCID" >/dev/null 2>&1 || true; fi
  if [ -n "$VOL" ]; then docker volume rm "$VOL" >/dev/null 2>&1 || true; fi
  rm -rf "$DATA" "$JAR"
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; [ -n "$CID" ] && docker logs "$CID" 2>&1 | tail -30 >&2; exit 1; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  step "docker build $IMAGE"
  docker build -t "$IMAGE" .
fi

# The container runs as uid 10001; a bind mount keeps the host's ownership, so open the scratch folder up.
chmod 777 "$DATA"

start() {
  CID="$(docker run -d -p 127.0.0.1::5180 -v "$DATA:/data" "$IMAGE")"
  PORT="$(docker port "$CID" 5180/tcp | head -1 | sed 's/.*://')"
  BASE="http://127.0.0.1:$PORT"
  for _ in $(seq 1 60); do
    if curl -fsS "$BASE/api/health" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  fail "server did not answer $BASE/api/health within 60s"
}

step "start container (random port, scratch volume)"
start
echo "container ${CID:0:12} on $BASE"
curl -fsS "$BASE/api/health"; echo

step "carries its software bill of materials"
docker exec "$CID" test -s /app/SBOM.cdx.json || fail "the image has no /app/SBOM.cdx.json"
docker exec "$CID" node -e 'const b=JSON.parse(require("fs").readFileSync("/app/SBOM.cdx.json","utf8")); if (b.bomFormat!=="CycloneDX"||b.components.length<10) process.exit(1); console.log("SBOM: CycloneDX "+b.specVersion+", "+b.components.length+" components")' || fail "/app/SBOM.cdx.json is not a usable CycloneDX file"

step "runs as non-root"
UID_IN="$(docker exec "$CID" id -u)"
[ "$UID_IN" != "0" ] || fail "container process runs as root"
echo "uid $UID_IN"

step "a fresh named volume starts owner-only, and draughtsman is on PATH"
VOL="draughtsman-smoke-$$-$RANDOM"
VCID="$(docker run -d -v "$VOL:/data" "$IMAGE")"
for _ in $(seq 1 60); do
  if docker logs "$VCID" 2>&1 | grep -q 'First-run setup code'; then break; fi
  sleep 1
done
MODE_OWNER="$(docker exec "$VCID" stat -c '%a %u' /data)"
[ "$MODE_OWNER" = "700 10001" ] || fail "named volume /data is '$MODE_OWNER', expected '700 10001'"
if docker logs "$VCID" 2>&1 | grep -q 'open to other users'; then fail "the data folder permissions warning fired on a fresh named volume"; fi
docker exec "$VCID" draughtsman --version >/dev/null || fail "draughtsman is not on PATH in the image"
docker exec "$VCID" dotnet /app/draughtsman.dll --version >/dev/null || fail "the long dotnet /app/draughtsman.dll form no longer works"
echo "volume /data is $MODE_OWNER; draughtsman and dotnet /app/draughtsman.dll both run"
docker rm -f "$VCID" >/dev/null; VCID=""
docker volume rm "$VOL" >/dev/null; VOL=""

step "first-run sign-in, then an API token"
CODE="$(docker logs "$CID" 2>&1 | sed -n 's/.*First-run setup code: *//p' | head -1 | tr -d '\r ')"
[ -n "$CODE" ] || fail "no first-run setup code in the container log"
curl -fsS -c "$JAR" -X POST "$BASE/api/auth/first-run" -H 'Content-Type: application/json' \
  -d "{\"email\":\"smoke@example.com\",\"displayName\":\"Smoke\",\"password\":\"a-long-enough-password\",\"setupCode\":\"$CODE\"}" >/dev/null
XSRF="$(awk '$6=="XSRF-TOKEN"{print $7}' "$JAR")"
TOKEN="$(curl -fsS -b "$JAR" -X POST "$BASE/api/tokens" -H 'Content-Type: application/json' -H "X-XSRF-TOKEN: $XSRF" \
  -d '{"name":"smoke"}' | json 'd["token"]')"

step "create a document through the API"
DOC="$(curl -sS -X POST "$BASE/api/documents" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -d '{"title":"Smoke","type":"architecture","nodes":[{"id":"n_a","kind":"service","shape":"rounded","label":"A"},{"id":"n_b","kind":"service","shape":"rounded","label":"B"}],"edges":[{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_b"}}]}')"
ID="$(printf '%s' "$DOC" | json 'd["id"]' 2>/dev/null)" || fail "create document refused: $DOC"
echo "created $ID"

step "sidecar layout works inside the image"
curl -fsS -X POST "$BASE/api/layout" -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" -d "$DOC" \
  | json 'sum(1 for n in d["document"]["nodes"] if n.get("geometry")) == 2 or sys.exit("layout gave no geometry")' >/dev/null

step "data survives a container restart"
docker rm -f "$CID" >/dev/null
CID=""
start
TITLE="$(curl -fsS -H "Authorization: Bearer $TOKEN" "$BASE/api/documents/$ID" | json 'd["title"]')"
[ "$TITLE" = "Smoke" ] || fail "document $ID missing after restart"
echo "document $ID and the API token both persisted"

printf '\nPASS: %s builds, runs as uid %s, serves the API and keeps /data across a restart\n' "$IMAGE" "$UID_IN"
