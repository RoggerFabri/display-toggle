import Testing
@testable import PeerLogic

// Identities as CoreGraphics reports them. `dell` is a real DELL P2422HE.
private let dell = DisplayIdentity(vendor: 4268, model: 41414, serial: 827871298)!
private let lg = DisplayIdentity(vendor: 7789, model: 30471, serial: 1001)!
private let twin = DisplayIdentity(vendor: 1234, model: 5678, serial: 0)! // identical monitors, no serial

private func saved(_ id: UInt32, _ identity: DisplayIdentity?) -> SavedDisplay {
    SavedDisplay(id: id, identity: identity)
}

/// `known` is what the system reports per id right now; any other id is unknown.
private func reconcile(_ entries: [SavedDisplay], online: [UInt32] = [],
                       known: [UInt32: DisplayIdentity]) -> [SavedDisplay] {
    reconcileSavedDisplays(entries, onlineExternals: online, identity: { known[$0] })
}

@Suite struct DisplayIdentityReading {
    @Test func zerosMeanTheIDIsGone() {
        #expect(DisplayIdentity(vendor: 0, model: 0, serial: 0) == nil)
    }

    @Test func allOnesMeanTheIDWasNeverAssigned() {
        #expect(DisplayIdentity(vendor: .max, model: .max, serial: .max) == nil)
    }

    @Test func aMonitorWithoutSerialIsStillKnown() {
        #expect(DisplayIdentity(vendor: 1234, model: 5678, serial: 0) != nil)
    }
}

@Suite struct ReconcileSavedDisplays {
    // The reported bug: switched off as id 2; after a reboot the same monitor is online as id 3
    // and id 2 is gone. Must yield one entry, under id 3 (one menu row, not two).
    @Test func monitorBackUnderNewIDMovesToIt() {
        #expect(reconcile([saved(2, dell)], online: [3], known: [3: dell]) == [saved(3, dell)])
    }

    @Test func switchedOffMonitorKeepsItsID() {
        // Offline (software-disabled), so not in the online list, but its id still reports it.
        #expect(reconcile([saved(2, dell)], known: [2: dell]) == [saved(2, dell)])
    }

    @Test func goneMonitorIsDropped() {
        #expect(reconcile([saved(2, dell)], online: [], known: [:]) == [])
    }

    @Test func idReusedByAnotherMonitorIsNotThisOne() {
        // id 2 is the LG now: the saved Dell can't be switched back on through it.
        #expect(reconcile([saved(2, dell)], online: [2], known: [2: lg]) == [])
        // … and if the Dell is online elsewhere, the entry follows it there.
        #expect(reconcile([saved(2, dell)], online: [2, 5], known: [2: lg, 5: dell]) == [saved(5, dell)])
    }

    @Test func onlyOnlineExternalsAreRemapTargets() {
        // The Dell reports as id 7 but is switched off there (not online): entries only move onto
        // online displays, since there's no list of offline ones to search.
        #expect(reconcile([saved(2, dell)], online: [], known: [7: dell]) == [])
    }

    @Test func legacyEntryAdoptsTheIdentityItsIDReports() {
        #expect(reconcile([saved(2, nil)], known: [2: dell]) == [saved(2, dell)])
    }

    @Test func legacyEntryWithUnknownIDIsDropped() {
        // No identity to look for: it can't be moved, and the id switches nothing on.
        #expect(reconcile([saved(2, nil)], online: [3], known: [3: dell]) == [])
    }

    @Test func duplicateIDsCollapseToTheFirst() {
        #expect(reconcile([saved(2, dell), saved(2, dell)], online: [5], known: [2: dell, 5: dell])
                == [saved(2, dell)])
    }

    @Test func twinsKeepTheirIDsAndNeverShareOne() {
        // Twin A kept id 6; twin B's id 4 is gone: it moves to 7, not onto A's 6.
        #expect(reconcile([saved(4, twin), saved(6, twin)], online: [6, 7], known: [6: twin, 7: twin])
                == [saved(7, twin), saved(6, twin)])
        // Both gone: each moves to a different online twin.
        #expect(reconcile([saved(4, twin), saved(5, twin)], online: [6, 7], known: [6: twin, 7: twin])
                == [saved(6, twin), saved(7, twin)])
        // More saved twins than online ones: the extra is dropped.
        #expect(reconcile([saved(4, twin), saved(5, twin)], online: [6], known: [6: twin]) == [saved(6, twin)])
    }

    @Test func mixedListKeepsOrder() {
        let entries = [saved(2, dell), saved(9, lg), saved(11, nil)]
        #expect(reconcile(entries, online: [3], known: [3: dell, 9: lg]) == [saved(3, dell), saved(9, lg)])
    }
}

@Suite struct SavedDisplayPrefs {
    @Test func roundTrips() {
        let entries = [saved(2, dell), saved(3, nil), saved(UInt32.max - 1, lg)]
        let ids = entries.map { Int($0.id) }
        #expect(savedDisplays(ids: ids, identities: identitiesPlist(entries)) == entries)
    }

    @Test func idsFromAnOlderVersionHaveNoIdentity() {
        #expect(savedDisplays(ids: [2, 5], identities: [:]) == [saved(2, nil), saved(5, nil)])
    }

    @Test func identitiesForOtherIDsAreIgnored() {
        // An older version rewrote `ids` and left `displayIdentities` behind.
        #expect(savedDisplays(ids: [3], identities: ["2": [4268, 41414, 827871298]]) == [saved(3, nil)])
    }

    @Test func malformedValuesAreSkipped() {
        #expect(savedDisplays(ids: [-1, 1 << 40, 2], identities: [:]) == [saved(2, nil)])
        let bad: [String: Any] = ["2": [4268, 41414], "3": "dell", "4": [-1, 1, 1], "5": [0, 0, 0]]
        #expect(savedDisplays(ids: [2, 3, 4, 5], identities: bad)
                == [saved(2, nil), saved(3, nil), saved(4, nil), saved(5, nil)])
    }
}
