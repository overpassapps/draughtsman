#!/usr/bin/env bash
# Tests for the release's software bill of materials guards (scripts/lib/sbom.sh, called by publish.sh and release.sh), run by
# scripts/verify-local.sh. Each rule is checked in both directions: a release with its SBOM beside the archives and inside all six of
# them passes, and every way of losing it (no file, a hollow file, a file that is not in SHA256SUMS, an archive without it, an archive
# with a different one, an archive that was not built, a tree that cannot make one) is refused for the right reason, naming the culprit.
#
#   scripts/sbom.test.sh
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/archive-layout.sh
source "$repo_root/scripts/lib/archive-layout.sh"
# shellcheck source=lib/sbom.sh
source "$repo_root/scripts/lib/sbom.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/sbom-test.XXXXXX")"
trap 'rm -rf "${work:?}"' EXIT

failures=0
pass() { echo "PASS: $1"; }
fail() { echo "FAIL: $1"; failures=$((failures + 1)); }
version="0.1.0"

# A small SBOM that satisfies scripts/check-sbom.mjs (its own rules are tested in scripts/check-sbom.test.mjs).
write_good_sbom() {
  cat >"$1" <<'SBOM'
{"bomFormat":"CycloneDX","specVersion":"1.5","serialNumber":"urn:uuid:3e671687-395b-5f5f-a0b5-0e9c1a4c8f10","version":1,
 "metadata":{"component":{"type":"application","bom-ref":"draughtsman","name":"draughtsman","version":"0.1.0"}},
 "components":[
  {"type":"library","bom-ref":"pkg:nuget/YamlDotNet@18.1.0","name":"YamlDotNet","version":"18.1.0","scope":"required","purl":"pkg:nuget/YamlDotNet@18.1.0","properties":[{"name":"draughtsman:origin","value":"Draughtsman.Server"}]},
  {"type":"library","bom-ref":"pkg:npm/react@18.3.1","name":"react","version":"18.3.1","scope":"required","purl":"pkg:npm/react@18.3.1","properties":[{"name":"draughtsman:origin","value":"web/package-lock.json"}]},
  {"type":"library","bom-ref":"pkg:npm/elkjs@0.12.0","name":"elkjs","version":"0.12.0","scope":"required","purl":"pkg:npm/elkjs@0.12.0","properties":[{"name":"draughtsman:origin","value":"sidecar/package-lock.json"}]},
  {"type":"application","bom-ref":"pkg:generic/nodejs@22.23.3","name":"Node.js","version":"22.23.3","scope":"required","purl":"pkg:generic/nodejs@22.23.3"}]}
SBOM
}

# make_archive OUT RID [SBOM FILE OR "none"]: an archive shaped like publish.sh's, whose SBOM.cdx.json is that file (or absent).
make_archive() {
  local out="$1" rid="$2" sbom="${3:-$out/draughtsman-$version.cdx.json}" base stage
  base="draughtsman-$version-$rid"
  stage="$work/stage-$rid-$RANDOM"
  mkdir -p "$stage/$base/wwwroot"
  printf 'x' >"$stage/$base/wwwroot/index.html"
  [ "$sbom" = "none" ] || cp "$sbom" "$stage/$base/$SBOM_ARCHIVE_NAME"
  case "$rid" in
    win-*) (cd "$stage" && zip -qr "$out/$base.zip" "$base") ;;
    *) (cd "$stage" && COPYFILE_DISABLE=1 tar -czf "$out/$base.tar.gz" "$base") ;;
  esac
  rm -rf "${stage:?}"
}

# A complete release: the SBOM beside six archives that each carry it, and a SHA256SUMS that lists it.
make_release() {
  local out="$1" rid
  rm -rf "${out:?}"; mkdir -p "$out"
  write_good_sbom "$out/draughtsman-$version.cdx.json"
  for rid in "${ARCHIVE_RIDS[@]}"; do make_archive "$out" "$rid"; done
  printf 'aaaa  draughtsman-%s-linux-x64.tar.gz\nbbbb  draughtsman-%s.cdx.json\n' "$version" "$version" >"$out/SHA256SUMS"
}

# expect_ok LABEL OUT [REPO] / expect_refused LABEL NEEDLE OUT [REPO]
expect_ok() {
  local out
  if out="$(guard_release_sboms "$2" "$version" "${3:-}" "${ARCHIVE_RIDS[@]}" 2>&1)"; then pass "$1"; else fail "$1: $out"; fi
}
expect_refused() {
  local out
  if out="$(guard_release_sboms "$3" "$version" "${4:-}" "${ARCHIVE_RIDS[@]}" 2>&1)"; then fail "$1: the guard passed a release it should refuse"; elif grep -qF -- "$2" <<<"$out"; then pass "$1"; else fail "$1: refused for the wrong reason: $out"; fi
}

if ! command -v zip >/dev/null; then echo "FAIL: zip is required to build the Windows archive fixtures"; exit 1; fi

release="$work/release"
make_release "$release"
expect_ok "a release with its SBOM beside the archives and inside all six passes" "$release"

make_release "$release"; rm "$release/draughtsman-$version.cdx.json"
expect_refused "no SBOM beside the archives is refused" "the release has no software bill of materials" "$release"

make_release "$release"; : >"$release/draughtsman-$version.cdx.json"
expect_refused "an empty SBOM file is refused" "is missing" "$release"

make_release "$release"; printf '{"bomFormat":"CycloneDX","specVersion":"1.5","components":[]}' >"$release/draughtsman-$version.cdx.json"
expect_refused "a hollow SBOM is refused" "is not usable" "$release"

make_release "$release"; printf 'aaaa  draughtsman-%s-linux-x64.tar.gz\n' "$version" >"$release/SHA256SUMS"
expect_refused "an SBOM that SHA256SUMS does not list is refused" "does not list draughtsman-$version.cdx.json" "$release"

make_release "$release"; rm "$release/SHA256SUMS"
expect_refused "no SHA256SUMS at all is refused" "does not list" "$release"

make_release "$release"; rm "$release/draughtsman-$version-linux-x64.tar.gz"; make_archive "$release" linux-x64 none
expect_refused "a tar.gz archive without SBOM.cdx.json is refused, naming it" "draughtsman-$version-linux-x64.tar.gz carries no draughtsman-$version-linux-x64/$SBOM_ARCHIVE_NAME" "$release"

make_release "$release"; rm "$release/draughtsman-$version-win-x64.zip"; make_archive "$release" win-x64 none
expect_refused "a Windows zip without SBOM.cdx.json is refused, naming it" "draughtsman-$version-win-x64.zip carries no" "$release"

make_release "$release"; rm "$release/draughtsman-$version-osx-arm64.tar.gz"; : >"$work/empty.json"; make_archive "$release" osx-arm64 "$work/empty.json"
expect_refused "an archive whose SBOM is empty is refused" "osx-arm64.tar.gz carries no" "$release"

make_release "$release"; rm "$release/draughtsman-$version-linux-arm64.tar.gz"; sed 's/18.1.0/18.1.1/g' "$release/draughtsman-$version.cdx.json" >"$work/other.json"; make_archive "$release" linux-arm64 "$work/other.json"
expect_refused "an archive carrying a different SBOM is refused" "is not the release's draughtsman-$version.cdx.json" "$release"

make_release "$release"; rm "$release/draughtsman-$version-win-arm64.zip"
expect_refused "an archive that was not built is refused" "draughtsman-$version-win-arm64.zip was not built" "$release"

# Every platform is checked, not only the first: removing the SBOM from any one archive is caught, and only that one is named.
for rid in "${ARCHIVE_RIDS[@]}"; do
  make_release "$release"
  base="draughtsman-$version-$rid"
  case "$rid" in win-*) rm "$release/$base.zip" ;; *) rm "$release/$base.tar.gz" ;; esac
  make_archive "$release" "$rid" none
  out="$(guard_release_sboms "$release" "$version" "" "${ARCHIVE_RIDS[@]}" 2>&1)"; status=$?
  named="$(grep -c "carries no" <<<"$out")"
  if [ "$status" -ne 0 ] && [ "$named" = 1 ] && grep -qF "$base." <<<"$out"; then pass "$rid: its archive losing the SBOM is caught, alone"; else fail "$rid: status $status, $named archives named: $out"; fi
done

# The cross-check against the sources is wired in: the four-package SBOM above is not what this repository ships.
make_release "$release"
expect_refused "with the repository's sources, an SBOM that omits their packages is refused" "but not in the SBOM" "$release" "$repo_root"

# guard_sbom_inputs: the tree can make an SBOM at all.
tree="$work/tree"
make_tree() { local f; rm -rf "${tree:?}"; mkdir -p "$tree/scripts" "$tree/web" "$tree/sidecar"; for f in scripts/generate-sbom.mjs scripts/check-sbom.mjs scripts/node-runtime.txt web/package-lock.json sidecar/package-lock.json Directory.Build.props; do cp "$repo_root/$f" "$tree/$f"; done; }
make_tree
if out="$(guard_sbom_inputs "$tree" 2>&1)"; then pass "a tree with the generator, the checker and the lock files can make an SBOM"; else fail "guard_sbom_inputs refused a complete tree: $out"; fi
for f in scripts/generate-sbom.mjs scripts/check-sbom.mjs scripts/node-runtime.txt web/package-lock.json sidecar/package-lock.json Directory.Build.props; do
  make_tree; rm "$tree/$f"
  if out="$(guard_sbom_inputs "$tree" 2>&1)"; then fail "guard_sbom_inputs passed a tree without $f"; elif grep -qF "$f is missing" <<<"$out"; then pass "a tree without $f is refused"; else fail "without $f: wrong reason: $out"; fi
done
make_tree; printf 'this is not javascript (' >"$tree/scripts/check-sbom.mjs"
if out="$(guard_sbom_inputs "$tree" 2>&1)"; then fail "guard_sbom_inputs passed a checker that does not parse"; elif grep -qF "does not parse" <<<"$out"; then pass "a checker that does not parse is refused"; else fail "unparseable checker: wrong reason: $out"; fi
if out="$(guard_sbom_inputs "$repo_root" 2>&1)"; then pass "this repository can make an SBOM"; else fail "guard_sbom_inputs refused this repository: $out"; fi

# generate_release_sbom, end to end, when the server has been restored (verify-local.sh restores first).
if [ -f "$repo_root/src/Draughtsman.Server/obj/project.assets.json" ]; then
  first="$work/first.cdx.json"; second="$work/second.cdx.json"
  repo_version="$(release_version "$repo_root/Directory.Build.props" 2>/dev/null || sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo_root/Directory.Build.props" | head -1)"
  if generate_release_sbom "$repo_root" "$first" "$repo_version" 2026-10-14 >"$work/gen.log" 2>&1; then pass "the SBOM is generated and checked against the sources"; else fail "generate_release_sbom: $(cat "$work/gen.log")"; fi
  generate_release_sbom "$repo_root" "$second" "$repo_version" 2026-10-14 >/dev/null 2>&1
  if cmp -s "$first" "$second"; then pass "the same inputs and build date give a byte-identical SBOM"; else fail "two runs gave different SBOMs"; fi
  if grep -q '"timestamp": "2026-10-14T00:00:00.000Z"' "$first"; then pass "the SBOM is stamped with the pinned build date"; else fail "no pinned timestamp in the SBOM"; fi
  if generate_release_sbom "$repo_root" "$work/wrong.cdx.json" "9.9.9" 2026-10-14 >"$work/gen2.log" 2>&1; then fail "an SBOM for the wrong version was accepted"; else pass "an SBOM that does not describe the release's version is refused"; fi
else
  echo "SKIP: generate_release_sbom end to end (run dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj first)"
fi

if [ "$failures" -gt 0 ]; then echo "$failures SBOM guard test(s) failed"; exit 1; fi
echo "all SBOM guard tests passed"
