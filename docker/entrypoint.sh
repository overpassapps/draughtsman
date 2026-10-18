#!/bin/sh
# Seeds a default draughtsman.yaml into the volume-mounted data folder only when it is missing, so a fresh install
# gets the image's build-time choices (Chromium enabled when INSTALL_CHROMIUM=true built it) without ever
# overwriting a config an operator has already saved there.
set -eu

mkdir -p "$DRAUGHTSMAN_DATA"

if [ -f /app/.chromium-enabled ] && [ ! -f "$DRAUGHTSMAN_DATA/draughtsman.yaml" ]; then
  cp /app/draughtsman.chromium-default.yaml "$DRAUGHTSMAN_DATA/draughtsman.yaml"
fi

exec dotnet /app/draughtsman.dll "$@"
