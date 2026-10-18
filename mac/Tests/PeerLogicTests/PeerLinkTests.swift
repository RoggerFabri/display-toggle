import Foundation
import Network
import Testing
@testable import PeerLogic

// Real PeerLinks over loopback (no Bonjour advertising). Each test uses its own ports.

/// Records a PeerLink's callbacks on its own serial queue.
private final class Observer: @unchecked Sendable {
    let queue = DispatchQueue(label: "test.observer")
    private var _connected = false
    private var _everConnected = false
    private var _problem: PeerProblem?
    private var _states: [Bool] = []

    var connected: Bool { queue.sync { _connected } }
    var everConnected: Bool { queue.sync { _everConnected } }
    var problem: PeerProblem? { queue.sync { _problem } }
    var states: [Bool] { queue.sync { _states } }

    func link() -> PeerLink {
        let l = PeerLink(callbackQueue: queue)
        l.onConnection = { [self] c, _, _ in _connected = c; if c { _everConnected = true } }
        l.onProblem = { [self] p, _ in _problem = p }
        l.onPeerState = { [self] on in _states.append(on) }
        return l
    }
}

private func until(_ seconds: Double = 6, _ condition: () -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline {
        if condition() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return condition()
}

private var nextPort: UInt16 = 41_000 + UInt16.random(in: 0..<2_000)
private func freshPorts() -> (UInt16, UInt16) {
    defer { nextPort += 2 }
    return (nextPort, nextPort + 1)
}

private let idA = "aaaaaaaa-0000-4000-8000-00000000000a"
private let idB = "bbbbbbbb-0000-4000-8000-00000000000b"

/// A raw TCP client that writes `lines` to 127.0.0.1:`port` and keeps the socket open.
private func rawClient(port: UInt16, lines: [String]) -> NWConnection {
    let c = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
    c.stateUpdateHandler = { state in
        if case .ready = state {
            c.send(content: Data(lines.map { $0 + "\n" }.joined().utf8), completion: .contentProcessed { _ in })
        }
    }
    c.start(queue: DispatchQueue(label: "test.raw"))
    return c
}

// Serialized: the suites bind real ports and share `nextPort`.
@Suite(.serialized) struct LoopbackLink {
    @Test func secretLinkCarriesStateBothWays() async {
        let (pa, pb) = freshPorts()
        let oa = Observer(), ob = Observer()
        let a = oa.link(), b = ob.link()
        // Both dial each other, so this also exercises the one-each-way tie-break.
        a.start(peerHost: "127.0.0.1", port: pb, currentOn: true, secret: "s3cret", id: idA, listenPort: pa, advertise: false)
        b.start(peerHost: "127.0.0.1", port: pa, currentOn: false, secret: "s3cret", id: idB, listenPort: pb, advertise: false)
        defer { a.stop(); b.stop() }
        #expect(await until { oa.connected && ob.connected })
        a.sendState(false)
        #expect(await until { ob.states.last == false })
        b.sendState(true)
        #expect(await until { oa.states.last == true })
    }

    @Test func strangersHelloDoesNotDropTheLink() async {
        let (pa, pb) = freshPorts()
        let oa = Observer(), ob = Observer()
        let a = oa.link(), b = ob.link()
        a.start(peerHost: "127.0.0.1", port: pb, currentOn: true, secret: "s3cret", id: idA, listenPort: pa, advertise: false)
        b.start(peerHost: "", port: pa, currentOn: false, secret: "s3cret", id: idB, listenPort: pb, advertise: false)
        defer { a.stop(); b.stop() }
        #expect(await until { oa.connected && ob.connected })

        let stranger = rawClient(port: pb, lines: [
            #"{"v":2,"type":"hello","host":"evil","id":"cccccccc-0000-4000-8000-00000000000c","on":true,"nonce":"0123456789abcdef0123456789abcdef"}"#,
        ])
        defer { stranger.cancel() }
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        #expect(oa.connected && ob.connected)
        a.sendState(false)
        #expect(await until { ob.states.last == false })
    }

    @Test func mixedModeNeverConnects() async {
        let (pa, pb) = freshPorts()
        let oa = Observer(), ob = Observer()
        let a = oa.link(), b = ob.link()
        a.start(peerHost: "127.0.0.1", port: pb, currentOn: true, secret: "s3cret", id: idA, listenPort: pa, advertise: false)
        b.start(peerHost: "", port: pa, currentOn: false, secret: "", id: idB, listenPort: pb, advertise: false)
        defer { a.stop(); b.stop() }
        #expect(await until { oa.problem == .notAuthenticated && ob.problem == .requiresSecret })
        #expect(!oa.everConnected) // the secret side never reports the link up
        #expect(!ob.connected)
    }

    @Test func refusedDialIsRetriedOnceThePeerComesUp() async {
        let (pa, pb) = freshPorts()
        let oa = Observer(), ob = Observer()
        let a = oa.link()
        a.start(peerHost: "127.0.0.1", port: pb, currentOn: true, secret: "", id: idA, listenPort: pa, advertise: false)
        defer { a.stop() }
        try? await Task.sleep(nanoseconds: 1_000_000_000) // first dial refused: nothing listens on pb yet
        let b = ob.link()
        b.start(peerHost: "", port: pa, currentOn: false, secret: "", id: idB, listenPort: pb, advertise: false)
        defer { b.stop() }
        #expect(await until(8) { oa.connected && ob.connected })
    }

    @Test func v1HelloShowsNeedsUpdate() async {
        let (_, pb) = freshPorts()
        let ob = Observer()
        let b = ob.link()
        b.start(peerHost: "", port: pb, currentOn: false, secret: "", id: idB, listenPort: pb, advertise: false)
        defer { b.stop() }
        try? await Task.sleep(nanoseconds: 300_000_000) // let the listener come up
        let old = rawClient(port: pb, lines: [#"{"v":1,"type":"hello","host":"old","on":true}"#])
        defer { old.cancel() }
        #expect(await until { ob.problem == .needsUpdate })
        #expect(!ob.everConnected)
    }
}
