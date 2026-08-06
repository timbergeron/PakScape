# Changelog

- Added Windows and Linux click-pause-click inline rename on an already-selected
  item's name, matching macOS and the system file managers.
- Added Linux Undo/Redo parity with bounded history for archive additions, pastes,
  moves, folder creation, renames, and deletion, including saved-revision tracking.
- Made Linux contextual conversion menus file-specific and removed the generic
  contextual Save As submenu.
- Expanded Linux search from the current folder to every archive path, with
  metadata, multi-term, compact-text, and wildcard matching.
- Added Linux **File > Open PAK Folder** integration through the desktop's
  default file manager.
- Added modeless Linux Get Info windows with multi-selection support and shared
  format metadata, available from context menus and `Alt+Enter`.
- Added Linux contextual Add Files, Add Folder, and New Folder actions that
  target the selected folder.
- Replaced Linux's rename prompt with inline rename across all four views and
  added three-level Large Icons zoom controls.
- Added Linux interactive six-face skybox preview with drag look, wheel zoom,
  bounded decoding, and view reset.
- Added Linux multi-window document support with isolated per-window state,
  last-window shutdown, duplicate-open activation, and `Ctrl+W` close behavior.

Notable user-visible changes are documented here.

## Unreleased

### Added

- Cross-platform read/write support for KEX Engine `.kpf` resource archives,
  including document creation, native file associations, and safe ZIP validation.
- macOS BSP level previews now open fitted to the panel instead of running under
  the marker controls, and zoom with the scroll wheel or a trackpad pinch, with
  drag to pan and double-click to reset. Each marker checkbox now appears only
  when the level actually places that item, and the first level preview of a
  session opens in a larger panel.
- Portable Swift tests for PAK round trips, unsafe paths, missing payloads, format limits, document mutation, and PK3 preflight checks.
- Windows regression tests for unsafe paths, unsupported PAK names, and atomic replacement.
- Archive-wide Windows search now matches names, full paths, types, metadata, compact names, and wildcard patterns.
- Windows Undo and Redo retain up to 50 archive mutations while tracking the last saved revision.
- A self-contained Windows installer bundles the native preview libraries and registers PakScape as a PAK/PK3 opener.
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
- Model archive context menus can export every embedded MDL skin as LMP, JPEG, PNG, or TGA; viewer context menus save the currently selected skin.
- BSP archive context menus can export every embedded mip texture as LMP, JPEG, PNG, or TGA while preserving safe texture names.
- WAD archive context menus can export every uncompressed WAD2 or WAD3 mip texture as LMP, JPEG, PNG, or TGA, following the WADCleaver-style batch workflow.
- Demo details on macOS, Windows, and Linux now come from reading the recorded server messages, reporting the levels played, the level title, length, game mode, mod directory, players, closing scores, and network protocol.
- Quake `.sav` headers now populate metadata on macOS, Windows, and Linux with the save comment, map, skill, elapsed time, version, and remaster mod directory; the Details column shows the map and skill.
- "Play Demo in Browser…" hands a `.dem` to the q1tools web player, along with the open archive when it holds the demo's maps. The demo is not uploaded: PakScape publishes it on a loopback socket under an unguessable path that expires, and the browser fetches it back from this machine. Double-clicking a demo plays it too, instead of handing the file to an application that cannot open it.
- Get Info on macOS opens in its own window instead of a centered sheet, so the archive stays usable behind it and several items can be compared side by side. Each window is placed off the top-left of its archive window and cascades from the last one, a second Get Info on the same item brings its window forward, and Escape, Return, or Command-W closes it. Windows for items deleted from the archive close themselves.
- Get Info on macOS and the details pane on Windows and Linux now say what a file is for, not only what format it is. `progs.dat` and the other compiled QuakeC programs report their version, progdefs CRC, function and statement counts, entity field count, and string size; `end1.bin` and `end2.bin` are read as the 80 × 25 DOS text screens the DOS release printed on exit, headline included; and `quake.rc`, `default.cfg`, `config.cfg`, `autoexec.cfg`, `progs.src`, `palette.lmp`, `colormap.lmp`, `pop.lmp`, `gfx.wad`, and a dozen other well-known names and extensions each carry a line explaining what they do. The sentence stays out of the narrow details column, which keeps showing counts.
- `.loc` and `.src` files preview as text, and `.rc` scripts are named as Quake console scripts.
- BSP brush models — the ammo boxes, health kits, and prefab props Quake stores as `.bsp` — open in the model viewer on macOS, Windows, and Linux, textured from the faces and textures in the file itself. They are told apart from playable levels by content rather than by the `b_` naming habit: a brush model is a BSP with one hull, no visibility data, and no spawn point. Levels keep their flat overview preview.
- `.spr` and `.spr32` sprites open in the model viewer on macOS, Windows, and Linux, playing every frame at the intervals stored in the file as a fullbright card the camera starts square to and can still be orbited. Frame groups and eight-way angled frames are unrolled into the same flipbook, and a sprite the viewer cannot read — a Half-Life sprite, for one — still falls back to its flat first frame.
- Multi-frame MDL and MD3 models animate in the model viewer, with a play/pause control and speeds from 0.25× to 4× on macOS, Windows, and Linux.
- BSP level overviews can mark green, yellow, and red armor, megahealth, Quad, Ring, Pentagram, rocket launchers, lightning guns, and CTF flags. Markers stay out of archive thumbnails by default; Quick Preview checkboxes turn each group on when wanted.
- Complete Quake skybox image sets on macOS can be opened from any face through its context menu or the **View Skybox** button in Quick Look, using an interactive drag-to-look preview with scroll zoom, view reset, and a direct return to the original image.
- Complete Quake skybox image sets on Windows now offer the same **View Skybox** context-menu and Quick Preview actions, with drag-to-look, scroll zoom, view reset, and a direct return to the original image.
- Windows Get Info now opens modeless per-item windows with thumbnails, exact sizes, archive paths, folder summaries, modified dates, and format metadata; multiple selections open offset windows for side-by-side comparison.
- The Windows item context menu now includes destination-aware **Add Files**, **Add Folder**, and **New Folder** actions; invoking them on a folder targets that folder, while files and empty space target the current folder.
- Windows renaming now happens inline in every view: F2 or **Rename** edits the visible name, Enter commits, Escape cancels, and new folders enter rename mode immediately. Large-icon view also has three zoom levels via **View > Zoom In/Out** or Ctrl++/Ctrl+-.
- Windows now supports multiple archive windows. New and Open create independent document windows with separate undo history, previews, temporary files, and unsaved-change handling; reopening an already-open archive activates its existing window.
- Get Info and the Details column now recognize BSP2 and Quake 64 levels, MD3 and MD5 models, LIT and VIS map sidecars, NAV2 bot navigation, DDS images, Ogg/Opus/FLAC audio, IT/S3M/XM/MOD/UMX music, and additional Quake text formats such as FGD, PTS, RTLIGHTS, SCR, and SKIN.

### Changed

- Audio preview thumbnails now use a warm rust/olive/grey gradient in place of
  the previous purple-to-blue one on Windows, Linux, and macOS.
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
