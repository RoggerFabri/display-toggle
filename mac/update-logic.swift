// update-logic.swift
// Pure self-update decisions (no networking, no UI): version compare, manifest signature check,
// strict manifest decode and asset hash check. Compiled into the app by `make` and unit-tested by
// `make test`, like peer-logic.swift. Windows implements the same contract; keep the rules identical.

import CryptoKit
import Foundation

enum Release {
    /// SubjectPublicKeyInfo (DER, base64) of the key CI signs `manifest.json` with.
    static let publicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4jcwU42Q5tlgqeX8oyeLCcmTs2FPxiKUiAeYjEkbjhrQC+opSQ6cTAfaysybeCWv+ffH/S7gSdyCvzUKeps1GA=="
    static var publicKey: Data { Data(base64Encoded: publicKeyBase64)! }

    static let manifestURL =
        URL(string: "https://github.com/RoggerFabri/display-toggle/releases/latest/download/manifest.json")!
    static let signatureURL =
        URL(string: "https://github.com/RoggerFabri/display-toggle/releases/latest/download/manifest.json.sig")!
    static let downloadBase = "https://github.com/RoggerFabri/display-toggle/releases/download/"

    /// Key of this platform's entry in the manifest's `assets`.
    static let macAssetKey = "macos"
    /// manifest.json + manifest.json.sig together.
    static let maxManifestBytes = 64 * 1024
    static let maxAssetBytes: Int64 = 100 * 1024 * 1024
    static let devVersion = "0.0.0-dev"
}

// MARK: - Versions

/// A release version: exactly `X.Y.Z`, decimal, no leading zeros, no suffix.
struct ReleaseVersion: Comparable, Equatable, CustomStringConvertible {
    let major: Int
    let minor: Int
    let patch: Int

    init?(_ text: String) {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var nums: [Int] = []
        for p in parts {
            guard !p.isEmpty, p.count <= 9, p.allSatisfy({ $0 >= "0" && $0 <= "9" }),
                  p == "0" || !p.hasPrefix("0"), let n = Int(p) else { return nil }
            nums.append(n)
        }
        major = nums[0]; minor = nums[1]; patch = nums[2]
    }

    static func < (a: ReleaseVersion, b: ReleaseVersion) -> Bool {
        (a.major, a.minor, a.patch) < (b.major, b.minor, b.patch)
    }

    var description: String { "\(major).\(minor).\(patch)" }
}

/// Drops SemVer build metadata (`+…`).
private func withoutBuildMetadata(_ v: String) -> String {
    String(v.split(separator: "+", maxSplits: 1, omittingEmptySubsequences: false).first ?? "")
}

/// A dev build: anything with a `-` suffix (e.g. `0.0.0-dev`), or that isn't a release version at all.
func isDevVersion(_ version: String) -> Bool {
    ReleaseVersion(withoutBuildMetadata(version)) == nil
}

/// True if `candidate` (from a manifest) is a release version newer than `current`. A dev build is
/// older than every release.
func isNewerRelease(_ candidate: String, than current: String) -> Bool {
    guard let new = ReleaseVersion(candidate) else { return false }
    guard let cur = ReleaseVersion(withoutBuildMetadata(current)) else { return true }
    return new > cur
}

/// The version stamped into the embedded Info.plist by `make VERSION=…`, else the dev version.
func appVersion(from info: [String: Any]?) -> String {
    guard let v = info?["CFBundleShortVersionString"] as? String,
          !v.trimmingCharacters(in: .whitespaces).isEmpty else { return Release.devVersion }
    return v.trimmingCharacters(in: .whitespaces)
}

// MARK: - Manifest

struct ReleaseAsset: Equatable {
    let name: String
    let sha256: String   // 64 lowercase hex
    let size: Int64
}

struct ReleaseManifest: Equatable {
    let version: String
    let tag: String
    let notesURL: String?
    /// This platform's entry in `assets` (other platforms' entries are ignored). Nil when the
    /// release has none — the caller treats that as "no update for this platform".
    let asset: ReleaseAsset?
}

enum UpdateError: Error, Equatable, CustomStringConvertible {
    case tooLarge
    case badSignature
    case badManifest(String)
    case missingAsset
    case assetMismatch
    case http(Int)
    case failed(String)

    var description: String {
        switch self {
        case .tooLarge: return "the download is larger than allowed"
        case .badSignature: return "the release manifest's signature is not valid"
        case .badManifest(let why): return "the release manifest is invalid (\(why))"
        case .missingAsset: return "the release has no macOS download"
        case .assetMismatch: return "the download doesn't match the signed manifest"
        case .http(let code): return "the server answered HTTP \(code)"
        case .failed(let why): return why
        }
    }
}

/// ECDSA P-256 / SHA-256 over the exact manifest bytes; `signature` is DER (openssl dgst -sign).
func verifyManifestSignature(_ manifest: Data, signature: Data, publicKeyDER: Data) -> Bool {
    guard let key = try? P256.Signing.PublicKey(derRepresentation: publicKeyDER),
          let sig = try? P256.Signing.ECDSASignature(derRepresentation: signature) else { return false }
    return key.isValidSignature(sig, for: manifest)
}

private struct RawAsset: Decodable {
    let name: String
    let sha256: String
    let size: Int64
}

/// An `assets` entry that never fails to decode, so another platform's entry can't break ours.
private struct LenientAsset: Decodable {
    let asset: RawAsset?
    init(from decoder: Decoder) throws { asset = try? RawAsset(from: decoder) }
}

private struct RawManifest: Decodable {
    let version: String
    let tag: String?
    let notesURL: String?
    let assets: [String: LenientAsset]

    enum CodingKeys: String, CodingKey {
        case version, tag, assets
        case notesURL = "notes_url"
    }
}

private func isLowerHex64(_ s: String) -> Bool {
    s.utf8.count == 64 && s.utf8.allSatisfy { ($0 >= 48 && $0 <= 57) || ($0 >= 97 && $0 <= 102) }
}

/// A plain file name, safe to put in a URL path and on disk.
private func isSafeAssetName(_ s: String) -> Bool {
    guard !s.isEmpty, s.utf8.count <= 128, !s.hasPrefix(".") else { return false }
    return s.utf8.allSatisfy { c in
        (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c == 46 || c == 45 || c == 95
    }
}

/// Verifies the signature first, then decodes strictly: `version` and `assets` are required, `tag`
/// is optional (defaults to, and must equal, `v<version>`), `published` / `notes_url` and unknown
/// fields are ignored, and only `platform`'s asset is validated. Throws `UpdateError`.
func parseSignedManifest(_ data: Data, signature: Data, publicKeyDER: Data,
                         platform: String = Release.macAssetKey) throws -> ReleaseManifest {
    guard data.count + signature.count <= Release.maxManifestBytes else { throw UpdateError.tooLarge }
    guard verifyManifestSignature(data, signature: signature, publicKeyDER: publicKeyDER) else {
        throw UpdateError.badSignature
    }
    let raw: RawManifest
    do { raw = try JSONDecoder().decode(RawManifest.self, from: data) }
    catch { throw UpdateError.badManifest("not the expected JSON") }

    guard ReleaseVersion(raw.version) != nil else { throw UpdateError.badManifest("version") }
    let tag = raw.tag ?? "v\(raw.version)"
    guard tag == "v\(raw.version)" else { throw UpdateError.badManifest("tag") }
    var asset: ReleaseAsset?
    if let entry = raw.assets[platform] {
        guard let a = entry.asset else { throw UpdateError.badManifest("asset") }
        guard isSafeAssetName(a.name) else { throw UpdateError.badManifest("asset name") }
        guard isLowerHex64(a.sha256) else { throw UpdateError.badManifest("asset sha256") }
        guard a.size > 0, a.size <= Release.maxAssetBytes else { throw UpdateError.badManifest("asset size") }
        asset = ReleaseAsset(name: a.name, sha256: a.sha256, size: a.size)
    }
    return ReleaseManifest(version: raw.version, tag: tag, notesURL: raw.notesURL, asset: asset)
}

/// `…/releases/download/<tag>/<name>`.
func assetDownloadURL(tag: String, asset: ReleaseAsset) -> URL? {
    URL(string: Release.downloadBase + tag + "/" + asset.name)
}

/// The release page (the manifest's `notes_url` if it's on GitHub over https).
func releasePageURL(_ m: ReleaseManifest) -> URL {
    if let s = m.notesURL, let u = URL(string: s), u.scheme == "https", u.host == "github.com" { return u }
    return URL(string: "https://github.com/RoggerFabri/display-toggle/releases/tag/\(m.tag)")!
}

// MARK: - Assets

func sha256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

/// Size and SHA-256 both match the signed manifest entry.
func assetMatches(_ data: Data, _ asset: ReleaseAsset) -> Bool {
    Int64(data.count) == asset.size && sha256Hex(data) == asset.sha256
}

// MARK: - Install

/// Install is offered only for an installed copy (`<prefix>/bin/<binary>`, normally
/// /usr/local/bin/display-toggle), not for a build run from a checkout (e.g. mac/display-toggle).
func isInstalledLocation(_ resolvedPath: String) -> Bool {
    let dir = (resolvedPath as NSString).deletingLastPathComponent
    return resolvedPath.hasPrefix("/") && (dir as NSString).lastPathComponent == "bin"
}

/// Automatic update checks run for the installed copy whatever its version (`make install` builds
/// 0.0.0-dev, which every release is newer than), and never for a build run from a checkout.
/// "Check for updates" works everywhere.
func runsAutomaticUpdateChecks(resolvedPath: String) -> Bool {
    isInstalledLocation(resolvedPath)
}

/// The launch agent label `make install` registers.
func launchAgentLabel(user: String) -> String { "com.\(user).display-toggle" }

/// `osascript` arguments that replace `target` with `staged` and re-sign it, behind the standard
/// admin prompt. Paths travel as argv and are quoted by AppleScript, so no shell escaping here.
func adminReplaceArguments(staged: String, target: String) -> [String] {
    [
        "-e", "on run argv",
        "-e", "do shell script \"/usr/bin/install -m 755 \" & quoted form of (item 1 of argv) & \" \" & "
            + "quoted form of (item 2 of argv) & \" && /usr/bin/codesign --sign - --force \" & "
            + "quoted form of (item 2 of argv) with administrator privileges",
        "-e", "end run",
        staged, target,
    ]
}
