# PakScape native libraries

This directory builds the two private C ABIs shared by all three desktop
editions. Both are built by the same CMake project.

## `pakscape_audio`

Supports the same music formats enabled by QSS-M:

- Sampled audio: WAV, MP3, FLAC, Ogg Vorbis, and Ogg Opus
- Tracker modules: IT, S3M, XM, MOD, and UMX

miniaudio provides platform output plus WAV/MP3/FLAC decoding, stb_vorbis
decodes Ogg Vorbis, libopusfile decodes Ogg Opus, and libopenmpt renders the
tracker formats. Dependencies are pinned and statically linked into this one
private library; the exported ABI does not expose third-party types.

## `pakscape_model`

Parses the QSS-M model formats — Quake MDL, Quake III MD3, and Doom 3 MD5 —
and software renders them for Quick Preview. It has no third-party
dependencies.

Keeping the parser, the orbit camera, and the rasterizer in one library means
every edition shares the same interaction feel and the same picture: damped
orbit with inertia, bounding-sphere framing, clamped pitch and zoom, pan, an
idle turntable, camera-anchored studio lighting, and a baked contact shadow.
Each app only forwards input and blits the BGRA rows the renderer produces.

Frames are rasterized in horizontal bands across worker threads, at screen
resolution while the camera is moving and supersampled once it settles, so
dragging stays responsive without giving up a clean still image.

MDL skins are embedded, so those models preview on their own. MD3 and MD5 name
their skins instead, so the library reports each request and the app resolves it
against the open archive; surfaces with no skin fall back to a neutral studio
material.

## Building

Build the libraries before building a desktop app:

```bash
native/scripts/build-linux.sh
native/scripts/build-macos.sh
```

On Windows, run `native/scripts/build-windows.ps1` in PowerShell. CMake fetches
the pinned dependency sources during the first configuration. Each script also
runs `ctest`, which covers both the audio and model ABIs.
