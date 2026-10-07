# PakScape Mac App Store listing

- App Store Connect app: 6820197786
- Bundle ID: timbergeron.PakScape
- Team: 36AUSRR8UM
- Version: 1.0.1
- Build: 2
- Price: Free
- Category: Utilities
- Name: PakScape
- Subtitle: Quake archives & previews
- Keywords: Quake,pak,pk3,kpf,archive,editor,extract,mod,preview,model,texture,audio
- Support: https://github.com/timbergeron/PakScape/issues
- Privacy: https://github.com/timbergeron/PakScape/blob/main/docs/PRIVACY.md

## Description

Explore, edit, and create Quake PAK, PK3, and KPF archives with a native Mac interface.

Browse archive contents as folders, lists, or icons. Add files and folders, rename entries, copy and paste, extract content, and save your changes. Undo and Redo help you work confidently, while archive-wide search finds names, paths, types, and format details.

Press Space for Quick Preview. Inspect text and common images, listen to audio, view Quake textures, and explore 3D models and skyboxes. BSP overviews help you inspect level layouts. Get Info shows sizes, paths, and format metadata.

PakScape supports multiple document windows, Finder integration, drag and drop, recent files, and the system light and dark appearance. Archive validation checks unsafe paths and bounds resource use when handling malformed content.

Files are processed locally. No account, advertisements, or tracking are included. Optional demo playback opens a separate browser player or a Quake engine you choose.

Requires macOS 14 or later. Supports Apple silicon and Intel Macs. Game data is not included. PakScape is an independent utility and is not affiliated with the makers of Quake.

## Review notes

No login is required. Use File > New to create an archive, then add files from your Mac, save, reopen, and extract them. The app edits user-selected PAK, PK3, and KPF archives and includes local previews. No game assets are bundled.

The sandbox's incoming-network entitlement supports an optional temporary loopback-only HTTP server for user-initiated browser demo playback. The server binds to 127.0.0.1, limits requests, and uses an unguessable token. It does not listen on the LAN or upload archives to a server. The archive editor and built-in previews work without demo playback or an external game engine.

## Build and validation

Run native/scripts/build-macos.sh and swift test --package-path macos --configuration release. Archive with xcodebuild using Release, generic macOS destination, both arm64 and x86_64, and automatic signing for team 36AUSRR8UM. Export using method app-store-connect. Required-reason API declarations cover app preferences, user-selected file metadata, and temporary/container file metadata.
