import CryptoKit
import Foundation
import Testing
@testable import PeerLogic

// The shared test vector from the release contract (the Windows tests use the same one).
private let vectorKey = Data(base64Encoded:
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4BCU0nciMFrT1xf2Bvr7A0YsNFDqrUW0bxlnWkYIrYPS6mrw5s10jb9M2Q1zwykDT1dTdgie/skqbI6b01mdPQ==")!
private let vectorManifest = Data(
    #"{"version":"1.2.3","assets":{"windows-x64":{"name":"display-toggle-windows-x64.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","size":1}}}"#.utf8)
private let vectorSignature = Data(base64Encoded:
    "MEUCIQCTIpk4IoOVhz1mcxTcXIg4G9wHzkivQxm/oxmYJFup0gIgJ2Jmq27uUHoOg3w3HW0+P1IxUjlLZiVwo1fTum552os=")!

private let hashA = String(repeating: "a", count: 64)

/// Signs `json` with a throwaway key, so manifest edge cases can be tested past the signature check.
private func signed(_ json: String) throws -> ReleaseManifest {
    let key = P256.Signing.PrivateKey()
    let data = Data(json.utf8)
    let sig = try key.signature(for: data).derRepresentation
    return try parseSignedManifest(data, signature: sig, publicKeyDER: key.publicKey.derRepresentation)
}

private func manifest(version: String = "1.2.3", tag: String? = nil, extra: String = "",
                      name: String = "display-toggle-macos.tar.gz", sha: String = hashA, size: String = "456") -> String {
    let tagField = tag.map { #""tag":"\#($0)","# } ?? ""
    return #"{"version":"\#(version)",\#(tagField)\#(extra)"assets":{"macos":{"name":"\#(name)","sha256":"\#(sha)","size":\#(size)}}}"#
}

@Suite struct SignatureVector {
    @Test func verifies() {
        #expect(verifyManifestSignature(vectorManifest, signature: vectorSignature, publicKeyDER: vectorKey))
    }

    @Test func anyChangedByteFails() {
        for i in 0..<vectorManifest.count {
            var changed = vectorManifest
            changed[i] ^= 0x01
            #expect(!verifyManifestSignature(changed, signature: vectorSignature, publicKeyDER: vectorKey), "byte \(i)")
        }
    }

    @Test func releaseKeyFails() {
        #expect(!verifyManifestSignature(vectorManifest, signature: vectorSignature, publicKeyDER: Release.publicKey))
    }

    @Test func truncatedOrAppendedFails() {
        #expect(!verifyManifestSignature(vectorManifest.dropLast(), signature: vectorSignature, publicKeyDER: vectorKey))
        #expect(!verifyManifestSignature(vectorManifest + Data("\n".utf8), signature: vectorSignature, publicKeyDER: vectorKey))
    }

    @Test func malformedSignatureOrKeyFails() {
        #expect(!verifyManifestSignature(vectorManifest, signature: Data(), publicKeyDER: vectorKey))
        #expect(!verifyManifestSignature(vectorManifest, signature: Data([0x30, 0x00]), publicKeyDER: vectorKey))
        #expect(!verifyManifestSignature(vectorManifest, signature: vectorSignature, publicKeyDER: Data([1, 2, 3])))
    }

    @Test func releaseKeyIsAValidP256Key() throws {
        _ = try P256.Signing.PublicKey(derRepresentation: Release.publicKey)
    }

    @Test func vectorParses() throws {
        // No tag / published / notes_url and no macOS asset: still a valid manifest.
        let m = try parseSignedManifest(vectorManifest, signature: vectorSignature, publicKeyDER: vectorKey)
        #expect(m.version == "1.2.3")
        #expect(m.tag == "v1.2.3")
        #expect(m.asset == nil)
        let win = try parseSignedManifest(vectorManifest, signature: vectorSignature, publicKeyDER: vectorKey,
                                          platform: "windows-x64")
        #expect(win.asset == ReleaseAsset(name: "display-toggle-windows-x64.zip",
                                          sha256: String(repeating: "0", count: 64), size: 1))
    }

    @Test func signatureIsCheckedBeforeParsing() {
        #expect(throws: UpdateError.badSignature) {
            try parseSignedManifest(Data("not json".utf8), signature: vectorSignature, publicKeyDER: vectorKey)
        }
        #expect(throws: UpdateError.badSignature) {
            try parseSignedManifest(vectorManifest, signature: vectorSignature, publicKeyDER: Release.publicKey)
        }
    }

    @Test func oversizeIsRejected() {
        let big = Data(repeating: 0x20, count: Release.maxManifestBytes)
        #expect(throws: UpdateError.tooLarge) {
            try parseSignedManifest(big, signature: vectorSignature, publicKeyDER: vectorKey)
        }
    }
}

@Suite struct ManifestDecode {
    @Test func fullManifest() throws {
        let json = #"{"version":"1.4.0","tag":"v1.4.0","published":"2026-01-01T00:00:00Z","#
            + #""notes_url":"https://github.com/RoggerFabri/display-toggle/releases/tag/v1.4.0","#
            + #""assets":{"windows-x64":{"name":"display-toggle-windows-x64.zip","sha256":"\#(hashA)","size":123},"#
            + #""macos":{"name":"display-toggle-macos.tar.gz","sha256":"\#(hashA)","size":456}}}"#
        let m = try signed(json)
        #expect(m.version == "1.4.0")
        #expect(m.tag == "v1.4.0")
        #expect(m.asset == ReleaseAsset(name: "display-toggle-macos.tar.gz", sha256: hashA, size: 456))
        #expect(releasePageURL(m).absoluteString == "https://github.com/RoggerFabri/display-toggle/releases/tag/v1.4.0")
        #expect(assetDownloadURL(tag: m.tag, asset: m.asset!)?.absoluteString
                == "https://github.com/RoggerFabri/display-toggle/releases/download/v1.4.0/display-toggle-macos.tar.gz")
    }

    @Test func otherPlatformsAreIgnored() throws {
        let json = #"{"version":"1.2.3","assets":{"windows-x64":{"name":"../x","sha256":"zz","size":"big"},"#
            + #""linux":7,"macos":{"name":"display-toggle-macos.tar.gz","sha256":"\#(hashA)","size":456}}}"#
        #expect(try signed(json).asset?.size == 456)
    }

    @Test func missingOwnAssetParsesAsNone() throws {
        let json = #"{"version":"1.2.3","assets":{"windows-x64":{"name":"a.zip","sha256":"\#(hashA)","size":1}}}"#
        #expect(try signed(json).asset == nil)
    }

    @Test func malformedOwnAssetIsRejected() {
        #expect(throws: UpdateError.badManifest("asset")) {
            try signed(#"{"version":"1.2.3","assets":{"macos":{"name":"a.tar.gz","size":1}}}"#)
        }
        #expect(throws: UpdateError.badManifest("asset")) {
            try signed(#"{"version":"1.2.3","assets":{"macos":"display-toggle-macos.tar.gz"}}"#)
        }
    }

    @Test func unknownFieldsAreIgnored() throws {
        let m = try signed(manifest(extra: #""future":{"x":[1,2]},"channel":"stable","#))
        #expect(m.version == "1.2.3")
    }

    @Test func badVersionsAreRejected() {
        for v in ["1.2", "1.2.3.4", "1.2.3-beta", "v1.2.3", "01.2.3", "1.2.x", "", " 1.2.3", "1.2.3+b"] {
            #expect(throws: UpdateError.badManifest("version"), "\(v)") { try signed(manifest(version: v)) }
        }
    }

    @Test func tagMustMatchVersion() throws {
        #expect(throws: UpdateError.badManifest("tag")) { try signed(manifest(tag: "v9.9.9")) }
        #expect(throws: UpdateError.badManifest("tag")) { try signed(manifest(tag: "1.2.3")) }
        #expect(try signed(manifest(tag: "v1.2.3")).tag == "v1.2.3")
    }

    @Test func badHashesAreRejected() {
        for sha in [String(repeating: "A", count: 64), String(repeating: "a", count: 63),
                    String(repeating: "a", count: 65), String(repeating: "g", count: 64)] {
            #expect(throws: UpdateError.badManifest("asset sha256")) { try signed(manifest(sha: sha)) }
        }
    }

    @Test func badSizesAreRejected() {
        #expect(throws: UpdateError.badManifest("asset size")) { try signed(manifest(size: "0")) }
        #expect(throws: UpdateError.badManifest("asset size")) { try signed(manifest(size: "-5")) }
        #expect(throws: UpdateError.badManifest("asset size")) {
            try signed(manifest(size: String(Release.maxAssetBytes + 1)))
        }
        #expect(throws: (any Error).self) { try signed(manifest(size: "1.5")) }
        #expect(throws: (any Error).self) { try signed(manifest(size: #""456""#)) }
    }

    @Test func unsafeNamesAreRejected() {
        for name in ["../evil", "a/b.tar.gz", ".hidden", "", "with space.zip", "q?x=1"] {
            #expect(throws: UpdateError.badManifest("asset name"), "\(name)") { try signed(manifest(name: name)) }
        }
    }

    @Test func missingFieldsAreRejected() {
        #expect(throws: (any Error).self) { try signed(#"{"version":"1.2.3"}"#) }
        #expect(throws: (any Error).self) { try signed(#"{"assets":{}}"#) }
        #expect(throws: (any Error).self) { try signed("[]") }
        #expect(throws: (any Error).self) { try signed("") }
    }

    @Test func nonGitHubNotesURLFallsBackToReleasePage() throws {
        let m = try signed(manifest(extra: #""notes_url":"http://example.com/x","#))
        #expect(releasePageURL(m).absoluteString == "https://github.com/RoggerFabri/display-toggle/releases/tag/v1.2.3")
    }
}

@Suite struct Versions {
    @Test func devIsOlderThanEverything() {
        #expect(isNewerRelease("1.0.0", than: "0.0.0-dev"))
        #expect(isNewerRelease("0.0.0", than: "0.0.0-dev"))
        #expect(isNewerRelease("0.0.1", than: "9.9.9-dev"))
        #expect(isNewerRelease("1.0.0", than: "garbage"))
        #expect(isNewerRelease("1.0.0", than: ""))
    }

    @Test func numericCompare() {
        #expect(isNewerRelease("1.0.10", than: "1.0.9"))
        #expect(isNewerRelease("1.10.0", than: "1.9.9"))
        #expect(isNewerRelease("2.0.0", than: "1.99.99"))
        #expect(!isNewerRelease("1.2.3", than: "1.2.3"))
        #expect(!isNewerRelease("1.2.2", than: "1.2.3"))
        #expect(!isNewerRelease("0.9.9", than: "1.0.0"))
    }

    @Test func buildMetadataIsIgnoredOnCurrent() {
        #expect(!isNewerRelease("1.2.3", than: "1.2.3+abc"))
        #expect(isNewerRelease("1.2.4", than: "1.2.3+abc"))
    }

    @Test func invalidCandidateIsNeverNewer() {
        #expect(!isNewerRelease("2.0.0-dev", than: "1.0.0"))
        #expect(!isNewerRelease("2.0", than: "1.0.0"))
        #expect(!isNewerRelease("", than: "0.0.0-dev"))
        #expect(!isNewerRelease("99999999999.0.0", than: "1.0.0"))
    }

    @Test func devDetection() {
        #expect(isDevVersion("0.0.0-dev"))
        #expect(isDevVersion("1.2.3-rc1"))
        #expect(isDevVersion(""))
        #expect(!isDevVersion("1.2.3"))
        #expect(!isDevVersion("1.2.3+abc"))
    }

    @Test func versionFromInfoPlist() {
        #expect(appVersion(from: ["CFBundleShortVersionString": "1.4.2"]) == "1.4.2")
        #expect(appVersion(from: ["CFBundleShortVersionString": ""]) == Release.devVersion)
        #expect(appVersion(from: [:]) == Release.devVersion)
        #expect(appVersion(from: nil) == Release.devVersion)
    }
}

@Suite struct AssetsAndInstall {
    @Test func sha256OfKnownInput() {
        #expect(sha256Hex(Data("abc".utf8)) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }

    @Test func assetMatchChecksSizeAndHash() {
        let data = Data("abc".utf8)
        let hash = sha256Hex(data)
        #expect(assetMatches(data, ReleaseAsset(name: "x", sha256: hash, size: 3)))
        #expect(!assetMatches(data, ReleaseAsset(name: "x", sha256: hash, size: 4)))
        #expect(!assetMatches(data, ReleaseAsset(name: "x", sha256: hashA, size: 3)))
        #expect(!assetMatches(Data("abd".utf8), ReleaseAsset(name: "x", sha256: hash, size: 3)))
    }

    @Test func installedLocation() {
        #expect(isInstalledLocation("/Users/me/.local/bin/display-toggle")) // make install's default
        #expect(isInstalledLocation("/usr/local/bin/display-toggle"))
        #expect(isInstalledLocation("/opt/homebrew/bin/display-toggle"))
        #expect(!isInstalledLocation("/Users/me/src/display-toggle/mac/display-toggle"))
        #expect(!isInstalledLocation("display-toggle"))
        #expect(!isInstalledLocation("bin/display-toggle"))
    }

    @Test func automaticChecksFollowTheInstalledPathNotTheVersion() {
        // `make install` builds 0.0.0-dev; the installed copy must still check by itself.
        #expect(runsAutomaticUpdateChecks(resolvedPath: "/Users/me/.local/bin/display-toggle"))
        #expect(runsAutomaticUpdateChecks(resolvedPath: "/usr/local/bin/display-toggle"))
        #expect(runsAutomaticUpdateChecks(resolvedPath: "/opt/homebrew/bin/display-toggle"))
        #expect(!runsAutomaticUpdateChecks(resolvedPath: "/Users/me/src/display-toggle/mac/display-toggle"))
        #expect(!runsAutomaticUpdateChecks(resolvedPath: "display-toggle"))
    }

    @Test func launchAgentLabelMatchesMakefile() {
        #expect(launchAgentLabel(user: "jane") == "com.jane.display-toggle")
    }

    @Test func adminReplacePassesPathsAsArguments() {
        let args = adminReplaceArguments(staged: "/tmp/x y/display-toggle", target: "/usr/local/bin/display-toggle")
        #expect(args.suffix(2) == ["/tmp/x y/display-toggle", "/usr/local/bin/display-toggle"])
        #expect(args.filter { $0 == "-e" }.count == 3)
        #expect(args.contains { $0.contains("with administrator privileges") && $0.contains("codesign --sign - --force") })
        #expect(!args.dropLast(2).contains { $0.contains("/tmp/x y") })
    }
}
