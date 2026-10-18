#!/usr/bin/env bash
# Tests for the PDF-driver layout check a release archive must pass (scripts/lib/archive-layout.sh, called by publish.sh), run
# by scripts/verify-local.sh. QA B1: the shipped archive could not produce a PDF because the Playwright driver was not there
# whole and runnable. Every rule is checked in both directions: a good layout passes, and each defect (missing folder, missing
# Node, missing package file, not executable, wrong version, a Windows script or another platform's runtime in the wrong
# archive) is caught on its own, naming the file.
#
#   scripts/archive-layout.test.sh                 the fixtures only
#   ARCHIVE_STAGE=/path/to/unpacked RID=osx-arm64 scripts/archive-layout.test.sh    also check a real unpacked archive
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/archive-layout.sh
source "$repo_root/scripts/lib/archive-layout.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/archive-layout-test.XXXXXX")"
trap 'rm -rf "${work:?}"' EXIT

failures=0
pass() { echo "PASS: $1"; }
fail() { echo "FAIL: $1"; failures=$((failures + 1)); }
version="$(playwright_version "$repo_root")"
[ -n "$version" ] || { echo "FAIL: could not read PlaywrightVersion from the server csproj"; exit 1; }

# A complete, correct layout for $2 in $1.
make_stage() {
  local stage="$1" rid="$2" node
  rm -rf "${stage:?}"
  node="$stage/.playwright/$(playwright_node_relpath "$rid")"
  mkdir -p "$(dirname "$node")" "$stage/.playwright/package/lib" "$stage/.playwright/package/bin"
  printf 'node bytes' >"$node"
  if ! is_windows_rid "$rid"; then chmod 755 "$node"; fi
  for f in cli.js index.js browsers.json; do printf '//' >"$stage/.playwright/package/$f"; done
  printf '{ "name": "playwright-core", "version": "%s" }\n' "$version" >"$stage/.playwright/package/package.json"
  printf '//' >"$stage/.playwright/package/lib/x.js"
  printf '#' >"$stage/.playwright/package/bin/x.sh"
  if is_windows_rid "$rid"; then printf 'ps' >"$stage/playwright.ps1"; fi
}

# expect_ok STAGE RID LABEL / expect_defect STAGE RID LABEL TEXT: the check passes, or fails mentioning TEXT.
expect_ok() {
  local out
  if out="$(verify_playwright_driver "$1" "$2" "$version")"; then pass "$3"; else fail "$3: $out"; fi
}
expect_defect() {
  local out
  if out="$(verify_playwright_driver "$1" "$2" "$version")"; then fail "$3: the check passed a broken layout"; elif grep -qF -- "$4" <<<"$out"; then pass "$3"; else fail "$3: failed for the wrong reason: $out"; fi
}

for rid in osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64; do
  stage="$work/$rid"
  make_stage "$stage" "$rid"
  expect_ok "$stage" "$rid" "$rid: a complete driver beside the executable passes"
done

stage="$work/defect"
rid=osx-arm64
rel="$(playwright_node_relpath "$rid")"

make_stage "$stage" "$rid"; rm -rf "$stage/.playwright"
expect_defect "$stage" "$rid" "no .playwright folder is caught" ".playwright/ is missing"

make_stage "$stage" "$rid"; rm "$stage/.playwright/$rel"
expect_defect "$stage" "$rid" "a missing Node runtime is caught (the QA B1 state: it was inside the executable)" "$rel is missing"

make_stage "$stage" "$rid"; : >"$stage/.playwright/$rel"
expect_defect "$stage" "$rid" "an empty Node runtime is caught" "is empty"

make_stage "$stage" "$rid"; chmod 644 "$stage/.playwright/$rel"
expect_defect "$stage" "$rid" "a Node runtime that is not executable is caught (the QA B1 'Permission denied')" "must be executable by everyone"

make_stage "$stage" "$rid"; chmod 744 "$stage/.playwright/$rel"
expect_defect "$stage" "$rid" "a Node runtime only its owner can run is caught (a service account would be refused)" "is mode 744"

make_stage "$stage" "$rid"; rm "$stage/.playwright/package/cli.js"
expect_defect "$stage" "$rid" "a missing cli.js is caught (the QA B1 'Process exited': package/ never met node)" "package/cli.js is missing"

make_stage "$stage" "$rid"; rm "$stage/.playwright/package/package.json"
expect_defect "$stage" "$rid" "a missing package.json is caught" "package/package.json is missing"

make_stage "$stage" "$rid"; rm -rf "$stage/.playwright/package/lib"
expect_defect "$stage" "$rid" "an incomplete package (no lib/) is caught" "package/lib is missing"

make_stage "$stage" "$rid"; printf '{ "version": "0.0.1" }\n' >"$stage/.playwright/package/package.json"
expect_defect "$stage" "$rid" "a playwright package of another version than the server pins is caught" "pins $version"

make_stage "$stage" "$rid"; printf 'ps' >"$stage/playwright.ps1"
expect_defect "$stage" "$rid" "playwright.ps1 in a macOS archive is caught (QA P24)" "playwright.ps1"

make_stage "$stage" "$rid"; mkdir -p "$stage/.playwright/node/linux-x64"; printf 'x' >"$stage/.playwright/node/linux-x64/node"
expect_defect "$stage" "$rid" "another platform's Node runtime is caught" "linux-x64 Node runtime shipped in a osx-arm64 archive"

# The Windows archive keeps its script and needs no execute bit.
make_stage "$stage" win-x64; chmod 644 "$stage/.playwright/$(playwright_node_relpath win-x64)"
expect_ok "$stage" win-x64 "win-x64: the installer script is allowed there and node.exe needs no mode"
make_stage "$stage" win-x64; rm "$stage/.playwright/node/win32_x64/node.exe"
expect_defect "$stage" win-x64 "win-x64: a missing node.exe is caught" "node.exe is missing"

# win-arm64 (D5): Microsoft.Playwright has no Arm64 Windows driver, so the archive carries the x64 one (Windows 11 on Arm emulates it).
make_stage "$stage" win-arm64
if [ "$(playwright_node_relpath win-arm64)" = "node/win32_x64/node.exe" ]; then pass "win-arm64: the driver is the x64 node.exe, which is what Playwright looks for on every Windows"; else fail "win-arm64 does not use the win32_x64 driver"; fi
chmod 644 "$stage/.playwright/node/win32_x64/node.exe"
expect_ok "$stage" win-arm64 "win-arm64: node.exe needs no mode and the installer script is allowed"
rm "$stage/.playwright/node/win32_x64/node.exe"
expect_defect "$stage" win-arm64 "win-arm64: a missing node.exe is caught" "node.exe is missing"
make_stage "$stage" win-arm64; mkdir -p "$stage/.playwright/node/linux-arm64"; printf 'x' >"$stage/.playwright/node/linux-arm64/node"
expect_defect "$stage" win-arm64 "win-arm64: another platform's runtime is caught (what the NuGet targets copy for a runtime they do not know)" "linux-arm64 Node runtime shipped in a win-arm64 archive"
if is_windows_rid win-arm64 && is_windows_rid win-x64 && ! is_windows_rid linux-arm64 && ! is_windows_rid osx-arm64; then pass "is_windows_rid names exactly the two Windows platforms"; else fail "is_windows_rid is wrong"; fi

# IIS leftovers (D9): a clean archive passes; web.config and the in-process module are each caught, wherever they sit.
iis="$work/iis"
make_stage "$iis" win-x64
if verify_no_iis_assets "$iis" win-x64 >/dev/null; then pass "an archive without IIS leftovers passes"; else fail "a clean archive was refused"; fi
printf '<configuration/>' >"$iis/web.config"
if out="$(verify_no_iis_assets "$iis" win-x64)"; then fail "a web.config in the archive root was passed"; elif grep -qF "web.config is an IIS" <<<"$out"; then pass "a web.config in the archive root is caught, naming the file"; else fail "web.config caught for the wrong reason: $out"; fi
rm "$iis/web.config"; mkdir -p "$iis/sub"; printf 'dll' >"$iis/sub/aspnetcorev2_inprocess.dll"
if out="$(verify_no_iis_assets "$iis" win-x64)"; then fail "the in-process module in a subfolder was passed"; elif grep -qF "sub/aspnetcorev2_inprocess.dll" <<<"$out"; then pass "the ASP.NET Core Module's in-process dll is caught anywhere in the archive"; else fail "dll caught for the wrong reason: $out"; fi

# normalise_playwright_modes turns what NuGet's cache holds (owner-only execute) into what the check accepts, and nothing else.
make_stage "$stage" "$rid"; chmod 744 "$stage/.playwright/$rel"; chmod 755 "$stage/.playwright/package/cli.js"
normalise_playwright_modes "$stage" "$rid"
expect_ok "$stage" "$rid" "normalising the modes makes a 744 Node runtime pass"
if [ "$(stat -c '%a' "$stage/.playwright/package/cli.js" 2>/dev/null || stat -f '%Lp' "$stage/.playwright/package/cli.js")" = 644 ]; then pass "package files are plain readable files (644), not executable"; else fail "package files kept an execute bit"; fi

if ! verify_playwright_driver "$work/nothing" "plan9-riscv" >/dev/null; then pass "an unknown platform is refused"; else fail "an unknown platform passed"; fi

# A real unpacked archive, when given one.
if [ -n "${ARCHIVE_STAGE:-}" ]; then
  expect_ok "$ARCHIVE_STAGE" "${RID:?set RID with ARCHIVE_STAGE}" "the real archive at $ARCHIVE_STAGE passes"
fi

echo
if [ "$failures" -eq 0 ]; then echo "archive layout tests: all passed"; else echo "archive layout tests: $failures failed"; exit 1; fi
