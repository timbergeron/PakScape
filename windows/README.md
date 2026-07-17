# PakStudio Windows

This folder contains the Windows desktop edition of PakScape, built as a WPF app on .NET 8.

Current capabilities include:

- Open, create, edit, and atomically save Quake PAK and PK3 archives.
- Add files or folders, create folders, rename, delete, and export archive items.
- Browse folders with large-icon, small-icon, list, and detail views.
- Open archived files in their registered Windows application.
- Track recent archives and prompt before discarding unsaved work.

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
