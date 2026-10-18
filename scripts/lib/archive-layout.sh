#!/usr/bin/env bash
# What a release archive's folder must hold for server-side PDF export to work with nothing but a config change (sourced by
# scripts/publish.sh; driven by scripts/archive-layout.test.sh). QA B1: the Playwright driver (a Node runtime and the playwright
# package, the .playwright/ folder) was swallowed by the single-file executable and self-extracted without its execute bit and
# without the package folder, so PDF could not be enabled on the shipped archive. The fix keeps the folder beside the
# executable, whole; this is the check that it stays that way.

# The six platforms a release archive is built for, in the order publish.sh builds them (and release.sh checks them).
ARCHIVE_RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64)

# is_windows_rid RID: win-x64 and win-arm64 (zip archives, node.exe, no execute bits).
is_windows_rid() {
  case "$1" in win-*) return 0 ;; *) return 1 ;; esac
}

# playwright_node_relpath RID: where Microsoft.Playwright's build targets put the platform's Node runtime, under .playwright/.
playwright_node_relpath() {
  case "$1" in
    osx-arm64) echo "node/darwin-arm64/node" ;;
    osx-x64) echo "node/darwin-x64/node" ;;
    linux-x64) echo "node/linux-x64/node" ;;
    linux-arm64) echo "node/linux-arm64/node" ;;
    # Microsoft.Playwright ships no Arm64 Windows driver: win-arm64 carries the x64 one, which Windows 11 on Arm runs under its
    # x64 emulation (verified on a real Arm64 Windows 11: docs/install-windows.md), and Playwright itself asks for win32_x64 on Windows.
    win-x64|win-arm64) echo "node/win32_x64/node.exe" ;;
    *) return 1 ;;
  esac
}

# playwright_version REPO_ROOT: the version Draughtsman.Server.csproj pins (the one place it is written).
playwright_version() {
  sed -n 's|.*<PlaywrightVersion>\(.*\)</PlaywrightVersion>.*|\1|p' "$1/src/Draughtsman.Server/Draughtsman.Server.csproj" | head -1
}

# normalise_playwright_modes STAGE RID: NuGet's cache holds the Node runtime as rwxr--r-- (owner-only execute), which a service
# account other than the extracting user cannot run, and tar keeps modes. Everyone may read and run it.
normalise_playwright_modes() {
  local stage="$1" rid="$2" node
  node="$stage/.playwright/$(playwright_node_relpath "$rid")"
  [ -f "$node" ] || return 0
  if ! is_windows_rid "$rid"; then chmod 755 "$node"; fi
  find "$stage/.playwright" -type d -exec chmod 755 {} +
  find "$stage/.playwright" -type f ! -path "$node" -exec chmod 644 {} +
}

# verify_playwright_driver STAGE RID [PLAYWRIGHT_VERSION]: prints one line per defect and returns 1 if any. Pure file checks, so
# it runs for every platform's archive from any host.
verify_playwright_driver() {
  local stage="$1" rid="$2" want="${3:-}" rel pw node pkg bad=0 mode got
  rel="$(playwright_node_relpath "$rid")" || { echo "archive layout: unknown platform '$rid'"; return 1; }
  pw="$stage/.playwright"
  node="$pw/$rel"
  pkg="$pw/package"
  note() { echo "archive layout ($rid): $*"; bad=1; }

  [ -d "$pw" ] || { note ".playwright/ is missing, so PDF export has no driver"; return 1; }

  if [ ! -f "$node" ]; then
    note ".playwright/$rel is missing (the Node runtime of the PDF driver; it must sit beside the executable, not inside it)"
  elif [ ! -s "$node" ]; then
    note ".playwright/$rel is empty"
  elif ! is_windows_rid "$rid"; then
    # Every execute bit: a service account that did not unpack the archive must be able to run it (QA B1: mode 644, Permission denied).
    mode="$(stat -c '%a' "$node" 2>/dev/null || stat -f '%Lp' "$node")"
    if [ ! -x "$node" ] || [ "$(( 0$mode & 0111 ))" -ne 73 ]; then note ".playwright/$rel is mode $mode; it must be executable by everyone (755)"; fi
  fi

  for required in package/cli.js package/index.js package/package.json package/browsers.json; do
    [ -f "$pw/$required" ] || note ".playwright/$required is missing (the playwright package must travel with the Node runtime)"
  done
  [ -d "$pkg/lib" ] || note ".playwright/package/lib is missing or incomplete"
  [ -d "$pkg/bin" ] || note ".playwright/package/bin is missing or incomplete"

  if [ -f "$pkg/package.json" ] && [ -n "$want" ]; then
    got="$(sed -n 's|.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*|\1|p' "$pkg/package.json" | head -1)"
    if [ "$got" != "$want" ]; then note "the shipped playwright package is $got but the server pins $want (the browser install command would fetch the wrong revision)"; fi
  fi

  # The Windows installer script is not for any other platform (QA P24).
  if ! is_windows_rid "$rid" && [ -e "$stage/playwright.ps1" ]; then note "playwright.ps1 (a Windows PowerShell script) is in a $rid archive"; fi

  # Only this platform's runtime: another platform's would be a hundred megabytes nobody can run.
  if [ -d "$pw/node" ]; then
    for other in "$pw"/node/*/; do
      [ -d "$other" ] || continue
      if [ "$(basename "$other")" != "$(dirname "$rel" | sed 's|^node/||')" ]; then note "$(basename "$other") Node runtime shipped in a $rid archive"; fi
    done
  fi

  return "$bad"
}

# smoke_playwright_driver STAGE RID: runs the shipped driver's own CLI when this host can (same platform); proves the Node file
# starts and that cli.js, package and Node really belong together. Prints the version; returns 0 on success or when it cannot be run here.
smoke_playwright_driver() {
  local stage="$1" rid="$2" host out
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) host=osx-arm64 ;;
    Darwin-x86_64) host=osx-x64 ;;
    Linux-x86_64) host=linux-x64 ;;
    Linux-aarch64) host=linux-arm64 ;;
    *) host="" ;;
  esac
  [ "$host" = "$rid" ] || { echo "driver smoke ($rid): not run (host is ${host:-unknown})"; return 0; }
  out="$(env -i HOME="${TMPDIR:-/tmp}" PATH=/usr/bin:/bin "$stage/.playwright/$(playwright_node_relpath "$rid")" "$stage/.playwright/package/cli.js" --version 2>&1)" || { echo "driver smoke ($rid): the shipped driver did not run: $out"; return 1; }
  echo "driver smoke ($rid): $out"
}

# verify_no_iis_assets STAGE RID: prints one line per defect and returns 1 if any. A Microsoft.NET.Sdk.Web publish leaves a web.config
# (ASP.NET Core Module, in-process hosting) and can leave the module's native dll beside the executable (the dll is also bundled inside
# the single-file executable, where it is inert and cannot be dropped). Draughtsman is never hosted in IIS (an
# IIS site reverse-proxies to the service, docs/deployment.md), and an administrator who pointed a site at the archive folder would get
# in-process hosting instead of the service, so neither file ships (D9 of the Windows install proof).
verify_no_iis_assets() {
  local stage="$1" rid="$2" bad=0 leftover
  while IFS= read -r leftover; do
    [ -n "$leftover" ] || continue
    echo "archive layout ($rid): ${leftover#"$stage"/} is an IIS in-process hosting leftover of dotnet publish and must not ship"
    bad=1
  done < <(find "$stage" -type f \( -iname 'web.config' -o -iname 'aspnetcorev2_inprocess.dll' \) 2>/dev/null)
  return "$bad"
}
