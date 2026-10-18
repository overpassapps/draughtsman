#!/usr/bin/env bash
# Proves, on real release archives, that the stored AI API key and the saved SMTP password no longer depend on where the program is installed
# (docs/upgrade-backup.md, "Where to put the new version"; CHANGELOG, "Fixed (stored secrets no longer depend on where the program is installed)"):
#
#   scripts/dataprotection-pin-proof.sh --old draughtsman-0.1.0-osx-arm64.tar.gz --new draughtsman-0.1.0-osx-arm64.tar.gz [--key-ring key|none]
#
# --old is a build that PREDATES the pin (the program's content root is the Data Protection application discriminator, so its secrets are bound to the
# folder it runs from); --new is the build under test. Both are release archives from scripts/publish.sh, run from different folders.
#
#   1. the OLD archive, in one folder, on a new data folder: fill it through its API (populate.mjs: users, documents, branding, AI settings with a stored
#      key, outgoing email with a saved SMTP password), check it (verify.mjs), stop it, keep a pristine copy of the data folder and a backup made by the OLD
#      binary with --include-secrets (a backup taken BEFORE the change).
#   2. the NEW archive unpacked in a DIFFERENT folder beside the old one: `config check` from it says the secrets are readable and will be re-protected
#      (and writes nothing); start it on the same data: every check passes, both secrets read, they were re-protected under the pinned name, and the earlier
#      settings files are kept whole in backups/secrets-*.
#   3. the OLD program folder DELETED, the NEW program moved to a third folder: start it again with nothing named: both secrets read, nothing was
#      re-protected a second time (a second move needs no fallback), and `config check` agrees.
#   4. a pristine copy of the old data with the old folder unknown (deleted, nothing records it) and the program somewhere new: `config check` and the
#      admin pages name both secrets as unreadable with what to do, the settings files are byte for byte untouched, no backup was made; then the old
#      folder is named in DRAUGHTSMAN_LEGACY_INSTALL_PATHS and the secrets read and are re-protected.
#   5. the backup taken by the OLD binary restored with the NEW binary onto a program at yet another path: unreadable until the old folder is named,
#      then it reads.
#   6. going back: the OLD program on the data the NEW one re-protected shows the two secrets as needing to be entered again (it can only read them from
#      the folder it ran from), and putting back the backups/secrets-* copies as documented brings them back.
#
# Everything runs on free random ports with scratch data under a temporary folder outside $HOME, stops what it starts by exact pid and removes its
# scratch folder. The output is the evidence quoted in docs/upgrade-backup.md.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
here="$repo_root/scripts/upgrade-fixture"
case "${1:-}" in -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;; esac
old=""; new=""; work=""; key_ring="key"; keep=0
while [ $# -gt 0 ]; do
  case "$1" in
    --old) old="${2:?}"; shift 2 ;;
    --new) new="${2:?}"; shift 2 ;;
    --work) work="${2:?}"; shift 2 ;;
    --key-ring) key_ring="${2:?}"; shift 2 ;;
    --keep) keep=1; shift ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "dataprotection-pin-proof: unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$old" ] && [ -n "$new" ] || { echo "dataprotection-pin-proof: --old and --new are required" >&2; exit 2; }
case "$key_ring" in none|key) ;; *) echo "dataprotection-pin-proof: --key-ring is none or key" >&2; exit 2 ;; esac

if [ -z "$work" ]; then work="$(mktemp -d "${TMPDIR:-/tmp}/dataprotection-pin-proof.XXXXXX")"; else mkdir -p "$work"; fi
work="$(cd "$work" && pwd -P)"
case "$work" in "$HOME"/*) echo "dataprotection-pin-proof: the work folder $work is under \$HOME; use --work outside it" >&2; exit 1 ;; esac

pid=""
cleanup() {
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null || true; fi
  if [ "$keep" = "1" ]; then echo "dataprotection-pin-proof: scratch kept at $work"; else rm -rf "${work:?}"; fi
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
apps="$work/apps"
mkdir -p "$apps"

unpack() { # unpack <archive> <folder>
  rm -rf "$2"; mkdir -p "$2"; tar -xzf "$1" -C "$2" --strip-components=1
  [ -x "$2/draughtsman" ] || { echo "dataprotection-pin-proof: no draughtsman executable in $1" >&2; exit 1; }
}
# start <program folder> <data folder> <log> [extra NAME=value ...]: the program in its own folder on that data; sets pid and port
start() {
  local program="$1" data="$2" log="$3"; shift 3
  port="$(free_port)"
  (cd "$program" && exec env -i "DRAUGHTSMAN_DATA=$data" "ASPNETCORE_URLS=http://127.0.0.1:$port" "PATH=$PATH" "HOME=$work" "TMPDIR=$work" \
      "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$@" ./draughtsman >"$log" 2>&1) &
  pid=$!
}
stop() { kill "$pid" 2>/dev/null || true; wait_gone "$pid" || { echo "dataprotection-pin-proof: pid $pid did not stop" >&2; exit 1; }; wait "$pid" 2>/dev/null || true; pid=""; }
# run_cli <program folder> <output file> <args...>: a verb of the program, with the same environment the service has
run_cli() {
  local program="$1" out="$2"; shift 2
  set +e
  env -i "PATH=$PATH" "HOME=$work" "TMPDIR=$work" "DRAUGHTSMAN_KEY_RING_PROTECTION=$key_ring" "DRAUGHTSMAN_KEY_ENCRYPTION_KEY=$kek" "$@" >"$out" 2>&1
  cli_exit=$?
  set -e
}
tree_sha() { (cd "$1" && find . -type f ! -name '*.tmp' | LC_ALL=C sort | while read -r f; do printf '%s  %s\n' "$(sha "$f")" "$f"; done) | { if command -v sha256sum >/dev/null; then sha256sum; else shasum -a 256; fi; } | cut -d' ' -f1; }
secrets_backups() { ls -d "$1"/backups/secrets-* 2>/dev/null | wc -l | tr -d ' '; }

# ------------------------------------------------------------------------------------------------ 1. the OLD archive
step "1. the OLD archive, in its own folder, on a new data folder: fill it through its API"
old_app="$apps/old"
data="$work/data"
unpack "$old" "$old_app"
echo "  old: $("$old_app/draughtsman" --version)   program folder: $old_app"
start "$old_app" "$data" "$work/old-1.log"
wait_http "$port" || { tail -30 "$work/old-1.log"; echo "the old program did not start" >&2; exit 1; }
code="$(grep -o 'setup code: [A-Z0-9]*' "$work/old-1.log" | head -1 | awk '{print $3}' || true)"
node "$here/populate.mjs" --base "http://127.0.0.1:$port" ${code:+--setup-code "$code"} --examples "$repo_root/docs/examples" --out "$work/expected.json" --label proof
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --self-check; then ok "the old program serves what it was filled with, the AI key and the SMTP password readable from its own folder"; else bad "the old program does not match its own expectations"; fi
stop
ai_before="$(sha "$data/ai/settings.json")"; email_before="$(sha "$data/email/settings.json")"
echo "  ai/settings.json sha256 $ai_before; email/settings.json sha256 $email_before"
cp -R "$data" "$work/data-original"
run_cli "$old_app" "$work/old-backup.txt" "$old_app/draughtsman" backup --include-secrets --out "$work/old-backup.zip" "--DRAUGHTSMAN_DATA=$data"
if [ "$cli_exit" -eq 0 ] && [ -f "$work/old-backup.zip" ]; then ok "the OLD binary took a backup with its secrets (a backup made before the change)"; else cat "$work/old-backup.txt"; bad "the old backup failed (exit $cli_exit)"; fi

# ------------------------------------------------------------------------------------------------ 2. new folder beside the old one
step "2. the NEW archive in a different folder beside the old one: config check, then start it on the old data"
new_app="$apps/new"
unpack "$new" "$new_app"
echo "  new: $("$new_app/draughtsman" --version)   program folder: $new_app (the old one is still at $old_app)"
tree_before="$(tree_sha "$data")"
run_cli "$new_app" "$work/config-check-1.txt" "$new_app/draughtsman" config check "--DRAUGHTSMAN_DATA=$data"
echo "  \$ draughtsman config check   (Stored secrets section)"
sed -n '/^Stored secrets/,/^$/p' "$work/config-check-1.txt" | sed 's/^/    /'
if [ "$cli_exit" -eq 0 ]; then ok "config check says the new version would start (exit 0)"; else bad "config check exited $cli_exit"; fi
grep -q "AI API key.*can still be read" "$work/config-check-1.txt" && grep -q "SMTP password.*can still be read" "$work/config-check-1.txt" && ok "config check says both secrets can still be read and will be re-protected" || bad "config check does not report both secrets readable"
[ "$(tree_sha "$data")" = "$tree_before" ] && ok "config check changed nothing in the data folder" || bad "config check changed the data folder"

start "$new_app" "$data" "$work/new-1.log"
wait_http "$port" || { tail -40 "$work/new-1.log"; bad "the new program did not start on the old data"; exit 1; }
grep -E "Stored secrets:" "$work/new-1.log" | cut -c1-260 | sed 's/^ */    /'
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --secrets-readable; then ok "the new program, from another folder, serves every document, user, token, version, setting and BOTH secrets the old one did"; else bad "the new program does not match what the old one served"; fi
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=reprotected,SMTP password=reprotected" && ok "both secrets were re-protected at this start (Admin > AI and Admin > Email say so)" || bad "the secrets were not reported re-protected"
stop
[ "$(sha "$data/ai/settings.json")" != "$ai_before" ] && ok "ai/settings.json now holds the re-protected value" || bad "ai/settings.json was not rewritten"
backup="$(ls -d "$data"/backups/secrets-* | head -1)"
[ "$(secrets_backups "$data")" = "1" ] && [ "$(sha "$backup/ai/settings.json")" = "$ai_before" ] && [ "$(sha "$backup/email/settings.json")" = "$email_before" ] && ok "the earlier ai/ and email/ settings files are kept byte for byte in $(basename "$backup")" || bad "the earlier settings files are not in a backup"
grep -q "install-paths.json" <(ls "$data/keys") && ok "the program's folder was recorded in keys/install-paths.json ($(grep -c '"path"' "$data/keys/install-paths.json") entry)" || bad "no install-paths.json"
if grep -q "THROWAWAY" "$data/keys/install-paths.json"; then bad "the record holds a secret"; else ok "the record holds no secret"; fi
echo "  ai/settings.json after: sha256 $(sha "$data/ai/settings.json")"

# ------------------------------------------------------------------------------------------------ 3. the second move
step "3. the old program folder DELETED and the new one MOVED to a third folder: nothing named, nothing to fall back on"
rm -rf "$old_app"
moved="$work/elsewhere/deeper/draughtsman-moved"
mkdir -p "$(dirname "$moved")"; mv "$new_app" "$moved"
echo "  program folder now: $moved   (no folder named anywhere; the old one no longer exists)"
ai_mid="$(sha "$data/ai/settings.json")"
start "$moved" "$data" "$work/new-2.log"
wait_http "$port" || { tail -40 "$work/new-2.log"; bad "the moved program did not start"; exit 1; }
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --secrets-readable; then ok "the program in its third folder serves everything, both secrets readable"; else bad "the moved program does not match"; fi
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=current,SMTP password=current" && ok "both secrets are current: nothing needed re-protecting, so no fallback was used" || bad "the secrets were not current after a second move"
stop
[ "$(sha "$data/ai/settings.json")" = "$ai_mid" ] && [ "$(secrets_backups "$data")" = "1" ] && ok "the second start wrote nothing to the settings and took no second backup" || bad "the second start changed the settings or took another backup"
run_cli "$moved" "$work/config-check-2.txt" "$moved/draughtsman" config check "--DRAUGHTSMAN_DATA=$data"
sed -n '/^Stored secrets/,/^$/p' "$work/config-check-2.txt" | sed 's/^/    /'
grep -q "AI API key.*protected by the key ring alone" "$work/config-check-2.txt" && ok "config check from the third folder: the secrets do not depend on the folder" || bad "config check from the third folder does not say so"

# ------------------------------------------------------------------------------------------------ 4. the old folder unknown
step "4. a pristine copy of the old data, the old folder gone and unknown: said plainly, nothing altered, then named and recovered"
data_b="$work/data-b"
cp -R "$work/data-original" "$data_b"
rm -rf "$data_b/keys/install-paths.json"
far="$work/far/new-b"; mkdir -p "$(dirname "$far")"; unpack "$new" "$far"
tree_b="$(tree_sha "$data_b")"
run_cli "$far" "$work/config-check-3.txt" "$far/draughtsman" config check "--DRAUGHTSMAN_DATA=$data_b"
sed -n '/^Stored secrets/,/^$/p' "$work/config-check-3.txt" | sed 's/^/    /'
[ "$cli_exit" -eq 0 ] && ok "config check still says the server would start (a secret it cannot read is a warning, never a refusal)" || bad "config check exited $cli_exit"
grep -q "AI API key.*cannot be read" "$work/config-check-3.txt" && grep -q "keyRing.legacyInstallPaths" "$work/config-check-3.txt" && ok "config check names the AI key, says why and says what to do" || bad "config check does not name the unreadable AI key"
[ "$(tree_sha "$data_b")" = "$tree_b" ] && ok "config check changed nothing" || bad "config check changed the data folder"

start "$far" "$data_b" "$work/new-3.log"
wait_http "$port" || { tail -40 "$work/new-3.log"; bad "the program did not start on data whose secrets it cannot read"; exit 1; }
grep -E "Stored secrets:" "$work/new-3.log" | cut -c1-300 | sed 's/^ */    /'
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want unreadable --states "AI API key=unreadable,SMTP password=unreadable" && ok "the admin pages say the AI key and the SMTP password cannot be read, and how to recover" || bad "the admin pages do not say so"
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json"; then ok "everything else is intact: documents, users, tokens, versions, branding, themes (the server started)"; else bad "the server does not serve the rest of the data"; fi
stop
[ "$(sha "$data_b/ai/settings.json")" = "$ai_before" ] && [ "$(sha "$data_b/email/settings.json")" = "$email_before" ] && [ "$(secrets_backups "$data_b")" = "0" ] && ok "both settings files are byte for byte untouched and no backup was needed" || bad "an unreadable secret was altered"

echo "  naming the old folder (the one that was deleted in step 3): DRAUGHTSMAN_LEGACY_INSTALL_PATHS=$old_app"
start "$far" "$data_b" "$work/new-4.log" "DRAUGHTSMAN_LEGACY_INSTALL_PATHS=$old_app"
wait_http "$port" || { tail -40 "$work/new-4.log"; bad "the program did not start with the old folder named"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=reprotected,SMTP password=reprotected" && ok "naming the old folder, even though it no longer exists, reads both secrets and re-protects them" || bad "naming the old folder did not recover the secrets"
stop

# ------------------------------------------------------------------------------------------------ 5. a backup taken before the change
step "5. the backup the OLD binary took (with secrets) restored by the NEW binary onto a program at yet another path"
data_c="$work/data-c"
run_cli "$far" "$work/restore.txt" "$far/draughtsman" restore "$work/old-backup.zip" "--DRAUGHTSMAN_DATA=$data_c"
[ "$cli_exit" -eq 0 ] && ok "restore of the old bundle succeeded" || { cat "$work/restore.txt"; bad "restore exited $cli_exit"; }
[ "$(sha "$data_c/ai/settings.json")" = "$ai_before" ] && ok "the restored ai/settings.json is the old file byte for byte" || bad "the restored settings differ"
start "$far" "$data_c" "$work/new-5.log"
wait_http "$port" || { tail -40 "$work/new-5.log"; bad "the program did not start on the restored data"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want unreadable && ok "restored under a program elsewhere with the old folder unknown: named as unreadable, with the way to recover" || bad "the restored secrets were not reported"
stop
start "$far" "$data_c" "$work/new-6.log" "DRAUGHTSMAN_LEGACY_INSTALL_PATHS=$old_app"
wait_http "$port" || { tail -40 "$work/new-6.log"; bad "the program did not start with the old folder named"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable --states "AI API key=reprotected,SMTP password=reprotected" && ok "with the old folder named, the old backup's secrets read and are re-protected" || bad "the old backup's secrets did not read"
stop

# ------------------------------------------------------------------------------------------------ 6. going back
step "6. going back: the OLD program on the data the NEW one re-protected"
unpack "$old" "$old_app"
start "$old_app" "$data" "$work/old-2.log"
wait_http "$port" || { tail -40 "$work/old-2.log"; bad "the old program did not start on the re-protected data"; exit 1; }
node "$here/secrets.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --want readable >/dev/null 2>&1 && bad "the old program read secrets it should not be able to" || ok "the old program shows both secrets as needing to be entered again (it reads them only from the folder it ran from)"
stop
backup="$(ls -d "$data"/backups/secrets-* | head -1)"
cp "$backup/ai/settings.json" "$data/ai/settings.json"; cp "$backup/email/settings.json" "$data/email/settings.json"
echo "  \$ cp backups/$(basename "$backup")/{ai,email}/settings.json -> <data>/{ai,email}/   (the documented way back)"
start "$old_app" "$data" "$work/old-3.log"
wait_http "$port" || { tail -40 "$work/old-3.log"; bad "the old program did not start after the settings were put back"; exit 1; }
if node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$work/expected.json" --same-install-path; then ok "with the earlier settings files put back the old program serves everything and both secrets read again"; else bad "the old program does not serve what it did"; fi
stop

echo
echo "dataprotection-pin-proof: $passed passed, $failed failed"
[ "$failed" -eq 0 ]
