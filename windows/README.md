# PakStudio Windows

This folder contains the Windows desktop port of PakScape, built as a WPF app on .NET 8.

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
