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

### Changed

- macOS 14 is now the minimum deployment target.
- Save As now updates the active document location.
- Archive and export writes use atomic replacement where appropriate.
- Imports report failures instead of silently skipping unreadable items.
- Finder Services request a sandbox-approved output folder before writing results.
- The Windows edition now supports archive editing, import/export, recent files, navigation, keyboard shortcuts, and unsaved-change prompts.
- Windows tests use the maintained xUnit.net v3 packages.

### Security

- Reject unsafe, duplicate, conflicting, overlong, and control-character archive paths.
- Reject overlapping PAK payload ranges and symlink traversal during directory imports.
- Inspect PK3 paths, features, symlinks, and declared expanded sizes before extraction.
- Refuse to serialize missing payload data instead of producing corrupt zero-byte entries.
