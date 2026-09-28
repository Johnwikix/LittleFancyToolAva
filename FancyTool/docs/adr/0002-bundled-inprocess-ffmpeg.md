# Replace BYO ffmpeg.exe with bundled in-process FFmpeg shared libraries

The video transcode tool used to shell out to a user-provided FFmpeg 9.0 build (`ffmpeg.exe`/`ffprobe.exe`
picked from a "bring your own" directory), mirroring the WinUI music player's earlier audio pipeline.
Following the music player's current `FFmpegAudioConverter` approach, transcoding now calls the FFmpeg C API
in-process through `FFmpeg.AutoGen` 9.0.1.1: `avformat` demux/mux, `avcodec` decode/encode, an `avfilter`
graph for crop/deinterlace/denoise/scale/fps/pix_fmt (GIF via palettegen/paletteuse), `swresample` for the
audio path, and hardware encoders (NVENC/QSV/AMF on Windows, VAAPI on Linux) by encoder name. The GPL shared
libraries (avcodec-63/avformat-63/avutil-61/avfilter-12/swscale-10/swresample-7 from BtbN
`ffmpeg-n9.0.1-84-g946fcce07b`, win-x64 DLLs + linux-x64 .so) are committed under
`FancyTool/Libraries/FFmpeg/` and copied next to the app at build time — users no longer download anything.

**Status**: accepted

**Considered Options**:
- **Keep ffmpeg.exe subprocess + BYO directory** — dropped: external download step, per-user drift across
  FFmpeg builds, progress parsing from stderr, and process-management overhead.
- **In-process via FFmpeg.AutoGen with bundled shared libs** — chosen: same FFmpeg version for every user,
  direct progress from frame timestamps, filtergraph strings stay 1:1 with the previous CLI arguments.

**Consequences**:
- The repository license changed from MIT to GPL-3.0: the bundled build contains GPL components
  (libx264/libx265/libmp3lame…), so distributing it inside the app requires a GPL-compatible license.
  FFmpeg's GPL text ships with the app as `Licenses/FFmpeg.txt`; build provenance and SHA-256 checksums are
  recorded in `FancyTool/Libraries/FFmpeg/BUILD_INFO.md`.
- `PublishAot` for win-x64 Release was disabled: FFmpeg.AutoGen 9.x resolves native functions through
  reflection-based `DynamicallyLoadedBindings`, which NativeAOT cannot support. Windows releases are now
  self-contained managed publishes (larger than the previous native single-file, otherwise equivalent).
- Two-pass encoding is limited to libx264/libx265 at bitrate rate control (their `stats` private option
  drives the pass log file, exactly like the CLI's `-pass/-passlogfile`); other encoders fall back to a
  single pass.
- The old `CustomFfmpegDirectory` preference, BYO folder UI, and `AppPaths.Ffmpeg*` paths are gone;
  stale keys in existing `preferences.json` files are ignored harmlessly.
- Upgrading the bundled libraries requires matching the FFmpeg.AutoGen binding version (9.0.x ABI) — see
  the upgrade notes in `Libraries/FFmpeg/BUILD_INFO.md`.
