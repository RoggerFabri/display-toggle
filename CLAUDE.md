# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

Two sibling apps that do the same job on each OS, plus a shared network protocol:

- **`mac/`** — the macOS menu-bar app: `display-toggle.swift` (UI, displays, discovery),
  `peer-logic.swift` (pure, unit-tested logic), `peer-link.swift` (the TCP peer link),
  `update-logic.swift` (self-update decisions: versions, manifest signature/parse, asset hash,
  installed-path / auto-check rule — pure, unit-tested), `updater.swift` (self-update networking,
  install and restart — app only, not in the test target), `Info.plist` (embedded, via a generated
  copy), `launch-agent.plist`, `Makefile`, and `Package.swift` + `Tests/` for `make test`
  (details below).
- **`windows/`** — the Windows system-tray equivalent (C#/.NET WinForms), for a laptop with an
  internal panel + external monitor. Toggling switches the display **topology** (the same
  extend/internal modes as Win+P): `DisplayConfig.cs` calls the in-process CCD API
  `SetDisplayConfig(SDC_TOPOLOGY_EXTEND | INTERNAL)`, with `DisplaySwitch.cs` (spawns
  `DisplaySwitch.exe`) as a fallback. "On" (extended) = any non-internal CCD target is active
  (`DisplayTargets.cs`), falling back to the attached count (`NativeDisplay.cs`) when CCD is
  unavailable. A switch is saved and broadcast only if it worked; for the `DisplaySwitch.exe`
  fallback that means a re-query confirms the new topology. Self-update: `UpdateLogic.cs` (versions,
  manifest signature/parse, asset hash, installed-copy check, the helper script — pure, tested) and
  `Updater.cs` (HTTP, download + staging, launching the helper). The csproj default version is
  `0.0.0-dev`; releases pass `-p:Version=X.Y.Z`.
  See `windows/README.md`.
- **`PROTOCOL.md`** — the peer-link wire protocol both apps implement.

The **peer link** coordinates a single shared monitor wired to both machines (dual input):
each app both listens and dials a persistent TCP connection to the other, and they mirror
**inversely** — when one releases the monitor (display off), the other grabs it (display on).
Local toggles broadcast a `state` message; received state is applied silently (never echoed),
so a toggle settles in exactly one hop. The peer is picked from discovery or entered by hand
(*Set peer…*), and stored alongside display state. On the Mac that lives in `PeerLink`
(`peer-link.swift`) + `AppDelegate.applyRemoteState`; on Windows in `windows/PeerLink.cs` +
`TrayApp.OnPeerState`.

The shared monitor is expected to auto-select whichever input has a live signal, so releasing on
one machine (signal drops) lets the other's grab take over.

**Peer discovery** (DNS-SD `_display-toggle._tcp`) lets each app pick the other from a
*Choose peer ▸* submenu: Mac `PeerBrowser` + `ServiceResolver` (dnssd), Windows `PeerDiscovery.cs`
(dnsapi). Resolution never opens a TCP probe to the peer. See `PROTOCOL.md` "Discovery" and
`docs/specs/peer-discovery.md`.

The peer link speaks **protocol v2** (v1 peers are refused as *needs update*). With the
**optional shared secret**, a session key is derived from both sides' nonces and ids, each side
sends a role-bound proof, and every later message carries `seq` + `mac`; self-reflection checks
reject our own id or nonces. A new connection only replaces the active one once established (a
deterministic tie-break decides between one-each-way dials), pending connections time out after
10s, and version/secret mismatches back off to 30s and show in the menu. Also a **max line
length** (8 KB) and a **ping/pong heartbeat + TCP keepalive** (~15s ping, ~40s stale timeout).
Dials time out (Mac 10s, Windows 5s), and a dial still connecting is cancelled once a link
becomes active, so a late SYN can't replace a working link. The handshake is a pure state
machine (Mac `PeerSession` in `peer-logic.swift`, Windows `windows/PeerSession.cs`) tested against
the vectors in `PROTOCOL.md`; both sides also test real links over loopback (Mac
`Tests/PeerLogicTests/PeerLinkTests.swift`, Windows `DisplayToggle.Tests/PeerLinkTests.cs`).
See `PROTOCOL.md`.

**Windows threading:** the UI thread never waits on slow work. Topology switches run one at a
time on a FIFO worker (`TrayApp.QueueSwitch`), each deciding from live state when it runs, so
a local toggle and a peer-driven switch can't interleave; peer sends go through a per-connection
queue drained by a writer task; discovery's `Peers` is a lock-free snapshot, and dnsapi is never
called under its `_gate` lock (that deadlock once froze the tray menu). The tray icon is only
re-added on a session switch (RDP/unlock) or when the tray icon size changes (a DPI switch,
`TrayApp.OnDisplayChanged`); other display and theme changes just re-render it, debounced.
Update checks and downloads run on the thread pool and report back through `Post`; installing hands
off to a detached PowerShell helper (it waits for this process to exit, swaps the files in and
relaunches), and the app then quits the normal way.

`.github/workflows/build.yml` builds and tests both apps on every push to any branch — `dotnet build` +
`dotnet test` on `windows-latest`, and `make build` + `make test` (swiftc + embedded Info.plist, then the
Swift Testing tests) on `macos-latest`.

**Releases:** `.github/workflows/release.yml` runs on every push to `main` (and *Run workflow* with a
`bump` input: patch/minor/major). Its `plan` job bumps the latest `vX.Y.Z` tag (none yet → `v1.0.0`)
and, on a push, releases only if app files changed since that tag (`mac/*.swift` except
`Package.swift`, `mac/Info.plist`/`Makefile`/`launch-agent.plist`, `windows/**` except
`DisplayToggle.Tests/` and `*.md`). `build-windows` (`dotnet publish -p:Version=…`, zipped flat) and
`build-macos` (`make dist VERSION=…`, a universal tarball) run the tests and build the assets;
`publish` writes `manifest.json` with `.github/scripts/make-manifest.py` (compact JSON, fixed key
order, no trailing newline: version, tag, published, notes_url, and per asset name/sha256/size), signs
those exact bytes with the `RELEASE_SIGNING_KEY` secret (ECDSA P-256/SHA-256, raw DER signature →
`manifest.json.sig`), verifies it against `.github/release-signing.pub.pem` (the same public key the
apps embed), and runs `gh release create` with the two assets + manifest + signature. The apps fetch
`releases/latest/download/manifest.json(.sig)` and verify the signature before parsing anything.
Changing the manifest format or the key means changing both apps and their shared test vector.
`build.yml`'s `release-tooling` job runs the `make-manifest.py` tests on every branch.
Each app needs only its own asset; a release without it is "no update" for that platform.
**Automatic update checks** (~60 s after start, then every 6 h) run only for the installed copy
(Windows `UpdateLogic.IsInstalledCopy`: `%LOCALAPPDATA%\display-toggle\display-toggle.exe`; Mac
`runsAutomaticUpdateChecks`: a `…/bin/display-toggle` path), whatever its version — `make install`
builds `0.0.0-dev`, which is older than every release, so it's offered the latest one. A build run
from a checkout never checks by itself; *Check for updates* works everywhere. Nothing installs
without a click.

## Build

macOS — run from `mac/`:

```sh
cd mac
make                  # compile display-toggle binary (VERSION defaults to 0.0.0-dev)
make dist VERSION=X.Y.Z  # release asset: universal, ad-hoc signed → mac/dist/display-toggle-macos.tar.gz
make clean            # remove binary, gen/ and dist/
sudo make install     # build + install to /usr/local/bin + register launchd agent
sudo make uninstall   # remove binary and launchd agent
make run              # build and run locally (kills any existing instance first)
make test             # unit tests (Swift Testing): peer-logic + update-logic + loopback peer-link tests
```

`make` runs `swiftc` on the five sources (`display-toggle.swift peer-logic.swift peer-link.swift
update-logic.swift updater.swift`) and embeds `gen/Info.plist` — a generated copy of `Info.plist`
with `CFBundleShortVersionString` stamped to `VERSION` (default `0.0.0-dev`; a changed VERSION
forces a relink) — into the binary's `__TEXT,__info_plist` section (`-Xlinker -sectcreate …`). That declares the Bonjour service and
Local Network usage for peer discovery without an `.app` bundle. Its `CFBundleIdentifier` must
**differ** from the `com.display-toggle` defaults suite, or `UserDefaults(suiteName:)` returns nil
and the app crashes on launch. The app is built by plain `swiftc` (no Xcode project). Its entry
point is `@main enum DisplayToggleApp`, because top-level code isn't allowed with more than one
file. `peer-logic.swift` holds the pure discovery decisions (self-filter, address pick,
follow/auto-pick, checkmark) and the v2 `PeerSession` handshake, so they can be unit-tested.
`peer-link.swift` holds `PeerLink` (sockets + connection management), compiled into the test target
too so the link is tested over loopback (`listenPort`/`advertise:` exist for that). `update-logic.swift`
is in the test target as well; `updater.swift` (URLSession, `osascript`, `launchctl`) is not. Updater
state lives on the main queue; network, disk and process work runs on its own queue. `mac/Package.swift`
exists **only** for `make test` (`swift test`, tests in `mac/Tests/PeerLogicTests`). Keep new
testable logic pure and put it in `peer-logic.swift`; UI and display code stays in `display-toggle.swift`.
`PeerLink` logs to the unified log: `log stream --predicate 'subsystem == "com.display-toggle"'`.

Windows — run from `windows/` (`make` targets mirror the Mac set; OS-specific steps live in
`windows/scripts/*.ps1`):

```sh
cd windows
make                  # dotnet build -c Release
make clean            # remove bin/obj
make install          # publish to %LOCALAPPDATA%\display-toggle + Startup-folder autostart shortcut
make uninstall        # remove install + autostart shortcut
make run              # stop instance, build, launch (run.ps1 stops first to release the exe lock)
make test             # unit + loopback tests (DisplayToggle.Tests)
```

The tray app is single-instance (`Global\display-toggle.singleton`). `display-toggle --quit` and
the scripts stop it through the `Global\display-toggle.quit` event, so it removes its tray icon
(a force-kill leaves a ghost icon). `--toggle` / `--extend` / `--internal` hand the switch to the
running tray through `Global\display-toggle.<cmd>` events (so the peer is told and config.json
isn't written behind its back), and only switch directly when no tray is running. Settings are
saved atomically (temp file + move); a file that can't be read is left alone (`ReadOnly`), and a
corrupt one is kept as `config.json.bad-<timestamp>`. Autostart is a Startup-folder shortcut, so
there's no restart after a crash.

## Architecture

The Mac app is a menu bar app (`NSApplication` with `.accessory` activation policy — no Dock icon). UI, display control and discovery live in `display-toggle.swift`; the pure peer logic is in `peer-logic.swift` and the peer link in `peer-link.swift`. A second instance exits at once: `main()` takes an exclusive `flock` on `display_toggle.lock` in the per-user temp dir.

**Layers, top to bottom:**

1. **Private API loading** — `dlopen`/`dlsym` at runtime, no private headers needed.
   - macOS 15+/26: `CGSConfigureDisplayEnabled(CGDisplayConfigRef?, CGDirectDisplayID, Bool) -> CGError` from CoreGraphics, wrapped in a `CGBeginDisplayConfiguration` / `CGCompleteDisplayConfiguration(.permanently)` transaction.
   - macOS 13–14 fallback: `CoreDisplay_Display_SetUserEnabled(CGDirectDisplayID, Bool)` from CoreDisplay.
   - `cgsConfigureEnabled` is tried first; `cdSetEnabled` is the fallback.

2. **Display discovery** — `CGGetOnlineDisplayList` to enumerate connected displays; `CGDisplayIsBuiltin() == 0` (returns `boolean_t` / `Int32`, not Swift `Bool`) to identify externals.

3. **State persistence** — `UserDefaults(suiteName: "com.display-toggle")`, stored in `~/Library/Preferences/com.display-toggle.plist`. Keys: `on` (Bool), `ids` ([Int] of `CGDirectDisplayID`s), `peerHost`/`peerPort`/`peerSecret` for the peer link, `instanceId`/`peerId` for discovery, `peerNone` (Bool) for *Choose peer ▸ None (standalone)*, which stops the link and auto-pick (Windows: `Settings.PeerNone`), and `updateNotified` (String), the last release version we showed an update notification for (Windows: `Settings.LastNotifiedVersion`, kept in memory even when the config is `ReadOnly`). IDs are stored on disable so they can be re-enabled even if the display drops off the online list while software-disabled.

4. **AppDelegate** — `NSStatusItem` in `NSStatusBar.system`. Left-click calls `performToggle()`; right-click calls `showMenu()` which sets `statusItem.menu`, calls `performClick`, then clears the menu asynchronously so subsequent left-clicks are not intercepted by it.

   `AppDelegate.isOn` tracks *intent* (last toggle direction), not a live hardware probe. The icon and right-click label reflect this value. When no external display is online (a software-disabled one drops off the list), `aggregateOn()` falls back to the saved `on` state rather than probing hardware. `setExternals(to:)` applies a target state to all externals; `performToggle()` calls it and then `peer.sendState(isOn)`, while `applyRemoteState(peerOn:)` calls it *without* broadcasting. The per-display switch rows (`toggleDisplayFromView`) flip one display and broadcast, and do nothing (beep) if the switch fails.

   `PeerLink` (Network.framework) runs the TCP peer on its own `DispatchQueue`; callbacks hop to the main queue (`onConnection`, `onPeerState`, and `onProblem` for the *needs update* / *not authenticated* / *requires a secret* menu statuses). `applyRemoteState` applies the *inverse* of the peer's state and only when it differs, so remote-driven changes never echo.

5. **Icon** — ON: `"display"` SF Symbol as template image. OFF: same symbol with `NSImage.SymbolConfiguration(paletteColors: [.tertiaryLabelColor])` for a faded appearance.

## Install / autostart

`make install` generates `~/Library/LaunchAgents/com.<username>.display-toggle.plist` from `launch-agent.plist` (template) using `sed`, then `launchctl bootout`s any existing agent and `launchctl bootstrap`s it into `gui/<uid>` — so a re-install stops the old instance and launches the new binary immediately (via `RunAtLoad`), the launchd analogue of Windows' stop+relaunch. `KeepAlive` `{SuccessfulExit: false}` restarts it after a crash but not after Quit; self-update relies on it too (it restarts via `launchctl kickstart -k`, and if that fails exits non-zero so launchd restarts the new binary). Uses `SUDO_USER` env var to resolve the real user when invoked with `sudo`. Template variables in `launch-agent.plist`: `DISPLAY_TOGGLE_LABEL` → `com.<username>.display-toggle`, `DISPLAY_TOGGLE_BINPATH` → install path.
