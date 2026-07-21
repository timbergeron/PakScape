# PakScape for Windows

This folder contains the Windows desktop edition of PakScape, built as a WPF app on .NET 8.

Current capabilities include:

- Open, create, edit, and atomically save Quake PAK and PK3 archives.
- Add files or folders, create folders, rename, delete, and export archive items.
- Cut, copy, paste, select all, and drag archive items to or from Windows Explorer.
- Browse with Back/Forward history and large-icon, small-icon, list, and detail views.
- Sort by name, type, size, or modified date and show image/Quake thumbnails in every view.
- Preview selected archive items without extracting them by pressing Space.
- Open archived files in their registered Windows application.
- Track recent archives and prompt before discarding unsaved work.
- Follow the Windows light/dark app-mode setting, including live theme changes.
- Create either PAK or PK3 documents, open archives passed by Windows, and filter the current folder.

Projects:

- `PakStudio.App`: WPF shell, MVVM, dialogs, and theme resources
- `PakStudio.Core`: archive domain model, pathing, validation, and service contracts
- `PakStudio.Formats`: PAK format reader/writer and format registry
- `PakStudio.Tests`: unit tests for the portable logic

Build prerequisites:

- Windows 10/11
- Visual Studio 2022 with the `.NET desktop development` workload
- .NET 8 SDK

Open `PakStudio.sln` in Visual Studio on Windows to build and run the app.

## Quick Preview

Select one or more archive items and press Space, or choose **View > Quick Preview**. Press Space or Escape to close the preview; use the arrow keys or the on-screen controls to move through a multi-item selection.

Rich previews are available for:

- Plain text: `.cfg`, `.txt`, `.log`, `.md`, `.json`, `.xml`, `.yaml`, `.yml`, `.ini`, `.csv`, `.qc`, `.map`, `.ent`, `.rc`, `.shader`, `.def`, `.menu`, and `.arena`.
- Common images: `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.tif`, and `.tiff`.
- Quake content: `.bsp`, `.lmp`, `.mdl`, `.pcx`, `.spr`, `.tga`, and `gfx.wad`.

Folders and unsupported or malformed files receive a metadata preview rather than being extracted or launched. Preview preparation is limited to 1,000 items, 128 MB per file, and 256 MB per selection. Text is truncated after 2 MB, and decoded images are limited to 8,192 pixels per dimension and 16,777,216 total pixels.

Run the portable regression suite from the repository root with:

```powershell
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release
```

## Engineering standards

- PAK and PK3 paths are validated without silently trimming or normalizing significant characters.
- Imports reject symbolic links, junctions, and unstable files while enforcing 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total limits across the existing document and each import.
- Archive saves and filesystem exports stage and flush their output before committing it.
- Windows device names, alternate-data-stream separators, trailing periods, and trailing spaces are rejected before export.
- Temporary previews use a private per-process directory and are removed when the application exits.
- Nullable analysis, deterministic builds, and warnings-as-errors are enabled for every Windows project.
