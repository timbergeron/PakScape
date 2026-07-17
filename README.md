# PakScape

PakScape is a Quake `.pak` and `.pk3` archive browser and editor for macOS, inspired by the original PakScape developed by Peter Engström. A Windows WPF edition is under active development in [`windows/`](windows/README.md).

## Features

- Browse archives as a folder tree, list, or icon grid.
- Add, rename, move, copy, remove, and export files and folders.
- Preview common images, sounds, and Quake assets, including BSP, LMP, MDL, PCX, SPR, TGA, and WAD files.
- Use Quick Look and open archived files in their default macOS apps.
- Extract PAK/PK3 files and create PAK files through macOS Finder Services.

## Build the macOS app

PakScape supports macOS 14 and later. Open `PakScape.xcodeproj` in Xcode 26 or build it from Terminal:

```bash
xcodebuild \
  -project PakScape.xcodeproj \
  -scheme PakScape \
  -configuration Release \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY="" \
  ARCHS="arm64 x86_64" \
  ONLY_ACTIVE_ARCH=NO
```

The resulting app is unsigned. On first launch, Control-click the app in Finder, choose **Open**, and confirm. Keep Gatekeeper enabled; you should not need `sudo`, `chmod`, or a system-wide security change.

## Build the Windows app

The Windows port requires Windows 10/11, the .NET 8 SDK, and the Visual Studio 2022 **.NET desktop development** workload. See the [Windows README](windows/README.md) for its project layout.

```powershell
dotnet build windows/PakStudio.sln --configuration Release
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release
```

GitHub Actions builds the macOS app and builds/tests the Windows solution on every push and pull request to `main`.

## Run the archive-core tests

The archive reader/writer has a standalone Swift Package test target, so its safety and round-trip tests can run without launching the app:

```bash
swift test
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full development checklist, [CHANGELOG.md](CHANGELOG.md) for unreleased changes, and [SECURITY.md](SECURITY.md) for responsible vulnerability reporting.

## Licensing

This repository does not currently include a standalone license file. Confirm the licensing of the original PakScape work and the current source before redistributing the app.
