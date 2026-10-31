# display-toggle

Toggle a shared external monitor on/off from the menu bar (macOS) or system tray (Windows) —
and optionally **link the two machines** so the monitor hands off between them: when one releases
the display, the other grabs it.

## Layout

| Path | What |
|------|------|
| [`mac/`](mac/) | macOS menu-bar app (Swift, built with `swiftc` from the stock Command Line Tools — no Xcode project). |
| [`windows/`](windows/) | Windows system-tray app (C#/.NET WinForms). |
| [`PROTOCOL.md`](PROTOCOL.md) | The peer-link wire protocol (v2) both apps implement. |

Each app:

- **Left-click** the icon → toggle the external monitor on/off.
- **Right-click** → a toggle row per external monitor, this machine's IP, peer status,
  **Choose peer ▸** (peers found on the LAN, *None (standalone)*, *Set peer manually…*), quit.
- A **green dot** on the icon means a peer is linked (and, with a shared secret, authenticated).

## The handoff

The two apps hold a persistent TCP connection and mirror **inversely** — one releases the shared
monitor, the other grabs it. Each side both listens and dials, so the peer only has to be known on
**one** side. The shared monitor auto-selects whichever input has a live signal, so releasing on
one machine lets the other's grab take over. See [`PROTOCOL.md`](PROTOCOL.md).

```
        Mac                    shared monitor                Windows
   ┌───────────┐            ┌─────────────────┐           ┌───────────┐
   │  ▣ ON     │════════════│                 │           │  ▢ OFF    │
   │  (grab)   │  signal ✓  │   showing Mac   │  no signal│ (release) │
   └─────┬─────┘            └─────────────────┘           └─────┬─────┘
         │                                                      │
         └─────────────────── peer link (LAN) ──────────────────┘

                    click either icon  ⇅  they swap

   ┌───────────┐            ┌─────────────────┐           ┌───────────┐
   │  ▢ OFF    │  no signal │                 │  signal ✓ │  ▣ ON     │
   │ (release) │            │ showing Windows │═══════════│  (grab)   │
   └─────┬─────┘            └─────────────────┘           └─────┬─────┘
         │                                                      │
         └─────────────────── peer link (LAN) ──────────────────┘
```

- **Click** either app's icon: that machine releases (or grabs) the monitor.
- The **peer link** tells the other machine, which does the opposite, so exactly one machine has it.
- The monitor shows whichever input has a live signal.

## Quick start

**macOS** (see [`mac/README.md`](mac/README.md)):

```sh
cd mac
make              # build
make install      # build + install to ~/.local/bin + login autostart (restarted after a crash)
make run          # build and run locally
make test         # unit + loopback tests
```

**Windows** (see [`windows/README.md`](windows/README.md)):

```sh
cd windows
make              # build (Release)
make install      # publish to %LOCALAPPDATA% + login autostart
make run          # stop any instance, build, and launch
make test         # unit + loopback tests
```

Both machines must run a **v2** build: an older peer is refused and shown as *needs update*, so
update both together.

The Windows toggle switches the display topology (extend ⇄ internal-only, the same as Win+P) via
the CCD API, so it's built for a laptop with an internal panel plus the shared external monitor.

## Releases and updates

Releases are automatic: every push to `main` that changes app code (the Mac sources and build
files, or `windows/` outside its tests and docs) publishes the next patch version as a
[GitHub Release](https://github.com/RoggerFabri/display-toggle/releases) — a Windows zip, a
universal macOS binary, and a `manifest.json` with each asset's SHA-256, **signed** with the
release key both apps embed.

Installed apps check for a new release a minute after start and then every 6 hours (or on demand
with **Check for updates**). They only trust a manifest whose signature verifies, and never update
on their own: a newer version shows as **Update available: vX.Y.Z — Install and restart** at the top
of the menu, plus one notification. Clicking it downloads the asset, checks its size and hash
against the signed manifest, replaces the installed app and restarts it; on any failure the current
install is left as it was. Automatic checks run for the installed copy whatever its version: after
`make install` (which builds `0.0.0-dev`, older than every release) the app checks by itself and is
offered the latest release. A build run from a checkout (`make run`) never checks automatically.

The **first install is still manual**: `make install` from a checkout (above), which also sets up
login autostart, or the asset from the latest release put where `make install` would put it
(Windows: unzip into `%LOCALAPPDATA%\display-toggle`; Mac: `~/.local/bin/display-toggle`), since
only that installed copy checks for and installs updates. On the Mac, expect macOS to ask for Local Network
access again after an update.

To cut a **minor or major** release (or re-release without app changes): **Actions → release →
Run workflow** on `main`, and pick the **bump** (`patch` / `minor` / `major`).

## Pairing

1. Run the app on both machines.
2. They find each other on the LAN (DNS-SD). With no peer set and exactly one other instance
   found, it pairs automatically. Otherwise pick it from **Choose peer ▸**, or use
   **Set peer manually…** with the IP shown in the other machine's menu
   (**This Mac/PC: `<ip>:<port>`**) and the same port (default `51763`). Allow the port through the
   firewall if prompted.
3. Toggle on either side; the other mirrors inversely. The green dot confirms the link.

**Choose peer ▸ None (standalone)** stops the link entirely (no listening, advertising or dialling);
picking a peer again turns it back on.

**Optional — authenticate the link.** The peer port is open on the LAN, so anyone on your network
could otherwise flip your displays, and discovery records can be spoofed. In **Set peer manually…**, set the
**same shared secret** on both machines. Each connection then derives a session key from the secret
and both sides' random nonces, each side proves it knows the secret, and every later message is
MACed and sequence-numbered, so nothing can be forged, replayed or relayed. A connection that
hasn't proved the secret can't take over or drop a working link. Leave it blank to run open.

If the link can't come up, the peer line in the menu says why: *needs update* (the other side runs
an older protocol), *not authenticated* (its secret is missing or different), or *requires a
secret* (it has one and this side doesn't).

The secret is stored in plain text in each app's settings (Mac:
`~/Library/Preferences/com.display-toggle.plist`, Windows: `%APPDATA%\display-toggle\config.json`),
readable by your user account. See [`PROTOCOL.md`](PROTOCOL.md).
