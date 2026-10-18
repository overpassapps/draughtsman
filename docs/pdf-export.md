# PDF export

There are two ways to get a PDF, and the first needs nothing installed.

| Route | What it is | Needs |
| --- | --- | --- |
| **Browser print** (the default) | File > Export PDF… draws the diagram on a page and opens your browser's print dialog; you choose **Save as PDF**. | Nothing: it runs in the browser and makes no request. |
| **Server Chromium** (optional) | The server prints the diagram through a headless Chromium and returns a file: `GET /api/documents/{id}/pdf`, the `render_document` MCP tool's `pdf` format, and the dialog's **Export PDF** button once it is switched on. | A Chrome, Chromium or Edge on the server ([Turn it on](#turn-it-on)). The route for headless, scripted or scheduled PDFs and for agents. |

## Browser print: the default

File > Export PDF… (or the command palette) opens one dialog with the three options below, a line saying what it does ("Opens your browser's print dialog: choose Save as PDF.") and an **Open print dialog** button. It works on any document with something on it, for an Editor and in a read-only preview, with or without the server component: the dialog never says PDF is unavailable.

What it does:

- It draws **the same SVG that File > Export SVG and Export PNG produce** (`web/src/editor/exportDiagram.tsx`: one renderer, so the shapes, theme colours, connector routes and fonts match by construction, and anything later added to that export, such as a notice, is printed too). It is the theme you have picked in the editor's Theme menu, as SVG and PNG export are; the server route instead uses the organisation default.
- It lays that SVG out on a page of the size you chose (`@page size`): A4, A3 or Letter in either orientation, or **Fit to diagram**, where the page is cut to the drawing plus its margins. On a fixed page the drawing is **scaled down to sit inside the margins, never up**, centred, on a **single sheet**. This mirrors the server route's page maths (`src/Draughtsman.Server/Export/PdfPage.cs`).
- Backgrounds are printed (`print-color-adjust: exact`), and the page is painted the drawing's own paper colour, so a dark theme does not sit in a white frame. The bundled fonts travel inside the SVG, as they do for PNG export.
- The margin is padding inside the sheet and the `@page` margin is 0, so the browser has no margin area of its own to print a header or footer (page address, date) into.
- The document's title is the print document's title, which Chrome offers as the file name.

It opens the print view in a window made inside your click, and **if the browser blocks that window it prints from a hidden frame in the same page instead**, so a blocked popup costs nothing. Nothing is sent to the server and no request is made.

What it cannot do: the file is made by **your browser**, in a print dialog rather than a download, so its details are the browser's (some browsers add their own header and footer, or offer their own scaling; choose the page size in the dialog to match what you asked for); an agent or a script cannot ask for it, and it cannot run headless or on a schedule. Those still want the server route.

## Server Chromium: optional

PDF from the server needs a headless Chromium. Without it everything else works, including browser print above; the dialog then simply has the one route. When the server has proved a browser launch, the same dialog offers **Export PDF** (the server's file) as the primary button and **Print in browser…** beside it.

## Turn it on

PDF needs two things on the server: the **driver** (Playwright's Node runtime and package, which a release archive ships) and a **browser** (Chromium or Chrome, which it does not). You supply the browser; you do not install a driver.

1. **Get a browser.** Either:

   - **Use one you already have.** Set `export.chromium.path` (step 2) to a Chrome, Chromium or Edge executable, for example `/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`. Nothing is downloaded.
   - **Or download Playwright's Chromium**, pinned to the Playwright version this build of the server uses. In a **release archive** run the installer with the driver that ships beside the executable (no Node on the machine needed), from the archive folder, as the account that runs the server:

     ```console
     ./.playwright/node/darwin-arm64/node ./.playwright/package/cli.js install chromium     # macOS Apple silicon
     ```

     The folder under `.playwright/node/` is `darwin-arm64`, `darwin-x64`, `linux-x64`, `linux-arm64` or `win32_x64` (`node.exe` on Windows) to match your platform. Where Node is installed anywhere, or in a build tree, `npx playwright@1.63.0 install chromium` fetches the same browser. It needs the internet once (several hundred megabytes: about 560 MB on disk on macOS, in the full Chromium, the headless shell and ffmpeg) and keeps the browser in the installing account's cache (`~/Library/Caches/ms-playwright` on macOS, `~/.cache/ms-playwright` on Linux, `%USERPROFILE%\AppData\Local\ms-playwright` on Windows); a server running as a different account, such as a service, needs `PLAYWRIGHT_BROWSERS_PATH` set to a shared folder for both the install and the server.

   The version matters. The server launches the Chromium *revision* that its own Playwright library expects; an unpinned CLI installs a different revision and the server reports the browser as absent. The server prints the exact command in `GET /api/health` (`export.pdfHint`) and in the 501 answer of the PDF endpoint, so it always matches the running build.

   In the Docker image: build with `--build-arg INSTALL_CHROMIUM=true` (README, "Docker"); the image already pins the same version.

2. **Enable it** in `draughtsman.yaml` in the data folder, then restart:

   ```yaml
   export:
     chromium:
       enabled: true
       path: ""   # optional: a Chromium or Chrome executable to use instead of Playwright's own download
       timeoutSeconds: 60   # optional: the longest one render may take (see "A browser that hangs")
   ```

   Nothing else is needed: the server finds the driver beside its own executable and points Playwright at it itself (it also repairs a lost execute bit on the Node file when it is allowed to). You do not set `PLAYWRIGHT_DRIVER_SEARCH_PATH` or change any file mode.

## The driver, and telling a missing driver from a missing browser

A release archive's folder holds `.playwright/` beside the executable: `node/<platform>/node` (the runtime Playwright drives) and `package/` (the playwright package). They must stay together, whole, with the Node file executable. `scripts/publish.sh` refuses to build an archive where they are not, and runs the shipped driver once to prove it starts.

When the server's PDF cannot work, the administrator's view of `GET /api/health` (`export.pdfProblem`), a note in the Export PDF dialog (printing from the browser still works beside it) and the Status page say **which part**:

| `pdfProblem` | Meaning | The fix |
| --- | --- | --- |
| `disabled` | `export.chromium.enabled` is not `true`. | Turn it on (step 2). |
| `driver` | The `.playwright` folder is missing, incomplete (no `package/cli.js`), its Node file will not run, or the driver process would not start. The hint names the file. **Installing a browser does not help.** | Unpack the release archive again over the install (keep `.playwright/` whole) and restart. On macOS also run `xattr -dr com.apple.quarantine` on the folder if it was downloaded. |
| `browser` | The driver is fine but no Chromium or Chrome could be launched: not installed, the wrong revision, or an `export.chromium.path` that is wrong or will not start. | Install the pinned browser or fix the path (step 1). Checked again within 30 seconds, no restart. |

An Editor sees only whether PDF is available; the hint names server paths and is for administrators.

## What was verified, and where

- **Browser print, in real Chrome (5 October 2026, macOS arm64, a release build of the server).** From File > Export PDF… on the sample "Order service" diagram: the dialog opened with the one-line explanation and no mention of an install; **Open print dialog** opened the print view in a window made inside the click and called `print()` once, the dialog closed, and the document carried the embedded Inter face and `@page{size:297mm 210mm;margin:0}`. With `window.open` forced to return `null` (a blocked popup), A4 portrait and a 50 mm margin printed from the hidden frame instead, the diagram scaled down to 415.7 px (110 mm, the width inside the margins). Each printed document was then rendered to PDF by Chrome with `preferCSSPageSize` and background graphics **off**: one page each, A4 landscape and A4 portrait, the drawing centred and not clipped, the dark node fills and the paper colour printed anyway (`print-color-adjust: exact`), the embedded font loaded. **Not run:** Chrome's own print dialog (a native window that automation cannot capture, so its "Save as PDF" step, and whether it shows a header or footer for a zero-margin page, were not observed), Firefox, Safari and Edge, and an evaluation-notice printout (that task had not landed).
- **A real archive, on one platform (1 October 2026).** The `osx-arm64` archive built by `scripts/publish.sh` was unpacked to a scratch folder and started from a different working directory with an empty environment (`env -i HOME=<scratch> PATH=/usr/bin:/bin`). With only `export.chromium.enabled: true` and either (a) `export.chromium.path` set to the system Google Chrome or (b) Playwright's Chromium installed by the archive's own driver (`./.playwright/node/darwin-arm64/node ./.playwright/package/cli.js install chromium`, 14 seconds, about 560 MB on disk including the headless shell and ffmpeg), `GET /api/documents/{id}/pdf` answered `200` with a body beginning `%PDF-1.4` (17,145 bytes with (a), 16,933 with (b)), and the editor's File > Export PDF dialog downloaded `order-service.pdf` (17,425 bytes) in real Chrome. Removing `.playwright/package/cli.js` made health report `pdfProblem: "driver"` and the dialog say the driver is not usable; with only the browser missing it reported `browser`; a Node file with its execute bit removed was repaired by the server at start.
- **Earlier claims were from a different build.** Before this was fixed, "verified on macOS" meant a build tree (framework-dependent `dotnet run`), where the driver sits beside `draughtsman.dll`. The single-file archive was never run, and it could not enable PDF (QA B1). Do not read old notes as archive evidence.
- **Windows 11 on Arm, from a running service (1 October 2026).** The `win-arm64` archive (driver: the x64 Node runtime under Windows' emulation, since Microsoft.Playwright ships no Arm64 one) installed as a service running as `LocalService` produced `%PDF-1.4` documents of a one-page diagram with **Microsoft Edge 154** (`export.chromium.path`: five renders in a row from the `win-x64` build, then `win-arm64`, 2.5 to 5 seconds) and with **Playwright's Chromium** (installed by the archive's own driver, 706 MB, 39 seconds, `PLAYWRIGHT_BROWSERS_PATH` given to the service through its `Environment` registry value; renders took 1.5 to 8 seconds). A browser frozen on purpose mid-render (every Playwright-launched Edge process suspended) made the request fail with `504` after the 15-second limit it was given, left no Edge or driver process of that render running, left an unrelated Edge started by hand alone, and the next render worked. An earlier run on the same machine saw Edge hang under the service account with no answer for minutes and its helper processes left behind; that could not be reproduced afterwards, so what made Edge hang then is not known, only that it can no longer hang a request.
- **Not run on a real host:** the Linux archives, the `osx-x64` one, and Windows on Intel or AMD hardware (the `win-x64` archive was run under emulation on Arm). Their layout is checked by `scripts/archive-layout.test.sh` (driver present, whole, executable, no other platform's runtime), and the Node runtime inside each is the one Microsoft.Playwright ships for that platform.

## How availability is decided

`GET /api/health` reports `export.pdf: true` only when the browser has **actually printed a page**: the probe prints a tiny one through the same driver, browser and print path a real render takes (a browser that merely launches proves nothing about printing, which is where a hung one stops), under a time limit, and requires a PDF back. The server probes lazily on the first health request, then caches the answer: five minutes after a success, thirty seconds after a failure (so installing the browser is noticed without a restart, but a failing Chromium cannot be launched continuously by polling). Only one probe runs at a time. A successful or failed PDF render also refreshes the answer.

`enabled: true` alone is therefore not enough for the dialog to offer the server's file. A missing or wrong-revision browser, an executable that will not start, or a driver that is not usable reads as "not available" (with `export.pdfProblem` saying which), and `GET /api/documents/{id}/pdf` answers `501` with the same hint.

## A browser that hangs: time limits, stopped processes, the profile folder

Every render, and the probe above, has a hard limit: `export.chromium.timeoutSeconds` (60; the probe never waits more than 30). Playwright's own
calls cannot be cancelled, so a render that overruns, or whose caller gave up, is ended by stopping the processes **that render started**: its
Playwright driver and everything below it (the browser and its helpers), by process id. The driver is identified when it starts (a new child of the
server that is running exactly the driver's own `node` file, never the sidecar's), remembered with its start time, and a process that cannot be
positively identified is left alone; nothing is ever stopped by name. Then the request fails with **`504 PDF export timed out`** (MCP `render_document`
reports the same text as a tool error) naming what to do, `GET /api/health` says the same until the next probe (30 seconds), the render slot is given back
(a hung render no longer ends in `429 busy`), and the server log has a warning listing the stopped process ids.

Chromium gets a **profile folder of its own** for each launch, `<data folder>/chromium/profile-<id>`, deleted after the render (a crashed run's leftovers
are swept at the next start), so it never depends on the service account's temp or profile folders and nothing it writes survives. It starts with
`--disable-gpu` (a service has no desktop to attach a GPU process to) and `--disable-sync`, on top of Playwright's defaults; the page it prints still has
no JavaScript, no network and no downloads, and the browser still gets none of the server's environment variables.

If you see the `504` or the health hint "did not finish within N seconds": the browser starts and then never answers. Found with Microsoft Edge under a
Windows service; use Playwright's Chromium there ([`install-windows.md`](install-windows.md#3-install-the-service)). Raise `timeoutSeconds` only for a very
large diagram.

## Options

`GET /api/documents/{id}/pdf` takes three optional query parameters, also offered in the editor's Export PDF dialog (which applies the same three to browser print):

| Parameter | Values | Default |
| --- | --- | --- |
| `pageSize` | `fit`, `a4`, `a3`, `letter` | `fit` (the page is cut to the drawing, one page, nothing scaled) |
| `orientation` | `landscape`, `portrait` (ignored for `fit`) | `landscape` |
| `margin` | millimetres, 0 to 50 | `0` for `fit`, `10` otherwise |

A fixed page scales the drawing down to sit inside its margins (never up), centred, on a single page. Anything outside those values is a `400`. The editor's dialog opens on A4 landscape (the atlas default, `design/atlas/export-dialog.md`); the endpoint defaults to `fit` so an existing caller with no query keeps getting one page.

## What the PDF looks like

The PDF is printed from the same server-rendered SVG as `render_document`, which resolves the shipped diagram theme (kind defaults, the document's own token overrides, each element's own style, the theme's export background) for **every** document type, so it matches the client's themed PNG and SVG. The server uses the **organisation's default theme** (Admin > Themes) when one is set, read live so a change reaches the next PDF with no restart, and the shipped Overpass theme otherwise: the theme's base, palette swaps and export background are applied exactly as the editor applies them (`web/src/theme/orgTheme.ts`). A document's own `theme` token overrides sit on top. It does not know which theme an individual viewer picked in the editor's theme menu (that choice is view state, not stored on the document), so a PDF matches the editor only when the viewer is on the organisation default. Fonts are the server's `sans-serif`.

## The decision: browser print is the default

The audit (PR-07, SALE-39, decision Q8) left the default open. The maintainer chose **client-side print to PDF as the default, with Chromium optional** (5 October 2026), which is what this page describes. It fits the self-hosted, air-gapped pitch: nothing extra to install, one renderer for PNG, SVG and PDF, and no Chromium attack surface unless an administrator chooses it. The alternative, bundling Chromium in the image by default (about 350 MB more, a larger patching surface, a browser process per export, and still not available to the bare Windows and Linux binaries), was not built.
