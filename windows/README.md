# PakScape for Windows

This folder contains the Windows desktop edition of PakScape, built as a WPF app on .NET 8.

Current capabilities include:

- Open, create, edit, and atomically save Quake PAK and PK3 archives.
- Add files or folders, create folders, rename, delete, and export archive items.
- Browse folders with large-icon, small-icon, list, and detail views.
- Open archived files in their registered Windows application.
- Track recent archives and prompt before discarding unsaved work.
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
