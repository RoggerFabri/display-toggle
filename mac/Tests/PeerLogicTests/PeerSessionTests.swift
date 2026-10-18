import Foundation
import Testing
@testable import PeerLogic

// PROTOCOL.md "Test vectors" — the Windows tests check the same strings.
private let secret = "correct horse"
private let nD = "00112233445566778899aabbccddeeff"
private let nL = "ffeeddccbbaa99887766554433221100"
private let idD = "4f1c2b3a-0000-4000-8000-000000000001"
private let idL = "9e8d7c6b-0000-4000-8000-000000000002"
private let K = "96c594917831a601c95535419dcf0039da5be5dff680fed3faac55ed40b6a9c2"

@Suite struct Vectors {
    @Test func sessionKeyMatches() {
        #expect(sessionKey(secret: secret, nD: nD, nL: nL, idD: idD, idL: idL) == K)
    }

    @Test func proofsMatch() {
        #expect(authProof(key: K, role: .dialer) == "9ebc15d5188dd9d5e7257d4bd5e00e65d9818d7cc960f757547422689696aa65")
        #expect(authProof(key: K, role: .listener) == "7d41a2f69679cbeffaafaf5d91414556f231e86787d85d65f9ec3c972b0fc7cb")
    }

    @Test func macsMatch() {
        #expect(messageMAC(key: K, role: .dialer, seq: 1, type: "state", on: false)
            == "0f7ad2edb53a51e9597bb584625d624dafb11913f4e47738acb900e6a251818f")
        #expect(messageMAC(key: K, role: .listener, seq: 1, type: "state", on: true)
            == "38b16fc774a7c35072c7cc9d800ea51bbd27aa818fab1cdf2fa53ec078fd3099")
        #expect(messageMAC(key: K, role: .dialer, seq: 2, type: "ping", on: nil)
            == "297973222f8eab884e579e82f3caa77fe91ab95dee6d45ec5c3da5b9b52b46a3")
    }
}

/// Two sessions wired back to back: what one sends, the other receives.
private struct Pair {
    let dialer: PeerSession
    let listener: PeerSession
    var dialerEvents: [SessionEvent] = []
    var listenerEvents: [SessionEvent] = []

    init(secretD: String = secret, secretL: String = secret) {
        dialer = PeerSession(role: .dialer, secret: secretD, myID: idD, myNonce: nD)
        listener = PeerSession(role: .listener, secret: secretL, myID: idL, myNonce: nL)
    }

    /// Deliver `m` to `to`, then keep delivering whatever the sessions send until it settles.
    mutating func deliver(_ m: WireMessage, toListener: Bool) { deliver([(m, toListener)]) }

    mutating func deliver(_ initial: [(WireMessage, Bool)]) {
        var queue = initial
        while !queue.isEmpty {
            let (msg, toL) = queue.removeFirst()
            let wire = WireMessage.parse(msg.encoded().dropLast()) // through JSON, like the socket
            let events = (toL ? listener : dialer).receive(wire)
            if toL { listenerEvents += events } else { dialerEvents += events }
            for case .send(let out) in events { queue.append((out, !toL)) }
        }
    }

    /// Both sides send `hello` as soon as the socket is up, before reading anything.
    mutating func handshake() {
        deliver([(dialer.hello(host: "mac", on: true), true), (listener.hello(host: "win", on: false), false)])
    }
}

@Suite struct Handshake {
    @Test func keyedHandshakeEstablishesBothSides() {
        var p = Pair()
        p.handshake()
        #expect(p.dialer.established && p.listener.established)
        #expect(p.listener.peerHost == "mac" && p.dialer.peerID == idL)
    }

    @Test func wrongSecretIsNotAuthenticated() {
        var p = Pair(secretD: secret, secretL: "wrong")
        p.handshake()
        #expect(p.listenerEvents.contains(.close(.notAuthenticated)) || p.dialerEvents.contains(.close(.notAuthenticated)))
        #expect(!(p.dialer.established && p.listener.established))
    }

    @Test func openModeEstablishesOnHello() {
        var p = Pair(secretD: "", secretL: "")
        p.handshake()
        #expect(p.dialer.established && p.listener.established)
    }

    @Test func openSideClosesOnUnexpectedAuth() {
        var p = Pair(secretD: secret, secretL: "")
        p.handshake()
        #expect(p.listenerEvents.contains(.close(.requiresSecret)))
        #expect(!p.dialer.established)
    }

    @Test func firstLineMustBeHello() {
        let s = PeerSession(role: .listener, secret: secret, myID: idL)
        #expect(s.receive(WireMessage(type: "state", on: true)) == [.close(.protocolError)])
        #expect(PeerSession(role: .listener, secret: "", myID: idL).receive(nil) == [.close(.protocolError)])
    }

    @Test func v1HelloNeedsUpdate() {
        let s = PeerSession(role: .listener, secret: "", myID: idL)
        #expect(s.receive(WireMessage(type: "hello", v: 1, host: "old", id: idD, nonce: nD)) == [.close(.needsUpdate)])
        #expect(PeerSession(role: .listener, secret: "", myID: idL)
            .receive(WireMessage(type: "hello", v: nil, id: idD, nonce: nD)) == [.close(.needsUpdate)])
    }

    @Test func badNonceOrIdIsRejected() {
        for hello in [WireMessage(type: "hello", id: idD, nonce: "ABCDEF0123456789ABCDEF0123456789"), // uppercase
                      WireMessage(type: "hello", id: idD, nonce: "abc"),
                      WireMessage(type: "hello", id: "", nonce: nD),
                      WireMessage(type: "hello", nonce: nD)] {
            #expect(PeerSession(role: .listener, secret: "", myID: idL).receive(hello) == [.close(.protocolError)])
        }
    }
}

@Suite struct SelfReflection {
    @Test func ownIdCloses() {
        let s = PeerSession(role: .listener, secret: secret, myID: idL)
        #expect(s.receive(WireMessage(type: "hello", id: idL.uppercased(), nonce: nD)) == [.close(.selfReflection)])
    }

    @Test func ownNonceOnThisConnectionCloses() {
        let s = PeerSession(role: .listener, secret: secret, myID: idL, myNonce: nL)
        #expect(s.receive(WireMessage(type: "hello", id: idD, nonce: nL)) == [.close(.selfReflection)])
    }

    @Test func nonceIssuedOnAnotherConnectionCloses() {
        let s = PeerSession(role: .listener, secret: secret, myID: idL, issuedNonces: { [nD] })
        #expect(s.receive(WireMessage(type: "hello", id: idD, nonce: nD)) == [.close(.selfReflection)])
    }
}

@Suite struct Messages {
    @Test func stateIsDeliveredWithSeqAndMac() {
        var p = Pair()
        p.handshake()
        let m = p.dialer.outgoing("state", on: false)
        #expect(m.seq == 1 && m.mac == hmacHex(key: K, "d|1|state|0"))
        p.deliver(m, toListener: true)
        #expect(p.listenerEvents.last == .peerState(false))
    }

    @Test func pingGetsAMacedPong() {
        var p = Pair()
        p.handshake()
        p.deliver(p.dialer.outgoing("ping"), toListener: true)
        #expect(!p.dialerEvents.contains { if case .close = $0 { true } else { false } })
    }

    @Test func badSeqCloses() {
        for seq in [0, 2, -1] {
            var p = Pair()
            p.handshake()
            var m = p.dialer.outgoing("state", on: true)
            m.seq = seq
            m.mac = messageMAC(key: K, role: .dialer, seq: seq, type: "state", on: true)
            #expect(p.listener.receive(m) == [.close(.protocolError)])
        }
    }

    @Test func replayCloses() {
        var p = Pair()
        p.handshake()
        let m = p.dialer.outgoing("state", on: true)
        #expect(p.listener.receive(m) == [.peerState(true)])
        #expect(p.listener.receive(m) == [.close(.protocolError)])
    }

    @Test func reflectedMessageCloses() {
        // The listener's own message sent back to it: role "l" in the mac, but it expects "d".
        var p = Pair()
        p.handshake()
        #expect(p.listener.receive(p.listener.outgoing("state", on: true)) == [.close(.protocolError)])
    }

    @Test func tamperedOnCloses() {
        var p = Pair()
        p.handshake()
        var m = p.dialer.outgoing("state", on: true)
        m.on = false
        #expect(p.listener.receive(m) == [.close(.protocolError)])
    }

    @Test func stateBeforeAuthIsNotAuthenticated() {
        let s = PeerSession(role: .listener, secret: secret, myID: idL, myNonce: nL)
        _ = s.receive(WireMessage(type: "hello", id: idD, nonce: nD))
        #expect(s.receive(WireMessage(type: "state", on: true, seq: 1, mac: "00")) == [.close(.notAuthenticated)])
    }

    @Test func stateWithoutOnIsIgnoredButConsumesSeq() {
        var p = Pair()
        p.handshake()
        #expect(p.listener.receive(p.dialer.outgoing("state")) == [])
        #expect(p.listener.receive(p.dialer.outgoing("state", on: true)) == [.peerState(true)])
    }

    @Test func nonBooleanOnAndNonIntegerSeqReadAsAbsent() {
        let m = WireMessage.parse(Data(#"{"type":"state","on":1,"seq":1.5,"v":2}"#.utf8))
        #expect(m?.on == nil && m?.seq == nil && m?.v == 2)
        #expect(WireMessage.parse(Data(#"{"type":"state","on":true,"seq":true}"#.utf8))?.seq == nil)
        #expect(WireMessage.parse(Data("[1]".utf8)) == nil)
    }

    @Test func openModeIgnoresStateWithoutOn() {
        var p = Pair(secretD: "", secretL: "")
        p.handshake()
        #expect(p.listener.receive(WireMessage(type: "state")) == [])
        #expect(p.listener.receive(WireMessage(type: "state", on: true)) == [.peerState(true)])
    }
}

@Suite struct TieBreak {
    @Test func sameDialerNewerWins() {
        #expect(newConnectionWins(activeOutbound: true, newOutbound: true, myID: "b", peerID: "a"))
        #expect(newConnectionWins(activeOutbound: false, newOutbound: false, myID: "a", peerID: "b"))
    }

    @Test func oneEachWayLowerIdsDialWins() {
        // I'm "a" (lower): keep the connection I dialled.
        #expect(newConnectionWins(activeOutbound: false, newOutbound: true, myID: "a", peerID: "b"))
        #expect(!newConnectionWins(activeOutbound: true, newOutbound: false, myID: "a", peerID: "b"))
        // I'm "b" (higher): keep the connection the peer dialled.
        #expect(newConnectionWins(activeOutbound: true, newOutbound: false, myID: "B", peerID: "a"))
        #expect(!newConnectionWins(activeOutbound: false, newOutbound: true, myID: "b", peerID: "A"))
    }

    @Test func bothEndsAgree() {
        for (activeOut, newOut) in [(true, false), (false, true)] {
            // My view and the peer's view of the same two connections (outbound flips sides).
            let mine = newConnectionWins(activeOutbound: activeOut, newOutbound: newOut, myID: idD, peerID: idL)
            let theirs = newConnectionWins(activeOutbound: !activeOut, newOutbound: !newOut, myID: idL, peerID: idD)
            #expect(mine == theirs)
        }
    }
}
