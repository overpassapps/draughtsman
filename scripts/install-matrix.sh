#!/bin/sh
# The install matrix entry point: see scripts/install-matrix/matrix.sh --help and docs/testing-installs.md. Not part of verify-local.sh (it needs Docker and takes a while).
exec "$(cd "$(dirname "$0")" && pwd)/install-matrix/matrix.sh" "$@"
