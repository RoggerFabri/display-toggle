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
sudo make install # build + install + login autostart (restarted after a crash)
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
