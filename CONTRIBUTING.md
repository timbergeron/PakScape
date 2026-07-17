# Contributing to PakScape

Thanks for helping improve PakScape. Keep changes focused, explain user-visible behavior, and include regression coverage for archive-format changes.

## Before submitting a change

Run the portable archive tests:

```bash
swift test
dotnet run --project windows/PakStudio.Tests/PakStudio.Tests.csproj --configuration Release
```

On macOS, also build the app:

```bash
xcodebuild \
  -project PakScape.xcodeproj \
  -scheme PakScape \
  -configuration Debug \
  CODE_SIGNING_REQUIRED=NO \
  CODE_SIGN_IDENTITY=""
```

## Engineering expectations

- Treat archive contents and paths as untrusted input.
- Never replace missing or unreadable payloads with empty data.
- Preserve the open document when an export fails.
- Use atomic replacement for user documents where the platform supports it.
- Add a regression test for parsing, serialization, or path-validation fixes.
- Keep platform-specific UI out of the portable archive-format code.

GitHub Actions must pass for both the macOS and Windows jobs before merging.
