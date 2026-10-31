// peer-logic.swift
// Pure peer-discovery decisions (no networking, no UI), compiled into the app by `make` and
// unit-tested by `make test` (Swift Testing via mac/Package.swift). Windows mirrors these rules in
// PeerDiscoveryLogic; keep the semantics identical. See PROTOCOL.md "Discovery". Also the
// Mac-only bookkeeping of switched-off displays (end of file).

import CryptoKit
import Foundation

struct DiscoveredPeer: Equatable {
    let id: String
    let name: String
    let host: String   // resolved IPv4
    let port: UInt16
    let os: String
}

/// IPv4 address + netmask of a local interface, host byte order.
struct LocalSubnet: Equatable {
    let addr: UInt32
    let mask: UInt32
}

/// TXT keys are case-insensitive (RFC 6763 §6.4): lowercase them so lookups like `txt["id"]` work.
func normalizedTXT(_ txt: [String: String]) -> [String: String] {
    Dictionary(txt.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { first, _ in first })
}

/// A TXT `id` as used for matching: an empty value counts as missing, and it's lowercased because
/// ids compare case-insensitively (the Mac advertises uppercase UUIDs, .NET Guids are lowercase).
func normalizedID(_ raw: String?) -> String? {
    guard let id = raw?.trimmingCharacters(in: .whitespaces), !id.isEmpty else { return nil }
    return id.lowercased()
}

func sameID(_ a: String, _ b: String) -> Bool {
    a.caseInsensitiveCompare(b) == .orderedSame
}

/// True if a browse result is our own advertisement: match the TXT `id`, or the instance name
/// when the advertiser sent no (or an empty) `id`. Both compare case-insensitively (DNS labels are
/// case-insensitive; Windows does the same).
func isSelf(txtID: String?, name: String, selfID: String, selfName: String) -> Bool {
    if let id = normalizedID(txtID) { return sameID(id, selfID) }
    return name.caseInsensitiveCompare(selfName) == .orderedSame
}

/// Choose among a host's resolved IPv4s (host byte order). A host can publish addresses for every
/// interface (VPN, WSL/Hyper-V vEthernet…), so prefer one on a local subnet, then the first
/// remaining one. Link-local 169.254/16 is never picked.
func pickAddress(_ candidates: [UInt32], localSubnets: [LocalSubnet]) -> UInt32? {
    let usable = candidates.filter { $0 >> 16 != 0xA9FE }
    let onLink = usable.first { ip in localSubnets.contains { ip & $0.mask == $0.addr & $0.mask } }
    return onLink ?? usable.first
}

func ipv4String(_ ip: UInt32) -> String {
    "\(ip >> 24).\(ip >> 16 & 0xFF).\(ip >> 8 & 0xFF).\(ip & 0xFF)"
}

/// The discovered peer to adopt now, if any. Only while disconnected (no churn on multi-homed
/// peers): follow the chosen peer (`peerID`) to a new address/port, or auto-pick the only peer
/// found when no peer is configured at all. Never when the user chose *None* (`standalone`).
func peerToAdopt(connected: Bool, host: String, port: UInt16, peerID: String,
                 discovered: [DiscoveredPeer], standalone: Bool = false) -> DiscoveredPeer? {
    guard !connected, !standalone else { return nil }
    if !peerID.isEmpty {
        guard let p = discovered.first(where: { sameID($0.id, peerID) }) else { return nil }
        return p.host != host || p.port != port ? p : nil
    }
    if host.isEmpty, discovered.count == 1 { return discovered[0] }
    return nil
}

/// Whether `p` is the configured peer (menu checkmark): by id when chosen from discovery,
/// else by host + port for a manually entered peer.
func isCurrentPeer(_ p: DiscoveredPeer, peerID: String, host: String, port: UInt16) -> Bool {
    peerID.isEmpty ? (p.host == host && p.port == port) : sameID(p.id, peerID)
}

// MARK: - Peer link v2 session (PROTOCOL.md "Authentication", "Messages")
// The handshake and per-message checks as a pure state machine: lines in, events out. PeerLink
// (display-toggle.swift) owns the sockets and acts on the events. Windows mirrors this in PeerSession.


enum LinkRole: String {
    case dialer = "d", listener = "l"
    var peer: LinkRole { self == .dialer ? .listener : .dialer }
}

/// HMAC-SHA256 of `message` keyed with `key`'s UTF-8 bytes, as lowercase hex (PROTOCOL `H(k, m)`).
func hmacHex(key: String, _ message: String) -> String {
    let code = HMAC<SHA256>.authenticationCode(for: Data(message.utf8), using: SymmetricKey(data: Data(key.utf8)))
    return code.map { String(format: "%02x", $0) }.joined()
}

func sessionKey(secret: String, nD: String, nL: String, idD: String, idL: String) -> String {
    hmacHex(key: secret, "display-toggle/2 session|\(nD)|\(nL)|\(idD)|\(idL)")
}

func authProof(key: String, role: LinkRole) -> String { hmacHex(key: key, "auth|\(role.rawValue)") }

func messageMAC(key: String, role: LinkRole, seq: Int, type: String, on: Bool?) -> String {
    hmacHex(key: key, "\(role.rawValue)|\(seq)|\(type)|\(on.map { $0 ? "1" : "0" } ?? "")")
}

func constantTimeEquals(_ a: String, _ b: String) -> Bool {
    let x = Array(a.utf8), y = Array(b.utf8)
    guard x.count == y.count else { return false }
    var diff: UInt8 = 0
    for i in x.indices { diff |= x[i] ^ y[i] }
    return diff == 0
}

/// Exactly 32 lowercase hex chars.
func isValidNonce(_ s: String) -> Bool {
    s.utf8.count == 32 && s.utf8.allSatisfy { (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }
}

func randomNonce() -> String {
    var rng = SystemRandomNumberGenerator() // cryptographically secure on Apple platforms
    return (0..<16).map { _ in String(format: "%02x", UInt8.random(in: 0...255, using: &rng)) }.joined()
}

/// One protocol line. Fields of the wrong JSON type read as absent, so a `state` whose `on` isn't a
/// real boolean is ignored rather than read as false, and a non-integer `seq` fails the seq check.
struct WireMessage: Equatable {
    var type: String
    var v: Int? = 2
    var host: String? = nil
    var id: String? = nil
    var nonce: String? = nil
    var on: Bool? = nil
    var proof: String? = nil
    var seq: Int? = nil
    var mac: String? = nil

    /// The message as a JSON line, `\n`-terminated.
    func encoded() -> Data {
        var obj: [String: Any] = ["type": type]
        if let v { obj["v"] = v }
        if let host { obj["host"] = host }
        if let id { obj["id"] = id }
        if let nonce { obj["nonce"] = nonce }
        if let on { obj["on"] = on }
        if let proof { obj["proof"] = proof }
        if let seq { obj["seq"] = seq }
        if let mac { obj["mac"] = mac }
        var data = (try? JSONSerialization.data(withJSONObject: obj, options: .sortedKeys)) ?? Data()
        data.append(0x0A)
        return data
    }

    /// Parse one line (without its `\n`). nil if it isn't a JSON object with a string `type`.
    static func parse(_ line: Data) -> WireMessage? {
        guard let obj = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any],
              let type = obj["type"] as? String else { return nil }
        func string(_ k: String) -> String? { obj[k] as? String }
        return WireMessage(type: type, v: obj["v"].flatMap(jsonInteger), host: string("host"), id: string("id"),
                           nonce: string("nonce"), on: obj["on"].flatMap(jsonBool), proof: string("proof"),
                           seq: obj["seq"].flatMap(jsonInteger), mac: string("mac"))
    }
}

private func isCFBoolean(_ n: NSNumber) -> Bool { CFGetTypeID(n) == CFBooleanGetTypeID() }

// `as? Bool` / `as? Int` would also accept 1 / true / 1.0 through NSNumber bridging.
private func jsonBool(_ v: Any) -> Bool? {
    guard let n = v as? NSNumber, isCFBoolean(n) else { return nil }
    return n.boolValue
}

private func jsonInteger(_ v: Any) -> Int? {
    guard let n = v as? NSNumber, !isCFBoolean(n), !CFNumberIsFloatType(n) else { return nil }
    return n.intValue
}

enum SessionClose: Equatable {
    case needsUpdate        // peer speaks another protocol version
    case notAuthenticated   // wrong proof, or a MACed message before the proof
    case selfReflection     // the "peer" is us, reflected (PROTOCOL "Self-reflection checks")
    case requiresSecret     // open mode: the peer sent an auth, so it has a secret and we don't
    case protocolError      // bad first line, bad nonce/id, bad seq/mac, duplicate hello/auth
}

enum SessionEvent: Equatable {
    case send(WireMessage)
    case established        // the peer is authenticated (open mode: its hello arrived)
    case peerState(Bool)
    case close(SessionClose)
}

/// One connection's v2 session. Not thread-safe: drive it from one queue.
final class PeerSession {
    let role: LinkRole
    let myID: String
    let myNonce: String
    private let secret: String
    private let issuedNonces: () -> Set<String>

    private(set) var helloReceived = false
    private(set) var established = false
    private(set) var peerHost: String?
    private(set) var peerID: String?
    private var key: String?
    private var txSeq = 0
    private var rxSeq = 0

    /// `issuedNonces` returns the nonces we issued on every live or pending connection.
    init(role: LinkRole, secret: String, myID: String, myNonce: String = randomNonce(),
         issuedNonces: @escaping () -> Set<String> = { [] }) {
        self.role = role
        self.secret = secret
        self.myID = myID
        self.myNonce = myNonce
        self.issuedNonces = issuedNonces
    }

    func hello(host: String, on: Bool) -> WireMessage {
        WireMessage(type: "hello", host: host, id: myID, nonce: myNonce, on: on)
    }

    /// A `state` / `ping` / `pong` to send, with `seq` + `mac` once the session is keyed.
    func outgoing(_ type: String, on: Bool? = nil) -> WireMessage {
        var m = WireMessage(type: type, on: on)
        if let key {
            txSeq += 1
            m.seq = txSeq
            m.mac = messageMAC(key: key, role: role, seq: txSeq, type: type, on: on)
        }
        return m
    }

    /// Feed one received line (nil = not a JSON object with a `type`).
    func receive(_ message: WireMessage?) -> [SessionEvent] {
        guard helloReceived else {
            guard let m = message, m.type == "hello" else { return [.close(.protocolError)] }
            return receiveHello(m)
        }
        guard let m = message else { return [] }
        if m.type == "hello" { return [.close(.protocolError)] }
        return secret.isEmpty ? receiveOpen(m) : receiveKeyed(m)
    }

    private func receiveHello(_ m: WireMessage) -> [SessionEvent] {
        guard m.v == 2 else { return [.close(.needsUpdate)] }
        guard let nonce = m.nonce, isValidNonce(nonce), let id = m.id, normalizedID(id) != nil else {
            return [.close(.protocolError)]
        }
        if normalizedID(id) == normalizedID(myID) || nonce == myNonce || issuedNonces().contains(nonce) {
            return [.close(.selfReflection)]
        }
        helloReceived = true
        peerHost = m.host
        peerID = id
        if secret.isEmpty {
            established = true
            return [.established]
        }
        let k = role == .dialer
            ? sessionKey(secret: secret, nD: myNonce, nL: nonce, idD: myID, idL: id)
            : sessionKey(secret: secret, nD: nonce, nL: myNonce, idD: id, idL: myID)
        key = k
        return [.send(WireMessage(type: "auth", proof: authProof(key: k, role: role)))]
    }

    private func receiveOpen(_ m: WireMessage) -> [SessionEvent] {
        switch m.type {
        case "auth": return [.close(.requiresSecret)]
        case "state": return m.on.map { [.peerState($0)] } ?? []
        case "ping": return [.send(outgoing("pong"))]
        default: return []
        }
    }

    private func receiveKeyed(_ m: WireMessage) -> [SessionEvent] {
        guard let key else { return [.close(.protocolError)] }
        switch m.type {
        case "auth":
            guard !established else { return [.close(.protocolError)] }
            guard let proof = m.proof, constantTimeEquals(proof, authProof(key: key, role: role.peer)) else {
                return [.close(.notAuthenticated)]
            }
            established = true
            return [.established]
        case "state", "ping", "pong":
            guard established else { return [.close(.notAuthenticated)] }
            guard let seq = m.seq, seq == rxSeq + 1, let mac = m.mac,
                  constantTimeEquals(mac, messageMAC(key: key, role: role.peer, seq: seq, type: m.type, on: m.on)) else {
                return [.close(.protocolError)]
            }
            rxSeq = seq
            switch m.type {
            case "state": return m.on.map { [.peerState($0)] } ?? [] // seq consumed either way
            case "ping": return [.send(outgoing("pong"))]
            default: return []
            }
        default:
            return []
        }
    }
}

/// PROTOCOL "Connection management" tie-break: does a newly established connection replace the
/// active one? Same dialer → the newer wins (the older is stale). One each way → keep the one
/// dialled by the side whose id sorts lower. Unknown id → the newer wins.
func newConnectionWins(activeOutbound: Bool, newOutbound: Bool, myID: String, peerID: String?) -> Bool {
    guard activeOutbound != newOutbound, let me = normalizedID(myID), let peer = normalizedID(peerID) else {
        return true
    }
    return newOutbound == (me < peer)
}

// MARK: - Switched-off displays (Mac only)
// A CGDirectDisplayID isn't stable: after a reboot, reconnect or OS upgrade the same monitor can come
// back under another id, and a freed id can later go to a different display. So each display we
// switch off is saved with its EDID identity, and the saved list is re-bound to the current ids
// before use. Ids are UInt32 (CGDirectDisplayID) so this file needs no CoreGraphics.

/// A display's EDID vendor / model / serial (CGDisplayVendorNumber, …ModelNumber, …SerialNumber).
struct DisplayIdentity: Equatable {
    let vendor: UInt32
    let model: UInt32
    let serial: UInt32

    /// nil when CoreGraphics doesn't know the id: it reports all zeros for an id that has gone and
    /// 0xFFFFFFFF for one it never handed out. A switched-off (offline) display still reports its own.
    init?(vendor: UInt32, model: UInt32, serial: UInt32) {
        guard vendor != .max, vendor != 0 || model != 0 || serial != 0 else { return nil }
        self.vendor = vendor
        self.model = model
        self.serial = serial
    }
}

/// A display we switched off. `identity` is nil when it was saved by a version that didn't store one.
struct SavedDisplay: Equatable {
    let id: UInt32
    let identity: DisplayIdentity?
}

/// Re-bind the saved switched-off displays to the ids the system uses now (`identity` reads an id's
/// live identity). Duplicate ids collapse to the first; then each entry is
/// 1. kept if its id still reports its identity (an entry without one adopts what its id reports);
/// 2. else moved to an online external with its identity that no other entry holds;
/// 3. else dropped: its id is gone or belongs to another display, so it can't turn this one back on.
func reconcileSavedDisplays(_ saved: [SavedDisplay], onlineExternals: [UInt32],
                            identity: (UInt32) -> DisplayIdentity?) -> [SavedDisplay] {
    var seen = Set<UInt32>()
    let unique = saved.filter { seen.insert($0.id).inserted }
    var result: [SavedDisplay?] = unique.map { entry in
        guard let live = identity(entry.id), entry.identity == nil || entry.identity == live else { return nil }
        return SavedDisplay(id: entry.id, identity: live)
    }
    var held = Set(result.compactMap { $0?.id })
    for (i, entry) in unique.enumerated() where result[i] == nil {
        guard let want = entry.identity,
              let id = onlineExternals.first(where: { !held.contains($0) && identity($0) == want }) else { continue }
        result[i] = SavedDisplay(id: id, identity: want)
        held.insert(id)
    }
    return result.compactMap { $0 }
}

/// Read the prefs encoding: `ids` is the list of switched-off ids (the key older versions read and
/// write), and `displayIdentities` maps String(id) to [vendor, model, serial]. An id with no valid
/// identity there (saved by an older version) reads with identity nil; invalid ids are skipped.
func savedDisplays(ids: [Int], identities: [String: Any]) -> [SavedDisplay] {
    ids.compactMap { raw in
        guard let id = UInt32(exactly: raw) else { return nil }
        let parts = (identities[String(id)] as? [Int])?.compactMap { UInt32(exactly: $0) } ?? []
        let identity = parts.count == 3 ? DisplayIdentity(vendor: parts[0], model: parts[1], serial: parts[2]) : nil
        return SavedDisplay(id: id, identity: identity)
    }
}

/// The `displayIdentities` value for `saved` (entries without an identity are left out).
func identitiesPlist(_ saved: [SavedDisplay]) -> [String: [Int]] {
    var out: [String: [Int]] = [:]
    for d in saved {
        if let i = d.identity { out[String(d.id)] = [Int(i.vendor), Int(i.model), Int(i.serial)] }
    }
    return out
}
