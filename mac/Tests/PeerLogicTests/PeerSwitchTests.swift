import Testing
@testable import PeerLogic

private let refused: Int32 = 1001 // kCGErrorFailure

@Suite struct PeerSwitchRetries {
    @Test func alreadyThereNeedsNoSwitch() {
        var s = PeerSwitch()
        #expect(s.received(desired: true, current: true) == nil)
        #expect(s.pending == nil)
    }

    // The reported bug: the peer released the monitor, the system refused to switch it on here, and
    // the request was dropped, leaving the monitor dark on both machines.
    @Test func refusedSwitchIsRetried() {
        var s = PeerSwitch()
        #expect(s.received(desired: true, current: false) == true)
        s.attempted(true, error: refused)
        #expect(s.pending?.on == true && s.pending?.error == refused)
        #expect(s.retry(current: false) == true)
        s.attempted(true, error: nil)
        #expect(s.pending == nil)
        #expect(s.retry(current: true) == nil)
    }

    @Test func failedRetryStaysPending() {
        var s = PeerSwitch()
        _ = s.received(desired: true, current: false)
        s.attempted(true, error: refused)
        #expect(s.retry(current: false) == true)
        s.attempted(true, error: refused)
        #expect(s.retry(current: false) == true)
    }

    @Test func reachedMeanwhileIsDropped() {
        var s = PeerSwitch()
        _ = s.received(desired: true, current: false)
        s.attempted(true, error: refused)
        #expect(s.retry(current: true) == nil)
        #expect(s.pending == nil)
    }

    @Test func newerPeerStateReplacesThePendingOne() {
        var s = PeerSwitch()
        _ = s.received(desired: true, current: false)
        s.attempted(true, error: refused)
        // The peer grabbed the monitor back: nothing to do here, and the old "on" must not return.
        #expect(s.received(desired: false, current: false) == nil)
        #expect(s.retry(current: false) == nil)
    }

    @Test func localToggleCancelsThePeerRequest() {
        var s = PeerSwitch()
        _ = s.received(desired: true, current: false)
        s.attempted(true, error: refused)
        s.cancel()
        #expect(s.retry(current: false) == nil)
    }
}
