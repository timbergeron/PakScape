# Contributing to PakScape

Thanks for helping improve PakScape. Keep changes focused, explain user-visible behavior, and include regression coverage for archive-format changes.

## Before submitting a change

Run the portable archive tests:

```bash
swift test --package-path macos
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release
dotnet run --project linux/PakScape.Linux.Tests/PakScape.Linux.Tests.csproj --configuration Release
```

On macOS, also build the app:

```bash
xcodebuild \
  -project macos/PakScape.xcodeproj \
  -scheme PakScape \
  -configuration Debug \
  -derivedDataPath macos/build \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY=""
```

## Engineering expectations

- Treat archive contents and paths as untrusted input.
- Preserve the shared 50,000-entry, 256-component path-depth, 1 GiB per-file, and 2 GiB total safety limits unless a reviewed format change requires otherwise.
- Never replace missing or unreadable payloads with empty data.
- Preserve the open document when an export fails.
- Use atomic replacement for user documents where the platform supports it.
- Bound dimensions, counts, and allocation sizes before decoding previews or walking archive tables.
- Add a regression test for parsing, serialization, or path-validation fixes.
- Keep platform-specific UI out of the portable archive-format code.

For Linux changes, build `linux/PakScape.Linux.slnx` in Release mode and validate the Debian package on Ubuntu 24.04.

GitHub Actions must pass for the macOS, Windows, and Linux jobs before merging.
