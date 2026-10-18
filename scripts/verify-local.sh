#!/usr/bin/env bash
# Runs everything the CI macOS leg runs (.github/workflows/ci.yml), in the same order,
# on this Mac. See AGENTS.md "Local verification" for why this exists.
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root" || exit 1

log_dir="$(mktemp -d "${TMPDIR:-/tmp}/verify-local.XXXXXX")"
echo "Full logs: $log_dir"

step_num=0

# Runs "$@" (optionally in directory $dir), logs its output, prints a PASS/FAIL line,
# and exits the whole script non-zero on the first failure.
step() {
  local dir="$1" name="$2" log_file slug
  shift 2
  step_num=$((step_num + 1))
  slug="$(printf '%s' "$name" | tr -c 'A-Za-z0-9' '_')"
  log_file="$log_dir/$(printf '%02d' "$step_num")-${slug}.log"

  if ( cd "$dir" && "$@" ) >"$log_file" 2>&1; then
    echo "PASS: $name"
  else
    echo "FAIL: $name (log: $log_file)"
    exit 1
  fi
}

step . "dotnet restore" dotnet restore

# Also runs npm ci and the esbuild bundle in sidecar/ (src/Draughtsman.Sidecar/Draughtsman.Sidecar.csproj).
step . "dotnet build" dotnet build --no-restore --configuration Release

if [ -n "${DRAUGHTSMAN_TEST_POSTGRES:-}" ]; then
  echo "DRAUGHTSMAN_TEST_POSTGRES is set: the Postgres storage suite will run too."
fi
step . "dotnet test" dotnet test --no-build --configuration Release

step . "dotnet format --verify-no-changes" dotnet format --verify-no-changes

step web "npm ci" npm ci
step web "npm run lint" npm run lint
step web "npm run build" npm run build
step web "npm test" npm test

# node_modules and dist already exist from the dotnet build step above; this runs the
# sidecar's own Vitest suite the same way CI's "npm test (sidecar)" step does.
step sidecar "npm test (sidecar)" npm test

# Packaging scripts (release guards, offline image export) and the notices file the archives and image ship.
step . "third-party notices are current" node scripts/generate-notices.mjs --check
step . "release guard tests (version needs an upgrade fixture)" scripts/release.test.sh
step . "upgrade fixture scripts parse" bash -c 'set -e; for f in scripts/upgrade-fixture/*.mjs; do node --check "$f"; done; bash -n scripts/make-upgrade-fixture.sh; bash -n scripts/upgrade-path-proof.sh; bash -n scripts/dataprotection-pin-proof.sh; bash -n scripts/dataprotection-pin-proof-docker.sh; scripts/make-upgrade-fixture.sh --help >/dev/null; scripts/upgrade-path-proof.sh --help >/dev/null; scripts/dataprotection-pin-proof.sh --help >/dev/null; scripts/dataprotection-pin-proof-docker.sh --help >/dev/null'
step . "docker-export tests" scripts/docker-export.test.sh
step . "archive docs tests" scripts/archive-docs.test.sh
step . "archive layout tests (PDF driver)" scripts/archive-layout.test.sh

# The air-gap proof (docs/air-gapped.md): what ships to a browser and to the Node sidecar can reach no other host, and every release carries
# its software bill of materials. The bundles exist from the dotnet build and the web build above; the SBOM scripts need the dotnet restore.
step . "web app and sidecar bundles reference no other host" node scripts/check-offline-bundles.mjs all
step . "offline-bundle checker, SBOM checker and SBOM generator tests" node --test scripts/check-offline-bundles.test.mjs scripts/check-sbom.test.mjs scripts/generate-sbom.test.mjs
step . "SBOM guard tests (an archive without its SBOM fails the release)" scripts/sbom.test.sh
step . "offline smoke scripts parse" bash -c 'set -e; bash -n scripts/docker-offline-smoke.sh; node --check scripts/lib/offline-smoke-client.mjs'

# The install matrix (docs/testing-installs.md) itself needs Docker and takes minutes, so it is NOT run here; its pure parts (report formatter, version
# comparison, redaction, expectations, cookie jar, proxy rules, zip reader) are unit-tested, and its scripts must at least parse.
step . "install matrix: unit tests and script syntax (no Docker)" bash -c 'set -e; node --test scripts/install-matrix/test/*.test.mjs; for f in scripts/install-matrix/*.mjs scripts/install-matrix/lib/*.mjs; do node --check "$f"; done; sh -n scripts/install-matrix/matrix.sh; sh -n scripts/install-matrix.sh; scripts/install-matrix.sh --list >/dev/null'

echo "All steps passed."
