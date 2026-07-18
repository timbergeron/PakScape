# PakScape

PakScape is a Quake `.pak` and `.pk3` archive browser and editor for macOS, Windows, and Linux, inspired by the original PakScape developed by Peter Engström.

## Platform editions

| Platform | Desktop stack | Supported systems | Documentation |
| --- | --- | --- | --- |
| macOS | Swift, SwiftUI, and AppKit | macOS 14 or later | [macOS development guide](macos/README.md) |
| Windows | C# and WPF on .NET 8 | Windows 10 and 11 | [Windows development guide](windows/README.md) |
| Linux | C# and Avalonia on .NET 10 | Ubuntu 24.04 and 26.04, x86-64 and ARM64 | [Linux development guide](linux/README.md) |

Each edition uses the platform's native desktop conventions while sharing the same archive-safety principles and core feature set.

## Features

- Browse archives as a folder tree, list, or icon grid.
- Add, rename, move, copy, remove, and export files and folders.
- Press Space to preview selected text, common images, and Quake assets, including BSP, LMP, MDL, PCX, SPR, TGA, and WAD files.
- Read and write PAK and PK3 archives with traversal, duplicate-path, symlink, and size validation.
- Integrate with platform file pickers, recent files, drag and drop, and native keyboard navigation.

GitHub Actions builds and tests all three editions on every push and pull request to `main`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development checklist, [CHANGELOG.md](CHANGELOG.md) for unreleased changes, and [SECURITY.md](SECURITY.md) for vulnerability reporting.

## Licensing

This repository does not currently include a standalone license file. Confirm the licensing of the original PakScape work and the current source before redistributing the app.
