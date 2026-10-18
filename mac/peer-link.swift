// peer-link.swift
// The TCP peer link (PROTOCOL.md): sockets and connection management around PeerSession
// (peer-logic.swift). Compiled into the app by `make`, and into the test target so the link can be
// exercised over loopback by `make test`.

import Foundation
import Network
import os
import SystemConfiguration

/// `log stream --predicate 'subsystem == "com.display-toggle"'` to watch the link.
private let log = Logger(subsystem: "com.display-toggle", category: "peer")

let peerServiceType = "_display-toggle._tcp"

/// Computer name ("Jane's Mac") — the DNS-SD instance name we advertise.
func computerName() -> String {
    (SCDynamicStoreCopyComputerName(nil, nil) as String?) ?? ProcessInfo.processInfo.hostName
}

// MARK: - Peer link  (see PROTOCOL.md)
// Symmetric TCP peer: listens on `port` and dials `peerHost:port`. Each connection runs a v2
// PeerSession (peer-logic.swift); one *established* connection is active at a time, and a newcomer
// only replaces it once it authenticates, by the protocol's tie-break. Local toggles broadcast
// `state`; received `state` is applied inversely and silently by AppDelegate (shared-monitor
// handoff). Callbacks are delivered on `callbackQueue` (main).

/// Why the link to a reachable peer isn't up (shown instead of "offline").
enum PeerProblem {
    case needsUpdate, notAuthenticated, requiresSecret
}

final class PeerLink {
    var onPeerState: ((Bool) -> Void)?
    // (connected, endpoint IP, peer hostname)
    var onConnection: ((Bool, String?, String?) -> Void)?
    // (problem or nil to clear, peer hostname if known)
    var onProblem: ((PeerProblem?, String?) -> Void)?

    /// One TCP connection and its session.
    private final class Link {
        let conn: NWConnection
        let outbound: Bool
        let session: PeerSession
        let endpoint: String
        var buffer = Data()
        var lastRx = Date()
        var ready = false   // TCP connected (the peer knows about this connection)
        var closed = false

        init(conn: NWConnection, outbound: Bool, session: PeerSession) {
            self.conn = conn
            self.outbound = outbound
            self.session = session
            endpoint = "\(conn.endpoint)"
        }
    }

    private let queue = DispatchQueue(label: "com.display-toggle.peer")
    private let callbackQueue: DispatchQueue
    private var listener: NWListener?
    private var active: Link?
    private var pending: [Link] = []
    private var peerHost = ""
    private var port: UInt16 = 51763        // the peer's port (we dial it)
    private var listenPort: UInt16 = 51763  // ours; the same as `port` except in tests
    private var advertise = true
    private var secret = ""
    private var myID = ""
    private var currentOn = true
    private var reconnectScheduled = false
    // Bumped by stopLocked(); a delayed listener retry from an earlier start() sees a newer value and bails.
    private var generation = 0
    private let hostName = ProcessInfo.processInfo.hostName

    private let maxLineBytes = 8192
    private let maxPending = 4
    private let pingInterval: TimeInterval = 15
    private let staleTimeout: TimeInterval = 40
    private let handshakeTimeout: TimeInterval = 10
    private let redialDelay: TimeInterval = 3
    private let backoffDelay: TimeInterval = 30   // after needs-update / mixed-mode failures

    init(callbackQueue: DispatchQueue = .main) {
        self.callbackQueue = callbackQueue
    }

    /// `listenPort` defaults to `port` (both machines use the same one); `advertise` = DNS-SD.
    func start(peerHost: String, port: UInt16, currentOn: Bool, secret: String, id: String,
               listenPort: UInt16? = nil, advertise: Bool = true) {
        queue.async {
            self.stopLocked()
            self.peerHost = peerHost
            self.port = port
            self.listenPort = listenPort ?? port
            self.advertise = advertise
            self.secret = secret
            self.myID = id
            self.currentOn = currentOn
            self.startListener()
            self.dial()
        }
    }

    func stop() { queue.async { self.stopLocked() } }

    /// Broadcast a LOCAL state change (never called for remote-driven changes), on the active link.
    func sendState(_ on: Bool) {
        queue.async {
            self.currentOn = on
            if let link = self.active { self.send(link.session.outgoing("state", on: on), on: link) }
        }
    }

    private func stopLocked() {
        generation += 1
        peerHost = "" // a pending scheduleDial must not re-dial after stop()
        listener?.cancel(); listener = nil
        let wasConnected = active != nil
        for link in pending + [active].compactMap({ $0 }) {
            link.closed = true
            link.conn.cancel()
        }
        pending.removeAll()
        active = nil
        if wasConnected { callbackQueue.async { self.onConnection?(false, nil, nil) } }
        callbackQueue.async { self.onProblem?(nil, nil) }
    }

    // TCP with keepalive so a silently-dropped link is noticed by the OS too.
    private func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
        tcp.keepaliveInterval = 5
        tcp.keepaliveCount = 3
        // A peer whose firewall silently drops SYNs (e.g. the Windows app restarting) would otherwise
        // keep one dial in flight for ~30s of SYN retries, blocking a fresh one (dialer rule).
        tcp.connectionTimeout = 10
        return NWParameters(tls: nil, tcp: tcp)
    }

    private func startListener() {
        let gen = generation
        listener?.cancel(); listener = nil
        guard let nwPort = NWEndpoint.Port(rawValue: listenPort) else { return }
        do {
            let l = try NWListener(using: tcpParameters(), on: nwPort)
            // Advertise the listener over DNS-SD so peers can pick us from their discovery list.
            if advertise {
                l.service = NWListener.Service(name: computerName(), type: peerServiceType, domain: nil,
                                               txtRecord: NWTXTRecord(["v": "1", "id": myID, "os": "mac"]))
            }
            l.newConnectionHandler = { [weak self] conn in
                guard let self = self else { return }
                guard self.pending.count < self.maxPending else { conn.cancel(); return }
                self.open(conn, outbound: false)
            }
            l.stateUpdateHandler = { [weak self, weak l] state in
                if case .failed = state, let self = self, self.listener === l { self.retryListener(gen) }
            }
            l.start(queue: queue)
            listener = l
        } catch {
            retryListener(gen)
        }
    }

    /// Retry a failed listener (e.g. the port is still held by the one just cancelled) — unless the
    /// link was stopped or restarted meanwhile, which would otherwise resurrect it (and its Bonjour
    /// advertisement) after *None*, or leave an orphan listener nothing can cancel.
    private func retryListener(_ gen: Int) {
        queue.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self = self, self.generation == gen else { return }
            self.startListener()
        }
    }

    /// Dialer rule: at most one outbound dial in flight, and none while a link is active.
    private func dial() {
        guard active == nil, !pending.contains(where: { $0.outbound }), !peerHost.isEmpty,
              let nwPort = NWEndpoint.Port(rawValue: port) else { return }
        open(NWConnection(host: NWEndpoint.Host(peerHost), port: nwPort, using: tcpParameters()), outbound: true)
    }

    private func scheduleDial(after delay: TimeInterval) {
        guard !reconnectScheduled, !peerHost.isEmpty else { return }
        reconnectScheduled = true
        queue.asyncAfter(deadline: .now() + delay) { [weak self] in
            self?.reconnectScheduled = false
            self?.dial()
        }
    }

    private func open(_ conn: NWConnection, outbound: Bool) {
        let session = PeerSession(role: outbound ? .dialer : .listener, secret: secret, myID: myID,
                                  issuedNonces: { [weak self] in
                                      guard let self = self else { return [] }
                                      return Set((self.pending + [self.active].compactMap { $0 }).map { $0.session.myNonce })
                                  })
        let link = Link(conn: conn, outbound: outbound, session: session)
        pending.append(link)
        log.info("open \(outbound ? "outbound" : "inbound", privacy: .public) \(link.endpoint, privacy: .public)")

        conn.stateUpdateHandler = { [weak self] state in
            guard let self = self, !link.closed else { return }
            switch state {
            case .ready:
                link.ready = true
                self.send(session.hello(host: self.hostName, on: self.currentOn), on: link)
                self.receiveLoop(link)
                // Not established in time (stranger, wrong secret, silent peer) → close; the active
                // link is untouched.
                self.queue.asyncAfter(deadline: .now() + self.handshakeTimeout) { [weak self] in
                    guard let self = self, !link.closed, !link.session.established else { return }
                    let unauthed = !self.secret.isEmpty && link.session.helloReceived
                    self.drop(link, problem: unauthed ? .notAuthenticated : nil)
                }
            case .waiting where outbound:
                // Refused / unreachable: NWConnection parks in .waiting and only retries on a network
                // path change, so a peer that comes up later would never be dialled. Drop → re-dial.
                self.drop(link)
            case .failed, .cancelled:
                self.closedByPeer(link)
            default:
                break
            }
        }
        conn.start(queue: queue)
    }

    /// Close `link` and forget it. If it was active, report the disconnect; then re-dial (dial()
    /// itself checks the dialer rule), after the backoff if the peer is known to be incompatible.
    private func drop(_ link: Link, problem: PeerProblem? = nil) {
        guard !link.closed else { return }
        link.closed = true
        link.conn.cancel()
        log.info("drop \(link.outbound ? "outbound" : "inbound", privacy: .public) \(link.endpoint, privacy: .public) active=\(self.active === link) problem=\(String(describing: problem), privacy: .public)")
        pending.removeAll { $0 === link }
        if active === link {
            active = nil
            callbackQueue.async { self.onConnection?(false, nil, nil) }
        }
        if let problem {
            let host = link.session.peerHost
            callbackQueue.async { self.onProblem?(problem, host) }
        }
        scheduleDial(after: problem == nil ? redialDelay : backoffDelay)
    }

    /// The socket closed under us. In secret mode, a peer that sent its hello but never proved the
    /// secret (it closed on our `auth`, or we timed out) is reported as *not authenticated*.
    private func closedByPeer(_ link: Link) {
        let unauthed = !secret.isEmpty && link.session.helloReceived && !link.session.established
        drop(link, problem: unauthed ? .notAuthenticated : nil)
    }

    private func handle(_ events: [SessionEvent], on link: Link) {
        for event in events where !link.closed {
            switch event {
            case .send(let m):
                send(m, on: link)
            case .established:
                promote(link)
            case .peerState(let on):
                // Accepted on any established connection (PROTOCOL "Connection management").
                callbackQueue.async { self.onPeerState?(on) }
            case .close(let reason):
                switch reason {
                case .needsUpdate: drop(link, problem: .needsUpdate)
                case .notAuthenticated: drop(link, problem: .notAuthenticated)
                case .requiresSecret: drop(link, problem: .requiresSecret)
                case .selfReflection, .protocolError: drop(link)
                }
            }
        }
    }

    /// An established connection becomes active, unless the tie-break keeps the current one.
    private func promote(_ link: Link) {
        pending.removeAll { $0 === link }
        if let current = active {
            guard newConnectionWins(activeOutbound: current.outbound, newOutbound: link.outbound,
                                    myID: myID, peerID: link.session.peerID) else {
                log.info("tie-break keeps the active link over \(link.endpoint, privacy: .public)")
                drop(link)
                return
            }
            log.info("tie-break: \(link.endpoint, privacy: .public) replaces \(current.endpoint, privacy: .public)")
            active = link
            drop(current) // no longer active, so no disconnect is reported
        } else {
            active = link
        }
        log.info("active \(link.outbound ? "outbound" : "inbound", privacy: .public) \(link.endpoint, privacy: .public)")
        // A dial still connecting is now pointless (dialer rule); the peer doesn't know about it yet,
        // so cancelling it can't make the peer's tie-break disagree with ours.
        for l in pending where l.outbound && !l.ready { drop(l) }
        let endpoint = link.endpoint, host = link.session.peerHost
        callbackQueue.async {
            self.onProblem?(nil, nil)
            self.onConnection?(true, endpoint, host)
        }
        startHeartbeat(link)
    }

    // Ping periodically; if nothing has arrived within the stale window, drop → reconnect.
    private func startHeartbeat(_ link: Link) {
        queue.asyncAfter(deadline: .now() + pingInterval) { [weak self] in
            guard let self = self, self.active === link else { return }
            if Date().timeIntervalSince(link.lastRx) > self.staleTimeout {
                self.drop(link)
                return
            }
            self.send(link.session.outgoing("ping"), on: link)
            self.startHeartbeat(link)
        }
    }

    private func receiveLoop(_ link: Link) {
        // Read at most one line's worth past the cap, so the buffer stays within cap + one read.
        link.conn.receive(minimumIncompleteLength: 1, maximumLength: maxLineBytes + 1) { [weak self] data, _, isComplete, error in
            guard let self = self, !link.closed else { return }
            if let data = data, !data.isEmpty {
                link.lastRx = Date()
                link.buffer.append(data)
                while let nl = link.buffer.firstIndex(of: 0x0A) {
                    let line = link.buffer.subdata(in: link.buffer.startIndex..<nl)
                    link.buffer.removeSubrange(link.buffer.startIndex...nl)
                    if line.count > self.maxLineBytes { self.drop(link); return }
                    if !line.isEmpty { self.handle(link.session.receive(WireMessage.parse(line)), on: link) }
                    if link.closed { return }
                }
                // No newline within the cap → abusive/garbage; drop the connection.
                if link.buffer.count > self.maxLineBytes { self.drop(link); return }
            }
            if isComplete || error != nil { self.closedByPeer(link); return }
            self.receiveLoop(link)
        }
    }

    private func send(_ m: WireMessage, on link: Link) {
        link.conn.send(content: m.encoded(), completion: .contentProcessed { _ in })
    }
}
