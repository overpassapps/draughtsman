#!/usr/bin/env bash
# Tests for scripts/release.sh's guards (PK-07, and the upgrade-fixture rule), run by scripts/verify-local.sh. Each case runs the real
# script with --check-only (so nothing is built) and asserts the exit status and the reason, in both directions: the good inputs
# pass, and every bad one is refused for the right reason. The cases that are not about the fixture run against a throwaway tree of
# migrations and a valid fixture for a made-up version, so they say nothing about the repository's own state.
#
#   scripts/release.test.sh
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release="$repo_root/scripts/release.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/release-test.XXXXXX")"
trap 'rm -rf "${work:?}"' EXIT

failures=0
today="$(node -e 'console.log(new Date().toISOString().slice(0, 10))')"

# expect <name> <expected exit 0|1> <stderr must contain, or empty> <args...>
expect() {
  local name="$1" want="$2" needle="$3"
  shift 3
  local out status
  out="$("$release" --check-only --version 9.9.9 --fixtures-dir "$work/fixtures" --migrations-dir "$work/migrations" "$@" 2>&1)"
  status=$?
  if [ "$status" -ne "$want" ]; then
    echo "FAIL: $name: exit $status, wanted $want"; echo "$out" | sed 's/^/    /'; failures=$((failures + 1)); return
  fi
  if [ -n "$needle" ] && ! grep -qF -- "$needle" <<<"$out"; then
    echo "FAIL: $name: output does not mention '$needle'"; echo "$out" | sed 's/^/    /'; failures=$((failures + 1)); return
  fi
  echo "PASS: $name"
}

# A tree of migrations and a fixture for version 9.9.9 that ends at the newest of them.
mkdir -p "$work/migrations"
for m in 20260101000000_Initial 20260102000000_AddThings 20260103000000_AddMore; do : >"$work/migrations/$m.cs"; : >"$work/migrations/$m.Designer.cs"; done
: >"$work/migrations/SqliteDraughtsmanDbContextModelSnapshot.cs"
make_fixture() { # make_fixture <dir> <label> <version> <last migration> [skip-file]
  local dir="$1" label="$2" version="$3" last="$4" skip="${5:-}"
  rm -rf "$dir"; mkdir -p "$dir/sqlite" "$dir/postgres"
  node -e '
    const [dir, label, version, last] = process.argv.slice(1);
    const all = ["20260101000000_Initial", "20260102000000_AddThings", "20260103000000_AddMore"];
    const migrations = all.slice(0, all.indexOf(last) + 1);
    require("fs").writeFileSync(`${dir}/manifest.json`, JSON.stringify({ label, build: { version, migrations } }));
  ' "$dir" "$label" "$version" "$last"
  for f in sqlite/data-folder.tar.gz sqlite/expected.json postgres/data-folder.tar.gz postgres/expected.json postgres/dump.sql.gz; do
    [ "$f" = "$skip" ] || : >"$dir/$f"
  done
}
make_fixture "$work/fixtures/v9.9.9" v9.9.9 9.9.9 20260103000000_AddMore

expect "a pinned date passes" 0 "guards passed" --build-date "$today"
expect "an unpinned build date is refused" 1 "--build-date YYYY-MM-DD is required"
expect "a malformed date is refused" 1 "must be a real date" --build-date "14/10/2026"
expect "an impossible date is refused" 1 "must be a real date" --build-date "2026-02-30"
expect "a date in the future is refused" 1 "not in the future" --build-date "2099-01-01"
expect "a past date is fine" 0 "guards passed" --build-date "2026-09-30"

# The upgrade-fixture rule: a release is cut only when its own version has a fixture that is whole and current.
nofix="$work/empty-fixtures"; mkdir -p "$nofix"
expect "a version with no upgrade fixture is refused" 1 "version 9.9.9 has no upgrade fixture" --build-date "$today" --fixtures-dir "$nofix"
make_fixture "$work/stale/v9.9.9" v9.9.9 9.9.9 20260102000000_AddThings
expect "a fixture taken before the newest migration is refused as stale" 1 "it is stale" --build-date "$today" --fixtures-dir "$work/stale"
make_fixture "$work/wrongversion/v9.9.9" v9.9.8 9.9.8 20260103000000_AddMore
expect "a fixture whose label is another version is refused" 1 "its label is" --build-date "$today" --fixtures-dir "$work/wrongversion"
make_fixture "$work/nopg/v9.9.9" v9.9.9 9.9.9 20260103000000_AddMore postgres/dump.sql.gz
expect "a fixture without the postgres dump is refused" 1 "postgres/dump.sql.gz is missing" --build-date "$today" --fixtures-dir "$work/nopg"
make_fixture "$work/nosqlite/v9.9.9" v9.9.9 9.9.9 20260103000000_AddMore sqlite/data-folder.tar.gz
expect "a fixture without the sqlite data folder is refused" 1 "sqlite/data-folder.tar.gz is missing" --build-date "$today" --fixtures-dir "$work/nosqlite"
echo "{ not json" >"$work/stale/v9.9.9/manifest.json"
expect "a fixture whose manifest is not JSON is refused" 1 "manifest.json is not valid JSON" --build-date "$today" --fixtures-dir "$work/stale"
# A new migration added after the fixture was made (the tree moves on, the fixture does not) is the stale case again.
: >"$work/migrations/20260104000000_AddEvenMore.cs"
expect "a migration added after the fixture was made makes it stale" 1 "it is stale" --build-date "$today"
rm -f "$work/migrations/20260104000000_AddEvenMore.cs"
expect "the fixture is accepted again once it matches the tree" 0 "upgrade fixture v9.9.9" --build-date "$today"

# The software bill of materials is checked after the build, from the outside (docs/air-gapped.md): a copy of release.sh in a throwaway tree
# runs a stand-in publish.sh that writes six archives, and the release must fail whenever any of them (or the file beside them) lacks the SBOM.
tree="$work/sbom-tree"
mkdir -p "$tree/scripts/lib" "$tree/web" "$tree/sidecar"
cp "$repo_root/scripts/release.sh" "$tree/scripts/release.sh"
cp "$repo_root"/scripts/lib/*.sh "$tree/scripts/lib/"
for f in scripts/check-sbom.mjs scripts/generate-sbom.mjs scripts/node-runtime.txt web/package-lock.json sidecar/package-lock.json; do cp "$repo_root/$f" "$tree/$f"; done
printf '<Project><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>\n' >"$tree/Directory.Build.props"
cat >"$tree/scripts/publish.sh" <<'FAKE'
#!/usr/bin/env bash
# Stand-in for scripts/publish.sh: six archives and an SBOM, spoiled the way $FAKE_PUBLISH says.
set -eu
out="$(cd "$(dirname "$0")/.." && pwd)/artifacts/release"
while [ $# -gt 0 ]; do case "$1" in --out) out="$2"; shift 2 ;; *) shift ;; esac; done
mkdir -p "$out"
sbom='{"bomFormat":"CycloneDX","specVersion":"1.5","serialNumber":"urn:uuid:3e671687-395b-5f5f-a0b5-0e9c1a4c8f10","version":1,"metadata":{"component":{"type":"application","bom-ref":"draughtsman","name":"draughtsman","version":"9.9.9"}},"components":[{"type":"library","bom-ref":"a","name":"YamlDotNet","version":"1","scope":"required","purl":"pkg:nuget/YamlDotNet@1","properties":[{"name":"draughtsman:origin","value":"Draughtsman.Server"}]},{"type":"library","bom-ref":"b","name":"react","version":"1","scope":"required","purl":"pkg:npm/react@1","properties":[{"name":"draughtsman:origin","value":"web/package-lock.json"}]},{"type":"library","bom-ref":"c","name":"elkjs","version":"1","scope":"required","purl":"pkg:npm/elkjs@1","properties":[{"name":"draughtsman:origin","value":"sidecar/package-lock.json"}]},{"type":"application","bom-ref":"d","name":"Node.js","version":"22","scope":"required","purl":"pkg:generic/nodejs@22"}]}'
[ "${FAKE_PUBLISH:-}" = "no-release-sbom" ] || printf '%s' "$sbom" >"$out/draughtsman-9.9.9.cdx.json"
for rid in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64; do
  base="draughtsman-9.9.9-$rid"; stage="$(mktemp -d)"; mkdir -p "$stage/$base"
  [ "${FAKE_PUBLISH:-}" = "no-sbom-in-$rid" ] || printf '%s' "$sbom" >"$stage/$base/SBOM.cdx.json"
  printf x >"$stage/$base/draughtsman"
  case "$rid" in win-*) (cd "$stage" && zip -qr "$out/$base.zip" "$base") ;; *) (cd "$stage" && tar -czf "$out/$base.tar.gz" "$base") ;; esac
  rm -rf "$stage"
done
[ "${FAKE_PUBLISH:-}" = "no-sums" ] || printf 'aaaa  draughtsman-9.9.9.cdx.json\n' >"$out/SHA256SUMS"
FAKE
chmod +x "$tree/scripts/publish.sh" "$tree/scripts/release.sh"
if command -v zip >/dev/null; then
  release_in_tree() { # release_in_tree <FAKE_PUBLISH mode>: a full release.sh run in the throwaway tree
    rm -rf "$tree/artifacts"
    FAKE_PUBLISH="$1" "$tree/scripts/release.sh" --version 9.9.9 --build-date "$today" --fixtures-dir "$work/fixtures" --migrations-dir "$work/migrations" 2>&1
  }
  out="$(release_in_tree ok)"; status=$?
  if [ "$status" -eq 0 ] && grep -qF "9.9.9 built" <<<"$out"; then echo "PASS: a release whose six archives and file all carry the SBOM completes"; else echo "FAIL: a complete release was refused: $out"; failures=$((failures + 1)); fi
  for spoil in no-release-sbom no-sums no-sbom-in-linux-x64 no-sbom-in-win-arm64 no-sbom-in-osx-x64; do
    out="$(release_in_tree "$spoil")"; status=$?
    if [ "$status" -ne 0 ] && ! grep -qF "9.9.9 built" <<<"$out"; then echo "PASS: release.sh exits non-zero when $spoil"; else echo "FAIL: release.sh did not fail when $spoil (exit $status): $out"; failures=$((failures + 1)); fi
  done
  rm -rf "$tree/web/package-lock.json"
  out="$("$tree/scripts/release.sh" --check-only --version 9.9.9 --build-date "$today" --fixtures-dir "$work/fixtures" --migrations-dir "$work/migrations" 2>&1)"; status=$?
  if [ "$status" -ne 0 ] && grep -qF "web/package-lock.json is missing" <<<"$out"; then echo "PASS: a tree that cannot make an SBOM is refused before anything is built"; else echo "FAIL: --check-only with no lock file (exit $status): $out"; failures=$((failures + 1)); fi
else
  echo "SKIP: the SBOM release-wiring cases need zip"
fi

# And the repository's own fixtures: the current <Version> has one only after someone made it for the release.
repo_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo_root/Directory.Build.props" | head -1)"
if [ -f "$repo_root/tests/upgrade-fixtures/v$repo_version/manifest.json" ]; then
  echo "NOTE: the repository has a fixture for v$repo_version; the refusal case below is skipped"
else
  out="$("$release" --check-only --build-date "$today" 2>&1)"; status=$?
  if [ "$status" -eq 1 ] && grep -qF "version $repo_version has no upgrade fixture" <<<"$out"; then echo "PASS: the repository's current version has no fixture yet, so a default release is refused"
  else echo "FAIL: the repository's own fixture rule: exit $status"; echo "$out" | sed 's/^/    /'; failures=$((failures + 1)); fi
fi

if [ "$failures" -gt 0 ]; then echo "$failures release guard test(s) failed"; exit 1; fi
echo "all release guard tests passed"
