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
                "ModelPreviewWindow.swift",
                "NativeAudioPlayer.swift",
                "PakDocument.swift",
                "PakExplorerApp.swift",
                "PakIconView.swift",
                "PakListView.swift",
                "PakItemInfoWindow.swift",
                "PakQuickLook.swift",
                "PakViewModel.swift",
                "PakScape-Bridging-Header.h",
                "PreferencesView.swift",
                "QuakeModelViewer.swift",
                "SkyboxPreviewWindow.swift",
            ],
            sources: [
                "PakModels.swift",
                "PakFormatDetails.swift",
                "PakItemInfoPlacement.swift",
                "QuakeDemoInspector.swift",
                "DemoPlaybackHandoff.swift",
            ]
        ),
        .testTarget(
            name: "PakArchiveCoreTests",
            dependencies: ["PakArchiveCore"]
        ),
    ]
)
