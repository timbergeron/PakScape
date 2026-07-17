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

## Platform integration

The app uses sandbox-approved file access, native document windows, Quick Look, default-application previews, and Finder Services. Treat all archive paths and payloads as untrusted input when changing import, preview, extraction, or save behavior.

See the root [contribution guide](../CONTRIBUTING.md), [changelog](../CHANGELOG.md), and [security policy](../SECURITY.md) before submitting changes.
