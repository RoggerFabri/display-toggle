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
make install          # install to ~/.local/bin and start it now + at every login
make uninstall        # stop it and remove the binary and the login agent
```

`make install` needs no `sudo`: the binary goes to `~/.local/bin` (`PREFIX=…` to change it), and the
launch agent and settings belong to your user too. Because you own the binary, updates replace it
without an administrator prompt. Re-running it replaces the binary and restarts the app at once.
Installs made before this went to `/usr/local/bin`; `make install` and `make uninstall` delete that
old copy, or print the `sudo rm` to run when they can't.

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

`make install` registers a launch agent (`~/Library/LaunchAgents/com.<user>.display-toggle.plist`)
that starts the app at login. launchd restarts it if it crashes, but not after you choose **Quit**.
`make uninstall` removes it.

## Updates

The menu shows the running version (*Version X.Y.Z*; a local build says `0.0.0-dev`). The installed
copy looks for a new release on GitHub a minute after launch and then every 6 hours, whatever its
version: after `make install` (a `0.0.0-dev` build, older than every release) it checks by itself
and is offered the latest release. A build run from `mac/` (`make run`) never checks by itself.
*Check for updates* checks on demand anywhere and says what it found. Nothing installs on its own.
The first install is `make install`, or the release binary put at `~/.local/bin/display-toggle`.

When a newer release is found, the menu gets a top line *Update available: vX.Y.Z — Install and
restart*, and macOS shows one notification for that version (sent through `osascript`, so it may
appear under *Script Editor*; a bare binary can't post its own notifications). Clicking the line
downloads the macOS archive, checks it against the release's signed manifest (ECDSA P-256 signature,
then size + SHA-256), makes sure the new binary starts and reports the expected version, then
replaces the installed binary (asking for an administrator password only when its folder isn't
writable, e.g. a root-owned `/usr/local/bin`) and restarts through the launch agent. If anything
fails, the installed copy is left as it was and an alert says why.

Only the installed copy (`…/bin/display-toggle`) updates itself. A build run from `mac/` points you
to the release instead: update the checkout and run `make install`.

Updates keep macOS's **Local Network** permission: every build is signed with the same
certificate, which macOS recognises as the same app (see *Code signing* below).

Release builds: `make VERSION=X.Y.Z` stamps the version into the embedded Info.plist, and
`make dist VERSION=X.Y.Z` builds the release asset `dist/display-toggle-macos.tar.gz` (a universal
arm64 + x86_64 binary for macOS 13+, signed with that certificate). `display-toggle --version`
prints the version.

## Code signing

macOS keys an app's privacy permissions to its code signature. An ad-hoc signature is a hash of one
build, so after each rebuild or update **Local Network** access can be asked for again, and the peer
link stops until it's allowed. So every build is signed with one self-signed certificate,
**display-toggle code signing**, and the permission granted once holds for later builds. (The
macOS firewall's *allow incoming connections* rule, by contrast, held across ad-hoc rebuilds when
tested on macOS 27.2.)

`mac/scripts/new-signing-identity.sh` creates it: it imports the identity into your login keychain,
for `make install`, and stores it as the `MACOS_SIGNING_P12` / `MACOS_SIGNING_P12_PASSWORD`
secrets of the `release` environment, for the release workflow. Run it once, and on another Mac
export the identity from Keychain Access (`.p12`) and import it there. Without it in a keychain,
`make` signs ad-hoc and says so. A new certificate counts as a new app.

## Notes

- Settings live in `~/Library/Preferences/com.display-toggle.plist`: the on/off intent, the
  displays turned off (so they can be turned back on after they drop off the display list), and the
  peer, including the shared secret in plain text. macOS can give a monitor a new display id after a
  reboot or upgrade, so each turned-off display is saved with its vendor/model/serial and followed to
  its new id; an id that no longer belongs to it is forgotten.
- Single instance: a second launch exits at once (a lock file in your temp dir).
- The binary embeds an `Info.plist` (Bonjour service + Local Network usage) instead of being an
  `.app` bundle, and `make install` signs it (see *Code signing*).
- Logs: `log stream --predicate 'subsystem == "com.display-toggle"'` live, or
  `log show --last 1h --predicate 'subsystem == "com.display-toggle"'` for display switches after
  the fact (each one with its result; the peer link's own lines are live only).
- When the peer releases the monitor but switching it on here fails (seen with the screen asleep and
  locked), the menu shows *Peer asked for on: refused* with the error, and the switch is retried when
  the screen wakes or unlocks, or the displays change.
