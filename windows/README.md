# PakScape for Windows

This folder contains the Windows desktop edition of PakScape, built as a WPF app on .NET 8.

Current capabilities include:

- Open, create, edit, and atomically save Quake PAK, PK3, and KPF archives.
- Work with multiple PAK, PK3, and KPF documents in independent archive windows.
- Add files or folders, create folders, rename, delete, and export archive items.
- Add files, imported folders, or new folders directly from the item context menu.
- Cut, copy, paste, select all, and drag archive items to or from Windows Explorer.
- Undo and redo up to 50 archive mutations.
- Browse with Back/Forward history and large-icon, small-icon, list, and detail views.
- Rename items inline with `F2`, the context menu, or a click on the name of an
  already-selected item, and adjust the large-icon view across three zoom levels.
- Sort by name, type, size, or modified date and show image/Quake thumbnails in every view.
- Search names, paths, types, and metadata across the entire archive.
- Preview selected archive items without extracting them by pressing Space.
- Inspect one or more items in modeless Get Info windows with selectable metadata.
- Open archived files in their registered Windows application.
- Track recent archives and prompt before discarding unsaved work.
- Follow the Windows light/dark app-mode setting, including live theme changes.
- Create PAK, PK3, or KPF documents and open archives passed by Windows.
- Ship as one portable `PakScape.exe` with the .NET runtime bundled; no installer.
- Register PAK/PK3/KPF file associations per user from inside the app.

Projects:

- `PakStudio.App`: WPF shell, MVVM, dialogs, and theme resources
- `PakStudio.Core`: archive domain model, pathing, validation, and service contracts
- `PakStudio.Formats`: PAK format reader/writer and format registry
- `PakStudio.Tests`: unit tests for the portable logic

Build prerequisites:

- Windows 10/11
- Visual Studio 2022 with the `.NET desktop development` workload and the
  `C++ CMake tools for Windows` component
- .NET 8 SDK
- A Visual Studio C++ toolchain

Open `PakStudio.sln` in Visual Studio and build or run `PakStudio.App`. The first
build automatically runs `../native/scripts/build-windows.ps1`; later builds run
it again only when a native source or build script changes.

## Single-file exe

Publish one self-contained `PakScape.exe` (no .NET install needed) from the repository
root:

```powershell
dotnet publish windows\PakStudio.App\PakStudio.App.csproj -p:PublishProfile=win-x64
```

The exe is written to `windows\artifacts\publish\win-x64`. It bundles the .NET runtime,
WPF, and both native preview libraries, which .NET extracts to
`%TEMP%\.net\PakScape` on first launch. There is no installer: put the exe anywhere
and run it. File associations are registered from inside the app, so if you move the
exe, register them again from the new location.

## Quick Preview

Select one or more archive items and press Space, or choose **View > Quick Preview**. Press Space or Escape to close the preview; use the arrow keys or the on-screen controls to move through a multi-item selection.

Rich previews are available for:

- Plain text: `.cfg`, `.txt`, `.log`, `.md`, `.json`, `.xml`, `.yaml`, `.yml`, `.ini`, `.csv`, `.qc`, `.map`, `.ent`, `.rc`, `.shader`, `.def`, `.menu`, and `.arena`.
- Cross-platform audio playback: `.wav`, `.mp3`, `.flac`, `.ogg`, `.opus`, `.it`, `.s3m`, `.xm`, `.mod`, and `.umx`.
- Common images: `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.tif`, and `.tiff`.
- Quake content: `.bsp`, `.lmp`, `.mdl`, `.pcx`, `.spr`, `.tga`, and `gfx.wad`. Quick Preview checkboxes can label major BSP items and CTF flags.

When an image belongs to a complete Quake `rt`, `bk`, `lf`, `ft`, `up`, and `dn`
face set in the same archive folder, its context menu and Quick Preview window
include **View Skybox**. Drag to look around, use the mouse wheel to zoom, and
choose **Reset View** or **Back to Image** as needed.

Choose **Get Info** or press Alt+Enter to open a modeless information window.
Selecting several items opens offset windows for comparison; with no selection,
Alt+Enter shows information for the current folder.

Creating or opening an archive uses a separate document window. Reopening a file
that is already open brings its existing window forward. Use Ctrl+W or
**File > Close Window** to close only the active document; each window prompts
independently for unsaved changes.

Folders and unsupported or malformed files receive a metadata preview rather than being extracted or launched. Audio playback uses PakScape's bundled native decoder rather than optional Windows codecs and falls back to metadata when a file is malformed. Preview preparation is limited to 1,000 items, 128 MB per file, and 256 MB per selection. Text is truncated after 2 MB, and decoded images are limited to 8,192 pixels per dimension and 16,777,216 total pixels.

Run the portable regression suite from the repository root with:

```powershell
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release
```

## Engineering standards

- PAK, PK3, and KPF paths are validated without silently trimming or normalizing significant characters.
- Imports reject symbolic links, junctions, and unstable files while enforcing 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total limits across the existing document and each import.
- Archive saves and filesystem exports stage and flush their output before committing it.
- Windows device names, alternate-data-stream separators, trailing periods, and trailing spaces are rejected before export.
- Temporary previews use a private per-process directory and are removed when the application exits.
- Nullable analysis, deterministic builds, and warnings-as-errors are enabled for every Windows project.
