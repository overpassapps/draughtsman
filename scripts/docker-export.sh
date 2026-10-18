#!/usr/bin/env bash
# Packs an already-built Draughtsman image for a machine with no internet (PK-10): `docker save`, compressed, with a
# checksum and the two commands to run on the other side. Building the image needs the internet (base images, Node,
# npm); running it does not, so the image is built where there is a connection and carried across as a file.
#
#   scripts/docker-export.sh draughtsman:0.1.0                 writes artifacts/offline/draughtsman-0.1.0-image.tar.gz (+ .sha256)
#   scripts/docker-export.sh registry.example/draughtsman:0.1.0 --out /media/usb
#   scripts/docker-export.sh draughtsman:0.1.0 --dry-run       print what it would do; needs no Docker
#
# It only reads the local image store (`docker image inspect`, `docker save`, and one `cat` of /app/SBOM.cdx.json run in the image with
# no network); it never builds, pulls or pushes. An image that carries no software bill of materials is refused: the file leaves
# the build machine only with its SBOM (scripts/lib/sbom.sh, docs/air-gapped.md).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/sbom.sh
source "$repo_root/scripts/lib/sbom.sh"
die() { echo "docker-export: $*" >&2; exit 1; }

image=""
out_dir="$repo_root/artifacts/offline"
dry_run=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out_dir="${2:-}"; shift 2 ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown option $1" ;;
    *) [ -z "$image" ] || die "one image at a time"; image="$1"; shift ;;
  esac
done
[ -n "$image" ] || die "say which image to export, for example draughtsman:0.1.0 (see --help)"

# registry.example:5000/team/draughtsman:0.1.0 -> draughtsman-0.1.0
name="${image##*/}"
repo_name="${name%%[:@]*}"
tag="latest"
case "$name" in *:*) tag="${name#*:}" ;; esac
tag="${tag//[^A-Za-z0-9._-]/_}"
base="$repo_name-$tag"
archive="$out_dir/$base-image.tar.gz"

if command -v sha256sum >/dev/null; then sha256() { sha256sum "$1" | cut -d' ' -f1; }; else sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }; fi

if [ "$dry_run" = "1" ]; then
  echo "would run: docker image inspect $image"
  echo "would run: docker run --rm --network none --entrypoint cat $image /app/$SBOM_ARCHIVE_NAME   (the image must carry its software bill of materials)"
  echo "would run: docker save $image | gzip > $archive"
  echo "would write: $archive.sha256"
  exit 0
fi

command -v docker >/dev/null || die "docker is required on PATH"
docker image inspect "$image" >/dev/null 2>&1 || die "image '$image' is not in the local Docker store; build it first (docker build -t $image .) or pull it"
guard_image_sbom "$image" || exit 1
mkdir -p "$out_dir"
echo "saving $image"
docker save "$image" | gzip >"$archive.part"
mv "$archive.part" "$archive"
echo "$(sha256 "$archive")  $(basename "$archive")" >"$archive.sha256"

echo "wrote $archive ($(du -h "$archive" | cut -f1))"
echo "wrote $archive.sha256"
cat <<LOAD

On the offline host, check and load it:
  shasum -a 256 -c $(basename "$archive").sha256      # or sha256sum -c
  docker load -i $(basename "$archive")
  docker run -d --name draughtsman -p 127.0.0.1:5180:5180 -v draughtsman-data:/data $image
LOAD
