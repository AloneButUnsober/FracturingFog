# Cross-Platform Manual Smoke Tests

> Companion: [Cross-Platform Implementation Plan](CrossPlatform-ImplementationPlan.md) ·
> [Cross-Platform Roadmap](CrossPlatform-Roadmap.md) · [Technical Index](_Index.md)

> **Created 2026-06-12** for branch `feature/cross-platform-full`. Lists the
> manual user-visible checks that round out each phase's exit criteria. CI builds
> every assembly on the Linux + macOS legs; these procedures verify the runtime
> end-to-end on a real desktop before each phase is closed.
>
> Run them on every supported host before the corresponding phase PR merges to
> `main`. The host matrix matches the roadmap: `linux-x64` (Ubuntu 24.04 GNOME
> Wayland + X11), `linux-arm64` (Raspberry Pi OS), `osx-arm64` (macOS Sonoma on
> Apple Silicon), and `osx-x64` (Intel macOS). `win-x64` is covered implicitly
> by the WinExe regression and does not need re-running here.

---

## Phase X.A — Image I/O SkiaSharp swap

### A.S1 — PNG export, headless

1. `dotnet run --project FracturingFog.App -- --batch --image --out /tmp/smoke.png --width 800 --height 600 --maxiter 256`.
2. Confirm exit code 0 and `/tmp/smoke.png` exists.
3. Open the file in the host's default image viewer; confirm the Mandelbrot
   render looks correct (no inverted alpha, no swapped colour channels).
4. `file /tmp/smoke.png` reports `PNG image data` with the expected dimensions.

### A.S2 — Slideshow frame capture, headless

1. `dotnet run --project FracturingFog.App -- --batch --slideshow --out /tmp/slides --frames 8 --width 640 --height 480`.
2. Confirm eight `frame_NNNNNN.png` files land in `/tmp/slides/` with strictly
   increasing colours / view drift.
3. Spot-check `file /tmp/slides/frame_000001.png` for sane metadata.

### A.S3 — Watermark composition

1. Interactive: open the App, enable Watermark from the menu, render a frame.
2. Confirm the watermark text is legible (font fell back to Inter or a system
   sans-serif), outline + fill render in the configured colours.
3. Save the frame via "Save Image…" — confirm the saved PNG has the watermark
   baked in.

---

## Phase X.B — Audio capture abstraction

### B.S1 — Source picker visible, system loopback greyed

1. Launch the App. Open the audio-reactive slideshow settings dialog.
2. Confirm the source picker is present and **System loopback** + **Microphone**
   appear in the list with reduced opacity (dim, not removed).
3. Confirm the yellow `#FFCC00` banner at the top of the dialog reads
   "System audio capture is not supported on this OS." (or the localised
   variant) — verifying the colourblind-safe warning hue.

### B.S2 — File playback drives the analyzer

1. Pick **File** as the source; browse to a known-good WAV or MP3 (NAudio
   handles both cross-platform via the file-decode path).
2. Start the audio-reactive slideshow.
3. Confirm the slideshow advances on beat detections (theme/region switches
   coincide with audible kicks). The BPM readout in the settings dialog should
   show a non-zero value within ~5 s of playback start.
4. Stop the slideshow — confirm clean shutdown (no zombie audio threads;
   `ps` / `top` shows the process CPU drops to idle).

### B.S3 — Synth source (analyzer-only)

1. Pick **Synth** as the source.
2. Start the audio-reactive slideshow.
3. Confirm the slideshow advances on the synthesised beat pattern; no speaker
   output is expected (the noop backend routes the synth into the analyzer
   only).
4. Stop and confirm clean shutdown.

---

## Phase X.1 — Palette engine

### 1.S1 — PNG sheet round-trip

1. Open `FracturingFog.App` (or `PaletteBuilder` standalone). Open the palette
   builder; load a source image (PNG / JPEG from `Resources/Samples/` works).
2. Extract a palette (any method).
3. Export → choose **PNG sheet**, save to `/tmp/palette.png`.
4. Open `/tmp/palette.png` in a viewer; confirm the 1-column strip of swatch
   tiles renders with legible `#HEX RGB(r, g, b)` labels in luma-aware contrast
   (white text on dark swatches, black on light). Fallback font is acceptable
   if the host lacks Consolas.

### 1.S2 — PDF export round-trip

1. Same source image + extracted palette as 1.S1.
2. Export → choose **PDF document**. Open the PDF settings dialog.
3. Tick every option (cover page, source thumbnail, comparison page, gradient
   strip, swatch metadata, CVD rows). Pick A4 portrait, 2 columns.
4. Save to `/tmp/palette.pdf`.
5. Open `/tmp/palette.pdf` in the host's default PDF viewer.
   - Cover page: source thumbnail centred, "Method:" + settings dump readable.
   - Comparison page (if multiple extractors ran): one row per method, swatch
     strip + gradient under it.
   - Swatch grid pages: 2-column tile layout, RGB / hex plate centred on each
     swatch, metadata block under each, CVD strip (Proto / Deut / Trito) under
     that, gradient strip footer on every page, "page N of M" in the header.
6. Confirm no QuestPDF licence-violation watermark appears (Community licence
   set in the static ctor before any document renders).

### 1.S3 — Empty palette graceful path

1. Export PDF with zero swatches (extract from a uniform-colour image with
   the count knob set to 0 if the UI allows, or remove all rows manually).
2. Confirm `<PaletteName> — (empty)` page renders; PDF opens without error.

---

## Phase X.2 — Video export (placeholder)

Procedures land with Phase X.2 implementation. Outline:
- 1.S1 with `--video` flag: 100-frame slideshow renders to MP4 via
  `FfmpegVideoWriter` on Linux/macOS when ffmpeg is on PATH.
- 1.S2: "Install ffmpeg" instructions panel appears when ffmpeg is missing;
  rescan PATH button reflects post-install state without an app restart.

---

## Phase X.6 — Packaging

### 6.S1 — `dotnet publish` per RID

Run from a clean checkout:

```
dotnet publish FracturingFog.App -c Release -p:PublishProfile=linux-x64
dotnet publish FracturingFog.App -c Release -p:PublishProfile=linux-arm64
dotnet publish FracturingFog.App -c Release -p:PublishProfile=osx-arm64
dotnet publish FracturingFog.App -c Release -p:PublishProfile=osx-x64
dotnet publish FracturingFog.App -c Release -p:PublishProfile=win-x64
```

Each command emits a self-contained single-file archive under
`FracturingFog.App/publish/<rid>/`. Confirm:

1. Archive present + non-empty (Win archives bundle ffmpeg.exe per
   Slice 2.4).
2. Quick sanity launch on the matching host: `./FracturingFog.App`
   opens the Avalonia shell; `./FracturingFog.App --batch --image
   --out /tmp/smoke.png --width 320 --height 240` round-trips a PNG.

**Known publish blocker (CalculatorGen + ColorGen NETSDK1150).**

Until CalculatorGen and ColorGen are split into Lib + Cli sibling
projects, the App's self-contained publish trips NETSDK1150 because both
Exe projects are referenced transitively as libraries via UI.Avalonia.
The follow-up that fixes this:

1. Add `CalculatorGen.Lib` + `ColorGen.Lib` library projects holding the
   `*Api`, `*HotLoad`, and template-resolver source.
2. Slim `CalculatorGen` + `ColorGen` Exes to a thin `Program.cs` Main
   that dispatches into the Lib.
3. Retarget `UI.Avalonia.csproj` ProjectReferences at the new `*Lib`
   projects so the App publish chain only ever sees library refs.

Tracked separately; publish artifacts ship via the CI release workflow
(Slice 6.4) where the GitHub runner builds against a clean restore and
the publish profile drives a fresh single-RID closure.

### 6.S2 — Linux AppImage

After `dotnet publish -p:PublishProfile=linux-x64`:

```
Tools/Packaging/build-appimage.sh linux-x64
```

Confirm `dist/FracturingFog-linux-x64.AppImage` exists and is executable
(`./dist/FracturingFog-linux-x64.AppImage` opens the shell).

### 6.S3 — macOS `.app` bundle

After `dotnet publish -p:PublishProfile=osx-arm64`:

```
Tools/Packaging/build-mac-app.sh osx-arm64
```

Confirm `dist/FracturingFog.app/Contents/MacOS/FracturingFog.App` is
executable and Info.plist parses (`plutil -lint
dist/FracturingFog.app/Contents/Info.plist`). Code-signing is a separate
manual step until Apple Developer cert lands.

---

## Reporting failures

When a smoke test fails:

1. Capture the host OS + version (`uname -a` on Linux/macOS).
2. Capture `dotnet --info` output.
3. Capture the App's stderr — every smoke run can pipe through
   `2>&1 | tee /tmp/smoke.log`.
4. File against the corresponding phase in the implementation plan; the
   bug belongs on `feature/cross-platform-full` until the phase merges.
