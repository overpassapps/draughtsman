#!/usr/bin/env bash
# Proves, on real binaries, that a patch update keeps a customer's data (docs/upgrade-backup.md, "Patch updates", "Going back"):
#
#   scripts/upgrade-path-proof.sh archive --old draughtsman-0.1.0-osx-arm64.tar.gz --new draughtsman-0.1.1-osx-arm64.tar.gz
#   scripts/upgrade-path-proof.sh docker  --old draughtsman:0.1.0 --new draughtsman:0.1.1
#
# archive: unpack the OLD release archive, start it on a scratch data folder, fill it through its API (populate.mjs) and check it
#   (verify.mjs); stop it. Then do what an operator does for a patch update: replace the program folder IN PLACE (same path: Data
#   Protection ties the stored AI key to the install path) and keep the data folder; run `config check`; start the NEW archive and check
#   everything again. Then the way back: put the OLD program back, show it REFUSES the migrated database (exit code 3) without writing
#   to it, restore the pre-migration copy as documented (delete data.db-wal and data.db-shm, copy it over data.db), and start the OLD program on it
#   again and check it serves exactly what it did before.
# docker: the same across two images and one volume (/data); the way back is the older image refusing the volume.
#
# Everything runs on free random ports with scratch data under a temporary folder outside $HOME, stops what it starts by exact pid or
# container name, and removes its scratch folder. Output is the evidence quoted in docs/upgrade-backup.md.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
here="$repo_root/scripts/upgrade-fixture"
mode="${1:-}"; shift || true
case "$mode" in -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;; esac
old=""; new=""; work=""; key_ring="key"
while [ $# -gt 0 ]; do
  case "$1" in
    --old) old="${2:?}"; shift 2 ;;
    --new) new="${2:?}"; shift 2 ;;
    --work) work="${2:?}"; shift 2 ;;
    --key-ring) key_ring="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "upgrade-path-proof: unknown option $1" >&2; exit 2 ;;
  esac
done
case "$mode" in archive|docker) ;; *) echo "upgrade-path-proof: say 'archive' or 'docker' (see --help)" >&2; exit 2 ;; esac
[ -n "$old" ] && [ -n "$new" ] || { echo "upgrade-path-proof: --old and --new are required" >&2; exit 2; }

if [ -z "$work" ]; then work="$(mktemp -d "${TMPDIR:-/tmp}/upgrade-path-proof.XXXXXX")"; else mkdir -p "$work"; fi
work="$(cd "$work" && pwd -P)"
case "$work" in "$HOME"/*) echo "upgrade-path-proof: the work folder $work is under \$HOME; use --work outside it" >&2; exit 1 ;; esac

pid=""; container=""; volume=""
cleanup() {
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null || true; fi
  [ -z "$container" ] || docker rm -f "$container" >/dev/null 2>&1 || true
  [ -z "$volume" ] || docker volume rm -f "$volume" >/dev/null 2>&1 || true
  rm -rf "${work:?}"
}
trap cleanup EXIT

passed=0; failed=0
ok()   { passed=$((passed + 1)); echo "  PASS $*"; }
bad()  { failed=$((failed + 1)); echo "  FAIL $*"; }
step() { printf '\n== %s\n' "$*"; }
free_port() { node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
wait_http() { local i; for i in $(seq 1 240); do curl -s "http://127.0.0.1:$1/api/health" >/dev/null 2>&1 && return 0; sleep 0.5; done; return 1; }
wait_gone() { local i; for i in $(seq 1 120); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; return 1; }

kek="$(node -e 'console.log(require("crypto").randomBytes(32).toString("base64"))')"

# ------------------------------------------------------------------------------------------------ archive
if [ "$mode" = "archive" ]; then
  app="$work/app"
  data="$work/data"
  unpack() { rm -rf "$app"; mkdir -p "$app"; tar -xzf "$1" -C "$app" --strip-components=1; [ -x "$app/draughtsman" ] || { echo "upgrade-path-proof: no draughtsman executable in $1" >&2; exit 1; }; }
  start() { # start <log>: the program in $app on $data; sets pid and port
    port="$(free_port)"
    (cd "$app" && exec env -i "DRAUGHTSMAN_DATA=$data" "ASPNETCORE_URLS=http://127.0.0.1:$port" "PATH=$PATH" "HOME=$work" "TMPDIR=$work" "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" ./draughtsman >"$1" 2>&1) &
    pid=$!
  }
  stop() { kill "$pid" 2>/dev/null || true; wait_gone "$pid" || { echo "upgrade-path-proof: pid $pid did not stop" >&2; exit 1; }; wait "$pid" 2>/dev/null || true; pid=""; }

  step "1. the OLD archive on a new data folder: fill it through its API"
  unpack "$old"
  version_old="$("$app/draughtsman" --version)"; echo "  old: $version_old"
  start "$work/old-1.log"
  wait_http "$port" || { tail -30 "$work/old-1.log"; echo "the old program did not start" >&2; exit 1; }
  code="$(grep -o 'setup code: [A-Z0-9]*' "$work/old-1.log" | head -1 | awk '{print $3}' || true)"
  node "$here/populate.mjs" --base "http://127.0.0.1:$port" ${code:+--setup-code "$code"} --examples "$repo_root/docs/examples" --out "$work/expected.json" --label proof
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --self-check; then ok "the old program serves what it was filled with"; else bad "the old program does not match its own expectations"; fi
  stop
  hash_before="$(sha "$data/data.db")"
  echo "  data.db sha256 before the upgrade: $hash_before"

  step "2. patch update: replace the program IN PLACE, keep the data folder"
  unpack "$new"
  version_new="$("$app/draughtsman" --version)"; echo "  new: $version_new"
  echo "  \$ draughtsman config check"
  set +e
  env -i "PATH=$PATH" "HOME=$work" "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$app/draughtsman" config check "--DRAUGHTSMAN_DATA=$data" >"$work/config-check.txt" 2>&1
  check_exit=$?
  set -e
  sed 's/^/    /' "$work/config-check.txt"
  if [ "$check_exit" -eq 0 ]; then ok "config check says the new version would start (exit 0)"; else bad "config check exited $check_exit"; fi
  [ "$(sha "$data/data.db")" = "$hash_before" ] && ok "config check changed nothing (data.db is byte for byte the same)" || bad "config check changed data.db"

  step "3. start the NEW program on the old data"
  start "$work/new-1.log"
  wait_http "$port" || { tail -40 "$work/new-1.log"; bad "the new program did not start on the old data"; exit 1; }
  grep -E "Copied SQLite database|Applying [0-9]+ storage migration|Post-migration check passed|storage upgrade" "$work/new-1.log" | sed 's/^ */    /' || true
  copy="$(ls "$data"/data.db.pre-migration-* 2>/dev/null | tail -1 || true)"
  if [ -n "$copy" ]; then ok "the safety copy exists: $(basename "$copy")"; else bad "no pre-migration copy was taken"; fi
  [ -z "$copy" ] || { [ "$(sha "$copy")" = "$hash_before" ] && ok "the safety copy is byte for byte the old database" || bad "the safety copy differs from the old database"; }
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "the new program serves every document, user, token, version, setting and key the old one did (AI key readable: same install path)"; else bad "the new program does not match what the old one served"; fi
  stop
  hash_new="$(sha "$data/data.db")"

  step "4. going back: the OLD program on the migrated database"
  unpack "$old"
  start "$work/old-2.log"
  for i in $(seq 1 60); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
  if kill -0 "$pid" 2>/dev/null; then bad "the old program is still running on a database a newer version migrated"; stop; else
    set +e; wait "$pid"; refusal_exit=$?; set -e; pid=""
    grep -E "migrated by a newer version" "$work/old-2.log" | head -1 | cut -c1-260 | sed 's/^ */    /'
    [ "$refusal_exit" -eq 3 ] && ok "the old program refused to start (exit code 3)" || bad "the old program exited $refusal_exit, not 3"
  fi
  [ "$(sha "$data/data.db")" = "$hash_new" ] && ok "the refusal wrote nothing: data.db is byte for byte what the new version left" || bad "the old program changed the migrated database"
  grep -q "pre-migration-" "$work/old-2.log" && ok "the refusal names the pre-upgrade copy to restore" || bad "the refusal does not name the pre-upgrade copy"

  step "5. the documented rollback: put the safety copy back, run the old program"
  rm -f "$data/data.db-wal" "$data/data.db-shm"
  cp "$copy" "$data/data.db"
  [ "$(sha "$data/data.db")" = "$hash_before" ] && ok "data.db is byte for byte the database from step 1" || bad "the restored database differs from step 1"
  # A new build that pins the Data Protection application name re-protects the stored AI key and SMTP password at its first start and keeps the earlier
  # files in backups/secrets-<time>/ (docs/upgrade-backup.md, "Going back"): an older program reads them only from its own folder, so they go back too.
  secrets_backup="$(ls -d "$data"/backups/secrets-* 2>/dev/null | head -1 || true)"
  if [ -n "$secrets_backup" ]; then
    for part in ai email; do [ -f "$secrets_backup/$part/settings.json" ] && cp "$secrets_backup/$part/settings.json" "$data/$part/settings.json"; done
    echo "  the settings files the new version re-protected are back from $(basename "$secrets_backup")"
  fi
  start "$work/old-3.log"
  wait_http "$port" || { tail -30 "$work/old-3.log"; bad "the old program did not start on the restored database"; exit 1; }
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "the old program serves exactly what it did before the upgrade"; else bad "the old program does not serve what it did before"; fi
  stop
fi

# ------------------------------------------------------------------------------------------------ docker
if [ "$mode" = "docker" ]; then
  command -v docker >/dev/null || { echo "upgrade-path-proof: docker is required" >&2; exit 1; }
  volume="ds-proof-$$-data"; container="ds-proof-$$"
  docker volume create "$volume" >/dev/null
  run() { # run <image> <log>: start the image on the volume, published on a free host port; sets port
    port="$(free_port)"
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker run -d --name "$container" -v "$volume:/data" -p "127.0.0.1:$port:5180" -e "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" -e "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$1" >/dev/null
  }
  vol_sha() { docker run --rm -v "$volume:/data:ro" --entrypoint sh "$old" -c 'sha256sum /data/data.db' 2>/dev/null | cut -d' ' -f1 || true; }

  step "1. the OLD image on a new volume: fill it through its API"
  run "$old"
  wait_http "$port" || { docker logs "$container" 2>&1 | tail -30; echo "the old image did not start" >&2; exit 1; }
  code="$(docker logs "$container" 2>&1 | grep -o 'setup code: [A-Z0-9]*' | head -1 | awk '{print $3}' || true)"
  node "$here/populate.mjs" --base "http://127.0.0.1:$port" ${code:+--setup-code "$code"} --examples "$repo_root/docs/examples" --out "$work/expected.json" --label proof
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --self-check; then ok "the old image serves what it was filled with"; else bad "the old image does not match its own expectations"; fi
  docker stop "$container" >/dev/null
  hash_before="$(vol_sha)"
  echo "  data.db sha256 before the upgrade: $hash_before"

  step "2. patch update: the NEW image on the SAME volume"
  echo "  \$ docker run --rm -v <volume>:/data <new image> config check"
  set +e
  docker run --rm -v "$volume:/data" -e "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" -e "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$new" config check >"$work/config-check.txt" 2>&1
  check_exit=$?
  set -e
  sed 's/^/    /' "$work/config-check.txt"
  if [ "$check_exit" -eq 0 ]; then ok "config check says the new image would start (exit 0)"; else bad "config check exited $check_exit"; fi
  run "$new"
  wait_http "$port" || { docker logs "$container" 2>&1 | tail -40; bad "the new image did not start on the old volume"; exit 1; }
  docker logs "$container" 2>&1 | grep -E "Copied SQLite database|Applying [0-9]+ storage migration|Post-migration check passed" | sed 's/^ */    /' || true
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "the new image serves every document, user, token, version, setting and key the old one did"; else bad "the new image does not match what the old one served"; fi
  docker stop "$container" >/dev/null
  copies="$(docker run --rm -v "$volume:/data:ro" --entrypoint sh "$new" -c 'ls /data/data.db.pre-migration-* 2>/dev/null | wc -l' | tr -d ' ')"
  [ "${copies:-0}" -ge 1 ] && ok "the volume holds the pre-migration copy ($copies)" || bad "no pre-migration copy on the volume"
  hash_new="$(vol_sha)"

  step "3. going back: the OLD image on the migrated volume"
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker run -d --name "$container" -v "$volume:/data" -e "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" -e "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$old" >/dev/null
  refusal_exit="$(docker wait "$container")"
  docker logs "$container" 2>&1 | grep -E "migrated by a newer version" | head -1 | cut -c1-260 | sed 's/^ */    /'
  [ "$refusal_exit" = "3" ] && ok "the old image refused the volume (exit code 3)" || bad "the old image exited $refusal_exit, not 3"
  [ "$(vol_sha)" = "$hash_new" ] && ok "the refusal wrote nothing: data.db is byte for byte what the new image left" || bad "the old image changed the migrated database"

  step "4. the documented rollback on the volume: put the safety copy back, run the OLD image"
  docker rm -f "$container" >/dev/null 2>&1 || true
  # ...and, when the new image pinned the Data Protection application name and re-protected the stored secrets, the earlier settings files from backups/secrets-*.
  docker run --rm -v "$volume:/data" --entrypoint sh "$new" -c 'rm -f /data/data.db-wal /data/data.db-shm && cp "$(ls /data/data.db.pre-migration-* | tail -1)" /data/data.db; b="$(ls -d /data/backups/secrets-* 2>/dev/null | head -1)"; if [ -n "$b" ]; then for p in ai email; do [ -f "$b/$p/settings.json" ] && cp "$b/$p/settings.json" "/data/$p/settings.json"; done; fi; true'
  [ "$(vol_sha)" = "$hash_before" ] && ok "data.db is byte for byte the database from step 1" || bad "the restored database differs from step 1"
  run "$old"
  wait_http "$port" || { docker logs "$container" 2>&1 | tail -30; bad "the old image did not start on the restored volume"; exit 1; }
  if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "the old image serves exactly what it did before the upgrade"; else bad "the old image does not serve what it did before"; fi
  docker stop "$container" >/dev/null
fi

echo
echo "upgrade-path-proof ($mode): $passed passed, $failed failed"
[ "$failed" -eq 0 ]
