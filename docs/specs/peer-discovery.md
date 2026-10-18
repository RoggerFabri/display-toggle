# Spec — automatic peer discovery

Status: draft (2026-10-17)

## Goal

Let a user pick the peer from a list of instances found on the LAN instead of typing an IP.
Manual *Set peer…* stays as the fallback (multicast-blocking networks, VLANs, VPNs).

## Non-goals

- No change to the peer-link wire protocol (`hello` / `state` / `auth` / `ping` / `pong`).
- Discovery is **not** trust. Authentication stays with the optional shared secret (HMAC).
- No cross-subnet discovery (mDNS is link-local).

## Mechanism — DNS-SD over mDNS

Each instance advertises its existing TCP listener as a DNS-SD service and browses for others.

| item | value |
|------|-------|
| service type | `_display-toggle._tcp` |
| domain | `local.` |
| instance name | computer name (e.g. `J. Example Mac`, `WIN-LAPTOP`) |
| port | the configured peer port (default `51763`) |
| TXT `v` | `1` (discovery record version) |
| TXT `id` | random UUID generated once and persisted (`instanceId`); used to skip self and to track a peer across IP changes |
| TXT `os` | `mac` / `win` |

The TXT record never carries the secret or display state.

### Self-filtering

Drop any result whose TXT `id` equals our own `instanceId`. Ids compare **case-insensitively**
(Mac UUIDs are uppercase, .NET Guids lowercase), and TXT keys are case-insensitive. If `id` is
missing or empty, fall back to comparing the instance name with our computer name.

### Resolution

A discovered entry is resolved to an IPv4 address and port. IPv4 is preferred because the
peer link today dials `host:port`, and link-local IPv6 needs a scope ID. Entries that don't resolve
are hidden.

**Resolve with the DNS-SD API. Never resolve by opening a TCP connection to the peer.** The peer's
listener adopts every inbound connection and drops its current one, so a probe connection would
break a live link. Mac: `DNSServiceResolve` + `DNSServiceGetAddrInfo` (dnssd), re-resolved at most
every 10s. Windows: the SRV/A records from its own legacy-unicast query replies (see Validation order).

## Persistence

Two new keys, next to `peerHost` / `peerPort`:

- `instanceId` (string UUID) — our own ID, created on first launch.
- `peerId` (string) — the TXT `id` of the peer chosen from the list. It is empty when the peer
  was entered manually.

When a peer is chosen from the list, `peerHost` (the resolved IP), `peerPort` and `peerId` are written.
Then the existing `startPeer()` / `StartPeer()` runs. **IP-change follow:** if browsing later sees
the same `peerId` at a different address or port **while the link is disconnected**, update
`peerHost` / `peerPort` and restart the link silently. Never follow while connected, so a
multi-homed peer can't cause churn. *Set peer…* clears `peerId`.

Mac: `UserDefaults(suiteName: "com.display-toggle")`. Windows: `Settings` (`config.json`).

## Lifecycle

- Advertising runs whenever the listener runs.
- Browsing is continuous while the app runs. mDNS browsing is cheap, and the menu shows a
  current list the moment it opens. Results are held in an in-memory map `id → (name, host, port, os)`.
  Removals from the browser drop entries.
- **Auto-pick:** if `peerHost` is empty and exactly one peer has been discovered, adopt it
  automatically (same as choosing it). Windows shows a tray balloon. The Mac has no notification
  (an unbundled binary can't post them), so the menu's status line is enough. Never auto-replace a
  configured peer, and never auto-pick while the user has chosen **None (standalone)**.

## UI

Replace the single *Set peer…* item with a submenu (menus can't hold a combo box; a submenu is
the native equivalent on both OSes):

```
Peer: WIN-LAPTOP ✓              (existing status line, unchanged)
Choose peer ▸   ✓ WIN-LAPTOP — 198.51.100.20
                  other-mac — 198.51.100.31
                  ──────────
                  Searching…            (shown while list is empty, disabled)
                  Set peer manually…
```

- The current peer is checkmarked (matched by `peerId`, else by `peerHost:peerPort`).
- Choosing an entry applies it immediately (no dialog). The secret is unchanged; if the peer
  needs a secret, the user sets it with *Set peer manually…*, as today.
- There is one port setting, used to listen and dial, as today. Choosing an entry sets `peerPort` to
  the advertised port. Both sides are expected to use the same port.

## Platform implementation

### macOS (`mac/display-toggle.swift`)

- **Advertise:** set `listener.service = NWListener.Service(name: hostName, type: "_display-toggle._tcp", txtRecord: …)`
  on the existing `NWListener` in `PeerLink.startListener()`.
- **Browse:** a new `PeerBrowser` class wrapping `NWBrowser(for: .bonjourWithTXTRecord(type: "_display-toggle._tcp", domain: nil), using: .tcp)`,
  on its own queue, delivering a `[DiscoveredPeer]` snapshot to the main queue.
- **Resolve:** `NWBrowser` yields `.service` endpoints. A short-lived `NWConnection` to the endpoint
  resolves it (read `currentPath?.remoteEndpoint` once ready, then cancel). Alternatively, store the
  endpoint itself, but the persisted `peerHost` must remain a plain IP so the Windows-compatible
  dial path is unchanged.
- **Local Network privacy (macOS 15+):** Bonjour browsing requires `NSBonjourServices`
  (`_display-toggle._tcp`) and `NSLocalNetworkUsageDescription` in the process's Info.plist.
  The binary is unbundled, so embed one at link time:
  `swiftc … -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker Info.plist`
  (new `mac/Info.plist`, Makefile + CI updated).
  *Spike result (2026-10-17):* `NWBrowser` run as a launchd agent found both a local
  advertisement and the Windows probe (`WIN-PROBE`, TXT intact, removal delivered), **with and
  without** the embedded plist. We embed it anyway: it costs nothing and covers stricter enforcement
  on other macOS versions or machines.

### Windows (`windows/`)

- **Advertise:** P/Invoke `dnsapi.dll` `DnsServiceRegister` / `DnsServiceDeRegister` with
  `DnsServiceConstructInstance` / `DnsServiceFreeInstance` (Windows 10 1809+).
- **Discover:** a legacy-unicast PTR query (QM, to 224.0.0.251:5353 from an ephemeral port) every ~15s
  per up IPv4 interface, parsing PTR/SRV/TXT/A. Entries expire after 2 missed cycles. `DnsServiceBrowse`
  is kept only for goodbyes (PTR TTL 0), because it never reports adds here (see Validation order).
  dnsapi repeats our own PTR many times a second, so it must not trigger extra queries.
- **Code:** `PeerDiscovery.cs` (interop + query loop) raises events that `TrayApp` marshals to the UI
  thread via `Post`. `PeerDiscoveryLogic.cs` holds the pure rules (IsSelf, PickAddress, DecideAdopt,
  IsCurrent, PeerTable, MdnsPacket), unit-tested in `windows/DisplayToggle.Tests`.
- **Firewall:** mDNS is UDP 5353, handled by the Windows DNS Client service, which already has
  built-in "mDNS (UDP-In)" rules for the Private profile. Confirm on a real machine. `install.ps1`
  only adds a rule if this is missing.
- **Network profile:** discovery (and the existing peer-link rule) assumes the network is
  **Private**. Show "Searching…" with a hint when the profile is Public.
- **Menu:** `TrayApp.RebuildMenu()` gets the `Choose peer` `ToolStripMenuItem` with
  `DropDownItems`. `Settings` gets `InstanceId` and `PeerId`.

## Protocol doc

Add a "Discovery" section to `PROTOCOL.md` with the service type, TXT keys, self-filter and
IP-follow rules above.

## Testing

- Both machines on the same Wi-Fi/LAN. The Mac lists Windows within ~2s. Windows lists a newly
  started Mac within ~15s (one query cycle). Neither lists itself.
- Choose a peer, and the link connects (status ✓). With a secret configured on both, the link authenticates.
- Change the peer's IP (DHCP renew / switch interface), and the link follows via `peerId` without user action.
- Fresh install with no peer set and exactly one other instance: it is auto-picked once.
- Multicast blocked (guest network): the list stays "Searching…" and manual entry still works.
- Quit one instance, and it disappears from the other's list.

## Ownership

- macOS side + `PROTOCOL.md` + this spec: Mac session.
- Windows side (`windows/`): Windows laptop session.
- The Mac session reviews both sides once each is done.

## Validation order

Steps 1–2 were validated on 2026-10-17. Both machines see each other's probes, and removals arrive.
Windows findings (the Windows laptop): the network is Private, and the built-in mDNS rule is enabled for all
profiles, so `install.ps1` doesn't change. Apple Bonjour is installed and owns UDP 5353, and dnsapi still works
alongside it. The Windows browse reports removals (PTR TTL 0) but **not** adds announced after the browse started.
Root cause: dnsapi asks for unicast (QU) replies to port 5353, which Bonjour (and other mDNS apps) own on that
machine, so the DNS Client never sees them; only multicast goodbyes get through. Windows therefore
discovers peers with its own legacy-unicast PTR query (RFC 6762 §6.7) every ~15s per up IPv4 interface,
parses PTR/SRV/TXT/A, expires an entry after 2 missed cycles, and keeps the dnsapi browse for goodbyes only. Resolve returns A records for
every interface (VPN, WSL/Hyper-V vEthernet), so **both** sides prefer an address on the receiving
interface's subnet.

1. Mac: embedded Info.plist + `NWBrowser` sees *any* `_display-toggle._tcp` advertisement (spike).
2. Windows: `DnsServiceRegister` advertisement visible from the Mac (`dns-sd -B _display-toggle._tcp`),
   and `DnsServiceBrowse` sees the Mac. Check the firewall and network profile.
3. Full implementation on both sides plus the `PROTOCOL.md` update.
