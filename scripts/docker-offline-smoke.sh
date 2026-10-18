#!/usr/bin/env bash
# Proves the release image does its whole job with no network at all (docs/air-gapped.md, "Prove it on your own machine"): the container
# is started with `--network none`, so it has a loopback interface and nothing else, and a default instance is driven through first run,
# sign-in, a token, create, save, layout, the text exports, the server's own SVG render over MCP, and the optional components (which must
# answer "not here"). Anything that tried to reach another machine would fail in this container and show in its log; the script fails
# on that too, as well as on any step that does not work. Needs Docker. Not part of verify-local.sh (an image build takes minutes).
#
#   scripts/docker-offline-smoke.sh                    build draughtsman:airgap-proof, then run the checks
#   SKIP_BUILD=1 IMAGE=name:tag scripts/docker-offline-smoke.sh     reuse an image you already built
#
# Only its own container and scratch folder are created and removed (by id / path); nothing else is touched.
set -euo pipefail

cd "$(dirname "$0")/.."
IMAGE="${IMAGE:-draughtsman:airgap-proof}"
DATA="$(mktemp -d)"
CID=""

cleanup() {
  if [ -n "$CID" ]; then docker rm -f "$CID" >/dev/null 2>&1 || true; fi
  rm -rf "$DATA"
}
trap cleanup EXIT

step() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; [ -n "$CID" ] && docker logs "$CID" 2>&1 | tail -30 >&2; exit 1; }

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  step "docker build $IMAGE"
  docker build -t "$IMAGE" .
fi

# The container runs as uid 10001; a bind mount keeps the host's ownership, so open the scratch folder up.
chmod 777 "$DATA"

step "start the container with no network"
CID="$(docker run -d --network none -v "$DATA:/data" "$IMAGE")"
mode="$(docker inspect -f '{{.HostConfig.NetworkMode}}' "$CID")"
[ "$mode" = "none" ] || fail "the container's network mode is '$mode', not none"
# `--network none` leaves loopback, and the kernel may add address-less tunnel stubs (tunl0, ip6tnl0). What matters: no route anywhere (so no default
# route), and no address on any interface but loopback.
routes="$(docker exec "$CID" sh -c 'tail -n +2 /proc/net/route | wc -l' | tr -d ' ')"
[ "$routes" = "0" ] || fail "the container has $routes IPv4 route(s); with no network it should have none"
addressed="$(docker exec "$CID" sh -c "awk '{print \$6}' /proc/net/if_inet6 | sort -u | tr '\n' ' '")"
case "$addressed" in ""|"lo ") ;; *) fail "interfaces with an IPv6 address besides loopback: $addressed" ;; esac
echo "container ${CID:0:12}: network mode none, no routes, IPv6 addresses only on: ${addressed:-none }(interfaces: $(docker exec "$CID" sh -c "tail -n +3 /proc/net/dev | cut -d: -f1 | tr -d ' ' | sort | tr '\n' ' '"))"

healthy=0
for _ in $(seq 1 90); do
  if docker exec "$CID" node -e 'fetch("http://127.0.0.1:5180/api/health").then((r) => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))' >/dev/null 2>&1; then healthy=1; break; fi
  sleep 1
done
[ "$healthy" = 1 ] || fail "the server did not answer /api/health within 90 s with no network"
echo "the server is up with no network"

step "a default instance does its work with no network"
code="$(docker logs "$CID" 2>&1 | sed -n 's/.*First-run setup code: *//p' | head -1 | tr -d '\r ')"
[ -n "$code" ] || fail "no first-run setup code in the container log"
docker exec -i -e SETUP_CODE="$code" "$CID" node - <scripts/lib/offline-smoke-client.mjs || fail "the in-container client did not complete"

step "backup and the upgrade check work with no network"
docker exec "$CID" draughtsman backup --out /data/backups/ >"$DATA.backup.log" 2>&1 || { cat "$DATA.backup.log" >&2; rm -f "$DATA.backup.log"; fail "draughtsman backup failed with no network"; }
rm -f "$DATA.backup.log"
docker exec "$CID" sh -c 'ls /data/backups/*.zip >/dev/null' || fail "draughtsman backup wrote no zip"
docker exec "$CID" draughtsman config check >/dev/null || fail "draughtsman config check refused this data folder"
echo "a backup zip was written and config check passed"

step "the image carries its software bill of materials"
docker exec "$CID" node -e 'const b=JSON.parse(require("fs").readFileSync("/app/SBOM.cdx.json","utf8")); if (b.bomFormat!=="CycloneDX"||b.components.length<10) process.exit(1); console.log("SBOM: CycloneDX "+b.specVersion+", "+b.components.length+" components, draughtsman "+b.metadata.component.version)' || fail "/app/SBOM.cdx.json is missing or not a usable CycloneDX file"

step "nothing tried to leave"
if docker logs "$CID" 2>&1 | grep -E -i 'name or service not known|temporary failure in name resolution|network is unreachable|no route to host|nodename nor servname|could not resolve|SocketException|HttpRequestException|ENETUNREACH|EAI_AGAIN|ENOTFOUND' >"$DATA.log"; then
  cat "$DATA.log" >&2; rm -f "$DATA.log"
  fail "the log shows a network failure: something in a default instance reached for the network"
fi
rm -f "$DATA.log"
echo "no resolver, connect or HTTP failure in the container's log"
listeners="$(docker exec "$CID" sh -c "awk 'NR>1 && \$4==\"0A\" {print \$2}' /proc/net/tcp /proc/net/tcp6 | sort -u | tr '\n' ' '")"
echo "listening sockets (hex address:port): $listeners"

printf '\nPASS: %s ran a default instance start to finish in a container with no network (docs/air-gapped.md)\n' "$IMAGE"
