// display-toggle.swift
// Menu bar app — left-click to toggle, right-click for menu.
//
// Compile:  make   (swiftc with peer-logic.swift, peer-link.swift, update-logic.swift, updater.swift
//                  + an embedded Info.plist for Local Network / Bonjour access and the version)
// Test:     make test   (unit tests for peer-logic.swift + update-logic.swift, Swift Testing via Package.swift)
// Run:      ./display-toggle
//
// Private APIs used:
//   macOS 15+ / 26:  CGSConfigureDisplayEnabled  (CoreGraphics)
//   macOS 13–14:     CoreDisplay_Display_SetUserEnabled  (CoreDisplay)

import AppKit
import Foundation
import CoreGraphics
import CryptoKit
import Network
import SwiftUI
import SystemConfiguration
import dnssd

// MARK: - Private API

private typealias CGSConfigureDisplayEnabledFn =
    @convention(c) (CGDisplayConfigRef?, CGDirectDisplayID, Bool) -> CGError
private typealias CDSetUserEnabledFn = @convention(c) (CGDirectDisplayID, Bool) -> Void
private typealias CDIsUserEnabledFn  = @convention(c) (CGDirectDisplayID) -> Bool

private func loadSym<T>(_ handle: UnsafeMutableRawPointer, _ name: String) -> T? {
    dlsym(handle, name).map { unsafeBitCast($0, to: T.self) }
}

private let cgHandle = dlopen("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", RTLD_LAZY)
private let cdHandle = dlopen("/System/Library/Frameworks/CoreDisplay.framework/CoreDisplay",   RTLD_LAZY)

private let cgsConfigureEnabled: CGSConfigureDisplayEnabledFn? =
    cgHandle.flatMap { loadSym($0, "CGSConfigureDisplayEnabled") }
private let cdSetEnabled: CDSetUserEnabledFn? =
    cdHandle.flatMap { loadSym($0, "CoreDisplay_Display_SetUserEnabled") }
private let cdIsEnabled: CDIsUserEnabledFn? =
    cdHandle.flatMap { loadSym($0, "CoreDisplay_Display_IsUserEnabled") }

// MARK: - Display utilities

private func setDisplayEnabled(_ id: CGDirectDisplayID, _ enabled: Bool) -> Bool {
    if let fn = cgsConfigureEnabled {
        var config: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&config) == .success else { return false }
        let err = fn(config, id, enabled)
        guard err == .success else { CGCancelDisplayConfiguration(config); return false }
        return CGCompleteDisplayConfiguration(config, .permanently) == .success
    }
    if let fn = cdSetEnabled { fn(id, enabled); return true }
    return false
}

private func isDisplayEnabled(_ id: CGDirectDisplayID) -> Bool {
    CGDisplayIsActive(id) != 0 || (cdIsEnabled?(id) ?? false)
}

private func onlineDisplayIDs() -> [CGDirectDisplayID] {
    var count: UInt32 = 0
    CGGetOnlineDisplayList(0, nil, &count)
    guard count > 0 else { return [] }
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
    CGGetOnlineDisplayList(count, &ids, &count)
    return Array(ids.prefix(Int(count)))
}

private func displayName(for id: CGDirectDisplayID) -> String {
    let key = NSDeviceDescriptionKey("NSScreenNumber")
    for screen in NSScreen.screens {
        if let num = screen.deviceDescription[key] as? CGDirectDisplayID, num == id {
            let name = screen.localizedName
            var cache = prefs.dictionary(forKey: "names") as? [String: String] ?? [:]
            if cache[String(id)] != name { // called on every menu open — write only on change
                cache[String(id)] = name
                prefs.set(cache, forKey: "names")
            }
            return name
        }
    }
    let cache = prefs.dictionary(forKey: "names") as? [String: String] ?? [:]
    return cache[String(id)] ?? "Display \(id)"
}

// MARK: - State persistence  ~/Library/Preferences/com.display-toggle.plist

private let prefs = UserDefaults(suiteName: "com.display-toggle")!

private func readState() -> (on: Bool, ids: [CGDirectDisplayID]) {
    let on = prefs.object(forKey: "on") as? Bool ?? true
    let ids = (prefs.array(forKey: "ids") as? [Int] ?? []).map { CGDirectDisplayID($0) }
    return (on: on, ids: ids)
}

private func writeState(on: Bool, ids: [CGDirectDisplayID] = []) {
    prefs.set(on, forKey: "on")
    prefs.set(ids.map(Int.init), forKey: "ids")
}

private func readPeer() -> (host: String, port: UInt16) {
    let host = prefs.string(forKey: "peerHost") ?? ""
    let port = UInt16(exactly: prefs.integer(forKey: "peerPort")) ?? 0
    return (host: host, port: port == 0 ? 51763 : port)
}

private func writePeer(host: String, port: UInt16) {
    prefs.set(host, forKey: "peerHost")
    prefs.set(Int(port), forKey: "peerPort")
}

private func readSecret() -> String { prefs.string(forKey: "peerSecret") ?? "" }
private func writeSecret(_ secret: String) { prefs.set(secret, forKey: "peerSecret") }

/// Our persistent discovery ID (TXT `id`), created on first use — lets peers skip themselves and
/// follow each other across IP changes.
private func instanceID() -> String {
    if let id = prefs.string(forKey: "instanceId"), !id.isEmpty { return id }
    let id = UUID().uuidString
    prefs.set(id, forKey: "instanceId")
    return id
}

/// TXT `id` of the peer chosen from the discovery list; empty when the peer was entered manually.
private func readPeerID() -> String { prefs.string(forKey: "peerId") ?? "" }
private func writePeerID(_ id: String) { prefs.set(id, forKey: "peerId") }

/// True when the user picked *Choose peer ▸ None*: no listener, no advertising, no dialling, no
/// auto-pick — the app runs on its own until a peer is chosen again.
private func readStandalone() -> Bool { prefs.bool(forKey: "peerNone") }
private func writeStandalone(_ on: Bool) { prefs.set(on, forKey: "peerNone") }

/// This machine's LAN IPv4 (prefers en0/en1 — Wi-Fi / Ethernet), for showing in the menu.
private func localIPAddress() -> String? {
    var ifaddr: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
    defer { freeifaddrs(ifaddr) }

    var fallback: String?
    for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
        let flags = Int32(ptr.pointee.ifa_flags)
        guard let addr = ptr.pointee.ifa_addr else { continue }
        guard (flags & (IFF_UP | IFF_RUNNING)) == (IFF_UP | IFF_RUNNING),
              (flags & IFF_LOOPBACK) == 0,
              addr.pointee.sa_family == UInt8(AF_INET) else { continue }

        var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
        getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST)
        let ip = String(cString: host)
        let name = String(cString: ptr.pointee.ifa_name)
        if name == "en0" { return ip }      // primary interface wins outright
        if fallback == nil { fallback = ip } // else remember the first usable one
    }
    return fallback
}

// MARK: - Icon
// ON  → normal "display" template (adapts to light/dark menu bar)
// OFF → same symbol tinted with tertiaryLabelColor (visually faded)
// connected → a green dot overlaid on the status button (PeerDotView), not baked into the image:
// a composite image is non-template and fractionally sized, so it renders blurred and off-tint.

private func makeIcon(on: Bool) -> NSImage? {
    guard let symbol = NSImage(systemSymbolName: "display", accessibilityDescription: nil) else {
        return nil
    }
    if on {
        symbol.isTemplate = true
        return symbol
    }
    return symbol.withSymbolConfiguration(.init(paletteColors: [.tertiaryLabelColor]))
}

/// The "peer connected" dot. Ignores clicks so the status button still receives them.
private final class PeerDotView: NSView {
    static let diameter: CGFloat = 5

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.cornerRadius = Self.diameter / 2
    }

    override var wantsUpdateLayer: Bool { true }

    // Resolved here (not once at init) so the colour follows light/dark appearance changes.
    override func updateLayer() {
        layer?.backgroundColor = NSColor.systemGreen.cgColor
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

// MARK: - Menu item view

private struct MenuSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            ZStack {
                Capsule()
                    .fill(configuration.isOn ? Color.blue : Color(white: 0.45))
                    .frame(width: 36, height: 22)
                Circle()
                    .fill(Color.white)
                    .shadow(color: .black.opacity(0.2), radius: 1, y: 1)
                    .frame(width: 18, height: 18)
                    .offset(x: configuration.isOn ? 7 : -7)
                    .animation(.easeInOut(duration: 0.15), value: configuration.isOn)
            }
        }
        .buttonStyle(.plain)
    }
}

private struct DisplayToggleRow: View {
    let name: String
    let width: CGFloat
    @State private var isOn: Bool
    @State private var isHovered = false
    let onToggle: (Bool) -> Void

    init(name: String, width: CGFloat, isOn: Bool, onToggle: @escaping (Bool) -> Void) {
        self.name = name
        self.width = width
        self._isOn = State(initialValue: isOn)
        self.onToggle = onToggle
    }

    var body: some View {
        HStack {
            Text(name)
                .font(Font(NSFont.menuFont(ofSize: 0)))
                .foregroundStyle(isHovered ? Color.white : Color(NSColor.labelColor))
            Spacer()
            Toggle("", isOn: Binding(get: { isOn }, set: { v in isOn = v; onToggle(v) }))
                .toggleStyle(MenuSwitchStyle())
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 4)
        .frame(minWidth: width, maxWidth: .infinity, alignment: .leading)
        .background {
            if isHovered {
                RoundedRectangle(cornerRadius: 4)
                    .fill(Color.accentColor)
                    .padding(.horizontal, 5)
            }
        }
        .onHover { isHovered = $0 }
    }
}

// MARK: - Peer discovery (DNS-SD)
// Browses `_display-toggle._tcp` for other instances and resolves each to an IPv4 + port.
// Resolution goes through dnssd, never a TCP probe: a peer's listener adopts every inbound
// connection and drops its current one, so probing would break a live link. Callbacks are
// delivered on the main queue. See PROTOCOL.md "Discovery".

final class PeerBrowser {
    var onChange: (([DiscoveredPeer]) -> Void)?

    private let queue = DispatchQueue(label: "com.display-toggle.discovery")
    private var browser: NWBrowser?
    // Current browse results by TXT id, and what each resolved to.
    private var services: [String: (name: String, type: String, domain: String, os: String)] = [:]
    private var resolved: [String: DiscoveredPeer] = [:]
    private var resolvers: [String: ServiceResolver] = [:]
    private var selfID = ""
    // refresh() is called on every failed re-dial (~3s while the peer is down); throttle it so a
    // resolve gets the chance to finish instead of being cancelled and restarted each time.
    private var lastRefresh = Date.distantPast
    private let refreshInterval: TimeInterval = 10

    func start(selfID: String) {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.selfID = selfID
            let b = NWBrowser(for: .bonjourWithTXTRecord(type: peerServiceType, domain: nil), using: .tcp)
            b.browseResultsChangedHandler = { [weak self] results, _ in self?.update(results) }
            b.stateUpdateHandler = { [weak self] state in
                if case .failed = state {
                    // e.g. network change — restart the browse shortly.
                    self?.queue.asyncAfter(deadline: .now() + 2) { self?.restart() }
                }
            }
            b.start(queue: self.queue)
            self.browser = b
        }
    }

    /// Re-resolve every known peer (addresses can change without the browse record changing).
    /// At most once per `refreshInterval`, and never over a resolve that is still in flight.
    func refresh() {
        queue.async {
            guard Date().timeIntervalSince(self.lastRefresh) >= self.refreshInterval else { return }
            self.lastRefresh = Date()
            for id in self.services.keys where self.resolvers[id] == nil { self.resolve(id) }
        }
    }

    private func restart() {
        browser?.cancel(); browser = nil
        start(selfID: selfID)
    }

    private func update(_ results: Set<NWBrowser.Result>) {
        var seen: [String: (name: String, type: String, domain: String, os: String)] = [:]
        for result in results {
            guard case let .service(name, type, domain, _) = result.endpoint else { continue }
            var txt: [String: String] = [:]
            if case let .bonjour(record) = result.metadata { txt = normalizedTXT(record.dictionary) }
            // Self-filter by TXT id; fall back to the name for advertisers without one.
            let txtID = normalizedID(txt["id"])
            let id = txtID ?? name
            guard !isSelf(txtID: txtID, name: name, selfID: selfID, selfName: computerName()) else { continue }
            seen[id] = (name, type, domain, txt["os"] ?? "")
        }
        for id in services.keys where seen[id] == nil {
            resolvers.removeValue(forKey: id)?.cancel()
            resolved[id] = nil
        }
        let added = seen.keys.filter { services[$0] == nil }
        services = seen
        for id in added { resolve(id) }
        publish()
    }

    private func resolve(_ id: String) {
        guard let svc = services[id] else { return }
        resolvers.removeValue(forKey: id)?.cancel()
        let r = ServiceResolver(queue: queue) { [weak self] host, port in
            guard let self = self else { return }
            self.resolvers[id] = nil
            guard let svc = self.services[id] else { return }
            // A failed/timed-out resolve hides the entry rather than keeping a possibly stale address.
            self.resolved[id] = host.map { DiscoveredPeer(id: id, name: svc.name, host: $0, port: port, os: svc.os) }
            self.publish()
        }
        resolvers[id] = r
        r.start(name: svc.name, type: svc.type, domain: svc.domain)
    }

    private func publish() {
        let peers = resolved.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        DispatchQueue.main.async { self.onChange?(peers) }
    }
}

/// IPv4 subnets (address & mask) configured on interface `ifIndex`, as host-order pairs.
private func localSubnets(ifIndex: UInt32) -> [LocalSubnet] {
    var ifaddr: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
    defer { freeifaddrs(ifaddr) }
    var out: [LocalSubnet] = []
    for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
        guard let a = ptr.pointee.ifa_addr, let m = ptr.pointee.ifa_netmask,
              a.pointee.sa_family == UInt8(AF_INET),
              if_nametoindex(ptr.pointee.ifa_name) == ifIndex else { continue }
        let addr = a.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
        let mask = m.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
        out.append(LocalSubnet(addr: addr, mask: mask))
    }
    return out
}

/// One-shot DNS-SD resolve: service → host target + port → IPv4 address. Runs on `queue`;
/// `completion` fires exactly once (nil host on failure/timeout) unless cancelled first.
/// A host can publish A records for every interface it has (VPN, WSL/Hyper-V vEthernet…), so
/// addresses are collected briefly and one on the same subnet as the receiving interface wins.
private final class ServiceResolver {
    private let queue: DispatchQueue
    private var completion: ((String?, UInt16) -> Void)?
    private var resolveRef: DNSServiceRef?
    private var addrRef: DNSServiceRef?
    private var port: UInt16 = 0
    private var ifIndex: UInt32 = 0
    private var candidates: [UInt32] = []   // host-order IPv4s
    private var pickScheduled = false

    init(queue: DispatchQueue, completion: @escaping (String?, UInt16) -> Void) {
        self.queue = queue
        self.completion = completion
    }

    deinit { deallocate() } // safety net; normally finish()/cancel() already released the refs

    func start(name: String, type: String, domain: String) {
        let ctx = Unmanaged.passUnretained(self).toOpaque()
        let err = DNSServiceResolve(&resolveRef, 0, 0, name, type, domain, { _, _, ifIndex, err, _, hostTarget, port, _, _, ctx in
            guard let ctx = ctx else { return }
            let r = Unmanaged<ServiceResolver>.fromOpaque(ctx).takeUnretainedValue()
            guard err == kDNSServiceErr_NoError, let hostTarget = hostTarget else { r.finish(nil); return }
            r.lookUpAddress(host: hostTarget, port: UInt16(bigEndian: port), ifIndex: ifIndex)
        }, ctx)
        guard err == kDNSServiceErr_NoError, let ref = resolveRef else { finish(nil); return }
        DNSServiceSetDispatchQueue(ref, queue)
        queue.asyncAfter(deadline: .now() + 5) { [weak self] in self?.finish(nil) }
    }

    func cancel() {
        completion = nil
        deallocate()
    }

    private func lookUpAddress(host: UnsafePointer<CChar>, port: UInt16, ifIndex: UInt32) {
        self.port = port
        self.ifIndex = ifIndex
        if let ref = resolveRef { DNSServiceRefDeallocate(ref); resolveRef = nil }
        let ctx = Unmanaged.passUnretained(self).toOpaque()
        let err = DNSServiceGetAddrInfo(&addrRef, 0, ifIndex, DNSServiceProtocol(kDNSServiceProtocol_IPv4), host, { _, flags, _, err, _, addr, _, ctx in
            guard let ctx = ctx else { return }
            let r = Unmanaged<ServiceResolver>.fromOpaque(ctx).takeUnretainedValue()
            guard err == kDNSServiceErr_NoError, flags & kDNSServiceFlagsAdd != 0,
                  let addr = addr, addr.pointee.sa_family == UInt8(AF_INET) else { return }
            let ip = addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            r.addCandidate(ip)
        }, ctx)
        guard err == kDNSServiceErr_NoError, let ref = addrRef else { finish(nil); return }
        DNSServiceSetDispatchQueue(ref, queue)
    }

    private func addCandidate(_ ip: UInt32) {
        guard !candidates.contains(ip) else { return }
        candidates.append(ip)
        // Give the remaining A records a moment to arrive, then choose.
        if !pickScheduled {
            pickScheduled = true
            queue.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.pickCandidate() }
        }
    }

    private func pickCandidate() {
        guard let ip = pickAddress(candidates, localSubnets: localSubnets(ifIndex: ifIndex)) else { finish(nil); return }
        finish(ipv4String(ip))
    }

    private func finish(_ host: String?) {
        guard let done = completion else { return }
        completion = nil
        deallocate()
        done(host, port)
    }

    private func deallocate() {
        if let ref = resolveRef { DNSServiceRefDeallocate(ref); resolveRef = nil }
        if let ref = addrRef { DNSServiceRefDeallocate(ref); addrRef = nil }
    }
}

// MARK: - AppDelegate

class AppDelegate: NSObject, NSApplicationDelegate {
    var statusItem: NSStatusItem!
    var isOn = true
    let peer = PeerLink()
    var peerConnected = false
    private var peerDot: PeerDotView?
    var peerHostName: String?
    // Why a reachable peer isn't linked (version / secret mismatch), and its hostname if known.
    var peerProblem: (problem: PeerProblem, host: String?)?
    let discovery = PeerBrowser()
    var discovered: [DiscoveredPeer] = []
    let updater = Updater(defaults: prefs)

    func applicationDidFinishLaunching(_ notification: Notification) {
        isOn = readState().on

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.action = #selector(handleClick)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.target = self
            button.toolTip = "Display Toggle"
            // Re-place the peer dot when the button is laid out / resized (it may be connected
            // before the first layout, with zero bounds) …
            button.postsFrameChangedNotifications = true
            NotificationCenter.default.addObserver(forName: NSView.frameDidChangeNotification, object: button,
                                                   queue: .main) { [weak self] _ in self?.updateIcon() }
        }
        // … and when the menu bar moves to a display with a different backing scale (pixel grid).
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in self?.updateIcon() }
        updateIcon()

        peer.onPeerState = { [weak self] peerOn in self?.applyRemoteState(peerOn: peerOn) }
        peer.onConnection = { [weak self] connected, _, host in
            guard let self = self else { return }
            self.peerConnected = connected
            if !connected { self.peerHostName = nil }
            else if let host = host, !host.isEmpty { self.peerHostName = host }
            self.updateIcon()
            // The peer may have moved to a new IP — re-resolve so followDiscoveredPeer can catch it.
            if !connected { self.discovery.refresh() }
        }
        peer.onProblem = { [weak self] problem, host in
            self?.peerProblem = problem.map { ($0, host) }
        }
        startPeer()

        discovery.onChange = { [weak self] peers in
            self?.discovered = peers
            self?.followDiscoveredPeer()
        }
        discovery.start(selfID: instanceID())

        updater.onChange = { [weak self] in
            guard let self = self, let button = self.statusItem.button else { return }
            button.toolTip = self.updater.available.map { "Display Toggle — update available: v\($0.version)" }
                ?? "Display Toggle"
        }
        updater.startAutomaticChecks()
    }

    @objc func installUpdate() { updater.install() }
    @objc func checkForUpdates() { updater.check(userInitiated: true) }

    func startPeer() {
        if readStandalone() {
            peer.stop() // the cancelled connection doesn't report back, so clear the state here
            peerConnected = false
            peerHostName = nil
            updateIcon()
            return
        }
        let (host, port) = readPeer()
        peerProblem = nil
        peer.start(peerHost: host, port: port, currentOn: isOn, secret: readSecret(), id: instanceID())
    }

    /// Follow the chosen peer to a new address, or auto-pick the only one found (see peerToAdopt).
    private func followDiscoveredPeer() {
        let (host, port) = readPeer()
        if let p = peerToAdopt(connected: peerConnected, host: host, port: port,
                               peerID: readPeerID(), discovered: discovered, standalone: readStandalone()) {
            usePeer(p)
        }
    }

    private func usePeer(_ p: DiscoveredPeer) {
        writePeer(host: p.host, port: p.port)
        writePeerID(p.id)
        writeStandalone(false)
        startPeer()
    }

    /// "Choose peer ▸ None": forget the peer and stop the link entirely (standalone).
    @objc func chooseNoPeer() {
        writePeer(host: "", port: readPeer().port)
        writePeerID("")
        writeStandalone(true)
        startPeer()
    }

    @objc func choosePeer(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String,
              let p = discovered.first(where: { $0.id == id }) else { return }
        usePeer(p)
    }

    @objc func handleClick() {
        guard let event = NSApp.currentEvent else { return }
        if event.type == .rightMouseUp {
            showMenu()
        } else {
            performToggle()
        }
    }

    /// Current aggregate intent: on if any external is enabled, else the stored intent.
    private func aggregateOn() -> Bool {
        let externals = onlineDisplayIDs().filter { CGDisplayIsBuiltin($0) == 0 }
        return externals.isEmpty ? readState().on : externals.contains { isDisplayEnabled($0) }
    }

    /// Drive all externals to `on`. Updates state + icon. Returns true if applied.
    @discardableResult
    func setExternals(to on: Bool) -> Bool {
        let externals = onlineDisplayIDs().filter { CGDisplayIsBuiltin($0) == 0 }
        if on {
            var targets = externals.filter { !isDisplayEnabled($0) }
            for id in readState().ids where !targets.contains(id) { targets.append(id) }
            var ok = true
            for id in targets where !setDisplayEnabled(id, true) { ok = false }
            guard ok else { return false }
            writeState(on: true)
            isOn = true
        } else {
            let targets = externals.filter { isDisplayEnabled($0) }
            for id in targets { _ = displayName(for: id) }
            var ok = true
            for id in targets where !setDisplayEnabled(id, false) { ok = false }
            guard ok else { return false }
            writeState(on: false, ids: targets.isEmpty ? readState().ids : targets)
            isOn = false
        }
        updateIcon()
        return true
    }

    @objc func performToggle() {
        if setExternals(to: !aggregateOn()) {
            peer.sendState(isOn)
        } else {
            NSSound.beep()
            let alert = NSAlert()
            alert.messageText = "Couldn't switch the display"
            alert.informativeText = "The display change was rejected by the system."
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
        }
    }

    /// Apply the peer's state inversely and silently (no re-broadcast — breaks echo loops).
    func applyRemoteState(peerOn: Bool) {
        let desired = !peerOn
        guard aggregateOn() != desired else { return }
        setExternals(to: desired)
    }

    func toggleDisplayFromView(id: CGDirectDisplayID, on: Bool) {
        // On failure nothing changed: don't record it or tell the peer (it would grab the monitor
        // while we still drive it).
        guard setDisplayEnabled(id, on) else { NSSound.beep(); return }
        var offIDs = readState().ids
        if on { offIDs.removeAll { $0 == id } }
        else if !offIDs.contains(id) { offIDs.append(id) }
        writeState(on: offIDs.isEmpty, ids: offIDs)
        isOn = offIDs.isEmpty
        updateIcon()
        peer.sendState(isOn)
    }

    /// Minimum width for the SwiftUI toggle rows — enough to fit their own content. The rows also
    /// carry `autoresizingMask = .width`, so AppKit stretches them to the menu's *actual* width
    /// (set by the widest standard row, e.g. a long peer IP). The row therefore always spans the
    /// full menu with no trailing gap, without us having to estimate the standard-item chrome.
    private func toggleRowWidth(names: [String]) -> CGFloat {
        let font = NSFont.menuFont(ofSize: 0)
        func textWidth(_ s: String) -> CGFloat { (s as NSString).size(withAttributes: [.font: font]).width }
        // Must fit the row's own content: name + spacer(8) + switch(36) + h-padding(28).
        let nameContent = (names.map(textWidth).max() ?? 0) + 8 + 36 + 28
        return ceil(max(nameContent, 240))
    }

    func showMenu() {
        let menu = NSMenu()

        if let update = updateMenuItem() {
            menu.addItem(update)
            menu.addItem(.separator())
        }

        let externals = onlineDisplayIDs().filter { CGDisplayIsBuiltin($0) == 0 }
        let savedOffIDs = readState().ids
        let toggleIDs = externals + savedOffIDs.filter { !externals.contains($0) }

        // Build the informational labels first so we can size the toggle rows to match the
        // widest row exactly (otherwise a long IP/peer label makes the menu wider than the
        // fixed-width toggle rows, leaving a gap at their trailing edge).
        let (peerHost, peerPort) = readPeer()
        let thisTitle = "This Mac: \(localIPAddress() ?? "unknown"):\(peerPort)"
        // Prefer live connection state — a peer that dialed *us* counts as connected even
        // if we never configured a peer host ourselves.
        let peerTitle: String
        if readStandalone() {
            peerTitle = "Peer: none (standalone)"
        } else if !peerConnected, let (problem, host) = peerProblem {
            let who = host ?? (peerHost.isEmpty ? "peer" : "\(peerHost):\(peerPort)")
            switch problem {
            case .needsUpdate: peerTitle = "Peer: \(who) — needs update"
            case .notAuthenticated: peerTitle = "Peer: \(who) — not authenticated"
            case .requiresSecret: peerTitle = "Peer: \(who) — requires a secret"
            }
        } else if peerConnected {
            let who = peerHostName ?? (peerHost.isEmpty ? nil : peerHost)
            peerTitle = who.map { "Peer: \($0) ✓" } ?? "Peer: connected ✓"
        } else if peerHost.isEmpty {
            peerTitle = "Peer: not set"
        } else {
            peerTitle = "Peer: \(peerHost):\(peerPort) (offline)"
        }

        let rowWidth = toggleRowWidth(names: toggleIDs.map { displayName(for: $0) })

        for id in toggleIDs {
            let row = DisplayToggleRow(name: displayName(for: id), width: rowWidth, isOn: isDisplayEnabled(id) && externals.contains(id)) { [weak self] on in
                self?.toggleDisplayFromView(id: id, on: on)
            }
            let host = NSHostingView(rootView: row)
            host.frame.size = NSSize(width: rowWidth, height: host.fittingSize.height)
            // Let AppKit widen the row to the menu's real width (set by the widest standard item),
            // so the toggle sits flush at the trailing edge instead of leaving a gap.
            host.autoresizingMask = [.width]
            let item = NSMenuItem()
            item.view = host
            menu.addItem(item)
        }
        if toggleIDs.isEmpty {
            let none = NSMenuItem(title: "No external displays", action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
        }

        menu.addItem(.separator())
        let thisStatus = NSMenuItem(title: thisTitle, action: nil, keyEquivalent: "")
        thisStatus.isEnabled = false
        menu.addItem(thisStatus)
        let peerStatus = NSMenuItem(title: peerTitle, action: nil, keyEquivalent: "")
        peerStatus.isEnabled = false
        menu.addItem(peerStatus)
        menu.addItem(choosePeerItem(peerHost: peerHost, peerPort: peerPort))

        menu.addItem(.separator())
        let version = NSMenuItem(title: "Version \(updater.currentVersion)", action: nil, keyEquivalent: "")
        version.isEnabled = false
        menu.addItem(version)
        let busy = updater.checking || updater.installing
        let check = NSMenuItem(title: updater.checking ? "Checking for updates…" : "Check for updates",
                               action: busy ? nil : #selector(checkForUpdates), keyEquivalent: "")
        check.target = self
        check.isEnabled = !busy
        menu.addItem(check)

        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit",
                                action: #selector(NSApplication.terminate(_:)),
                                keyEquivalent: "q"))

        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        DispatchQueue.main.async { self.statusItem.menu = nil }
    }

    /// Top-of-menu line for a verified newer release: install it (installed copy), or open its
    /// release page (a build run from the checkout can't replace itself).
    private func updateMenuItem() -> NSMenuItem? {
        guard let m = updater.available else { return nil }
        let title: String
        if updater.installing {
            title = "Installing v\(m.version)…"
        } else if updater.canInstall {
            title = "Update available: v\(m.version) — Install and restart"
        } else {
            title = "Update available: v\(m.version) — run make install from a newer checkout"
        }
        let item = NSMenuItem(title: title, action: updater.installing ? nil : #selector(installUpdate), keyEquivalent: "")
        item.target = self
        item.isEnabled = !updater.installing
        return item
    }

    /// "Choose peer ▸" — None (standalone), discovered instances (current one checked), then manual entry.
    private func choosePeerItem(peerHost: String, peerPort: UInt16) -> NSMenuItem {
        discovery.refresh() // pick up address changes for the next time the menu opens
        let sub = NSMenu()
        let peerID = readPeerID()
        let standalone = readStandalone()
        let none = NSMenuItem(title: "None (standalone)", action: #selector(chooseNoPeer), keyEquivalent: "")
        none.state = standalone ? .on : .off
        sub.addItem(none)
        sub.addItem(.separator())
        for p in discovered {
            let item = NSMenuItem(title: "\(p.name) — \(p.host)", action: #selector(choosePeer(_:)), keyEquivalent: "")
            item.representedObject = p.id
            item.state = !standalone && isCurrentPeer(p, peerID: peerID, host: peerHost, port: peerPort) ? .on : .off
            sub.addItem(item)
        }
        if discovered.isEmpty {
            let searching = NSMenuItem(title: "Searching…", action: nil, keyEquivalent: "")
            searching.isEnabled = false
            sub.addItem(searching)
        }
        sub.addItem(.separator())
        sub.addItem(NSMenuItem(title: "Set peer manually…", action: #selector(setPeer), keyEquivalent: ""))
        let item = NSMenuItem(title: "Choose peer", action: nil, keyEquivalent: "")
        item.submenu = sub
        return item
    }

    @objc func setPeer() {
        let (host, port) = readPeer()

        let alert = NSAlert()
        alert.messageText = "Set peer"
        alert.informativeText = "The other machine's IP / hostname and port (use the same port on both). Optionally set a shared secret on both machines to authenticate the link."

        let hostField = NSTextField(frame: NSRect(x: 0, y: 60, width: 260, height: 24))
        hostField.stringValue = host
        hostField.placeholderString = "Peer IP, e.g. 198.51.100.50"
        let portField = NSTextField(frame: NSRect(x: 0, y: 30, width: 260, height: 24))
        portField.stringValue = String(port)
        portField.placeholderString = "Port (51763)"
        let secretField = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        secretField.stringValue = readSecret()
        secretField.placeholderString = "Shared secret (optional)"

        let accessory = NSView(frame: NSRect(x: 0, y: 0, width: 260, height: 84))
        accessory.addSubview(hostField)
        accessory.addSubview(portField)
        accessory.addSubview(secretField)
        alert.accessoryView = accessory

        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")

        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        let newHost = hostField.stringValue.trimmingCharacters(in: .whitespaces)
        let newPort = UInt16(portField.stringValue.trimmingCharacters(in: .whitespaces)) ?? 51763
        writePeer(host: newHost, port: newPort)
        writePeerID("") // manual entry — stop following a discovered peer
        writeStandalone(false)
        writeSecret(secretField.stringValue)
        startPeer()
    }

    func updateIcon() {
        guard let button = statusItem.button else { return }
        button.image = makeIcon(on: isOn)
        updatePeerDot(in: button)
    }

    /// Show/hide the green dot at the top-right corner of the symbol, kept inside the button and
    /// snapped to the pixel grid.
    private func updatePeerDot(in button: NSStatusBarButton) {
        guard peerConnected else {
            peerDot?.removeFromSuperview()
            peerDot = nil
            return
        }
        let dot = peerDot ?? PeerDotView(frame: .zero)
        if dot.superview !== button { button.addSubview(dot) }
        peerDot = dot

        let d = PeerDotView.diameter
        let image = button.cell?.imageRect(forBounds: button.bounds) ?? button.bounds
        let x = min(image.maxX - d * 0.6, button.bounds.maxX - d)
        let y = button.isFlipped ? max(image.minY - d * 0.4, button.bounds.minY)
                                 : min(image.maxY - d * 0.6, button.bounds.maxY - d)
        dot.frame = button.backingAlignedRect(NSRect(x: x, y: y, width: d, height: d),
                                              options: .alignAllEdgesNearest)
    }
}

// MARK: - Entry point

// `@main` rather than top-level code: the app is compiled from more than one file.
@main
enum DisplayToggleApp {
    static func main() {
        if CommandLine.arguments.dropFirst().contains("--version") {
            print(appVersion(from: Bundle.main.infoDictionary))
            exit(0)
        }

        // Exclusive non-blocking lock — second instance exits immediately. In the per-user temp dir,
        // not /tmp, so another user's lock file can't stop us from starting.
        let lockPath = (NSTemporaryDirectory() as NSString).appendingPathComponent("display_toggle.lock")
        let lockFd = open(lockPath, O_CREAT | O_RDWR, 0o600)
        guard lockFd >= 0, flock(lockFd, LOCK_EX | LOCK_NB) == 0 else { exit(0) }

        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let delegate = AppDelegate()
        app.delegate = delegate
        app.run()
    }
}
