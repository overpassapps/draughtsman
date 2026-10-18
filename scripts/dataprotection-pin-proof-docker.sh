#!/usr/bin/env bash
# The Docker half of scripts/dataprotection-pin-proof.sh: the stored AI API key and the saved SMTP password across two images and one /data volume,
# where the program's content root is /app in BOTH (the Docker image's program is always /app, the case an earlier build handled only by never moving).
#
#   scripts/dataprotection-pin-proof-docker.sh --old draughtsman:pin-old --new draughtsman:pin-new [--key-ring key|none]
#
# --old is an image built from a build that PREDATES the pin (its secrets are bound to /app/), --new the build under test.
#   1. the OLD image on a new volume: fill it through its API (populate.mjs) and check it (verify.mjs, secrets readable from its own /app).
#   2. `config check` from the NEW image on the same volume (read-only), then the NEW image started on it: every check passes, both secrets read and
#      were re-protected under the pinned name; the earlier settings files are kept whole in backups/secrets-* on the volume.
#   3. the NEW image again, a fresh container: both secrets are `current`, nothing was written, no second backup.
#   4. going back: the OLD image on the re-protected volume shows both secrets as needing to be entered again (it reads them only from /app under the
#      old binding); putting the backups/secrets-* copies back, as documented, brings them back.
#
# Free random host ports, a scratch volume and containers named for this run, all removed at the end by exact name. Output is the evidence quoted
# in docs/upgrade-backup.md.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
here="$repo_root/scripts/upgrade-fixture"
case "${1:-}" in -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;; esac
old=""; new=""; key_ring="key"
while [ $# -gt 0 ]; do
  case "$1" in
    --old) old="${2:?}"; shift 2 ;;
    --new) new="${2:?}"; shift 2 ;;
    --key-ring) key_ring="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "dataprotection-pin-proof-docker: unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$old" ] && [ -n "$new" ] || { echo "dataprotection-pin-proof-docker: --old and --new are required" >&2; exit 2; }
command -v docker >/dev/null || { echo "dataprotection-pin-proof-docker: docker is required" >&2; exit 1; }

work="$(mktemp -d "${TMPDIR:-/tmp}/dataprotection-pin-proof-docker.XXXXXX")"
case "$(cd "$work" && pwd -P)" in "$HOME"/*) echo "dataprotection-pin-proof-docker: scratch under \$HOME" >&2; exit 1 ;; esac
volume="ds-pin-proof-$$-data"; container="ds-pin-proof-$$"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm -f "$volume" >/dev/null 2>&1 || true
  rm -rf "${work:?}"
}
trap cleanup EXIT
docker volume create "$volume" >/dev/null

passed=0; failed=0
ok()   { passed=$((passed + 1)); echo "  PASS $*"; }
bad()  { failed=$((failed + 1)); echo "  FAIL $*"; }
step() { printf '\n== %s\n' "$*"; }
free_port() { node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }
wait_http() { local i; for i in $(seq 1 240); do curl -s "http://127.0.0.1:$1/api/health" >/dev/null 2>&1 && return 0; sleep 0.5; done; return 1; }
kek="$(node -e 'console.log(require("crypto").randomBytes(32).toString("base64"))')"

run() { # run <image>: start the image on the volume, published on a free host port; sets port
  port="$(free_port)"
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker run -d --name "$container" -v "$volume:/data" -p "127.0.0.1:$port:5180" -e "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" -e "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$1" >/dev/null
}
in_volume() { docker run --rm -v "$volume:/data" --entrypoint sh "$new" -c "$1"; }
vsha() { in_volume "sha256sum $1" | cut -d' ' -f1; }

step "1. the OLD image on a new volume: fill it through its API"
run "$old"
wait_http "$port" || { docker logs "$container" 2>&1 | tail -30; echo "the old image did not start" >&2; exit 1; }
code="$(docker logs "$container" 2>&1 | grep -o 'setup code: [A-Z0-9]*' | head -1 | awk '{print $3}' || true)"
node "$here/populate.mjs" --base "http://127.0.0.1:$port" ${code:+--setup-code "$code"} --examples "$repo_root/docs/examples" --out "$work/expected.json" --label proof
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --self-check; then ok "the old image serves what it was filled with, both secrets readable from /app"; else bad "the old image does not match its own expectations"; fi
docker stop "$container" >/dev/null
ai_before="$(vsha /data/ai/settings.json)"; email_before="$(vsha /data/email/settings.json)"
echo "  ai/settings.json sha256 $ai_before; email/settings.json sha256 $email_before"

step "2. the NEW image on the SAME volume: config check, then start it"
echo "  \$ docker run --rm -v <volume>:/data <new image> config check   (Stored secrets section)"
set +e
docker run --rm -v "$volume:/data" -e "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" -e "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$new" config check >"$work/config-check.txt" 2>&1
check_exit=$?
set -e
sed -n '/^Stored secrets/,/^$/p' "$work/config-check.txt" | sed 's/^/    /'
[ "$check_exit" -eq 0 ] && ok "config check says the new image would start (exit 0)" || bad "config check exited $check_exit"
grep -q "AI API key.*can still be read" "$work/config-check.txt" && grep -q "SMTP password.*can still be read" "$work/config-check.txt" && ok "config check says both secrets can be read and will be re-protected" || bad "config check does not report both secrets readable"
run "$new"
wait_http "$port" || { docker logs "$container" 2>&1 | tail -40; bad "the new image did not start on the old volume"; exit 1; }
docker logs "$container" 2>&1 | grep -E "Stored secrets:" | cut -c1-260 | sed 's/^ */    /'
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --secrets-readable; then ok "the new image serves every document, user, token, version, setting and both secrets the old one did"; else bad "the new image does not match what the old one served"; fi
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=reprotected,SMTP password=reprotected" && ok "both secrets were re-protected at this start" || bad "the secrets were not reported re-protected"
docker stop "$container" >/dev/null
[ "$(vsha /data/ai/settings.json)" != "$ai_before" ] && ok "ai/settings.json now holds the re-protected value" || bad "ai/settings.json was not rewritten"
backup="$(in_volume 'ls -d /data/backups/secrets-* | head -1')"
[ "$(in_volume "sha256sum $backup/ai/settings.json" | cut -d' ' -f1)" = "$ai_before" ] && [ "$(in_volume "sha256sum $backup/email/settings.json" | cut -d' ' -f1)" = "$email_before" ] && ok "the earlier settings files are kept byte for byte on the volume in $(basename "$backup")" || bad "the earlier files are not in the backup"
echo "  keys/install-paths.json: $(in_volume 'cat /data/keys/install-paths.json' | tr -d '\n ')"

step "3. the NEW image again, a fresh container: nothing left to move"
ai_mid="$(vsha /data/ai/settings.json)"
run "$new"
wait_http "$port" || { docker logs "$container" 2>&1 | tail -40; bad "the new image did not start again"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=current,SMTP password=current" && ok "both secrets are current: no fallback was needed" || bad "the secrets were not current"
docker stop "$container" >/dev/null
[ "$(vsha /data/ai/settings.json)" = "$ai_mid" ] && [ "$(in_volume 'ls -d /data/backups/secrets-* | wc -l | tr -d " "')" = "1" ] && ok "the second start wrote nothing and took no second backup" || bad "the second start changed the settings or took another backup"

step "4. going back: the OLD image on the re-protected volume"
run "$old"
wait_http "$port" || { docker logs "$container" 2>&1 | tail -40; bad "the old image did not start on the re-protected volume"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable >/dev/null 2>&1 && bad "the old image read secrets it should not be able to" || ok "the old image shows both secrets as needing to be entered again"
docker stop "$container" >/dev/null
in_volume "cp $backup/ai/settings.json /data/ai/settings.json && cp $backup/email/settings.json /data/email/settings.json"
echo "  \$ cp <volume>/backups/$(basename "$backup")/{ai,email}/settings.json -> <volume>/{ai,email}/   (the documented way back)"
run "$old"
wait_http "$port" || { docker logs "$container" 2>&1 | tail -40; bad "the old image did not start after the settings were put back"; exit 1; }
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "with the earlier settings files put back the old image serves everything and both secrets read again"; else bad "the old image does not serve what it did"; fi
docker stop "$container" >/dev/null

echo
echo "dataprotection-pin-proof-docker: $passed passed, $failed failed"
[ "$failed" -eq 0 ]
