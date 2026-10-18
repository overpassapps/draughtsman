#!/usr/bin/env bash
# Tests for scripts/docker-export.sh with a stand-in `docker` on PATH (no Docker needed, nothing is built or pulled):
# the archive is written and its checksum matches, an image that is not in the store is refused and leaves no file, and
# --dry-run touches nothing. Run by scripts/verify-local.sh.
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export_script="$repo_root/scripts/docker-export.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/docker-export-test.XXXXXX")"
trap 'rm -rf "${work:?}"' EXIT
mkdir -p "$work/bin"
failures=0

# A fake docker: only `draughtsman:0.1.0` and `draughtsman:no-sbom` exist; `save` writes recognisable bytes, and `run ... cat /app/SBOM.cdx.json`
# prints whatever $FAKE_SBOM names (absent: the image has no SBOM).
cat >"$work/bin/docker" <<'DOCKER'
#!/bin/sh
case "$1" in
  image) case "$3" in draughtsman:0.1.0|draughtsman:no-sbom) exit 0 ;; *) exit 1 ;; esac ;;
  save) printf 'fake-image-layers-for-%s' "$2" ;;
  run) [ -n "${FAKE_SBOM:-}" ] && [ -f "$FAKE_SBOM" ] && cat "$FAKE_SBOM" || exit 1 ;;
  *) exit 2 ;;
esac
DOCKER
chmod +x "$work/bin/docker"
# A small SBOM that satisfies scripts/check-sbom.mjs (the real rules are tested in scripts/check-sbom.test.mjs).
cat >"$work/good-sbom.json" <<'SBOM'
{"bomFormat":"CycloneDX","specVersion":"1.5","serialNumber":"urn:uuid:3e671687-395b-5f5f-a0b5-0e9c1a4c8f10","version":1,
 "metadata":{"component":{"type":"application","bom-ref":"draughtsman","name":"draughtsman","version":"0.1.0"}},
 "components":[
  {"type":"library","bom-ref":"pkg:nuget/YamlDotNet@18.1.0","name":"YamlDotNet","version":"18.1.0","scope":"required","purl":"pkg:nuget/YamlDotNet@18.1.0","properties":[{"name":"draughtsman:origin","value":"Draughtsman.Server"}]},
  {"type":"library","bom-ref":"pkg:npm/react@18.3.1","name":"react","version":"18.3.1","scope":"required","purl":"pkg:npm/react@18.3.1","properties":[{"name":"draughtsman:origin","value":"web/package-lock.json"}]},
  {"type":"library","bom-ref":"pkg:npm/elkjs@0.12.0","name":"elkjs","version":"0.12.0","scope":"required","purl":"pkg:npm/elkjs@0.12.0","properties":[{"name":"draughtsman:origin","value":"sidecar/package-lock.json"}]},
  {"type":"application","bom-ref":"pkg:generic/nodejs@22.23.3","name":"Node.js","version":"22.23.3","scope":"required","purl":"pkg:generic/nodejs@22.23.3"}]}
SBOM
printf '{"bomFormat":"CycloneDX","specVersion":"1.5","components":[]}' >"$work/hollow-sbom.json"
export FAKE_SBOM="$work/good-sbom.json"

check() {
  local name="$1" ok="$2"
  if [ "$ok" = "0" ]; then echo "PASS: $name"; else echo "FAIL: $name"; failures=$((failures + 1)); fi
}

out="$work/out"
PATH="$work/bin:$PATH" "$export_script" draughtsman:0.1.0 --out "$out" >"$work/log" 2>&1
check "an existing image is saved" "$([ -s "$out/draughtsman-0.1.0-image.tar.gz" ]; echo $?)"
check "the saved file is the gzip of docker save's output" "$([ "$(gzip -dc "$out/draughtsman-0.1.0-image.tar.gz")" = "fake-image-layers-for-draughtsman:0.1.0" ]; echo $?)"
(cd "$out" && if command -v sha256sum >/dev/null; then sha256sum -c draughtsman-0.1.0-image.tar.gz.sha256; else shasum -a 256 -c draughtsman-0.1.0-image.tar.gz.sha256; fi) >/dev/null 2>&1
check "the checksum file verifies the archive" "$?"
check "the load commands are printed" "$(grep -q 'docker load -i draughtsman-0.1.0-image.tar.gz' "$work/log"; echo $?)"

# The checksum must fail when the archive is altered (the check above is not vacuous).
printf 'tampered' >>"$out/draughtsman-0.1.0-image.tar.gz"
(cd "$out" && if command -v sha256sum >/dev/null; then sha256sum -c draughtsman-0.1.0-image.tar.gz.sha256; else shasum -a 256 -c draughtsman-0.1.0-image.tar.gz.sha256; fi) >/dev/null 2>&1
check "an altered archive fails its checksum" "$([ $? -ne 0 ]; echo $?)"

# The image must carry its software bill of materials (docs/air-gapped.md): none, or a hollow one, and nothing is exported.
FAKE_SBOM="" PATH="$work/bin:$PATH" "$export_script" draughtsman:no-sbom --out "$work/out-nosbom" >"$work/log-nosbom" 2>&1
status=$?
check "an image with no SBOM is refused" "$([ $status -ne 0 ] && grep -q 'carries no /app/SBOM.cdx.json' "$work/log-nosbom"; echo $?)"
check "...and no archive is written for it" "$([ ! -e "$work/out-nosbom/draughtsman-no-sbom-image.tar.gz" ]; echo $?)"
FAKE_SBOM="$work/hollow-sbom.json" PATH="$work/bin:$PATH" "$export_script" draughtsman:no-sbom --out "$work/out-hollow" >"$work/log-hollow" 2>&1
status=$?
check "an image whose SBOM is hollow is refused" "$([ $status -ne 0 ] && grep -q 'is not usable' "$work/log-hollow"; echo $?)"

PATH="$work/bin:$PATH" "$export_script" draughtsman:9.9.9 --out "$work/out2" >"$work/log2" 2>&1
status=$?
check "an image that is not in the store is refused" "$([ $status -ne 0 ] && grep -q 'not in the local Docker store' "$work/log2"; echo $?)"
check "...and leaves no archive behind" "$([ ! -e "$work/out2" ]; echo $?)"

PATH="$work/bin:$PATH" "$export_script" registry.example:5000/team/draughtsman:0.1.0 --out "$work/out3" --dry-run >"$work/log3" 2>&1
check "--dry-run names the file from the tag and writes nothing" "$(grep -q 'draughtsman-0.1.0-image.tar.gz' "$work/log3" && [ ! -e "$work/out3" ]; echo $?)"
check "--dry-run says it will look for the SBOM" "$(grep -q 'SBOM.cdx.json' "$work/log3"; echo $?)"

if [ "$failures" -gt 0 ]; then echo "$failures docker-export test(s) failed"; exit 1; fi
echo "all docker-export tests passed"
