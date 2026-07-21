# PakScape for macOS

The macOS edition is a native Swift application built with SwiftUI and AppKit. It supports macOS 14 or later, and release builds target both Apple silicon and Intel Macs.

## Project layout

- `PakScape.xcodeproj/` contains the application project metadata.
- `PakScape/` contains the application sources and asset catalog.
- `Package.swift` exposes the portable archive core for command-line testing.
- `Tests/` contains archive parsing, writing, validation, and regression tests.
- `Info.plist` declares document types, sandbox permissions, and Finder Services.

## Build

Open `macos/PakScape.xcodeproj` in Xcode 26, or build from the repository root:

```bash
xcodebuild \
  -project macos/PakScape.xcodeproj \
  -scheme PakScape \
  -configuration Release \
  -derivedDataPath macos/build \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY="" \
  ARCHS="arm64 x86_64" \
  ONLY_ACTIVE_ARCH=NO
```

The unsigned app is written to `macos/build/Build/Products/Release/PakScape.app`. On first launch, Control-click it in Finder, choose **Open**, and confirm. Keep Gatekeeper enabled; no system-wide security change is required.

## Test

Run the standalone archive-core suite from the repository root:

```bash
swift test --package-path macos --configuration release
```

The tests cover round trips, malformed and duplicate paths, traversal attempts, format limits, missing payloads, atomic document behavior, and PK3 extraction preflight checks.

## Engineering standards

- PAK, PK3, folder, and clipboard imports enforce 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total limits across the existing document and each operation.
- Folder imports reject symbolic links and read regular files through stable, bounded file handles.
- Document generation does not mutate the live model before SwiftUI commits the save, and exports use staged or atomic writes.
- Undo records only affected tree nodes, retains at most 50 actions, and keeps cross-document transfers copy-based so each archive has an independent history.
- Custom Quake image and BSP preview parsers cap dimensions, pixel counts, table sizes, and geometry counts before allocation.
- Native image previews are dimension-checked and downsampled through Image I/O; native Quick Look thumbnails stage only bounded payload ranges, queue at most 32 requests, and run at most four jobs concurrently.

## Platform integration

The app uses sandbox-approved file access, native document windows, Quick Look, default-application previews, and Finder Services. SwiftUI's reference-document lifecycle owns saving, Save As, edited-window state, close confirmation, read-only state, and standard Undo and Redo. File drops use typed Transferable URLs. Select one or more archive items and press Space to open a Finder-style Quick Look preview.

PakScape renders deterministic still-image previews for Quake BSP, LMP, MDL, PCX, SPR, TGA, and `gfx.wad` content. The icon and list views ask Quick Look Thumbnailing for native, Finder-style previews of system-supported documents and media, with content-aware system icons as the fallback. Other files are passed to the system Quick Look service when opened in the preview panel. Unknown, malformed, or unsupported formats still receive Quick Look's generic file preview. Preview preparation is limited to 1,000 items, 128 MB per file, and 256 MB per selection; inline native thumbnails are limited to 32 MB per file, text thumbnails use at most the first 2 MB, and decoded custom images are limited to 8,192 pixels per dimension and 16,777,216 total pixels.

Treat all archive paths and payloads as untrusted input when changing import, preview, extraction, or save behavior.

See the root [contribution guide](../CONTRIBUTING.md), [changelog](../CHANGELOG.md), and [security policy](../SECURITY.md) before submitting changes.
