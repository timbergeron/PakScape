# PakScape for Linux

PakScape for Linux is an Avalonia desktop application for browsing and editing Quake `.pak` and `.pk3` archives. It reuses the tested, platform-neutral C# archive core from the Windows edition while keeping Linux desktop integration and file handling in dedicated projects.

## Supported systems

Ubuntu 24.04 LTS on x86-64 and ARM64 is the baseline and CI release gate. Ubuntu 26.04 LTS is a supported forward target; it remains a manual compatibility check until GitHub's 26.04 hosted runner leaves preview. Other current Debian-based distributions should work when they provide the same native libraries, but they are not release gates yet.

The production default is Avalonia's mature X11 backend, which works directly on X11 and through XWayland on Wayland desktops. Avalonia's native Wayland backend remains experimental and is not enabled by default.

Debian packages are self-contained and do not require a system-wide .NET installation. The package still declares the native libraries required by .NET and Avalonia, including ICU, OpenSSL, Fontconfig, X11, ICE, and SM; `apt` resolves the exact Ubuntu versions.

## Development

Install the .NET 10 SDK, then restore, build, and run the tests:

```bash
dotnet restore linux/PakScape.Linux.slnx
dotnet build linux/PakScape.Linux.slnx --configuration Release --no-restore
dotnet run --project linux/PakScape.Linux.Tests/PakScape.Linux.Tests.csproj --configuration Release --no-build
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release --no-build
```

Run the application during development:

```bash
dotnet run --project linux/PakScape.Linux/PakScape.Linux.csproj
```

An archive path may be supplied on the command line. The installed desktop entry uses the same path to open `.pak` and `.pk3` files from Files/Nautilus.

## Quick Preview

Select one or more archive items and press Space, or choose **View > Quick Preview**. Press Space or Escape to close the preview; use the arrow keys or the on-screen controls to move through a multi-item selection.

Rich previews are available for:

- Plain text: `.cfg`, `.txt`, `.log`, `.md`, `.json`, `.xml`, `.yaml`, `.yml`, `.ini`, `.csv`, `.qc`, `.map`, `.ent`, `.rc`, `.shader`, `.def`, `.menu`, and `.arena`.
- Common images: `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.tif`, and `.tiff`.
- Quake content: `.bsp`, `.lmp`, `.mdl`, `.pcx`, `.spr`, `.tga`, and `gfx.wad`.

Folders and unsupported or malformed files receive a metadata preview rather than being extracted or launched. Preview preparation is limited to 1,000 items, 128 MB per file, and 256 MB per selection. Text is truncated after 2 MB, and decoded images are limited to 8,192 pixels per dimension and 16,777,216 total pixels.

## Build an Ubuntu package

On an Ubuntu or Debian build host with `dpkg-deb` installed:

```bash
linux/packaging/build-deb.sh 1.0.1 linux-x64
```

Use `linux-arm64` for ARM64. The script creates a self-contained `.deb` and portable `.tar.gz` under `linux/artifacts/`. Install the Debian package with `sudo apt install ./linux/artifacts/pakscape_1.0.1_amd64.deb` so dependencies are resolved.

The package follows the XDG Base Directory, desktop-entry, icon-theme, and shared-MIME-info conventions. Recent files are stored under `$XDG_STATE_HOME/pakscape`; temporary previews use a private directory under `$XDG_RUNTIME_DIR` when it is usable.

## Engineering standards

- MVVM keeps archive workflows testable and UI event code thin.
- Nullable analysis, recommended analyzers, deterministic builds, and warnings-as-errors are enabled.
- Imports reject symbolic links and enforce 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total limits across the existing document and each import.
- Archive and export writes are atomic; existing exported files are never overwritten silently.
- Ubuntu 24.04 CI builds x86-64 and ARM64 Release configurations, runs the portable and Linux-specific tests, and validates Debian packaging.

## Licensing

The repository does not yet include a standalone license. The generated package is marked for development and evaluation; confirm redistribution rights before publishing it as a release or submitting it to a package repository.
