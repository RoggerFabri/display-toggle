// swift-tools-version:5.9
// Unit tests only — the app itself is built by `make` (plain swiftc), not SwiftPM.
import PackageDescription

let package = Package(
    name: "display-toggle",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "PeerLogic", path: ".",
                exclude: ["display-toggle", "display-toggle.swift", "updater.swift", "Info.plist", "launch-agent.plist", "Makefile", "README.md", "Tests", "gen", "dist"],
                sources: ["peer-logic.swift", "peer-link.swift", "update-logic.swift"]),
        .testTarget(name: "PeerLogicTests", dependencies: ["PeerLogic"], path: "Tests/PeerLogicTests"),
    ]
)
