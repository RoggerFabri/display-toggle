// updater.swift
// Self-update for the installed app: checks the signed release manifest, offers an update in the
// menu, and (only when the user clicks it) downloads, verifies, replaces and restarts the binary.
// The decisions (versions, signature, manifest, hashes) live in update-logic.swift and are unit
// tested; this file is the networking / process glue. Updater state lives on the main queue; all
// network, disk and process work runs on `work` or URLSession's delegate queue.

import AppKit
import Foundation
import os

private let log = Logger(subsystem: "com.display-toggle", category: "update")

final class Updater {
    /// The version this binary was built as (`make VERSION=…`), `0.0.0-dev` for local builds.
    let currentVersion = appVersion(from: Bundle.main.infoDictionary)
    /// Called on the main queue whenever the menu-relevant state changes.
    var onChange: (() -> Void)?

    /// A verified newer release, if one was found.
    private(set) var available: ReleaseManifest?
    private(set) var checking = false
    private(set) var installing = false
    private var reportNextCheck = false
    /// "Install" was clicked while a check was running; install once it finishes.
    private var installAfterCheck = false
    private var timer: DispatchSourceTimer?

    private let defaults: UserDefaults
    private let work = DispatchQueue(label: "com.display-toggle.update")

    init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    /// Resolved path of the running binary.
    var executablePath: String {
        let url = Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])
        return url.resolvingSymlinksInPath().path
    }

    /// Install is only offered for the installed copy, not a build run from the checkout.
    var canInstall: Bool { isInstalledLocation(executablePath) }

    // MARK: Checking

    /// Automatic checks: 60 s after launch, then every 6 h — for the installed copy only, whatever
    /// its version; a build run from the checkout never checks by itself.
    func startAutomaticChecks() {
        guard runsAutomaticUpdateChecks(resolvedPath: executablePath), timer == nil else { return }
        let t = DispatchSource.makeTimerSource(queue: .main)
        t.schedule(deadline: .now() + 60, repeating: 6 * 60 * 60, leeway: .seconds(60))
        t.setEventHandler { [weak self] in self?.check(userInitiated: false) }
        t.resume()
        timer = t
    }

    /// Fetch + verify the latest manifest. A user-initiated check reports its outcome once.
    func check(userInitiated: Bool) {
        guard !installing else { return }
        if userInitiated { reportNextCheck = true }
        guard !checking else { return }
        checking = true
        onChange?()
        let current = currentVersion
        work.async {
            let result = Result { try Self.fetchLatestManifest() }
            DispatchQueue.main.async { self.finishCheck(result, current: current) }
        }
    }

    private func finishCheck(_ result: Result<ReleaseManifest, Error>, current: String) {
        checking = false
        let report = reportNextCheck
        reportNextCheck = false
        let queuedInstall = installAfterCheck
        installAfterCheck = false
        // After reporting: install whatever is available now (a failed check keeps the earlier
        // release; an up-to-date result means there's nothing left to install).
        defer { if queuedInstall { install() } }
        switch result {
        case .success(let m) where isNewerRelease(m.version, than: current) && m.asset == nil:
            // A release without a macOS build isn't an update for us: up to date, like Windows.
            available = nil
            log.info("release \(m.version, privacy: .public) has no macOS asset; up to date")
            onChange?()
            if report { alert("Display Toggle is up to date", "Version \(current) is the latest macOS release.") }
        case .success(let m) where isNewerRelease(m.version, than: current):
            available = m
            log.info("update available: \(m.version, privacy: .public)")
            notifyOnce(m, alreadyShown: report)
            onChange?()
            if report { reportAvailable(m) }
        case .success(let m):
            available = nil
            log.info("up to date (latest \(m.version, privacy: .public))")
            onChange?()
            if report { alert("Display Toggle is up to date", "Version \(current) is the latest release.") }
        case .failure(let error):
            log.error("update check failed: \(String(describing: error), privacy: .public)")
            onChange?()
            if report { alert("Couldn't check for updates", describe(error)) }
        }
    }

    /// manifest.json + .sig → verified, decoded manifest. Runs on `work` (blocks it).
    private static func fetchLatestManifest() throws -> ReleaseManifest {
        let dir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        // manifest + signature share one size budget.
        let manifestFile = try download(Release.manifestURL, limit: Int64(Release.maxManifestBytes),
                                        to: dir.appendingPathComponent("manifest.json"))
        let manifest = try Data(contentsOf: manifestFile)
        let sigFile = try download(Release.signatureURL, limit: Int64(Release.maxManifestBytes - manifest.count),
                                   to: dir.appendingPathComponent("manifest.json.sig"))
        let sig = try Data(contentsOf: sigFile)
        return try parseSignedManifest(manifest, signature: sig, publicKeyDER: Release.publicKey)
    }

    // MARK: Notification

    /// One notification per version (remembered in settings). A bare binary (no .app bundle) can't
    /// use UNUserNotificationCenter — it throws for a process without a bundle — so this goes
    /// through `osascript`'s `display notification`, which macOS shows under Script Editor.
    private func notifyOnce(_ m: ReleaseManifest, alreadyShown: Bool) {
        guard defaults.string(forKey: "updateNotified") != m.version else { return }
        defaults.set(m.version, forKey: "updateNotified")
        guard !alreadyShown else { return } // the user asked and gets an alert instead
        let text = "Version \(m.version) is available. Right-click the menu-bar icon to install it."
        work.async {
            let r = runTool("/usr/bin/osascript", [
                "-e", "on run argv",
                "-e", "display notification (item 1 of argv) with title (item 2 of argv)",
                "-e", "end run",
                text, "Display Toggle",
            ], timeout: 15)
            if r.status != 0 { log.error("notification failed: \(r.output, privacy: .public)") }
        }
    }

    // MARK: Install

    /// Download, verify, replace and restart. Only ever runs from the user's click.
    func install() {
        guard let m = available, !installing else { return }
        // A check is running (e.g. the automatic one): don't drop the click, run it when that's done.
        guard !checking else { installAfterCheck = true; return }
        guard canInstall else { NSWorkspace.shared.open(releasePageURL(m)); return }
        guard let asset = m.asset, let url = assetDownloadURL(tag: m.tag, asset: asset) else {
            alert("Couldn't install the update", describe(UpdateError.missingAsset))
            return
        }
        installing = true
        onChange?()
        let target = executablePath
        work.async {
            do {
                try Self.stageAndReplace(url: url, asset: asset, version: m.version, target: target)
                log.info("installed \(m.version, privacy: .public); restarting")
                Self.restart(target: target)
            } catch {
                log.error("update install failed: \(String(describing: error), privacy: .public)")
                DispatchQueue.main.async {
                    self.installing = false
                    self.onChange?()
                    self.alert("Couldn't install the update", "\(self.describe(error))\n\nThe installed version was left unchanged.")
                }
            }
        }
    }

    /// Everything up to and including the replace. Throws before touching `target` on any problem.
    private static func stageAndReplace(url: URL, asset: ReleaseAsset, version: String, target: String) throws {
        let dir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: dir) }

        // Download and check against the signed manifest.
        let archive = try download(url, limit: asset.size, to: dir.appendingPathComponent(asset.name))
        let archived = try Data(contentsOf: archive, options: .mappedIfSafe)
        guard assetMatches(archived, asset) else {
            throw UpdateError.assetMismatch
        }

        // Extract: the archive must hold exactly one regular file, `display-toggle`.
        let out = dir.appendingPathComponent("extracted", isDirectory: true)
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: false)
        let tar = runTool("/usr/bin/tar", ["-xzf", archive.path, "-C", out.path], timeout: 60)
        guard tar.status == 0 else { throw UpdateError.failed("couldn't unpack the download (\(tar.output))") }
        let entries = try FileManager.default.contentsOfDirectory(atPath: out.path)
        let staged = out.appendingPathComponent("display-toggle")
        let attrs = try? FileManager.default.attributesOfItem(atPath: staged.path)
        guard entries == ["display-toggle"], attrs?[.type] as? FileAttributeType == .typeRegular else {
            throw UpdateError.failed("the download doesn't contain the expected app binary")
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: staged.path)

        // It must run on this Mac and be the version the manifest promised.
        let probe = runTool(staged.path, ["--version"], timeout: 15)
        guard probe.status == 0, probe.output == version else {
            throw UpdateError.failed("the new version didn't start correctly")
        }

        // Replace: directly if we may write there, else behind the standard admin prompt.
        let targetDir = (target as NSString).deletingLastPathComponent
        let fm = FileManager.default
        if fm.isWritableFile(atPath: target), fm.isWritableFile(atPath: targetDir) {
            let inst = runTool("/usr/bin/install", ["-m", "755", staged.path, target], timeout: 60)
            guard inst.status == 0 else { throw UpdateError.failed("couldn't replace the app (\(inst.output))") }
            let sign = runTool("/usr/bin/codesign", ["--sign", "-", "--force", target], timeout: 60)
            if sign.status != 0 { log.error("codesign failed: \(sign.output, privacy: .public)") }
        } else {
            let admin = runTool("/usr/bin/osascript", adminReplaceArguments(staged: staged.path, target: target), timeout: 600)
            guard admin.status == 0 else {
                throw UpdateError.failed(admin.output.contains("-128")
                    ? "the administrator prompt was cancelled"
                    : "couldn't replace the app (\(admin.output))")
            }
        }
    }

    /// Restart into the new binary: through launchd when it runs us, else spawn it and quit.
    private static func restart(target: String) {
        let service = "gui/\(getuid())/\(launchAgentLabel(user: NSUserName()))"
        if runTool("/bin/launchctl", ["print", service], timeout: 15).status == 0 {
            // launchd stops this process and starts the new binary; on success we don't come back.
            // If we're still here, launchd didn't run *this* process (e.g. it was started by hand):
            // its new instance loses the single-instance lock to us, so fall through and hand over.
            let r = runTool("/bin/launchctl", ["kickstart", "-k", service], timeout: 30)
            log.info("launchctl kickstart returned \(r.status): \(r.output, privacy: .public)")
            if r.status != 0 {
                // Exit non-zero instead: the agent's KeepAlive {SuccessfulExit: false} restarts us
                // into the new binary. A waiter spawned here would die with us when launchd reaps
                // the job, and exiting 0 would leave the app stopped.
                log.error("kickstart failed; exiting so launchd restarts the new binary")
                DispatchQueue.main.async { exit(1) }
                return
            }
        }
        // Not under the launch agent (or kickstart left us running): start the new copy once we've exited —
        // it would otherwise lose the single-instance lock race and exit at once.
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec \"$2\"",
                       "sh", String(getpid()), target]
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { log.error("relaunch failed: \(error.localizedDescription, privacy: .public)") }
        DispatchQueue.main.async { NSApp.terminate(nil) }
    }

    // MARK: Reporting

    private func reportAvailable(_ m: ReleaseManifest) {
        let a = NSAlert()
        a.messageText = "Update available: v\(m.version)"
        if canInstall {
            a.informativeText = "You have version \(currentVersion). Install it and restart Display Toggle now?"
            a.addButton(withTitle: "Install and Restart")
            a.addButton(withTitle: "Later")
        } else {
            a.informativeText = "You have version \(currentVersion), run from a build folder rather than "
                + "the installed copy. Run `sudo make install` from a newer checkout to update."
            a.addButton(withTitle: "Show Release")
            a.addButton(withTitle: "OK")
        }
        NSApp.activate(ignoringOtherApps: true)
        guard a.runModal() == .alertFirstButtonReturn else { return }
        if canInstall { install() } else { NSWorkspace.shared.open(releasePageURL(m)) }
    }

    private func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }

    private func describe(_ error: Error) -> String {
        guard let e = error as? UpdateError else { return error.localizedDescription }
        let s = e.description
        return s.prefix(1).uppercased() + String(s.dropFirst()) + "."
    }
}

// MARK: - Helpers (run off the main queue)

private func makeTempDir() throws -> URL {
    let dir = FileManager.default.temporaryDirectory
        .appendingPathComponent("display-toggle-update-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false,
                                            attributes: [.posixPermissions: 0o700])
    return dir
}

/// Runs a tool to completion (stdout + stderr captured), killing it after `timeout`. Blocks.
@discardableResult
private func runTool(_ path: String, _ args: [String], timeout: TimeInterval) -> (status: Int32, output: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = pipe
    p.standardInput = FileHandle.nullDevice
    do { try p.run() } catch { return (-1, error.localizedDescription) }
    let killer = DispatchWorkItem { if p.isRunning { p.terminate() } }
    DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    killer.cancel()
    return (p.terminationStatus, String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines))
}

/// Downloads `url` to `destination` over HTTPS only (redirects too), failing on a non-200 status or
/// once more than `limit` bytes arrive. Times out after 30 s without data (headers included), not
/// on total duration (an hour is only a backstop). Blocks the calling (non-main) queue until done.
private func download(_ url: URL, limit: Int64, to destination: URL) throws -> URL {
    let done = DispatchSemaphore(value: 0)
    var outcome: Result<URL, Error> = .failure(UpdateError.failed("download didn't finish"))
    let d = LimitedDownload(limit: limit, destination: destination) { outcome = $0; done.signal() }
    let config = URLSessionConfiguration.ephemeral
    config.timeoutIntervalForRequest = 30      // idle: until the headers, then between reads
    config.timeoutIntervalForResource = 3600
    config.requestCachePolicy = .reloadIgnoringLocalCacheData
    config.urlCache = nil
    let session = URLSession(configuration: config, delegate: d, delegateQueue: nil)
    session.downloadTask(with: url).resume()
    session.finishTasksAndInvalidate()
    done.wait()
    return try outcome.get()
}

// Its state is only touched from the session's serial delegate queue.
private final class LimitedDownload: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let limit: Int64
    private let destination: URL
    private let completion: (Result<URL, Error>) -> Void
    private var failure: Error?
    private var saved = false

    init(limit: Int64, destination: URL, completion: @escaping (Result<URL, Error>) -> Void) {
        self.limit = limit
        self.destination = destination
        self.completion = completion
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        // Refusing hands back the redirect response itself, which then fails the 200 check.
        completionHandler(request.url?.scheme?.lowercased() == "https" ? request : nil)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        if totalBytesWritten > limit || totalBytesExpectedToWrite > limit {
            failure = UpdateError.tooLarge
            downloadTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard failure == nil else { return }
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { failure = UpdateError.http(status); return }
        guard downloadTask.response?.url?.scheme?.lowercased() == "https" else {
            failure = UpdateError.failed("the download was redirected away from HTTPS")
            return
        }
        let size = ((try? FileManager.default.attributesOfItem(atPath: location.path))?[.size] as? NSNumber)?.int64Value ?? 0
        guard size <= limit else { failure = UpdateError.tooLarge; return }
        do {
            try FileManager.default.moveItem(at: location, to: destination)
            saved = true
        } catch {
            failure = error
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let f = failure ?? error {
            completion(.failure(f))
        } else if saved {
            completion(.success(destination))
        } else {
            completion(.failure(UpdateError.failed("download didn't finish")))
        }
    }
}
