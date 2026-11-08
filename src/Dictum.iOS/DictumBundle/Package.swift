// swift-tools-version: 5.9
// Copyright (c) Kynni Pty Ltd. MIT licence (see README.md).
import PackageDescription

let package = Package(
    name: "DictumBundle",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "DictumBundle", targets: ["DictumBundle"]),
    ],
    targets: [
        .target(
            name: "DictumBundle",
            resources: [
                .copy("Resources/transfer-words.txt"),
                .copy("Resources/transfer-words.LICENSE"),
            ]
        ),
        .testTarget(name: "DictumBundleTests", dependencies: ["DictumBundle"]),
    ]
)
