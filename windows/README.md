# display-toggle (Windows)

The Windows counterpart to the macOS menu-bar app, for a **laptop with an internal panel plus an
external monitor** (e.g. the shared monitor). It links to a peer instance (the Mac) so the two share
one physical monitor — see [`../PROTOCOL.md`](../PROTOCOL.md).

- **Left-click** the tray icon → toggle the external monitor on/off.
- **Right-click** → a checkable toggle row per external monitor (checked = on; every row switches
  the whole topology), this machine's IP, peer status, *Choose peer ▸*, the version,
  *Check for updates*, and *Quit*.
- A **green dot** on the icon means the peer is linked.

Toggling switches the display **topology** — the same extend/internal modes as **Win+P** — via
the in-process CCD API (`SetDisplayConfig`), with `DisplaySwitch.exe` as a fallback:

- **On** → extend the desktop onto the external monitor (grab it).
- **Off** → internal panel only; the external loses signal and the Mac can take it over.

## Requirements

- Windows 10/11 **laptop** (needs an internal panel for the `/internal` state to make sense)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build) — the runtime to run

## Build & run

With `make` (mirrors the macOS commands):

```sh
cd windows
make            # build (Release)
make run        # stop any instance, build, and launch
make install    # publish to %LOCALAPPDATA%\display-toggle + register login autostart
make uninstall  # remove install + autostart
make clean      # remove bin/obj
make test       # unit tests + real peer links over loopback (DisplayToggle.Tests)
```

`make install` and `make run` stop a running instance cleanly first (so it removes its tray icon),
even when run from another session such as SSH.

Or directly with the .NET SDK:

```powershell
cd windows
dotnet build -c Release
./bin/Release/net8.0-windows/display-toggle.exe            # runs the tray app
./bin/Release/net8.0-windows/display-toggle.exe --list     # print detected displays, no changes
./bin/Release/net8.0-windows/display-toggle.exe --toggle   # extend <-> internal-only
./bin/Release/net8.0-windows/display-toggle.exe --extend   # grab (extend topology)
./bin/Release/net8.0-windows/display-toggle.exe --internal # release (internal-only topology)
./bin/Release/net8.0-windows/display-toggle.exe --quit     # ask the running tray app to exit cleanly
./bin/Release/net8.0-windows/display-toggle.exe --discover # list peers visible on the LAN (20 s)
```

When the tray app is running, `--toggle` / `--extend` / `--internal` ask it to do the switch, so the
peer is told and the tray's settings stay in sync; without it they switch directly.

## Pairing

1. Run the app on both machines.
2. They find each other on the LAN automatically (DNS-SD, see below): with no peer configured and
   exactly one other instance found, it is paired with a one-off notification. Otherwise
   right-click → **Choose peer ▸** and pick it, or **Set peer manually…** to enter the other
   machine's IP/hostname. Use the **same port** on both (default `51763`). Allow the port through
   the firewall if prompted (`make install` adds an inbound rule automatically when run elevated).
3. Optionally set the **same shared secret** on both machines (*Set peer manually…*) to
   authenticate the link: a per-connection session key, proofs from both sides, and a MAC + sequence
   number on every message (protocol v2). Otherwise anyone on the LAN could flip your displays.
   Blank = open mode.
4. Toggle on either side; the other mirrors inversely (one off ⇒ other on).

*Choose peer ▸ None (standalone)* stops the link: no listening, advertising or dialling. If the link
can't come up, the peer line says why: *needs update* (the peer runs an older protocol — update
both apps together), *not authenticated* (its secret is missing or different), or *requires a
secret* (it has one and this side doesn't).

Config is stored at `%APPDATA%\display-toggle\config.json`, including the secret in plain text. It is
saved atomically; a corrupt file is kept as `config.json.bad-<timestamp>` and the app starts on
defaults, and a file it can't read (e.g. locked) is never overwritten.

### Discovery

Each instance advertises `<computer name>._display-toggle._tcp.local` (mDNS/DNS-SD) on the peer
port with TXT `v=1`, `id=<per-install UUID>`, `os=win` — never the secret or display state. On
Windows the advertisement uses the in-box `dnsapi.dll` (Windows 10 1809+); discovery sends its
own legacy-unicast mDNS query every 15 s on each IPv4 interface, because `dnsapi`'s browse never
sees the replies when Bonjour (or a browser) also binds UDP 5353. A newly started peer can
therefore take up to ~15 s to appear in *Choose peer*; one that quits disappears at once (its
goodbye still arrives) or after ~45 s if it vanished without one. A peer chosen from the list is
remembered by its id, so if it comes back on a new IP while the link is down the app follows it.
Discovery needs the network profile set to **Private** (the menu hints when it's Public).
`display-toggle.exe --discover [secs]` lists what's visible from the command line.

The shared monitor is expected to auto-select whichever input has a live signal, so releasing on
one machine (its output drops) lets the other machine's grab take over.

## Updates

Releases are published on GitHub (`vX.Y.Z`) with a `manifest.json` signed by the release key. The
app checks `releases/latest/download/manifest.json` about a minute after it starts and then every
6 hours, and verifies the ECDSA P-256 signature against the public key built into the app before
reading anything in it. When a newer version is found, a menu item appears at the top, *Update
available: vX.Y.Z — Install and restart*, with one notification per version. *Check for updates*
runs a check now and says what it found (up to date / update available / couldn't check).

Nothing is installed until you click *Install and restart*. Then the app downloads
`display-toggle-windows-x64.zip` into a temp folder, checks its size and SHA-256 against the
signed manifest, unpacks it, starts a small hidden PowerShell helper and quits. The helper waits
for the app to exit (60 s at most; otherwise it changes nothing), backs up the install folder,
copies the new files over it (restoring the backup if that fails), starts the app again and deletes
the temp folder. It logs to `%TEMP%\display-toggle-update.log`. Config in `%APPDATA%` is not
touched. If any step before the hand-off fails, the app keeps running the current version and
shows why.

Self-update only applies to the copy `make install` put in `%LOCALAPPDATA%\display-toggle` (the
first install is `make install`, or the release zip unpacked there). Only that installed copy checks
automatically, whatever its version: `make install` builds `0.0.0-dev`, which is older than every
release, so after `make install` the app checks by itself and is offered the latest release. A
build run from elsewhere (`make run`, `bin\…`) never checks by itself; *Check for updates* still
works there, and only says that an update exists (run `make install` from a newer checkout
instead). A release without a Windows download counts as "up to date". Release builds get their
version from `dotnet publish -p:Version=X.Y.Z`; `display-toggle.exe --version` prints it.

## Autostart at login

`make install` handles this — it publishes to `%LOCALAPPDATA%\display-toggle` and drops a shortcut
in your Startup folder. `make uninstall` removes both. To do it by hand instead:

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$exe = (Resolve-Path ./bin/Release/net8.0-windows/display-toggle.exe).Path
$s = (New-Object -ComObject WScript.Shell).CreateShortcut("$startup\display-toggle.lnk")
$s.TargetPath = $exe; $s.Save()
```

Remove it by deleting `display-toggle.lnk` from `shell:startup`. The shortcut only starts the app at
login; it isn't restarted if it crashes.

## Notes

- Built for a **laptop**: toggling flips between extended (external on) and internal-only via
  the CCD API (`SetDisplayConfig`), with `DisplaySwitch.exe` as a fallback. "On" means an external
  (non-built-in) monitor is active; the new state is saved and sent to the peer only if the switch
  worked.
- Single-instance: a second launch exits immediately (named mutex).
- No drivers and no admin rights required (adding the firewall rule during `make install` needs an
  elevated prompt; it uses the configured peer port).
