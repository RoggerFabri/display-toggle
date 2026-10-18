# display-toggle (macOS)

A menu-bar app that turns the external monitor(s) on and off by software-disabling them, the way
unplugging would, without touching the cable. It links to a peer instance (the Windows laptop)
so the two share one physical monitor: see [`../PROTOCOL.md`](../PROTOCOL.md).

- **Left-click** the menu-bar icon → toggle every external display on/off.
- **Right-click** → a switch per external display (including ones that are currently off), this
  Mac's IP, peer status, *Choose peer ▸*, the version, *Check for updates*, and *Quit*.
- The icon is faded while the externals are off; a **green dot** means the peer is linked.

Turning a display **off** drops its signal, so the shared monitor switches to the other machine's
input; turning it **on** brings the Mac's signal back.

## Requirements

- macOS 13 or later (15+ and 26 use `CGSConfigureDisplayEnabled`; 13–14 use CoreDisplay's
  `SetUserEnabled`). Both are private APIs, loaded at runtime with `dlopen`.
- Xcode Command Line Tools (`xcode-select --install`): the app is built with plain `swiftc`, no
  Xcode project.

## Build & run

```sh
cd mac
make                  # build ./display-toggle
make run              # build and run locally (stops a running instance first)
make test             # unit tests (Swift Testing) + real peer links over loopback
make clean            # remove the binary and .build
sudo make install     # install to /usr/local/bin and start it now + at every login
sudo make uninstall   # stop it and remove the binary and the login agent
```

`make install` needs `sudo` only to write `/usr/local/bin`. Everything else (the launch agent,
settings) belongs to your user. Re-running it replaces the binary and restarts the app at once.

## Pairing

1. Run the app on both machines.
2. They find each other on the LAN (Bonjour / DNS-SD). With no peer set and exactly one other
   instance found, it pairs automatically. Otherwise pick it from **Choose peer ▸**, or use
   **Set peer manually…** with the other machine's IP/hostname and the same port (default `51763`).
3. Optionally set the **same shared secret** on both machines in **Set peer manually…**. Each
   connection is then authenticated and every message is MACed (protocol v2). Without one, anyone
   on the LAN could flip your displays. Blank = open mode.
4. Toggle on either side; the other mirrors inversely (one off ⇒ other on).

The first time it looks for peers, macOS asks to allow **Local Network** access. Allow it, or
discovery and the link won't work (*System Settings ▸ Privacy & Security ▸ Local Network*).

*Choose peer ▸ None (standalone)* stops the link: no listening, advertising or dialling. If the link
can't come up, the peer line says why: *needs update* (the peer runs an older protocol, so update
both apps together), *not authenticated* (its secret is missing or different), or *requires a
secret* (it has one and this side doesn't).

## Autostart

`sudo make install` registers a launch agent (`~/Library/LaunchAgents/com.<user>.display-toggle.plist`)
that starts the app at login. launchd restarts it if it crashes, but not after you choose **Quit**.
`sudo make uninstall` removes it.

## Updates

The menu shows the running version (*Version X.Y.Z*; a local build says `0.0.0-dev`). Released
builds look for a new release on GitHub a minute after launch and then every 6 hours; *Check for
updates* checks on demand (dev builds included) and says what it found. Nothing installs on its own.

When a newer release is found, the menu gets a top line *Update available: vX.Y.Z — Install and
restart*, and macOS shows one notification for that version (sent through `osascript`, so it may
appear under *Script Editor*; a bare binary can't post its own notifications). Clicking the line
downloads the macOS archive, checks it against the release's signed manifest (ECDSA P-256 signature,
then size + SHA-256), makes sure the new binary starts and reports the expected version, then
replaces the installed binary (asking for an administrator password when `/usr/local/bin` isn't
writable) and restarts through the launch agent. If anything fails, the installed copy is left as
it was and an alert says why.

Only the installed copy (`…/bin/display-toggle`) updates itself. A build run from `mac/` points you
to the release instead: update the checkout and run `sudo make install`.

After an update macOS may ask again for **Local Network** access, because the replaced binary is
ad-hoc signed and counts as a new app. Allow it, or the peer link stops working.

Release builds: `make VERSION=X.Y.Z` stamps the version into the embedded Info.plist, and
`make dist VERSION=X.Y.Z` builds the release asset `dist/display-toggle-macos.tar.gz` (a universal
arm64 + x86_64 binary for macOS 13+, ad-hoc signed). `display-toggle --version` prints the version.

## Notes

- Settings live in `~/Library/Preferences/com.display-toggle.plist`: the on/off intent, the ids of
  displays turned off (so they can be turned back on after they drop off the display list), and the
  peer, including the shared secret in plain text.
- Single instance: a second launch exits at once (a lock file in your temp dir).
- The binary embeds an `Info.plist` (Bonjour service + Local Network usage) instead of being an
  `.app` bundle, and `make install` ad-hoc signs it.
- Peer-link log: `log stream --predicate 'subsystem == "com.display-toggle"'`.
