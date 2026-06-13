# Fracturing Fog — Cross-Platform User Guide

> Companion: [FEATURES.md](../../FEATURES.md) (full feature tour) ·
> [Avalonia-UserGuide.md](Avalonia-UserGuide.md) (Avalonia shell tour) ·
> [Keyboard-Shortcuts.md](Keyboard-Shortcuts.md)
>
> Plan: [CrossPlatform-Implementation Plan](../Technical/CrossPlatform-ImplementationPlan.md) ·
> [Roadmap](../Technical/CrossPlatform-Roadmap.md) ·
> Smoke tests: [CrossPlatform-SmokeTests.md](../Technical/CrossPlatform-SmokeTests.md).

Fracturing Fog ships natively on Windows, Linux, and macOS. The Avalonia
shell is the same on every host; the rendering, audio, and video paths
pick the best backend for the OS at startup. This guide covers what
changes per OS, what to install before the first launch, and where the
gaps still are.

---

## 1. Install per OS

### Windows (10 / 11, x64)

1. Download `FracturingFog-win-x64.zip` from the
   [Releases page](https://github.com/dpiserve/FracturingFog/releases).
2. Unzip anywhere (`%LOCALAPPDATA%\Programs\FracturingFog\` works).
3. Double-click `FracturingFog.App.exe`. Bundled ffmpeg ships in
   `Tools/win-x64/ffmpeg.exe`; nothing else needs installing.

### Linux (x64 or arm64, glibc 2.27+)

1. Download `FracturingFog-linux-x64.AppImage` (or `-linux-arm64`) from
   the [Releases page](https://github.com/dpiserve/FracturingFog/releases).
2. `chmod +x FracturingFog-linux-x64.AppImage`.
3. Run it. The AppImage extracts to `~/.cache/appimage/` on first launch.

Optional but recommended for video export:

```
sudo apt install ffmpeg        # Debian / Ubuntu
sudo dnf install ffmpeg        # Fedora
sudo pacman -S ffmpeg          # Arch
```

The app picks ffmpeg off `PATH` automatically and surfaces "Rescan PATH"
in the FFmpeg Setup dialog so you do not have to restart after the
install.

### macOS (Apple Silicon — Sonoma+; Intel — Big Sur+)

1. Download `FracturingFog-osx-arm64.tar.gz` (or `-osx-x64`) from the
   [Releases page](https://github.com/dpiserve/FracturingFog/releases).
2. `tar xf FracturingFog-osx-arm64.tar.gz`.
3. Drag `FracturingFog.app` into `/Applications/`.
4. Right-click → Open the first time so Gatekeeper accepts the unsigned
   bundle (official signed builds are tracked in
   [CrossPlatform-Roadmap.md](../Technical/CrossPlatform-Roadmap.md)
   under "Phase X.6 follow-ups").

Optional for video export:

```
brew install ffmpeg
```

---

## 2. Renderer selection

Fracturing Fog auto-picks the best renderer for your host:

| OS      | Default                   | Override                                          |
|---------|---------------------------|---------------------------------------------------|
| Windows | DirectX 12 → DX11 fallback | `--renderer dx` \| `silk` \| `skia`               |
| Linux   | Silk.NET OpenGL (3.3 core / 4.10 fallback) | `--renderer silk` \| `skia`            |
| macOS   | Silk.NET OpenGL via CGL    | `--renderer silk` \| `skia`                       |

Use the `--renderer` CLI flag for parity testing or when the discrete
GPU is unavailable:

```
FracturingFog.App --renderer skia      # CPU fallback, works on every host
FracturingFog.App --renderer silk      # Force OpenGL on Windows
FracturingFog.App --renderer dx        # Force DirectX (Windows only)
```

The Hardware tab in `Help → Hardware` lists the picked accelerator + the
full DXGI / ILGPU / audio backend so you can confirm what is live.

---

## 3. Audio capability matrix

| Source            | Windows | Linux | macOS |
|-------------------|:-------:|:-----:|:-----:|
| System loopback   | ✓       | —     | —     |
| Microphone        | ✓       | —     | —     |
| File playback     | ✓       | ✓     | ✓     |
| Synthesised beat  | ✓       | ✓     | ✓     |

System loopback + microphone capture rely on WASAPI and currently land
only on Windows via the `FracturingFog.Audio.Win` backend. On Linux and
macOS the audio settings dialog greys those rows with a yellow banner
(`#FFCC00`) and the audio-reactive slideshow falls back to the file or
synth source. Source selection persists across hosts so a setting saved
on Windows still loads on Linux and vice versa — the dialog just dims
the unsupported options.

---

## 4. Video export

The video-export path uses Windows Media Foundation on Windows and
`ffmpeg` on Linux / macOS. On Windows the bundled `Tools/win-x64/
ffmpeg.exe` covers both the Media Foundation fallback (lossless H.264
when MF init fails) and the FFV1 preset that MF cannot encode.

When `ffmpeg` is missing on Linux or macOS, the video-export UI shows
the "Install ffmpeg" instructions panel with copy-paste commands for
apt, dnf, pacman, and Homebrew, plus a "Rescan PATH" button that
re-detects the binary so you do not have to restart after installing it.

| Preset                 | Container | Windows | Linux | macOS |
|------------------------|-----------|:-------:|:-----:|:-----:|
| Visually-Lossless H.264 | `.mp4`   | ✓       | ✓     | ✓     |
| Lossless H.264 (CRF 0) | `.mp4`    | ✓       | ✓     | ✓     |
| FFV1 (Lossless)        | `.mkv`    | ffmpeg only | ✓ | ✓     |

The `FfmpegSetupDialog` rescan flow + per-RID Tools probe (Slice 2.3)
also picks up a `ffmpeg` binary you drop into
`Tools/<rid>/` next to the published binary, in case a system-wide
install is not desirable.

---

## 5. Known limitations

* **Apple Silicon GPU compute via CPU.** ILGPU's Metal backend is not
  shipping yet, so `Help → Hardware` lists `CPU` as the preferred
  accelerator on macOS arm64. Compute kernels still run correctly, just
  slower than they would on CUDA / OpenCL.
* **Linux Wayland on NVIDIA.** The proprietary NVIDIA driver historically
  has issues with the EGL adapter the Silk renderer uses on Wayland. If
  the window stays black on a NVIDIA Wayland session, launch with
  `FracturingFog.App --renderer skia` (CPU) or switch to an X11 session.
  The CI matrix exercises both the X11 (xvfb) and Wayland (weston
  headless) legs so regressions in the Wayland adapter surface before
  release.
* **Touch / Mobile.** No iOS / Android / browser host today.
* **OpenAL system-audio capture.** Linux + macOS WASAPI-loopback
  equivalents (OpenAL loopback, PulseAudio monitor sources, CoreAudio
  Aggregate devices) are out of scope for the first cross-platform cut;
  see the audio-capability matrix above.

---

## 6. Where to file bugs

Bugs against the cross-platform path go through the same channel as
WinForms bugs — the project's GitHub issues — but tag them
`cross-platform` plus the OS (`linux`, `macos`, `windows`) so the
triage filter picks them up. Attach:

1. Host OS + version (`uname -a` on Linux/macOS, `winver` on Windows).
2. `dotnet --info` output if you ran from a publish tree.
3. Hardware tab dump (`Help → Hardware → Copy`).
4. Stderr captured via `FracturingFog.App 2>&1 | tee /tmp/ff.log`.

The smoke playbook ([CrossPlatform-SmokeTests.md](../Technical/CrossPlatform-SmokeTests.md))
covers the per-phase reproduction steps for the most common regressions.
