# PakScape native audio

This directory builds the private `pakscape_audio` C ABI used by all three
desktop editions. It supports the same music formats enabled by QSS-M:

- Sampled audio: WAV, MP3, FLAC, Ogg Vorbis, and Ogg Opus
- Tracker modules: IT, S3M, XM, MOD, and UMX

miniaudio provides platform output plus WAV/MP3/FLAC decoding, stb_vorbis
decodes Ogg Vorbis, libopusfile decodes Ogg Opus, and libopenmpt renders the
tracker formats. Dependencies are pinned and statically linked into this one
private library; the exported ABI does not expose third-party types.

Build the library before building a desktop app:

```bash
native/scripts/build-linux.sh
native/scripts/build-macos.sh
```

On Windows, run `native/scripts/build-windows.ps1` in PowerShell. CMake fetches
the pinned dependency sources during the first configuration.
