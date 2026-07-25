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

- Browse archives as a folder tree, list, or icon grid with inline thumbnails and platform-appropriate file-type icons.
- Add, rename, move, copy, remove, and export files and folders, with native Undo and Redo on macOS.
- Press Space to preview selected text, common images, and Quake assets, including BSP, LMP, MDL, PCX, SPR, TGA, and WAD files.
- Inspect QSS-M models — MDL, MD3, and MD5 — in an interactive viewer: drag to orbit, right-drag to pan, scroll or pinch to zoom, and leave it alone to watch it turn. Double-click a model to open it, and MD3 and MD5 skins are loaded from the archive.
- Watch SPR and SPR32 sprites animate in the same viewer, frame by frame at the rate the file asks for, on a fullbright card you can still orbit.
- Turn BSP brush models — ammo boxes, health kits, and prefab props — in the same viewer, textured from the file. PakScape spots them by their content, not their name, so a playable level keeps its flat overview instead.
- Preview QSS-M audio consistently on every platform: WAV, MP3, FLAC, Ogg Vorbis, Ogg Opus, IT, S3M, XM, MOD, and UMX.
- Find out what a file is for. `progs.dat` reports its progdefs CRC and how much code it holds, `end1.bin` is read as the DOS text screen it is, and `quake.rc`, `palette.lmp`, `gfx.wad`, and other well-known names each explain themselves in a line.
- Read and write PAK and PK3 archives with traversal, duplicate-path, symlink, and size validation.
- Integrate with platform file pickers, recent files, drag and drop, and native keyboard navigation.

GitHub Actions builds and tests all three editions on every push and pull request to `main`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development checklist, [CHANGELOG.md](CHANGELOG.md) for unreleased changes, and [SECURITY.md](SECURITY.md) for vulnerability reporting.

## Licensing

This repository does not currently include a standalone license file. Confirm the licensing of the original PakScape work and the current source before redistributing the app.
