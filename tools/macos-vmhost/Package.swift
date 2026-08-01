// swift-tools-version: 5.9
import PackageDescription

// Deliberately dependency-free: the only thing a contributor needs to build this is the Xcode
// command line tools they already have. A package-manager dependency here would be one more thing
// to fetch, pin and audit for a program that is ~300 lines of platform glue.
let package = Package(
    name: "crf-vmhost",
    platforms: [.macOS(.v13)],   // VZLinuxRosettaDirectoryShare arrived in macOS 13
    targets: [
        .executableTarget(name: "crf-vmhost", path: "Sources/crf-vmhost")
    ]
)
