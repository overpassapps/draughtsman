# syntax=docker/dockerfile:1
#
# Multi-stage build: sidecar and web are bundled by their own Node toolchains, then the .NET SDK stage publishes the
# server framework-dependent (no -r), so the same publish output runs under either arch's aspnet runtime image and
# buildx can produce x64+arm64 from one Dockerfile without per-arch publish steps (see README's release command).
#
# Chromium for PDF (build-spec-v0.1.md section 7) is optional and off by default: pass
# --build-arg INSTALL_CHROMIUM=true to install it and enable it for a data folder that doesn't already have a
# draughtsman.yaml. It is never forced onto an existing config (see docker/entrypoint.sh).

FROM node:22-bookworm-slim AS sidecar-build
# route.document bundles the editor's router from web/src (canvas/routeDocument.ts) rather than copying it, so the
# sources it reaches must sit alongside sidecar/ as they do in the repo: web/src, and the kind vocabularies its model
# modules import as JSON. No web dependencies are installed; scripts/build.mjs aliases yjs to the sidecar's own.
COPY web/src/ /src/web/src/
COPY src/Draughtsman.Core/Schemas/ /src/src/Draughtsman.Core/Schemas/
WORKDIR /src/sidecar
COPY sidecar/package.json sidecar/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY sidecar/ ./
# A release build: minified, licence headers kept (scripts/build.mjs).
RUN DRAUGHTSMAN_RELEASE=1 node scripts/build.mjs
# The air-gap guarantee, enforced where the bundle is made: a sidecar that imports a network module, or names an external address
# that is not on the reviewed list, fails the image build (docs/air-gapped.md).
COPY scripts/check-offline-bundles.mjs /src/scripts/check-offline-bundles.mjs
RUN node /src/scripts/check-offline-bundles.mjs sidecar /src/sidecar/dist/sidecar.js

FROM node:22-bookworm-slim AS web-build
WORKDIR /src
# The type check and generated example fixtures reach three levels up from web/src for the shared document schema,
# kind vocabularies and examples (web/tsconfig paths), so those repo-root folders must sit alongside web/ here too.
COPY themes/ themes/
COPY docs/examples/ docs/examples/
COPY docs/schema/ docs/schema/
COPY src/Draughtsman.Core/Schemas/ src/Draughtsman.Core/Schemas/
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build
# The same guarantee for the web app: nothing in web/dist loads from, or names, a host that is not on the reviewed list.
COPY scripts/check-offline-bundles.mjs /src/scripts/check-offline-bundles.mjs
RUN node /src/scripts/check-offline-bundles.mjs web /src/web/dist

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
# The date this build was made, shown on the About page. A release passes --build-arg DRAUGHTSMAN_BUILD_DATE=YYYY-MM-DD;
# empty means today, UTC.
ARG DRAUGHTSMAN_BUILD_DATE=
WORKDIR /src
COPY global.json Directory.Build.props Draughtsman.sln ./
COPY src/ src/
COPY themes/ themes/
# Draughtsman.Core.csproj embeds the shared schema from here as an EmbeddedResource.
COPY docs/schema/ docs/schema/
# ...and the DSL grammar from here (DslGrammar). Every file a csproj embeds from outside its own folder must be copied
# in this stage; tests/Draughtsman.Core.Tests/DockerfileBuildContextTests.cs fails when one is missing.
COPY docs/spec/dsl.grammar docs/spec/dsl.grammar
COPY --from=sidecar-build /src/sidecar/dist sidecar/dist
# Only to write the software bill of materials below (scripts/generate-sbom.mjs reads this stage's NuGet restore, and the two lock
# files); the SDK image has no Node, and the publish itself still needs none.
COPY --from=node:22-bookworm-slim /usr/local/bin/node /usr/local/bin/node
COPY scripts/generate-sbom.mjs scripts/check-sbom.mjs scripts/node-runtime.txt scripts/
COPY web/package.json web/package-lock.json web/
COPY sidecar/package.json sidecar/package-lock.json sidecar/
# BuildSidecar=false: the bundle above is already built, so this stage never needs Node.
RUN dotnet publish src/Draughtsman.Server/Draughtsman.Server.csproj -c Release -o /app -p:BuildSidecar=false \
    ${DRAUGHTSMAN_BUILD_DATE:+-p:DraughtsmanBuildDate=$DRAUGHTSMAN_BUILD_DATE}
COPY --from=web-build /src/web/dist /app/wwwroot
# Third-party licence notices (scripts/generate-notices.mjs; publish.sh and verify-local.sh keep the file current): in the
# image root and served by the running instance at /THIRD-PARTY-NOTICES.txt. The annotated config reference rides along.
COPY THIRD-PARTY-NOTICES.txt /app/THIRD-PARTY-NOTICES.txt
COPY THIRD-PARTY-NOTICES.txt /app/wwwroot/THIRD-PARTY-NOTICES.txt
COPY docs/draughtsman.example.yaml /app/draughtsman.example.yaml
# The CycloneDX 1.5 software bill of materials: the server's NuGet closure (this stage's restore), the npm packages of the web app and the
# sidecar, and the Node runtime. Checked against the sources before the image can exist: a hollow one fails the build. It is /app/SBOM.cdx.json,
# the same name as in every release archive; scripts/docker-export.sh refuses an image that lacks it.
RUN node scripts/generate-sbom.mjs --timestamp "${DRAUGHTSMAN_BUILD_DATE:-$(date -u +%Y-%m-%d)}T00:00:00Z" --out /app/SBOM.cdx.json \
    && node scripts/check-sbom.mjs /app/SBOM.cdx.json --version "$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)" --repo /src

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG INSTALL_CHROMIUM=false

# Loopback-by-default (DraughtsmanConfig.ServerConfig.Host) is untouched; ASPNETCORE_URLS is the existing override
# Program.cs already checks first, so binding every interface in the container needs no code or default change.
ENV ASPNETCORE_URLS=http://0.0.0.0:5180 \
    DRAUGHTSMAN_DATA=/data \
    PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright

# Node 22 on PATH: the server spawns it as the ELK/Yjs sidecar child process (ADR 0002); SidecarLocator falls back to
# PATH when no bundled runtime sits next to the binary.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -g 10001 draughtsman \
    && useradd -u 10001 -g draughtsman -M -s /usr/sbin/nologin draughtsman \
    && mkdir -p /data \
    && chmod 0700 /data

WORKDIR /app
COPY --from=publish /app ./

# Optional Chromium component (README's "PDF export" section documents the non-Docker equivalent of this install).
# The runtime image has no .NET SDK, so `dotnet tool install Microsoft.Playwright.CLI` cannot run here; the npm
# `playwright` package at the same version drives the identical browser download into $PLAYWRIGHT_BROWSERS_PATH,
# which the referenced Microsoft.Playwright 1.63.0 NuGet package (Draughtsman.Server.csproj) then finds at runtime.
RUN if [ "$INSTALL_CHROMIUM" = "true" ]; then \
      npx --yes playwright@1.63.0 install --with-deps chromium \
      && touch /app/.chromium-enabled; \
    fi
COPY docker/draughtsman.chromium-default.yaml /app/draughtsman.chromium-default.yaml
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
# `draughtsman` on PATH, so `docker exec <container> draughtsman backup` works; the long `dotnet /app/draughtsman.dll` form still does.
COPY docker/draughtsman /usr/local/bin/draughtsman
# /data is created 0700 above and owned by the runtime user here, before VOLUME: a named volume copies the image folder's
# mode and owner on first use, so it starts owner-only and the server's open-to-other-users warning never fires.
RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/draughtsman \
    && chown -R draughtsman:draughtsman /app /data

USER draughtsman
EXPOSE 5180
VOLUME ["/data"]
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
