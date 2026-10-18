# ADR 0001: Runtime and release targets

**Status:** Accepted, 2026-09-20

## Context

The build spec (section 7) describes one self-contained server executable that serves the SPA, REST API, WebSocket sync and MCP server, and that runs unchanged as a Docker container, a systemd service, a launchd service or a Windows service. The spec as written named .NET 9 and treated Windows as the primary bare-metal target. The maintainer has since decided that Linux, macOS and Windows are equal release targets and that the runtime should be the current LTS.

The operator installs this on their own tin, often air-gapped, often audited. Anything that depends on a host-installed runtime, a Windows-only API or a platform-specific rendering stack turns a five-minute install into a support ticket.

## Decision

1. **Runtime: .NET 10 (LTS).** The SDK is pinned in `global.json` to `10.0.100` with `rollForward: latestFeature`, so any 10.0.x feature band builds the repo and a machine with only 10.0.100 is not forced to upgrade. Language version follows the SDK default.
2. **Release targets, all equal:** `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `win-x64`. Every release publishes all five. No target is a port; a bug on one blocks the release on all.
3. **Publish mode:** self-contained, single-file (`PublishSingleFile=true`, `SelfContained=true`, runtime bundled). Nothing is installed on the host. Native assets that cannot be bundled (the Node sidecar, see ADR 0002) sit next to the executable inside the release archive.
4. **No Windows-only APIs.** Specifically:
   - No `System.Drawing` or `System.Drawing.Common`. Any server-side raster work uses a cross-platform library (SkiaSharp) or the sidecar's headless Chromium, never GDI+.
   - No registry access, no Windows event log, no Windows-integrated authentication assumptions. Configuration comes from `draughtsman.yaml` and environment variables only.
   - File paths are built with `Path.Combine`, `Path.GetFullPath` and friends, never string concatenation with `\`. Code must behave correctly on a case-sensitive file system (Linux) and a case-insensitive one (macOS default, Windows): never rely on case to distinguish two files, and compare paths with `StringComparison.Ordinal` after normalisation.
   - Line endings in generated text are `\n`. Repository files are normalised to LF via `.gitattributes`.
   - Process spawning (the sidecar) uses `ProcessStartInfo` with explicit argument lists, never a shell string.
5. **CI matrix:** `ubuntu-latest`, `macos-latest`, `windows-latest`, each running `dotnet build`, `dotnet test`, and the web `npm ci`, `npm run lint`, `npm run build`, `npm test`. A release gate job depends on all three legs; if any leg fails the release is blocked. `fail-fast` is off so a failure on one OS does not hide a different failure on another.
6. **Node 22 LTS** is the runtime for the web build (`web/`) and for the bundled sidecar (`sidecar/`). It is a build-time dependency for the SPA and a bundled runtime dependency for the sidecar; it is never something the customer installs.
7. **Docker images** are built for `linux/amd64` and `linux/arm64` from the same published binaries.

## Alternatives considered

- **.NET 9 (as the spec wrote it).** Rejected: STS, out of support before M4 would plausibly finish its month of dogfooding. .NET 10 is LTS and was already available when the project started.
- **Windows-first with Linux and macOS as ports.** Rejected: the Docker host is the most common install target for the operator, and the maintainers develop on macOS. Treating any of the three as secondary means bugs land there first and get found by customers.
- **Framework-dependent publish (smaller artefact).** Rejected: it requires the customer to install and patch a .NET runtime, breaking the "nothing else installed" promise and complicating audit conversations.
- **Native AOT.** Deferred, not rejected: it would shrink startup and artefact size, but EF Core, YamlDotNet reflection and ASP.NET Core minimal API trimming warnings need work first. Revisit after M3 once the dependency set is stable. Nothing in this ADR precludes it.

## Consequences

- Every new NuGet dependency is checked for a `System.Drawing` transitive reference and for native binaries covering all five RIDs before it is added.
- `Directory.Build.props` turns on nullable reference types and warnings-as-errors for every project so platform analyser warnings (CA1416 platform compatibility) fail the build rather than being ignored.
- The release archive per RID will be larger than a bare .NET binary because it carries the sidecar runtime (ADR 0002). That is an accepted cost of the install promise.
- Developers need the .NET 10 SDK and Node 22 locally; `README.md` records the exact commands.
