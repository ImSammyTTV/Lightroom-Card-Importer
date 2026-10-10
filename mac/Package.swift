// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "CardImporter",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "CardImporter", path: "Sources/CardImporter")
    ]
)
