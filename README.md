# PakScape

PakScape is a Quake `.pak` and `.pk3` archive browser and editor for macOS, Windows, and Linux, inspired by the original PakScape developed by Peter Engström. The Windows WPF edition lives in [`windows/`](windows/README.md), and the Ubuntu-focused Avalonia edition lives in [`linux/`](linux/README.md).

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

## Build the Linux app

The Linux edition targets Ubuntu 24.04/26.04 on x86-64 and ARM64 and requires the .NET 10 SDK for development:

```bash
dotnet restore linux/PakScape.Linux.slnx
dotnet build linux/PakScape.Linux.slnx --configuration Release --no-restore
dotnet run --project linux/PakScape.Linux/PakScape.Linux.csproj
```

See the [Linux README](linux/README.md) for the architecture, native dependencies, tests, Debian packaging, and supported-distribution policy.

GitHub Actions builds and tests the macOS, Windows, and Ubuntu editions on every push and pull request to `main`.

## Run the archive-core tests

The archive reader/writer has a standalone Swift Package test target, so its safety and round-trip tests can run without launching the app:

```bash
swift test
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full development checklist, [CHANGELOG.md](CHANGELOG.md) for unreleased changes, and [SECURITY.md](SECURITY.md) for responsible vulnerability reporting.

## Licensing

This repository does not currently include a standalone license file. Confirm the licensing of the original PakScape work and the current source before redistributing the app.
