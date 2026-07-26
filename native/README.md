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
idle turntable, and camera-anchored studio lighting.
Each app only forwards input and blits the BGRA rows the renderer produces.

MDL and MD3 poses are decoded as animations and loop at ten frames per second
by default. The view API keeps playback separate from the idle turntable and
lets each host pause it or apply a playback-speed multiplier.

Frames are rasterized in horizontal bands across worker threads, at screen
resolution while the camera is moving and supersampled once it settles, so
dragging stays responsive without giving up a clean still image.

MDL skins are embedded, so those models preview on their own. MD3 and MD5 name
their skins instead, so the library reports each request and the app resolves it
against the open archive; surfaces with no skin fall back to a neutral studio
material.

BSP brush models are read from the first hull the way the engine draws it: edges
walked through the surfedge list, a plane for each face normal, and the texinfo
axes for coordinates, with the file's own miptextures at mip level zero. Faces
are grouped per texture so one surface stands for one material. Because a `.bsp`
holds either a brush model or a playable level, `pkm_bsp_is_brush_model` answers
which from the content — one hull, no visibility data, no spawn point — and hosts
route only brush models to the viewer. The parser itself is happy to read a
level, so showing one would be a routing decision rather than new code.

Sprites are a flipbook rather than a mesh: every frame becomes its own quad,
sized and offset the way Quake hangs it off the sprite's origin, and the view
plays them at the intervals in the file. The camera starts square to the card,
skips the turntable, and draws the frames fullbright, because that is how the
game draws a sprite.

## Building

Build the libraries before building a desktop app:

```bash
native/scripts/build-linux.sh
native/scripts/build-macos.sh
```

On Windows, run `native/scripts/build-windows.ps1` in PowerShell. CMake fetches
the pinned dependency sources during the first configuration. Each script also
runs `ctest`, which covers both the audio and model ABIs.
