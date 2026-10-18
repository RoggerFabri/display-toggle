import Testing
@testable import PeerLogic

private func ip(_ s: String) -> UInt32 {
    s.split(separator: ".").reduce(0) { $0 << 8 | UInt32($1)! }
}

private func peer(_ id: String, _ host: String = "192.0.2.112", _ port: UInt16 = 51763) -> DiscoveredPeer {
    DiscoveredPeer(id: id, name: "peer-\(id)", host: host, port: port, os: "win")
}

@Suite struct SelfFilter {
    @Test func ownIDIsSelf() {
        #expect(isSelf(txtID: "me", name: "Other", selfID: "me", selfName: "Mac"))
    }

    @Test func otherIDIsNotSelfEvenWithSameName() {
        #expect(!isSelf(txtID: "you", name: "Mac", selfID: "me", selfName: "Mac"))
    }

    @Test func noIDFallsBackToName() {
        #expect(isSelf(txtID: nil, name: "Mac", selfID: "me", selfName: "Mac"))
        #expect(isSelf(txtID: nil, name: "j. example mac", selfID: "me", selfName: "J. Example Mac"))
        #expect(!isSelf(txtID: nil, name: "WIN-LAPTOP", selfID: "me", selfName: "Mac"))
    }
}

@Suite struct AddressPick {
    let lan = [LocalSubnet(addr: ip("192.0.2.35"), mask: ip("255.255.255.0"))]

    @Test func prefersOnSubnetOverVPNAndVirtual() {
        let candidates = [ip("100.64.0.117"), ip("172.16.0.1"), ip("192.0.2.112")]
        #expect(pickAddress(candidates, localSubnets: lan) == ip("192.0.2.112"))
    }

    @Test func neverPicksLinkLocal() {
        #expect(pickAddress([ip("169.254.10.1")], localSubnets: lan) == nil)
        #expect(pickAddress([ip("169.254.10.1"), ip("10.0.0.5")], localSubnets: lan) == ip("10.0.0.5"))
    }

    @Test func fallsBackToFirstWhenNothingOnSubnet() {
        #expect(pickAddress([ip("10.0.0.5"), ip("10.0.0.6")], localSubnets: lan) == ip("10.0.0.5"))
        #expect(pickAddress([ip("10.0.0.5")], localSubnets: []) == ip("10.0.0.5"))
    }

    @Test func emptyIsNil() {
        #expect(pickAddress([], localSubnets: lan) == nil)
    }

    @Test func formatsDottedQuad() {
        #expect(ipv4String(ip("192.0.2.112")) == "192.0.2.112")
        #expect(ipv4String(0) == "0.0.0.0")
    }
}

@Suite struct FollowAndAutoPick {
    @Test func neverActsWhileConnected() {
        #expect(peerToAdopt(connected: true, host: "", port: 51763, peerID: "", discovered: [peer("a")]) == nil)
        #expect(peerToAdopt(connected: true, host: "192.0.2.9", port: 51763, peerID: "a",
                            discovered: [peer("a", "192.0.2.112")]) == nil)
    }

    @Test func followsChosenPeerToNewAddress() {
        let moved = peer("a", "192.0.2.120")
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763, peerID: "a",
                            discovered: [peer("b"), moved]) == moved)
    }

    @Test func followsChosenPeerToNewPort() {
        let moved = peer("a", "192.0.2.112", 51800)
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763, peerID: "a",
                            discovered: [moved]) == moved)
    }

    @Test func unchangedChosenPeerIsNoop() {
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763, peerID: "a",
                            discovered: [peer("a")]) == nil)
    }

    @Test func chosenPeerMissingIsNoopEvenIfOthersExist() {
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763, peerID: "a",
                            discovered: [peer("b")]) == nil)
    }

    @Test func autoPicksOnlyPeerWhenNoneConfigured() {
        #expect(peerToAdopt(connected: false, host: "", port: 51763, peerID: "", discovered: [peer("a")]) == peer("a"))
    }

    @Test func noAutoPickWithSeveralPeers() {
        #expect(peerToAdopt(connected: false, host: "", port: 51763, peerID: "",
                            discovered: [peer("a"), peer("b")]) == nil)
    }

    @Test func noAutoPickWhenNothingDiscovered() {
        #expect(peerToAdopt(connected: false, host: "", port: 51763, peerID: "", discovered: []) == nil)
    }

    @Test func standaloneNeverAutoPicksOrFollows() {
        #expect(peerToAdopt(connected: false, host: "", port: 51763, peerID: "",
                            discovered: [peer("a")], standalone: true) == nil)
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763, peerID: "a",
                            discovered: [peer("a", "192.0.2.120")], standalone: true) == nil)
    }

    @Test func manualPeerIsNeverReplaced() {
        #expect(peerToAdopt(connected: false, host: "10.0.0.5", port: 51763, peerID: "",
                            discovered: [peer("a")]) == nil)
    }
}

@Suite struct Checkmark {
    @Test func byIDWhenChosenFromDiscovery() {
        #expect(isCurrentPeer(peer("a", "192.0.2.120"), peerID: "a", host: "192.0.2.112", port: 51763))
        #expect(!isCurrentPeer(peer("b"), peerID: "a", host: "192.0.2.112", port: 51763))
    }

    @Test func byHostAndPortWhenManual() {
        #expect(isCurrentPeer(peer("a"), peerID: "", host: "192.0.2.112", port: 51763))
        #expect(!isCurrentPeer(peer("a"), peerID: "", host: "192.0.2.112", port: 51800))
        #expect(!isCurrentPeer(peer("a"), peerID: "", host: "192.0.2.9", port: 51763))
    }
}

@Suite struct IDsAndTXT {
    @Test func emptyIDCountsAsMissing() {
        #expect(normalizedID("") == nil)
        #expect(normalizedID("  ") == nil)
        #expect(normalizedID(nil) == nil)
        // An empty id falls back to the name instead of matching nothing.
        #expect(isSelf(txtID: "", name: "Mac", selfID: "me", selfName: "Mac"))
        #expect(!isSelf(txtID: "", name: "WIN-LAPTOP", selfID: "me", selfName: "Mac"))
    }

    @Test func idsCompareCaseInsensitively() {
        let up = "0F1E2D3C-4B5A-4697-8877-665544332211"
        #expect(isSelf(txtID: up.lowercased(), name: "x", selfID: up, selfName: "Mac"))
        #expect(isCurrentPeer(peer(up.lowercased()), peerID: up, host: "", port: 0))
        let moved = peer("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", "192.0.2.120")
        #expect(peerToAdopt(connected: false, host: "192.0.2.112", port: 51763,
                            peerID: "A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D", discovered: [moved]) == moved)
    }

    @Test func txtKeysAreCaseInsensitive() {
        let txt = normalizedTXT(["ID": "abc", "Os": "win", "v": "1"])
        #expect(txt["id"] == "abc")
        #expect(txt["os"] == "win")
        #expect(txt["v"] == "1")
    }
}

@Suite struct LinkLocal {
    @Test func linkLocalDroppedEvenOnLinkLocalSubnet() {
        let apipa = [LocalSubnet(addr: ip("169.254.3.4"), mask: ip("255.255.0.0"))]
        #expect(pickAddress([ip("169.254.10.1")], localSubnets: apipa) == nil)
        #expect(pickAddress([ip("169.254.10.1"), ip("192.0.2.112")], localSubnets: apipa) == ip("192.0.2.112"))
    }
}
