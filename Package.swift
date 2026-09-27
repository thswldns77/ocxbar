// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "OCXBar",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "OCXBar", path: "Sources/OCXBar")
    ]
)
