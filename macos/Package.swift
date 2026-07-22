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
                "NativeAudioPlayer.swift",
                "PakDocument.swift",
                "PakExplorerApp.swift",
                "PakIconView.swift",
                "PakItemInfo.swift",
                "PakListView.swift",
                "PakQuickLook.swift",
                "PakViewModel.swift",
                "PakScape-Bridging-Header.h",
                "PreferencesView.swift",
            ],
            sources: ["PakModels.swift", "PakFormatDetails.swift"]
        ),
        .testTarget(
            name: "PakArchiveCoreTests",
            dependencies: ["PakArchiveCore"]
        ),
    ]
)
