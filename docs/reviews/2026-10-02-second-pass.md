# PakScape second review — 2026-10-02

Reviewed replacement imports and exports, macOS clipboard failure paths and live
cut semantics, and the ZIP save depth boundary. This is a targeted source review,
not an exhaustive audit or interactive desktop sign-off.

## Fixes

- Windows overwrite confirmation no longer deletes the existing export before
  preparing its replacement. Windows and Linux transfer services stage complete
  files or folders first. File replacements use an overwrite rename; folder and
  cross-kind replacements temporarily retain the original in a sibling backup
  and restore it if installation fails. Failed restoration reports the backup
  location. Cleanup failures do not mask the export's result.
- Both C# transfer services reject missing file payloads instead of exporting
  them as empty files.
- Windows and Linux replacement imports read into a detached staging tree and
  validate the resulting archive before replacing the original. Existing entry
  and payload counts are subtracted when calculating capacity. macOS applies the
  same net-capacity accounting using a candidate import budget.
- macOS clipboard snapshots now throw on unreadable files and preserve valid
  empty files. Selecting a folder and one of its descendants copies only the
  folder subtree once.
- Same-document macOS cut/paste validates live source membership and destination
  depth before mutation, preserving changes made after Cut. Deleted cut items
  are rejected, and items already in the destination retain their names without
  creating a redundant edit. Undo retains the original objects.
- macOS ZIP saving accepts an empty folder at the 256-component path boundary
  while still rejecting actual children beyond that boundary.

## Validation

- Shared C# suite: **216 passed**, zero failures or skips. The suite includes the
  Windows transfer service directly, covering all file/folder replacement
  combinations, failed export preparation, missing payloads, and net-capacity
  replacement validation.
- Linux service suite: **21 passed**, zero failures or skips, including all
  file/folder replacement combinations and failed preparation preserving the
  original destination.
- Core, format handlers, Linux services, the Windows transfer service, and both
  test assemblies were compiled from current sources with Roslyn and compiler
  warnings treated as errors. The host lacks the complete .NET SDK; the isolated
  test build reused existing dependency DLLs and generated test entry points,
  targeted .NET 10 references, and ran on .NET 10 with major roll-forward for the
  .NET 8-targeted suite.
- Seven added Swift tests cover strict snapshots, normalized selections, live
  cut items, invalid moves, replacement budgets, and the ZIP depth boundary.
  Swift and desktop app builds are validated by the platform CI matrix after
  push; Swift/Xcode are unavailable on this host.
- `git diff --check` passes.

No native code changed in this pass. No interactive desktop validation was
performed locally.
