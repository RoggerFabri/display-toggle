# Display Toggle — peer link protocol

Two instances (one Mac, one Windows) coordinate a **single shared monitor** that is
physically wired to both machines (dual input). The monitor auto-selects whichever
input carries a live signal, so:

- **grab**  = enable my output → my signal goes live → monitor shows me
- **release** = disable my output → my signal drops → monitor flips to the other machine

The two peers mirror each other **inversely**: when one releases, the other grabs.

## Transport

- **TCP**, UTF-8, **newline-delimited JSON** (one object per line). Protocol version **2**: every
  message carries `"v":2`.
- Symmetric peers. Each instance both **listens** on `port` and **dials** `peerHost:port`,
  retrying the dial every few seconds while it has no active link. On a connection, the side that
  opened it is the **dialer** (role `d`), the side that accepted it is the **listener** (role `l`).
- At most one connection is **active** (used for `state`). A new connection only replaces the
  active one once it is established (see *Connection management*), so a stranger connecting can't
  tear down a working link.
- Default port: **51763** (override per instance).

## Discovery (DNS-SD)

Each instance advertises its listener over mDNS / DNS-SD so the other can be picked from a list
instead of typing an IP. Discovery is a convenience, **not trust**: anyone on the LAN can advertise
another instance's `id` at their own address. With a shared secret set, a spoofed record can at
worst make us dial the wrong host and fail authentication (a denial of service); without one,
whoever we connect to can drive our display. Set a secret on networks you don't control.

- Service type `_display-toggle._tcp`, domain `local.`. The instance name is the computer name.
  The port is the listener port.
- TXT record: `v=1` (record version), `id=<uuid>` (persistent per install), `os=mac|win`. It never
  carries the secret or the display state.
- TXT keys are case-insensitive (RFC 6763 §6.4). `id` values compare **case-insensitively**: the Mac
  advertises uppercase UUIDs, Windows lowercase. An empty `id` counts as missing.
- Skip results whose `id` is your own. Without an `id`, fall back to comparing the instance name.
- Resolve to an IPv4 address. A host may publish addresses for VPN or virtual interfaces too, so
  prefer one on a local subnet: the Mac uses the subnets of the interface the service was seen on,
  and Windows uses all local IPv4 subnets with the default-route interface first. Never pick
  169.254/16. With no on-subnet match, take the first remaining address. **Resolve with the
  DNS-SD API or mDNS records, never with a TCP probe**: a probe would occupy one of the peer's
  pending-connection slots for up to 10s and show up as a failed handshake.
- Choosing a peer stores its address, port and `id`. While the link is **down**, if the same `id`
  shows up at a new address or port, follow it. If no peer is configured and exactly one is
  discovered, adopt it automatically.
- Choosing **None (standalone)** clears the peer and stops the link: no listener, no advertising,
  no dialling, no auto-pick. Browsing continues, so peers still appear in the menu. Choosing a
  peer or setting one manually leaves standalone mode.
- Responders must answer **legacy unicast** queries (RFC 6762 §6.7, sent from a port other than 5353).
  Windows discovers peers that way, because its DNS-SD browse asks for unicast (QU) replies to
  port 5353. When Apple Bonjour or a browser already owns that port, the DNS Client never receives
  those replies. Windows therefore sends its own PTR query every ~15s per IPv4 interface: a QM query
  to 224.0.0.251:5353 from an ephemeral port. It reads PTR/SRV/TXT/A from the replies, plus the
  reply's source IP as an address candidate. Legacy replies carry TTL ≤ 10, so Windows expires an
  entry after 2 missed query cycles rather than by record TTL. It uses the DNS-SD browse only for
  goodbyes (TTL 0). A newly
  started Mac can take up to ~15s to appear there. mDNSResponder on the Mac answers legacy unicast
  out of the box.

## Messages

Every message is a single-line JSON object, `\n`-terminated. Field order is not significant.

| field   | in            | meaning |
|---------|---------------|---------|
| `v`     | all           | protocol version, `2` |
| `type`  | all           | `hello` / `auth` / `state` / `ping` / `pong` |
| `host`  | `hello`       | sender's hostname (informational, shown in status) |
| `id`    | `hello`       | sender's discovery `id` (the TXT `id`), required, non-empty |
| `nonce` | `hello`       | 16 random bytes as exactly 32 lowercase hex chars, fresh per connection |
| `on`    | `hello`, `state` | sender's display **intent**: `true` = grabbing, `false` = released. Required in `state` |
| `proof` | `auth`        | handshake proof, lowercase hex (secret mode only) |
| `seq`   | `state`, `ping`, `pong` | per-direction counter, 1, 2, 3, … (secret mode only) |
| `mac`   | `state`, `ping`, `pong` | message MAC, lowercase hex (secret mode only) |

```json
{"v":2,"type":"hello","host":"my-mac","id":"4f1c…","on":true,"nonce":"9b0e…"}
{"v":2,"type":"auth","proof":"c41a…"}
{"v":2,"type":"state","on":false,"seq":1,"mac":"07d2…"}
{"v":2,"type":"ping","seq":2,"mac":"5e9f…"}
```

- **`hello`** — sent by both sides immediately on connect. Always carries `nonce` (it also keys
  the session in secret mode).
- **`auth`** — sent once the peer's `hello` has arrived, secret mode only.
- **`state`** — sent on every local toggle, on the active connection.
- **`ping` / `pong`** — liveness heartbeat (~every 15s); any received line resets the stale timer.

The **first** line on a connection must be a v2 `hello`; anything else closes it. After that,
receivers ignore unknown `type`s and lines that aren't JSON objects. A `state` without a boolean
`on` is ignored (it must never be read as `false`). In secret mode its `seq` and `mac` are still
verified first, and the `seq` is consumed, so the next message's `seq` check is unaffected.

`hello`'s `host` and `on` are **informational only** (status display): they are never applied to
hardware. Of the later messages, only the fields covered by `mac` (`type`, `seq`, `on`) are ever
acted on; any other field is ignored.

## Version check

A `hello` whose `v` is missing or not `2` is from an incompatible build: close the connection and
show the peer as *needs update* rather than *offline*. The dialer backs off to ~30s between
attempts to that peer while the mismatch persists. There is no v1 fallback: v1's handshake can be
bypassed (see below), so accepting it would keep the hole open.

## Authentication (optional shared secret)

The listener binds all interfaces, so without auth anyone on the LAN can send a `state` and flip
your displays. Set the **same secret** on both machines.

Notation: `H(k, m)` = HMAC-SHA256 with key `k` over the UTF-8 bytes of `m`, as lowercase hex.
`nD` / `nL` = the dialer's / listener's `hello` nonce, `idD` / `idL` their `hello` ids, all used
verbatim as received (no case folding). `|` is a literal `|`.

1. **Session key**, computed by both sides once both `hello`s are known:
   `K = H(secret, "display-toggle/2 session|" + nD + "|" + nL + "|" + idD + "|" + idL)`
   (the hex string's UTF-8 bytes are the key for the steps below). Binding the ids means a relay
   can't rewrite them to steer the tie-break.
2. **Proof**: each side sends `auth` with `proof = H(K, "auth|" + myRole)` (`d` or `l`).
   A side marks the peer **authenticated** when the received `proof` equals
   `H(K, "auth|" + peerRole)`.
3. **Every later message** (`state`, `ping`, `pong`) carries `seq` and
   `mac = H(K, myRole + "|" + seq + "|" + type + "|" + on)`, where `seq` is in decimal and `on`
   is `1`, `0`, or empty when absent. Each direction counts from 1; a receiver requires `seq` to be
   a non-negative JSON integer equal to last seen + 1, and closes on anything else.
4. A wrong `proof`, or a `state`/`ping`/`pong` before the peer is authenticated, closes the
   connection as *not authenticated*. A wrong `mac` or `seq` after that closes it as a plain
   disconnect. A connection that hasn't authenticated within **10s** of opening is closed.

Comparisons are constant-time over the lowercase hex. A `nonce` that isn't 32 lowercase hex chars,
or a missing/empty `id`, closes the connection.

**Self-reflection checks** (both modes), on receiving the peer's `hello`; either one closes the
connection:

- the peer's `id` equals our own `id` (normalized as in *Discovery*);
- the peer's `nonce` equals a nonce **we** issued on any live or pending connection.

Without them, an attacker who gets us to dial it (spoofed discovery) while it also dials us can
pick its two nonces from our two `hello`s so both sessions share one `K`, then relay between them:
we'd authenticate *ourselves* on both, and every `state` we send would come back on the other
connection and be applied inversely, undoing each toggle. Lining the sessions up requires reusing
our nonce (and, with the ids bound into `K`, our `id`), so the nonce check catches it even if the
attacker lies about `id`.

Why this shape (v1 used `mac = HMAC(secret, peerNonce)`):

- **No signing oracle.** v1 answered `auth` for any `hello`, so an attacker could connect to A,
  hand A's nonce to B in its own `hello`, and forward B's answer back to A — authenticated without
  the secret. In v2 the proof covers both nonces and the sender's role, and the attacker is the
  dialer on both connections it opens, so neither real peer ever produces the proof it needs.
  (If spoofed discovery gets a real peer to dial the attacker, the attacker can line the two
  sessions up, but then it can only relay the genuine peers' messages unchanged; it never learns `K`.)
- **No injection after the handshake.** v1 authenticated only the handshake. In v2 every message
  is MACed under the session key with a sequence number, so an on-path attacker can drop or delay
  traffic (a denial of service no protocol can prevent) but can't forge, reorder or replay it.
  Including the sender's role stops a message being reflected back to its sender.
- The secret never crosses the wire, only HMACs over per-connection nonces.

**Open mode** (no secret on this side): no `auth`, `seq` or `mac`; the peer counts as
authenticated as soon as its `hello` arrives.

**Mixed mode** (only one side has a secret): the secret side never authenticates the other, shows
*peer not authenticated*, and backs off to ~30s between dials like a version mismatch. The open
side receives an `auth` it didn't expect: it closes that connection, shows *peer requires a
secret*, and as dialer backs off to ~30s too. Both sides must use the same secret.

## Connection management

- A connection is **established** once the peer is authenticated (open mode: once its `hello`
  arrives). Only then may it become the active connection, and only then does the UI report the
  peer as connected (green dot / *Peer ✓*).
- While a link is active, a new connection stays *pending* until it is established; if it never
  is (wrong secret, timeout, version mismatch, stranger), it is closed and the active link is
  untouched. Keep at most 4 pending connections; refuse more. The 10s limit applies to every
pending connection, in open mode too (no `hello` within 10s → close). Pending connections are not
pinged.
- **Dialer rule:** a side has at most one outbound dial in flight, and never dials while it has an
  active link. So two connections opened by the same side can only mean the older one is stale.
- **Tie-break** when a second connection becomes established while one is active:
  - If both were opened by the same side, the newer one wins: by the dialer rule the older one is
    stale on the dialer, so both ends agree.
  - Otherwise (one dialled each way, e.g. both sides dialled at once), keep the connection dialled
    by the side whose `id` sorts lower (case-insensitive, normalized as in *Discovery*). Both sides
    know both `id`s and who dialled what, so they make the same choice without talking. If either
    `id` is missing, the newer one wins.
- The losing connection is closed by both sides. A side sends `state` on its active connection
  only, but **accepts** `state` on any established connection, so a toggle sent in the moment
  before the two sides agree on the winner isn't lost. (Don't re-send `state` after a switch
  instead: if the two intents had drifted apart, both re-sends would be applied inversely and
  both sides would end up in the same state.)

## Robustness

- **Max line length** (8 KB): counted per line, including bytes not yet terminated by `\n`; past
  the cap the connection is closed. A receiver must not buffer more than the cap plus one read.
- **Heartbeat + TCP keepalive**: each side pings ~every 15s and drops the link if nothing arrives
  for ~40s, so a silent drop (sleep/wake, Wi-Fi loss) is detected and re-dialled rather than sitting
  as a stale "connected". TCP keepalive: idle 15s, interval 5s, 3 probes.
- A dial that is refused or unreachable is retried on the normal ~3s schedule (don't rely on the OS
  to retry it).

## Test vectors

Both implementations' tests check these exact strings (`H` = HMAC-SHA256, lowercase hex).

```
secret          = "correct horse"
nD              = "00112233445566778899aabbccddeeff"
nL              = "ffeeddccbbaa99887766554433221100"
idD             = "4f1c2b3a-0000-4000-8000-000000000001"
idL             = "9e8d7c6b-0000-4000-8000-000000000002"
K               = 96c594917831a601c95535419dcf0039da5be5dff680fed3faac55ed40b6a9c2
proof (d)       = 9ebc15d5188dd9d5e7257d4bd5e00e65d9818d7cc960f757547422689696aa65
proof (l)       = 7d41a2f69679cbeffaafaf5d91414556f231e86787d85d65f9ec3c972b0fc7cb
mac d|1|state|0 = 0f7ad2edb53a51e9597bb584625d624dafb11913f4e47738acb900e6a251818f
mac l|1|state|1 = 38b16fc774a7c35072c7cc9d800ea51bbd27aa818fab1cdf2fa53ec078fd3099
mac d|2|ping|   = 297973222f8eab884e579e82f3caa77fe91ab95dee6d45ec5c3da5b9b52b46a3
```

Plus behavioural cases: a peer `hello` whose `nonce` equals one we issued → close; whose `id`
equals ours → close; a first line that isn't a v2 `hello` → close; `state` with `seq` repeated,
skipped, negative or non-integer → close; `state` without `on` → ignored.

## Semantics

**On local user toggle** (left-click, per-display switch, etc.):
1. Apply the change to local hardware.
2. Broadcast `{"v":2,"type":"state","on":<newState>}` (plus `seq`/`mac` in secret mode) to the peer.

**On receiving `state` with `on = peerOn`:**
1. Compute `desired = !peerOn`.
2. If local intent ≠ `desired`: apply `desired` to local hardware and update local intent,
   **without broadcasting** (this is what breaks echo loops — remote-driven changes are silent).
3. If already equal: no-op.

**On receiving `hello`:**
- Record the peer's reported state for status display. **Do not** change local hardware.
  (Least-surprise: connecting two machines never spontaneously flips a monitor; the
  inverse-mirroring only kicks in on an actual toggle afterward.)

## Why remote changes are silent

If received `state` messages were re-broadcast, a single toggle would ping-pong forever
(A off → B on → A off → …). Because step 2 above never echoes, the system settles in exactly
one hop: local toggle on A → A broadcasts once → B applies the inverse silently → done.

## Changes from v1

- `v` is `2`; v1 peers are refused with a *needs update* status (no fallback).
- `hello` always carries `nonce`, and now `id`; the first line must be a v2 `hello`.
- Auth: session key over both nonces and both ids, role-bound `proof` (`auth.mac` → `auth.proof`),
  a per-message `seq` + `mac` on everything after the handshake, and self-reflection checks.
  Hex is lowercase.
- A new connection replaces the active one only once established, with a deterministic tie-break;
  "connected" is reported only for an established connection.
- `state` without a boolean `on` is ignored. The 8 KB line cap counts unterminated bytes.
