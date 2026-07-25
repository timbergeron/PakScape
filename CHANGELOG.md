# Changelog

Notable user-visible changes are documented here.

## Unreleased

### Added

- Portable Swift tests for PAK round trips, unsafe paths, missing payloads, format limits, document mutation, and PK3 preflight checks.
- Windows regression tests for unsafe paths, unsupported PAK names, and atomic replacement.
- Native Windows PK3 read/write support with path, symlink, duplicate, and expanded-size validation.
- Windows build-and-test coverage in GitHub Actions.
- Registered macOS Finder Services and an app Settings window.
- Contributor, changelog, and security-reporting documentation.
- Ubuntu-focused Linux desktop app with PAK/PK3 editing, search, drag-and-drop import, recent files, keyboard navigation, and unsaved-change protection.
- Self-contained x86-64/ARM64 Debian packaging with XDG desktop and MIME integration.
- Linux-specific regression tests and Ubuntu 24.04 CI coverage.
- Finder-style Spacebar Quick Preview on macOS, Windows, and Linux, including text, common image, and Quake asset previews.
- Native audio controls in Quick Preview on Windows and Linux for common music and sound formats.
- Native Finder-style inline thumbnails for common files and content-aware fallback icons on macOS.
- A system-provided macOS toolbar search field backed by PakScape's archive-wide matching.
- Finder-style Get Info details for macOS archive files and folders, available from menus and Command-I.
- Standard Undo and Redo support for macOS archive edits.
- Bundled cross-platform Quick Preview audio for the QSS-M WAV, MP3, FLAC, Ogg Vorbis, Ogg Opus, IT, S3M, XM, MOD, and UMX formats.
- An interactive model viewer in Quick Preview for the QSS-M model formats — MDL, MD3, and MD5 — with an orbit camera, pan, wheel and pinch zoom, keyboard control, idle turntable, and studio lighting, shared by all three editions.
- MD3 and MD5 skins are resolved from the open archive, including Quake III `.skin` files, and surfaces without a skin fall back to a neutral material.
- Double-clicking a model opens it in the viewer instead of handing it to another application.
- Image context menus on macOS, Windows, and Linux can save `.lmp`, `.jpg`, `.png`, and `.tga` files in any of those four formats.
- Demo details on macOS, Windows, and Linux now come from reading the recorded server messages, reporting the levels played, the level title, length, game mode, mod directory, players, closing scores, and network protocol.
- "Play Demo in Browser…" hands a `.dem` to the q1tools web player, along with the open archive when it holds the demo's maps. The demo is not uploaded: PakScape publishes it on a loopback socket under an unguessable path that expires, and the browser fetches it back from this machine. Double-clicking a demo plays it too, instead of handing the file to an application that cannot open it.
- Get Info on macOS opens in its own window instead of a centered sheet, so the archive stays usable behind it and several items can be compared side by side. Each window is placed off the top-left of its archive window and cascades from the last one, a second Get Info on the same item brings its window forward, and Escape, Return, or Command-W closes it. Windows for items deleted from the archive close themselves.
- Get Info on macOS and the details pane on Windows and Linux now say what a file is for, not only what format it is. `progs.dat` and the other compiled QuakeC programs report their version, progdefs CRC, function and statement counts, entity field count, and string size; `end1.bin` and `end2.bin` are read as the 80 × 25 DOS text screens the DOS release printed on exit, headline included; and `quake.rc`, `default.cfg`, `config.cfg`, `autoexec.cfg`, `progs.src`, `palette.lmp`, `colormap.lmp`, `pop.lmp`, `gfx.wad`, and a dozen other well-known names and extensions each carry a line explaining what they do. The sentence stays out of the narrow details column, which keeps showing counts.
- `.loc` and `.src` files preview as text, and `.rc` scripts are named as Quake console scripts.
- BSP brush models — the ammo boxes, health kits, and prefab props Quake stores as `.bsp` — open in the model viewer on macOS, Windows, and Linux, textured from the faces and textures in the file itself. They are told apart from playable levels by content rather than by the `b_` naming habit: a brush model is a BSP with one hull, no visibility data, and no spawn point. Levels keep their flat overview preview.
- `.spr` and `.spr32` sprites open in the model viewer on macOS, Windows, and Linux, playing every frame at the intervals stored in the file as a fullbright card the camera starts square to and can still be orbited. Frame groups and eight-way angled frames are unrolled into the same flipbook, and a sprite the viewer cannot read — a Half-Life sprite, for one — still falls back to its flat first frame.

### Changed

- Model previews no longer draw a contact shadow beneath the model.
- macOS 14 is now the minimum deployment target.
- Save As now updates the active document location.
- Archive and export writes use atomic replacement where appropriate.
- Imports report failures instead of silently skipping unreadable items.
- Finder Services request a sandbox-approved output folder before writing results.
- The Windows edition now supports archive editing, import/export, recent files, navigation, keyboard shortcuts, and unsaved-change prompts.
- Windows tests use the maintained xUnit.net v3 packages.
- Linux builds use .NET 10 LTS and Avalonia 12 with warnings-as-errors and recommended analyzer rules.
- Windows and Linux now share the same modern archive shell, including integrated navigation, collapsible folder panes, contextual search, full-row selection, path tooltips, and light/dark styling.
- macOS sources, tests, project metadata, and documentation now live under `macos/`.
- Generated Xcode `DerivedData` is no longer tracked in the repository.
- macOS document saving, Save As, edited-window state, and close confirmation are now managed by SwiftUI's native document lifecycle.
- macOS file drops now use SwiftUI's typed Transferable API.
- macOS Undo stores operation-sized changes with a 50-action limit instead of retaining a full archive tree per edit.
- Cross-document cut/paste safely copies into the destination so Undo remains scoped to one archive.
- Native macOS thumbnails stage bounded payload ranges off the main thread, queue at most 32 requests, and limit Quick Look generation to four concurrent jobs.
- Windows and Linux audio preview no longer depends on optional system codecs or an external `mpv` process.

### Security

- Reject unsafe, duplicate, conflicting, overlong, and control-character archive paths.
- Reject overlapping PAK payload ranges and symlink traversal during directory imports.
- Inspect PK3 paths, features, symlinks, and declared expanded sizes before extraction.
- Refuse to serialize missing payload data instead of producing corrupt zero-byte entries.
- Reject Linux symlink imports, enforce bounded folder imports, use atomic exports, and isolate temporary previews in private XDG runtime storage.
- Apply shared 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total limits to macOS, Windows, and Linux imports and archive writers.
- Account for existing document contents during batch imports and clipboard paste operations.
- Reject Windows device names, alternate data stream paths, reparse points, and unstable source files during transfer operations.
- Bound macOS image, WAD, and BSP preview allocations before decoding untrusted assets.
- Bound Windows and Linux preview selections, text reads, image dimensions, and Quake asset decoding.
- Avoid mutating the macOS document model before a generated save is committed.
