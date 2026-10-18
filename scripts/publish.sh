#!/usr/bin/env bash
# Builds the release archive for one platform, or all six (PK-02, PK-08, PK-10, LG-03): a self-contained draughtsman
# executable with the web app, the sidecar and an official Node 22 beside it, an annotated draughtsman.example.yaml, the
# third-party notices and the customer guides (docs/), in one folder you unpack and run.
#
#   scripts/publish.sh osx-arm64                       one platform (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64, win-arm64)
#   scripts/publish.sh all --build-date 2026-10-14     all six, with the build date pinned
#   scripts/publish.sh linux-x64 --out /tmp/archives   somewhere else than artifacts/release
#
# Options: --build-date YYYY-MM-DD (or DRAUGHTSMAN_BUILD_DATE; a release pins it so the binary is reproducible, and
# scripts/release.sh insists; here an unpinned build says so and uses today), --out DIR.
#
# Needs: dotnet (SDK in global.json, with network for the runtime packs of other platforms the first time), Node 22 with npm
# (web and sidecar are built here), curl, tar, zip (the win-* archives), and a SHA-256 tool. The Node runtime is downloaded once into
# artifacts/cache/node and checked against scripts/node-runtime.txt; nothing is committed. Every platform cross-publishes
# from any host; only the host's own platform's archive can be run here (PK-02 proof) - see docs/install.md.
#
# Output: <out>/draughtsman-<version>-<rid>.tar.gz (.zip for win-x64 and win-arm64), <out>/draughtsman-<version>.cdx.json (the CycloneDX
# software bill of materials, also inside every archive as SBOM.cdx.json: scripts/lib/sbom.sh) and <out>/SHA256SUMS. A release that cannot
# produce its SBOM, or whose web app or sidecar bundle could reach another host (scripts/check-offline-bundles.mjs), does not build.
# Signing and notarisation are not done here (PK-06): an unsigned macOS or Windows download is stopped by Gatekeeper or SmartScreen
# until they are.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source "$repo_root/scripts/lib/archive-docs.sh"
source "$repo_root/scripts/lib/archive-layout.sh"
source "$repo_root/scripts/lib/sbom.sh"

ALL_RIDS=("${ARCHIVE_RIDS[@]}")

die() { echo "publish: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }

target=""
build_date="${DRAUGHTSMAN_BUILD_DATE:-}"
out_dir="$repo_root/artifacts/release"
while [ $# -gt 0 ]; do
  case "$1" in
    --build-date) build_date="${2:-}"; shift 2 ;;
    --out) out_dir="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown option $1" ;;
    *) [ -z "$target" ] || die "one platform or 'all' at a time"; target="$1"; shift ;;
  esac
done
[ -n "$target" ] || die "say which platform: ${ALL_RIDS[*]} or all (see --help)"

if [ "$target" = "all" ]; then rids=("${ALL_RIDS[@]}"); else rids=("$target"); fi
for rid in "${rids[@]}"; do
  case " ${ALL_RIDS[*]} " in *" $rid "*) ;; *) die "unknown platform '$rid' (expected ${ALL_RIDS[*]})" ;; esac
done

if [ -z "$build_date" ]; then
  build_date="$(date -u +%Y-%m-%d)"
  echo "publish: WARNING the build date is not pinned; using today ($build_date). A release must pass --build-date." >&2
fi
case "$build_date" in
  [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]) ;;
  *) die "--build-date must be YYYY-MM-DD, got '$build_date'" ;;
esac

for tool in dotnet node npm curl tar; do command -v "$tool" >/dev/null || die "$tool is required on PATH"; done
node_major="$(node -p 'process.versions.node.split(".")[0]')"
[ "$node_major" -ge 22 ] || die "Node 22 or later is needed to build the web app and the sidecar (found $(node --version))"
if command -v sha256sum >/dev/null; then sha256() { sha256sum "$1" | cut -d' ' -f1; }; else sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }; fi

version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -1)"
[ -n "$version" ] || die "could not read <Version> from Directory.Build.props"
mkdir -p "$out_dir" "$repo_root/artifacts/cache/node" "$repo_root/artifacts/stage"
out_dir="$(cd "$out_dir" && pwd)"
stage_root="$repo_root/artifacts/stage"


step "web app (source maps off)"
(cd web && npm ci --no-audit --no-fund && npm run build)
[ -f web/dist/index.html ] || die "web build produced no web/dist/index.html"
if find web/dist -name '*.map' | grep -q .; then die "web/dist contains source maps; a release must not ship them"; fi

step "sidecar bundle (minified, licence headers kept)"
(cd sidecar && npm ci --no-audit --no-fund && DRAUGHTSMAN_RELEASE=1 node scripts/build.mjs)

step "third-party notices up to date"
dotnet restore src/Draughtsman.Server/Draughtsman.Server.csproj >/dev/null
node scripts/generate-notices.mjs --check

step "web app and sidecar reference no other host"
node scripts/check-offline-bundles.mjs all

step "software bill of materials (CycloneDX 1.5)"
sbom_release="$(release_sbom_file "$out_dir" "$version")"
generate_release_sbom "$repo_root" "$sbom_release" "$version" "$build_date" || die "no software bill of materials, no release"
echo "sbom: $sbom_release"

# Reads scripts/node-runtime.txt: prints "<file> <sha256>" for a platform.
node_entry() { awk -v rid="$1" '$1 == rid { print $2, $3 }' "$repo_root/scripts/node-runtime.txt"; }

fetch_node() {
  local rid="$1" file sum version path
  read -r file sum <<<"$(node_entry "$rid")"
  [ -n "${file:-}" ] || die "scripts/node-runtime.txt has no line for $rid"
  version="$(echo "$file" | sed -E 's/^node-v([0-9.]+)-.*/\1/')"
  path="$repo_root/artifacts/cache/node/$file"
  if [ ! -f "$path" ] || [ "$(sha256 "$path")" != "$sum" ]; then
    echo "downloading $file" >&2
    curl -fSL --retry 3 -o "$path.part" "https://nodejs.org/dist/v$version/$file" >&2
    mv "$path.part" "$path"
  fi
  if [ "$(sha256 "$path")" != "$sum" ]; then
    rm -f "${path:?}"
    die "$file does not match the SHA-256 pinned in scripts/node-runtime.txt; refusing to bundle it"
  fi
  echo "$path"
}

# Puts node (and its licence) into $2 (the sidecar folder) from the verified download $1.
unpack_node() {
  local archive="$1" dest="$2" tmp root_dir
  tmp="$(mktemp -d)"
  case "$archive" in
    *.zip) unzip -q "$archive" -d "$tmp" ;;
    *) tar -xf "$archive" -C "$tmp" ;;
  esac
  root_dir="$(find "$tmp" -mindepth 1 -maxdepth 1 -type d | head -1)"
  if [ -f "$root_dir/node.exe" ]; then cp "$root_dir/node.exe" "$dest/node.exe"; else cp "$root_dir/bin/node" "$dest/node"; chmod 755 "$dest/node"; fi
  cp "$root_dir/LICENSE" "$dest/LICENSE-node.txt"
  rm -rf "${tmp:?}"
}

# The .NET runtime's own list of the software inside it, which a self-contained publish redistributes.
dotnet_notices() {
  local rid="$1" pack dir f
  for pack in "microsoft.netcore.app.runtime.$rid" "microsoft.aspnetcore.app.runtime.$rid"; do
    for dir in "$HOME/.nuget/packages/$pack"/*/; do
      for f in "${dir}THIRD-PARTY-NOTICES.TXT" "${dir}THIRD-PARTY-NOTICES.txt" "${dir}ThirdPartyNotices.txt"; do
        if [ -f "$f" ]; then echo "$f"; return 0; fi
      done
    done
  done
  return 1
}

for rid in "${rids[@]}"; do
  step "publish $rid"
  stage="$stage_root/draughtsman-$version-$rid"
  rm -rf "${stage:?}"
  dotnet publish src/Draughtsman.Server/Draughtsman.Server.csproj -c Release -r "$rid" -o "$stage" \
    -p:BuildSidecar=false -p:DraughtsmanBuildDate="$build_date" -p:DebugType=None -p:DebugSymbols=false

  cp -R web/dist "$stage/wwwroot"
  cp LICENSE "$stage/LICENSE"
  cp THIRD-PARTY-NOTICES.txt "$stage/THIRD-PARTY-NOTICES.txt"
  cp THIRD-PARTY-NOTICES.txt "$stage/wwwroot/THIRD-PARTY-NOTICES.txt"
  cp "$sbom_release" "$stage/$SBOM_ARCHIVE_NAME"
  cp docs/draughtsman.example.yaml "$stage/draughtsman.example.yaml"
  # The customer guides (docs/, SKILL.md, CHANGELOG.md, SECURITY.md) and INSTALL.md: scripts/lib/archive-docs.sh.
  add_archive_docs "$repo_root" "$stage"
  for legal_file in LICENSE LICENSE.txt LICENSE.md EULA.txt EULA.md; do
    if [ -f "$legal_file" ]; then cp "$legal_file" "$stage/$legal_file"; fi
  done
  # dotnet publish already placed sidecar/sidecar.js (from sidecar/dist); the release bundle was built above.
  [ -f "$stage/sidecar/sidecar.js" ] || die "$rid: sidecar/sidecar.js is missing from the publish output"
  cmp -s sidecar/dist/sidecar.js "$stage/sidecar/sidecar.js" || die "$rid: sidecar.js in the archive is not the release build"
  # What is in the archive, not only what was built: no other host in the web app or the sidecar it carries.
  node scripts/check-offline-bundles.mjs web "$stage/wwwroot" || die "$rid: the archive's web app could reach another host"
  node scripts/check-offline-bundles.mjs sidecar "$stage/sidecar/sidecar.js" || die "$rid: the archive's sidecar could reach another host"
  unpack_node "$(fetch_node "$rid")" "$stage/sidecar"
  if notices="$(dotnet_notices "$rid")"; then
    cp "$notices" "$stage/DOTNET-THIRD-PARTY-NOTICES.txt"
  else
    die "$rid: the .NET runtime pack's THIRD-PARTY-NOTICES file was not found in ~/.nuget/packages"
  fi

  node_file="$(node_entry "$rid" | cut -d' ' -f1)"
  {
    echo "Draughtsman $version"
    echo "platform:   $rid"
    echo "build date: $build_date"
    echo "node:       $(echo "$node_file" | sed -E 's/^node-(v[0-9.]+)-.*/\1/')"
  } >"$stage/BUILDINFO.txt"

  # PDF export's driver (.playwright/) must be real files beside the executable, whole and runnable by any account (QA B1): the
  # csproj keeps it out of the single-file bundle, this fixes the modes NuGet's cache gave it and refuses an archive without it.
  normalise_playwright_modes "$stage" "$rid"
  verify_playwright_driver "$stage" "$rid" "$(playwright_version "$repo_root")" || die "$rid: the PDF driver is not laid out as the server needs it (see above)"
  smoke_playwright_driver "$stage" "$rid" || die "$rid: the shipped PDF driver does not run"

  # What must not be in a customer's folder, and what must be.
  if find "$stage" \( -name '*.map' -o -name '*.pdb' \) | grep -q .; then die "$rid: source maps or debug symbols in the archive"; fi
  # dotnet publish's IIS leftovers (web.config, the in-process hosting module) are not part of this product's layout (D9).
  verify_no_iis_assets "$stage" "$rid" || die "$rid: the archive carries IIS in-process hosting files (see above)"
  for required in wwwroot/index.html sidecar/sidecar.js THIRD-PARTY-NOTICES.txt "$SBOM_ARCHIVE_NAME" draughtsman.example.yaml sidecar/LICENSE-node.txt INSTALL.md docs/index.md docs/upgrade-backup.md docs/troubleshooting.md docs/admin-guide.md SECURITY.md; do
    [ -e "$stage/$required" ] || die "$rid: $required is missing from the archive"
  done

  base="draughtsman-$version-$rid"
  rm -f "${out_dir:?}/${base:?}.tar.gz" "${out_dir:?}/${base:?}.zip"
  if is_windows_rid "$rid"; then
    command -v zip >/dev/null || die "zip is required for the $rid archive"
    (cd "$stage_root" && zip -qr "$out_dir/$base.zip" "$base")
    archive="$out_dir/$base.zip"
  else
    (cd "$stage_root" && COPYFILE_DISABLE=1 tar -czf "$out_dir/$base.tar.gz" "$base")
    archive="$out_dir/$base.tar.gz"
  fi
  guard_archive_sbom "$archive" "$base" "$sbom_release" "$version" || die "$rid: the archive does not carry the release's software bill of materials"
  echo "archive: $archive ($(du -h "$archive" | cut -f1))"
done

step "checksums"
(cd "$out_dir" && for f in draughtsman-*.tar.gz draughtsman-*.zip draughtsman-*.cdx.json; do if [ -f "$f" ]; then echo "$(sha256 "$f")  $f"; fi; done) | sort -k2 >"$out_dir/SHA256SUMS"
cat "$out_dir/SHA256SUMS"
