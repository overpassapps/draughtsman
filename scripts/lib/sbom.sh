#!/usr/bin/env bash
# The software bill of materials every release carries (sourced by scripts/publish.sh, scripts/release.sh and scripts/docker-export.sh;
# driven by scripts/sbom.test.sh). One CycloneDX 1.5 JSON file per release, written by scripts/generate-sbom.mjs from what
# `dotnet restore` and `npm ci` already put on disk (the NuGet closure of the server, the npm packages of the web app and the sidecar,
# the bundled Node runtime) and checked by scripts/check-sbom.mjs so a hollow one cannot ship. It lands in three places:
#   <out>/draughtsman-<version>.cdx.json   beside the archives, listed in SHA256SUMS
#   <archive>/SBOM.cdx.json                inside every release archive (the same bytes)
#   /app/SBOM.cdx.json                     inside the Docker image (the Dockerfile generates and checks it)
# Each function prints why it refused on stderr and returns non-zero.

SBOM_ARCHIVE_NAME="SBOM.cdx.json"
SBOM_SCRIPTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# release_sbom_file OUT_DIR VERSION: where the release's own copy sits, next to the archives.
release_sbom_file() {
  echo "$1/draughtsman-$2.cdx.json"
}

# guard_sbom_inputs REPO: the generator, the checker and everything the generator reads are in the tree. Cheap, needs no restore, so
# release.sh --check-only can run it; a tree that cannot produce an SBOM must not start building archives.
guard_sbom_inputs() {
  local repo="$1" needed bad=0
  for needed in scripts/generate-sbom.mjs scripts/check-sbom.mjs scripts/node-runtime.txt web/package-lock.json sidecar/package-lock.json Directory.Build.props; do
    if [ ! -f "$repo/$needed" ]; then
      echo "release: $needed is missing, so this release cannot carry a software bill of materials" >&2
      bad=1
    fi
  done
  [ "$bad" = 0 ] || return 1
  for needed in scripts/generate-sbom.mjs scripts/check-sbom.mjs; do
    if ! node --check "$repo/$needed" 2>/dev/null; then
      echo "release: $needed does not parse" >&2
      return 1
    fi
  done
}

# generate_release_sbom REPO OUT VERSION [BUILD_DATE]: writes OUT and checks it against the sources. Needs `dotnet restore` of the server
# first (publish.sh has run it). With a build date the SBOM is stamped at that date, midnight UTC, so the same inputs give the same file.
generate_release_sbom() {
  local repo="$1" out="$2" version="$3" build_date="${4:-}"
  local stamp=()
  [ -z "$build_date" ] || stamp=(--timestamp "${build_date}T00:00:00Z")
  if ! node "$SBOM_SCRIPTS_DIR/generate-sbom.mjs" --out "$out" ${stamp[@]+"${stamp[@]}"}; then
    echo "release: scripts/generate-sbom.mjs failed; a release is not cut without its software bill of materials" >&2
    return 1
  fi
  node "$SBOM_SCRIPTS_DIR/check-sbom.mjs" "$out" --version "$version" --repo "$repo" >&2
}

# sbom_from_archive ARCHIVE BASE: prints the SBOM inside ARCHIVE (whose single top folder is BASE) on standard output.
sbom_from_archive() {
  case "$1" in
    *.zip) unzip -p "$1" "$2/$SBOM_ARCHIVE_NAME" ;;
    *) tar -xOzf "$1" "$2/$SBOM_ARCHIVE_NAME" ;;
  esac
}

# guard_archive_sbom ARCHIVE BASE RELEASE_SBOM [VERSION]: the archive holds an SBOM, byte for byte the release's, and it is usable.
guard_archive_sbom() {
  local archive="$1" base="$2" release="$3" version="${4:-}" tmp status=0
  if [ ! -f "$archive" ]; then
    echo "release: $archive was not built" >&2
    return 1
  fi
  tmp="$(mktemp "${TMPDIR:-/tmp}/sbom-in-archive.XXXXXX")"
  if ! sbom_from_archive "$archive" "$base" >"$tmp" 2>/dev/null || [ ! -s "$tmp" ]; then
    echo "release: $(basename "$archive") carries no $base/$SBOM_ARCHIVE_NAME (or it is empty): every release archive must ship its software bill of materials" >&2
    status=1
  elif ! cmp -s "$tmp" "$release"; then
    echo "release: the $SBOM_ARCHIVE_NAME inside $(basename "$archive") is not the release's $(basename "$release")" >&2
    status=1
  elif ! node "$SBOM_SCRIPTS_DIR/check-sbom.mjs" "$tmp" ${version:+--version "$version"} >/dev/null 2>"$tmp.err"; then
    echo "release: the $SBOM_ARCHIVE_NAME inside $(basename "$archive") is not usable: $(tr '\n' ' ' <"$tmp.err")" >&2
    status=1
  fi
  rm -f "$tmp" "$tmp.err"
  return "$status"
}

# guard_release_sboms OUT_DIR VERSION REPO RID...: after the archives are built. The release's SBOM exists, is usable, is listed in
# SHA256SUMS, and every platform's archive carries it. REPO may be empty to skip the cross-check against the sources.
guard_release_sboms() {
  local out="$1" version="$2" repo="$3" rid release base archive bad=0
  shift 3
  release="$(release_sbom_file "$out" "$version")"
  if [ ! -s "$release" ]; then
    echo "release: $release is missing: the release has no software bill of materials" >&2
    return 1
  fi
  if ! node "$SBOM_SCRIPTS_DIR/check-sbom.mjs" "$release" --version "$version" ${repo:+--repo "$repo"} >/dev/null 2>"$release.err"; then
    echo "release: $(basename "$release") is not usable: $(tr '\n' ' ' <"$release.err")" >&2
    rm -f "$release.err"
    return 1
  fi
  rm -f "$release.err"
  if ! awk -v name="$(basename "$release")" '$2 == name { found = 1 } END { exit !found }' "$out/SHA256SUMS" 2>/dev/null; then
    echo "release: $out/SHA256SUMS does not list $(basename "$release")" >&2
    bad=1
  fi
  for rid in "$@"; do
    base="draughtsman-$version-$rid"
    case "$rid" in win-*) archive="$out/$base.zip" ;; *) archive="$out/$base.tar.gz" ;; esac
    guard_archive_sbom "$archive" "$base" "$release" "$version" || bad=1
  done
  return "$bad"
}

# guard_image_sbom IMAGE: the Docker image carries /app/SBOM.cdx.json and it is usable. Runs `cat` in the image (no network, nothing
# started but that one command), so an image built before the SBOM existed, or one whose build skipped it, is refused.
guard_image_sbom() {
  local image="$1" tmp status=0
  tmp="$(mktemp "${TMPDIR:-/tmp}/sbom-in-image.XXXXXX")"
  if ! docker run --rm --network none --entrypoint cat "$image" "/app/$SBOM_ARCHIVE_NAME" >"$tmp" 2>/dev/null || [ ! -s "$tmp" ]; then
    echo "docker-export: image '$image' carries no /app/$SBOM_ARCHIVE_NAME: rebuild it from a checkout that has the software bill of materials step" >&2
    status=1
  elif ! node "$SBOM_SCRIPTS_DIR/check-sbom.mjs" "$tmp" >/dev/null 2>"$tmp.err"; then
    echo "docker-export: the SBOM in image '$image' is not usable: $(tr '\n' ' ' <"$tmp.err")" >&2
    status=1
  fi
  rm -f "$tmp" "$tmp.err"
  return "$status"
}
