# Install on Windows (release archive and Windows service)

**What was run on Windows, and what was not.** On 1 October 2026 the archives were run on a real Windows 11 Pro machine
(**Arm64**, a virtual machine, build 26200, Windows Defender on, no .NET or Node on the PATH) and the problems that run found
were fixed and run again. Verified there: unpack, `--version`, a foreground start, first run, sign-in, the API and the web app
in Edge, `service install` and `uninstall`, a service that starts at boot with nobody logged on, the first-run setup code
(section 3), the data folder's access list, `backup`, `restore` and `reset-admin`, PDF export from the service with both Microsoft
Edge and Playwright's Chromium (section 3), and the DPAPI start-up check. **Not verified:** a physical or Intel machine (the `win-x64`
archive was run under Windows on Arm's x64 emulation, which is slower to start but behaves the same), the SmartScreen and Smart App
Control dialogs and the Windows Firewall prompt (they appear only to someone at the screen), IIS with Application Request Routing,
and any antivirus other than Windows Defender. Where a step below says "unverified", that is why.

## 1. Unpack and allow it to run

Pick the archive for the machine: `win-x64` for Intel and AMD, `win-arm64` for Windows on Arm (Snapdragon and similar). An
Arm machine can also run the `win-x64` archive under emulation, but it starts two to three times slower (about 6 to 10 seconds
warm against 4 seconds native) and the native one is the better choice. PowerShell:

```powershell
Get-FileHash .\draughtsman-0.1.0-win-x64.zip -Algorithm SHA256       # compare with the line in SHA256SUMS
Expand-Archive .\draughtsman-0.1.0-win-x64.zip -DestinationPath 'C:\Program Files\Draughtsman'
& 'C:\Program Files\Draughtsman\draughtsman-0.1.0-win-x64\draughtsman.exe' --version
```

- **Where to unpack it.** Somewhere the service account can read, and that stays where it is: the service points at the
  executable's path. `C:\Program Files\Draughtsman` is right. **Not a folder under `C:\Users`**: the service runs as `LocalService`,
  which cannot read anyone's user profile, so a service installed from there would fail to start with a bare "Access is denied".
  `service install` checks this first and refuses, before it changes anything, with the folder to use instead. Keep the destination
  short too: `Expand-Archive` into a path of about 140 characters or more fails (Windows' 260-character path limit), with a
  message about a folder that "does not exist".
- **The "downloaded from the internet" mark.** `Unblock-File` on the zip does nothing useful for `Expand-Archive`, which does not
  pass the mark on to the files it makes; it matters only if you extract with Explorer's *Extract All*.
- **SmartScreen and Smart App Control.** The archive is **not code-signed yet**. On a machine where SmartScreen is in its usual
  mode it may stop a double-clicked first run ("Windows protected your PC"): choose **More info**, then **Run anyway**. On a Windows 11
  machine where **Smart App Control** is switched on, an unsigned program with no reputation is blocked outright and there is no
  "Run anyway"; turn the setting off (it cannot be turned back on without resetting Windows) or wait for a signed build. Neither
  dialog could be observed in the run above (nobody was at the screen), so this is what Microsoft documents, not what was seen.
- No .NET or Node install is needed; the Node runtime is in the archive's `sidecar` folder.

## 2. Try it in the foreground

```powershell
$env:DRAUGHTSMAN_DATA = 'C:\Temp\ds-try'
& 'C:\Program Files\Draughtsman\draughtsman-0.1.0-win-x64\draughtsman.exe'
```

It binds `127.0.0.1:5180`; open <http://localhost:5180>; the setup code is in that console window. Ctrl+C stops it. Without
`DRAUGHTSMAN_DATA` the data folder is `data\` beside the executable. Windows Firewall may ask whether to allow the program
on private networks the first time it listens on anything other than loopback; leave it blocked until you decide to publish it
(a `0.0.0.0` bind was reachable from the same machine and not from another one on the private profile; the prompt itself was not seen).

A foreground run that starts with your own sign-in uses Windows DPAPI for the key ring. If it stops at once with
**"Windows DPAPI cannot be used to protect the key ring"**, the logon you started it from has no DPAPI master key (an SSH session with a
public key, a scheduled task run without a stored password): the message names the two safe ways forward, a key-encryption key or a
deliberate `keyRing.protection: none`, and the server does not start until you pick one. It is the same rule the macOS Keychain has.

## 3. Install the service

From an **elevated** PowerShell, with the archive unpacked under `C:\Program Files` (see section 1):

```powershell
& 'C:\Program Files\Draughtsman\draughtsman-0.1.0-win-x64\draughtsman.exe' service install --dry-run
```

The plan it prints (real output; the executable's path is the one you run it from, here shortened to `C:\Program Files\Draughtsman`):

```
# create folder C:\ProgramData\Draughtsman
# run icacls C:\ProgramData\Draughtsman /inheritance:r /remove:g *S-1-5-32-545 *S-1-1-0 *S-1-5-11 /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-19:(OI)(CI)F
# register the Windows event source draughtsman in the Application log
# run sc.exe create draughtsman binPath= "\"C:\Program Files\Draughtsman\draughtsman.exe\" serve --DRAUGHTSMAN_DATA \"C:\ProgramData\Draughtsman\"" start= auto obj= "NT AUTHORITY\LocalService" DisplayName= "Draughtsman diagram server"
# run sc.exe description draughtsman "Draughtsman diagram server"
# run sc.exe failure draughtsman reset= 86400 actions= restart/5000/restart/5000/restart/30000
# run sc.exe start draughtsman
```

Then run it without `--dry-run`, and check with `draughtsman.exe service status` or `Get-Service draughtsman`. It installs and starts in
about two seconds, and the server answers on `http://localhost:5180` a few seconds after that (about ten on the very first start, while
Windows Defender scans the executable).

- **Account and folder.** The service runs as `NT AUTHORITY\LocalService`. Its data folder, `C:\ProgramData\Draughtsman`, is made
  **private**: the install cuts the folder's inheritance and leaves full control to SYSTEM, the Administrators and the service account only.
  (`C:\ProgramData` gives every local user read and create rights, and an ordinary folder created there inherits them: any local account
  could have read the database with its password hashes, and planted a `draughtsman.yaml`.) Before and after, from the run above:

  ```
  before   BUILTIN\Users:(I)(OI)(CI)(RX)      BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)      + SYSTEM and Administrators
  after    NT AUTHORITY\LOCAL SERVICE:(OI)(CI)(F)   BUILTIN\Administrators:(OI)(CI)(F)   NT AUTHORITY\SYSTEM:(OI)(CI)(F)
  ```

  Do not add `Users` or `Everyone` back. At every start the server checks the folder and, if `Users`, `Everyone` or `Authenticated
  Users` can read or change it, logs a warning with the `icacls` command that fixes it (a folder the server creates itself, outside the
  service, is made private the same way when it is new; an existing one is only reported, never changed). `--user`, `--data` and `--name`
  change the account, folder and service name (`draughtsman.exe --help` lists them); the service shows in Services as
  **Draughtsman diagram server**.
- **The setup code.** Run `draughtsman first-run-code` from an **elevated** prompt:

  ```powershell
  & 'C:\Program Files\Draughtsman\draughtsman-0.1.0-win-x64\draughtsman.exe' first-run-code --DRAUGHTSMAN_DATA 'C:\ProgramData\Draughtsman'
  ```

  The code is **not** in the Event Viewer or in any log: every local user can read the Application log, and anyone who saw the code first
  could create the administrator. A service writes it to `C:\ProgramData\Draughtsman\setup-code.txt` instead, a file only Administrators and
  SYSTEM can read (a non-administrator account, `LocalService` included, gets "Access is denied" opening it), and the Application log
  entry from the source `draughtsman` says where it is: *The setup code is stored at C:\ProgramData\Draughtsman\setup-code.txt; read it as an
  Administrator.* `first-run-code` prints it (and refuses, with a message, once an administrator exists or if it cannot read the file). The file is
  deleted the moment the first administrator exists, and a restart before that writes a fresh code over it and ends the old one. The first-run
  page names this when it sees it is running as a service. The optional log file (`logging.file.enabled`) never holds the code either.
- **Logs.** The service writes to the Application event log under the source `draughtsman` (Event Viewer, Windows Logs, Application;
  `service install` registers the source, which needs the administrator rights the install already has, so the messages appear; without it
  the service's only event is an Event ID 0 error saying the source `draughtsman` was not found). A server that stops while starting, for example on the DPAPI check
  above, writes its reason there as an error. For ordinary files set `logging.file.enabled: true` in `draughtsman.yaml`: daily files appear in
  `C:\ProgramData\Draughtsman\logs\`.
- **Settings** go in `C:\ProgramData\Draughtsman\draughtsman.yaml`; restart with `Restart-Service draughtsman`.
- **The AI key's protection** defaults to Windows DPAPI, scoped to the account the service runs as (`LocalService`; verified working
  there). A backup made with `--include-secrets` therefore cannot be opened on another machine or by another account; keep the key
  encryption key approach in mind if you move servers ([`admin-guide.md`](admin-guide.md#the-api-key-and-the-key-ring)).
- **Admin commands** (`backup`, `restore`, `reset-admin`, `first-run-code`) take the data folder from `DRAUGHTSMAN_DATA` or
  `--DRAUGHTSMAN_DATA`: in an elevated PowerShell, `$env:DRAUGHTSMAN_DATA='C:\ProgramData\Draughtsman'` first, then
  `draughtsman.exe backup --out D:\Backups\`.
- **PDF export** from the editor needs nothing on the server (it prints from the user's own browser). **Server-side PDF** (scripts and agents) needs a Chromium; both ways below produced a PDF from the running service (a one-page diagram in 2 to 9 seconds).
  Every render and the check behind `/api/health` has a time limit (`export.chromium.timeoutSeconds`, 60): a browser that stops
  answering is stopped, by process id and nothing else, and the request fails with `504` naming what to do, instead of hanging.
  The browser gets a profile folder of its own under `C:\ProgramData\Draughtsman\chromium`, removed after each render. Health says PDF
  works only after a page was actually printed.
  - **Microsoft Edge**, already installed: `export.chromium.enabled: true` and
    `export.chromium.path: "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe"`. Worked from the service in the run above
    (Edge 154, five renders in a row, 2.5 to 5 seconds each). An earlier run on the same machine saw Edge accept the request and never answer
    under the service account, with its helper processes left running; it could not be reproduced afterwards, so the cause is not known. If
    you see `504` or a health hint about a browser that never prints, use Playwright's Chromium instead.
  - **Playwright's Chromium**, which does not depend on the Edge on the machine: from the archive folder run
    `.\.playwright\node\win32_x64\node.exe .\.playwright\package\cli.js install chromium` (about 700 MB and 40 seconds) with
    `$env:PLAYWRIGHT_BROWSERS_PATH='C:\ProgramData\ms-playwright'` set so a folder the service can read holds it, and give the service the same
    variable (it cannot read your user profile's cache): `Set-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\draughtsman -Name Environment
    -Type MultiString -Value 'PLAYWRIGHT_BROWSERS_PATH=C:\ProgramData\ms-playwright'`, then `export.chromium.enabled: true` with no `path`,
    and `Restart-Service draughtsman`. Verified.
  - On a Windows on Arm machine the PDF driver is the **x64** one, which Windows runs under emulation (both archives carry it, since Microsoft.Playwright
    ships no Arm64 build); both browsers above printed through it. `playwright.ps1` in the archive is Playwright's own installer script and
    is not needed. See [`pdf-export.md`](pdf-export.md).

## 4. Reach it

The service binds `127.0.0.1:5180`. For other people, put a reverse proxy on the same machine in front of it, IIS with
Application Request Routing, Caddy for Windows or nginx, and allow only 443 through the firewall
([`deployment.md`](deployment.md#iis-with-application-request-routing-windows)). Draughtsman itself is never hosted inside IIS: the
archive carries no `web.config`, and an IIS site proxies to the service.

## 5. Back up, upgrade, remove

Back up with the `backup` verb above (not by copying `data.db` while the service runs). To upgrade: back up, stop and remove the
service (`Stop-Service draughtsman`, then `draughtsman.exe service uninstall`), unpack the new archive (its folder is named for the new version, so the
old one stays until you delete it; `Expand-Archive` refuses to overwrite files without `-Force`), and run `service install` from the new
folder; the data folder is never touched and the database migrates itself ([`upgrade-backup.md`](upgrade-backup.md)). Run `draughtsman.exe config check` from the new folder first.
A program folder at a new path does **not** lose the stored AI API key or the saved SMTP password: they are protected by the key ring in the data folder, not by the install folder
([`upgrade-backup.md`](upgrade-backup.md#where-to-put-the-new-version)). Upgrading from a build older than this one, the first start reads a secret that build tied to its folder and re-protects it;
`draughtsman.exe config check` from the new folder says beforehand whether it can (the new folder sits beside the old `draughtsman-<version>-win-x64`, which it looks in) and, if not, names the
old folder to put in `keyRing.legacyInstallPaths`. (The Windows service path of this is unverified here; the logic is tested on every platform in CI.) To remove:
`draughtsman.exe service uninstall`, which also works when the service is already stopped; the data folder is left as it is. The Event Log source stays
registered (it is harmless; `Remove-EventLog -Source draughtsman` removes it).

## If it does not start

[`troubleshooting.md`](troubleshooting.md). Start in the Application event log. The single-file executable unpacks its two native libraries
(`e_sqlite3.dll` and the ASP.NET Core Module's inert `aspnetcorev2_inprocess.dll`) under the account's temporary folder; for `LocalService` that is
`C:\Windows\ServiceProfiles\LocalService\AppData\Local\Temp\.net\draughtsman\<hash>`, and no `DOTNET_BUNDLE_EXTRACT_BASE_DIR` is needed
for the service on Windows.
