// swift-tools-version: 5.10

import PackageDescription

let package = Package(
    name: "PakArchiveCore",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .library(name: "PakArchiveCore", targets: ["PakArchiveCore"]),
    ],
    targets: [
        .target(
            name: "PakArchiveCore",
            path: "PakScape",
            exclude: [
                "Assets.xcassets",
                "BspLevelPreviewRenderer.swift",
                "ContentView.swift",
                "FinderServices.swift",
                "PakDocument.swift",
                "PakExplorerApp.swift",
                "PakIconView.swift",
                "PakListView.swift",
                "PakQuickLook.swift",
                "PakViewModel.swift",
                "PreferencesView.swift",
            ],
            sources: ["PakModels.swift"]
        ),
        .testTarget(
            name: "PakArchiveCoreTests",
            dependencies: ["PakArchiveCore"]
        ),
    ]
)
