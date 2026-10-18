#!/usr/bin/env bash
# Builds every release archive, after the checks that keep a bad release from being cut (PK-07):
#
#   scripts/release.sh --build-date 2026-10-14                      all six platforms
#   scripts/release.sh --build-date 2026-10-14 --check-only         run the checks, build nothing
#
# It refuses when
#   * --build-date is absent, not a real YYYY-MM-DD date, or in the future: the build date is stamped into the binary, so a release
#     pins it (a reproducible build) and it never defaults to today;
#   * THIRD-PARTY-NOTICES.txt is stale (publish.sh runs `generate-notices.mjs --check`);
#   * the release's version (<Version> in Directory.Build.props) has no upgrade fixture: tests/upgrade-fixtures/v<version>/, made by
#     `scripts/make-upgrade-fixture.sh v<version>` from a build of this release and ending at the newest migration. That is the rule that
#     keeps every later release provably able to open the data this one writes (docs/upgrade-backup.md, "The release rule");
#   * the software bill of materials cannot be produced: scripts/generate-sbom.mjs, scripts/check-sbom.mjs and the lock files they read
#     must be in the tree (docs/air-gapped.md).
#
# It then runs scripts/publish.sh all, and afterwards checks what it built: the release's CycloneDX SBOM (draughtsman-<version>.cdx.json)
# exists, is usable and is listed in SHA256SUMS, and every one of the six archives carries the same file as SBOM.cdx.json. A release
# whose SBOM is missing from any of them exits non-zero. It does not sign or notarise (PK-06), push an image or publish anything: the
# archives, the SBOM and SHA256SUMS land in artifacts/release (or --out) for a person to upload.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
# shellcheck source=lib/release-guard.sh
. "$repo_root/scripts/lib/release-guard.sh"
# shellcheck source=lib/archive-layout.sh
. "$repo_root/scripts/lib/archive-layout.sh"
# shellcheck source=lib/sbom.sh
. "$repo_root/scripts/lib/sbom.sh"

build_date=""
check_only=0
version=""
fixtures_dir="$repo_root/tests/upgrade-fixtures"
migrations_dir="$repo_root/src/Draughtsman.Storage/Migrations/Sqlite"
out_dir="$repo_root/artifacts/release"
publish_args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --build-date) build_date="${2:-}"; shift 2 ;;
    --check-only) check_only=1; shift ;;
    --version) version="${2:-}"; shift 2 ;;
    --fixtures-dir) fixtures_dir="${2:-}"; shift 2 ;;
    --migrations-dir) migrations_dir="${2:-}"; shift 2 ;;
    --out) out_dir="${2:-}"; publish_args+=(--out "${2:-}"); shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "release: unknown option $1" >&2; exit 1 ;;
  esac
done

command -v node >/dev/null || { echo "release: node is required on PATH" >&2; exit 1; }
guard_build_date "$build_date"
[ -n "$version" ] || version="$(release_version "$repo_root/Directory.Build.props")"
guard_upgrade_fixture "$version" "$fixtures_dir" "$migrations_dir"
guard_sbom_inputs "$repo_root"
echo "release: guards passed (version $version, build date $build_date, upgrade fixture v$version, SBOM inputs present)"
if [ "$check_only" = "1" ]; then exit 0; fi

"$repo_root/scripts/publish.sh" all --build-date "$build_date" ${publish_args[@]+"${publish_args[@]}"}

# What publish.sh built, checked again from the outside: the SBOM must be beside the archives and inside every one of them.
out_dir="$(cd "$out_dir" && pwd)"
guard_release_sboms "$out_dir" "$version" "$repo_root" "${ARCHIVE_RIDS[@]}"
echo "release: $version built; archives, $(basename "$(release_sbom_file "$out_dir" "$version")") and SHA256SUMS are in $out_dir"
