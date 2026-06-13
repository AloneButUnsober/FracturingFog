# Cross-Platform Roadmap

> Companion pages: [Technical Index](_Index.md) · [Architecture Overview](Architecture-Overview.md) · [Performance Development Plan](Performance-DevelopmentPlan.md) · [Cross-Platform Implementation Plan](CrossPlatform-ImplementationPlan.md) · [Resources & Bibliography](../Resources-Bibliography.md)

> [!IMPORTANT]
> **Snapshot 2026-06-12.** Avalonia shell is the canonical UI; WinForms is deprecated and kept
> only as a `--winforms` fallback. The 2026-06-11 snapshot is stale — the GPU-compute,
> audio-reactive, Toy-mode, and slideshow-recording branches all landed on main since then and
> introduced **new** Windows-only surface that this snapshot folds in. The full execution path
> lives in [CrossPlatform-ImplementationPlan.md](CrossPlatform-ImplementationPlan.md); this file
> is the high-level checklist and gap analysis.

**Branch baseline:** `main` (HEAD `b8c7312`, post-Palette Builder reorg).
**Active tracking branch:** `feature/cross-platform-full` (created 2026-06-12 from main).
**Goal:** ship a runnable FracturingFog binary on **win-x64**, **linux-x64**, **linux-arm64**, **osx-arm64**, **osx-x64**.
**Non-goal (this doc):** mobile, Wasm browser host, touch UI, GPU compute parity on Apple Silicon Metal.

This file is a forward-looking checklist; it does not retell Phase 2 history (see
`PHASE2_AVALONIA_MIGRATION.md`). It picks up after Phase 2.4 (Silk + Skia backends already
merged, CI matrix green for the platform-neutral assemblies) and lists what still blocks an
end-user launching the full app on a non-Windows host.

---

## Current state (2026-06-12 snapshot)

### What already works on Linux/macOS

- `Abstractions`, `UI.Avalonia`, `Rendering.Silk`, `Rendering.Skia` build + publish under
  win-x64 / linux-x64 / osx-arm64 in CI (`.github/workflows/cross-platform-build.yml`).
- `Rendering.Silk.Smoke` renders one frame on win/linux/macOS (Linux uses xvfb; macOS uses
  invisible-window FBO; Windows is direct).
- Silk context adapters present: WGL (Win32), GLX (X11), EGL (Wayland), CGL/NSOpenGL (macOS).
- ILGPU `AcceleratorProbe` exposes CPU fallback when no CUDA/OpenCL device exists.
- `RendererFactory.NonWin32Backend` slot lets the bootstrap pick Silk or Skia on non-HWND
  surfaces.
- `Hosting/PngSlideshowFrameRecorder.cs` (post-roadmap addition) records slideshow frames via
  SkiaSharp — proves the Skia encode path is hot in main and reusable for the wider
  image-save migration.
- `PaletteBuilder.Lib` palette extraction path is SkiaSharp end-to-end (`BitmapSampler`,
  `PaletteExtractionService`, `HostPaletteExtractionService`).
- `FfmpegEncoder` (`Imaging/FfmpegEncoder.cs`) is **already** a process-based ffmpeg shell-out
  with PATH lookup, so the "Phase X.2" video-export-portability work is largely a matter of
  renaming `ffmpeg.exe` lookups and providing RID-keyed binaries.

### What still pins the shipped binary to Windows x64

| # | Blocker | File / artefact | Impact | Status vs prior snapshot |
|---|---|---|---|---|
| B1 | WinExe TFM = `net10.0-windows` + `UseWindowsForms=true` | `FracturingFogCLD.csproj` | Whole exe refuses to build off Windows. | Unchanged. |
| B2 | Legacy `MainForm` + `Views/*.cs` still compiled into the WinExe | `MainForm.cs`, `Views/**`, `Slideshow.cs`, `VideoZoom.cs`, `AudioReactive.cs`, `SlideshowConfig.cs`, `ImageCapture.cs` | WinForms types drag the `-windows` TFM along with them. | Unchanged. WinForms shell now has more siblings under root (slideshow/audioreactive root files) — all caught by the same exclude glob in the new App project. |
| B3 | PaletteBuilder.Lib `net10.0-windows` + `PDFsharp-gdi` (PDF exporter only) | `PaletteBuilder/PaletteBuilder.Lib.csproj`, `PaletteBuilder/Services/PdfPaletteExporter.cs` | Decode + sampling now SkiaSharp; only `PdfPaletteExporter` still drags GDI through `PDFsharp-gdi`. Drop or swap to QuestPDF → Lib lands on `net10.0`. | Unchanged. SkiaSharp swap landed; PDF swap is the last mile. |
| B4 | WinForms `ImagePaletteDialog` keeps a `System.Drawing.Bitmap` for `PictureBox` display | `Views/ImagePaletteDialog.cs` | Bridges to `SKBitmap` at the sampler boundary (deprecation-tail dialog). Goes away when WinForms shell is removed; not on the cross-platform critical path. | Unchanged. |
| B5 | Vortice DX11/12 hard-referenced in WinExe | `FracturingFogCLD.csproj` `<PackageReference>` block | Brings `runtime.win-x64.*` natives; ref'd by DX renderer classes that live in the WinExe today. | Unchanged. |
| B6 | MP4 export via Media Foundation P/Invoke | `Imaging/MP4Writer.cs` | `mfplat.dll` / `mfreadwrite.dll` are Win-only. | Partially mitigated: `Imaging/FfmpegEncoder.cs` already provides a process-based encoder. Need to flip the bootstrap to prefer it on non-Win and add Linux/macOS ffmpeg binaries. |
| B7 | `NativeMouseForwarder` HWND subclass (`comctl32`, `user32`) | `Hosting/NativeMouseForwarder.cs` | Already guarded by `OSPlatform.Windows`, but file still lives in the host. | Unchanged. |
| B8 | Console attach P/Invoke in batch + server entry points | `ServerHost/ServerEntry.cs`, `Batch/BatchEntry.cs` | `kernel32!AttachConsole`/`AllocConsole`. Cosmetic on non-Windows but currently unguarded. | Unchanged. |
| B9 | `RuntimeInformation.IsOSPlatform` checks scattered ad-hoc | `Hosting/HostHelpContentProvider.cs`, `MainForm.cs`, etc. | Inconsistent fallbacks; some still throw on non-Win. | Unchanged. |
| B10 | `BenchmarkDotNet` pulls `kernel32` P/Invokes via `Benchmarks/MandelbrotBench.cs` | `Benchmarks/MandelbrotBench.cs` | Dev-only; safe to gate but currently in main project. | Unchanged. |
| B11 | `Tools/ffmpeg.exe` copied to output | `FracturingFogCLD.csproj` `<Tools/ffmpeg.exe>` | Windows binary in output dir; need RID-keyed copy. | Unchanged. |
| B12 | ILGPU CUDA path assumes NVIDIA driver | `Calculators/*GpuCalculator*.cs`, `Calculators/UserBulbSandboxGpuCompiler.cs`, `Rendering/MandelbrotGpuKernel.cs` | Already falls back to CPU device, but needs verification on Apple Silicon (no OpenCL on macOS 14+, CUDA absent). | Expanded: GPU-compute branch added `UserBulbGpuCalculator`, `UserBulbSandboxGpuCompiler` + spike, and `Rendering/MandelbrotGpuKernel.cs`. All ILGPU; same CUDA/OpenCL/CPU fallback applies. |
| B13 | **NEW.** Audio engine uses NAudio Win-only APIs (`WaveOutEvent`, `NAudio.CoreAudioApi` WASAPI loopback) | `Audio/AudioEngine.cs`, `Audio/FractalSynth.cs`, `Audio/BeatAnalyzer.cs` | Audio-reactive slideshow path (landed `d8f77d2`) requires WASAPI loopback for system audio and WaveOutEvent for synth playback. Loopback capture has no cross-platform equivalent without per-OS adapters (PulseAudio/ALSA `parec`, macOS BlackHole/loopback driver). | **New blocker** — was not flagged in 2026-06-11 snapshot because the feature shipped after. |
| B14 | **NEW.** Avalonia shell has Win-only HWND P/Invoke for Toy-mode drag and inspect-click | `UI.Avalonia/Views/MainWindow.axaml.cs:777-781` (`ReleaseCapture`, `SendMessage`), `Hosting/AvaloniaShellBootstrap.cs:65` (`ClientToScreen`) | Toy-mode (8768f5e) borrowed the WinForms HWND drag trick. Calls will throw `DllNotFoundException` on Linux/macOS. Avalonia has `BeginMoveDrag(PointerPressedEventArgs)` — use that instead. | **New blocker** — landed after 2026-06-11 snapshot. |
| B15 | **NEW.** Engine-side image save/export still uses `System.Drawing` GDI+ | `Imaging/ImageExport.cs`, `Imaging/PngSequenceWriter.cs`, `Imaging/PosterRenderer.cs`, `Rendering/FractalOverlayCompositor.cs`, `Models/ColorThemeCsExporter.cs` and friends | These are engine code (not WinForms), referenced by both shells AND headless paths (Batch, Server). `System.Drawing.Common` 10.x throws on non-Win unless explicit opt-in. Migration target is SkiaSharp (already shipping via Avalonia + Rendering.Skia). `PngSlideshowFrameRecorder.cs` is a working precedent. | **New blocker** — was understated in 2026-06-11 snapshot ("phase X.0 splits this out" line for ImageCapture only). The surface is bigger; calling it out as its own phase. |
| B16 | **NEW.** `FfmpegInstaller.cs` auto-downloads the Windows ffmpeg zip from gyan.dev | `Imaging/FfmpegInstaller.cs` | The "install ffmpeg for me" path the FfmpegSetupDialog uses is Windows-only. Linux/macOS users get directed to `apt`/`brew`. Not a blocker — but the dialog and CLI surface need OS-aware copy. | **New cleanup item.** |
| B17 | **NEW.** `Hosting/FfmpegSetupDialog.cs` is a WinForms dialog inside the cross-platform `Hosting/` folder | `Hosting/FfmpegSetupDialog.cs` | The Hosting/ folder is otherwise UI-framework-neutral; this dialog drags WinForms into the host fragment. Belongs in the Win-only sub-project or rewritten as an Avalonia dialog. | **New cleanup item.** |

---

## Phase order

The phases below are the long-pole ordering. Each phase has an exit criterion; the
implementation plan (`CrossPlatform-ImplementationPlan.md`) breaks them into individual
slices with file lists and commit boundaries.

### Phase X.0 — Project geometry split (prereq for everything else)

**Goal:** introduce a separate cross-platform exe target without breaking the existing
Windows WinExe.

- [ ] Add `FracturingFog.App` (`net10.0`, `WinExe` on Win / `Exe` on other RIDs) as the new
  cross-platform entry point. References: `Abstractions`, `UI.Avalonia`, `Rendering.Silk`,
  `Rendering.Skia`, `Server`, `Client`, `CalculatorGen`, `ColorGen`. No WinForms, no Vortice,
  no `System.Drawing.Common`, no `PaletteBuilder.Lib` (until X.1 finishes).
- [ ] Move `Hosting/AvaloniaShellBootstrap.cs`, `Hosting/AvaloniaDialogs.cs`,
  `Hosting/HostColorThemeService.cs`, `Hosting/HostHelpContentProvider.cs`,
  `Hosting/ColorThemeDefAdapter.cs`, `Hosting/HostPaletteExtractionService.cs`,
  `Hosting/PngSlideshowFrameRecorder.cs` into `FracturingFog.App` (or into a new
  `FracturingFog.Hosting` `net10.0` lib both shells reference). Leave
  `Hosting/NativeMouseForwarder.cs` and `Hosting/FfmpegSetupDialog.cs` in a
  Windows-only fragment.
- [ ] Pull `Rendering/FractalRenderHost.cs` + `.Video.cs` + the 13+ calculator types
  (incl. the new `UserBulbGpuCalculator`, sandbox compilers, `MandelbrotGpuKernel.cs`) into a
  new `FracturingFog.Engine` `net10.0` project. Calculators have no UI deps; the move just
  severs them from `net10.0-windows`.
- [ ] Pull `Imaging/` (minus `MP4Writer.cs` and `FfmpegSetupDialog`) into
  `FracturingFog.Engine`. The SkiaSharp swap (Phase X.A) happens inside this project once
  it lives here.
- [ ] Pull `Audio/` into a new `FracturingFog.Audio` `net10.0` project with an
  `IAudioCaptureBackend` abstraction (Phase X.B fills in the OS-specific implementations).
- [ ] Keep `Rendering/DirectXRenderer.cs`, `DirectX12Renderer.cs`, `RenderFactory.cs` in a
  separate `FracturingFog.Rendering.D3D` `net10.0-windows` project (deferred since Phase 2.1;
  do it now). Keep `Imaging/MP4Writer.cs` here too.
- [ ] `FracturingFog.App` references DX + MP4Writer only via a Windows-conditional
  `ProjectReference`:
  ```xml
  <ProjectReference Include="..\Rendering.D3D\FracturingFog.Rendering.D3D.csproj"
                    Condition="'$([MSBuild]::IsOSPlatform(`Windows`))' == 'true'" />
  ```
- [ ] Existing `FracturingFogCLD.csproj` stays as the Windows-legacy WinExe (keeps `MainForm`
  + `Vortice` + `PaletteBuilder.Lib`) so nothing regresses for current users. Mark it
  `Obsolete` in the sln description.
- [ ] sln gains `FracturingFog.App`, `FracturingFog.Engine`, `FracturingFog.Audio`,
  `FracturingFog.Rendering.D3D`, optional `FracturingFog.Hosting`.

**Exit criteria:** `dotnet build FracturingFog.App.csproj -r linux-x64 --self-contained false`
succeeds in CI without referencing any `*-windows` TFM project. Existing
`FracturingFogCLD.csproj` still builds + runs on Windows unchanged.

### Phase X.A — Image I/O SkiaSharp swap (NEW)

**Goal:** strip `System.Drawing` from the engine + headless paths so the cross-platform host
can save PNG/TIFF/BMP, write PNG sequences, render posters, and composite watermark/grid
overlays.

- [ ] `Imaging/ImageExport.cs` → SkiaSharp. `Bitmap.LockBits` BGRA copy becomes
  `SKBitmap.InstallPixels` over the same `uint[]`. PNG/TIFF/BMP write via `SKImage.Encode`.
  Watermark composition via `SKCanvas.DrawText` + outline `SKPath`. `Color fontColor`
  signature swaps to `SKColor` or a UI-neutral RGB struct.
- [ ] `Imaging/PngSequenceWriter.cs` → SkiaSharp. `Bitmap` → `SKBitmap` snapshot per frame;
  `Encode` to disk. Stays threaded with the same semaphore gate. Even-dimension crop logic
  is unchanged.
- [ ] `Imaging/PosterRenderer.cs` → SkiaSharp. The renderer pulls `IColorMap` + calculator
  output; only the save tail uses `Bitmap` today. Swap the tail. Custom watermark composition
  → `SKCanvas`.
- [ ] `Rendering/FractalOverlayCompositor.cs` → SkiaSharp. Used for grid + halo overlays on
  exported frames. Pixel-loop falls back to SkiaSharp draw calls or a managed BGRA blit.
- [ ] `Models/ColorThemeCsExporter.cs` and the `Models/ColorSchemes/**` files: these use
  `System.Drawing.Color` purely as a struct (ARGB packing). Either swap to
  `System.Drawing.Primitives` (which IS cross-platform — only GDI+ types throw) or introduce a
  small `Rgba32` struct in `Abstractions/`. Decide once; prefer the latter so the engine has
  zero `System.Drawing.*` references.
- [ ] Audit `ImageFormat` enum usage in callers — collapse to a string ext + `SKEncodedImageFormat`
  pair at the save site.
- [ ] Remove `<PackageReference Include="System.Drawing.Common">` from any project that ends up
  in the `FracturingFog.App` reference closure.

**Exit criteria:** `FracturingFog.Engine` and `FracturingFog.App` build with `NoWarn=` empty
under `-r linux-x64 --self-contained false`, and the headless `--batch` PNG-sequence + PNG
single-image paths round-trip a render on a Linux CI runner.

### Phase X.B — Audio capture abstraction (NEW)

**Goal:** make the audio-reactive slideshow degrade gracefully on non-Windows without ripping
the feature out, and support file/synth paths on every RID.

- [ ] Introduce `IAudioCaptureBackend` in `Abstractions/Audio/` with the minimum surface the
  `BeatAnalyzer` needs: `Start(AudioFormat)`, `Stop()`, `DataAvailable` event yielding
  `float[]` PCM, plus a `Capabilities` enum (`SystemLoopback | Microphone | FilePlayback |
  SynthPlayback`).
- [ ] Split the current `AudioEngine` into a backend-neutral analyzer driver + per-backend
  source pump. Existing NAudio code becomes `WindowsNAudioBackend` inside a Win-only
  fragment.
- [ ] On Linux/macOS, ship a `NoopAudioBackend` that supports only `FilePlayback` (decoded
  via `NAudio.Core` MP3/WAV — that path *is* cross-platform when the WASAPI bits aren't
  touched) and `SynthPlayback` (synth analyzer-only, no speaker output). System loopback is
  marked unsupported; the audio-source picker grays it out with a one-line banner.
- [ ] Stretch goal: `OpenAlAudioBackend` (`Silk.NET.OpenAL`) for mic + speaker on Linux/macOS.
  Park behind a follow-up; not a launch blocker.
- [ ] Audio-reactive sweep settings remain editable; the running sweep simply receives a
  flat (silent) beat stream on hosts that can't capture.

**Exit criteria:** opening the audio-reactive slideshow on Linux shows the picker without
crashing; choosing "File" plays a local MP3 and drives the beat analyzer; choosing
"System loopback" displays "Not supported on this OS" instead of throwing.

### Phase X.1 — Palette engine demotion

**Goal:** strip Windows-only deps from the palette extraction path so the cross-platform host
can keep `IPaletteExtractionService`.

**Status (2026-06):** SkiaSharp swap landed on `feature/palette-builder-image-pipeline`. The
remaining work is the PDF exporter and the TFM flip.

- [x] `Imaging/PaletteExtraction/BitmapSampler.cs` — `System.Drawing.*` replaced with
  `SkiaSharp` (`SKBitmap`, `SKCodec`, `SKImageInfo`, `SKEncodedOrigin`). Pixel layout forced
  to `SKColorType.Bgra8888`. EXIF orientation via `SKCodec.EncodedOrigin`.
- [x] `PaletteBuilder/Services/PaletteExtractionService.cs` — `_sources: List<SKBitmap>`,
  decode via `SKCodec.GetPixels`. No `System.Drawing` imports remain.
- [x] `Hosting/HostPaletteExtractionService.cs` — same SkiaSharp swap; cache-key now covers
  full filter set + ROI.
- [x] File pickers extended: `.webp`, `.heic`, `.heif`.
- [ ] **Remove `PDFsharp-gdi` + `System.Drawing.Common`** from `PaletteBuilder.Lib.csproj`.
  Currently both survive only to feed `PaletteBuilder/Services/PdfPaletteExporter.cs`. Options:
  - **Swap to `QuestPDF`** *(recommended)* — cross-platform, MIT-friendly with optional
    commercial license. Rewrite `PdfPaletteExporter` against `QuestPDF.Fluent`. Honour the
    existing `PdfExportOptions` (page size, columns, cover page, CVD rows, etc.).
  - **Drop PDF export from the cross-platform host** and keep it in the Windows WinExe wrapper.
- [ ] **Once `PDFsharp-gdi` is gone**, flip `PaletteBuilder/PaletteBuilder.Lib.csproj` TFM from
  `net10.0-windows` → `net10.0`. Verify `PaletteBuilder/PaletteBuilder.csproj` (WinExe
  wrapper) and `FracturingFogCLD.csproj` (Windows host) still resolve the Lib via TFM
  downcompat.
- [ ] Optional split: `PaletteBuilder.Engine` (extractors + BitmapSampler + stop builder)
  carved out from the UI shell so `FracturingFog.App` references only the engine.
- [ ] Once the engine is portable, retire the `GdiToSkia` bridge in
  `Views/ImagePaletteDialog.cs` (deletes with the WinForms shell — same gate as the broader
  WinForms deprecation tail).

**Exit criteria:** `From Image…` palette flow round-trips an input PNG → ColorStopDef list on
Linux + macOS in a manual smoke. `PaletteBuilder.Lib` TFM is `net10.0` and `dotnet build`
succeeds on linux-x64.

### Phase X.2 — Video export portability

**Goal:** MP4 writer either works on every RID or fails gracefully.

- [ ] Introduce `IVideoWriter` in `Abstractions/Imaging/`.
- [ ] Keep current `MP4Writer` (Media Foundation P/Invoke) as `Win32MP4Writer` in the Windows
  fragment.
- [ ] **`Imaging/FfmpegEncoder.cs` already exists** and shells out to ffmpeg via
  `System.Diagnostics.Process`. Wrap it in an `IVideoWriter` adapter — `FfmpegVideoWriter` —
  and make it the default on Linux/macOS (and the Windows default if ffmpeg is present).
  - Detect bundled `Tools/<rid>/ffmpeg` (or `ffmpeg.exe` on Win) first, else fall through to
    `PATH`. Rename the existing `FindFfmpeg()` to look for both `ffmpeg` and `ffmpeg.exe`.
  - On Linux: ship `Tools/linux-x64/ffmpeg` + `Tools/linux-arm64/ffmpeg`; RID-keyed `<None
    Include>` blocks.
  - On macOS: rely on PATH (`brew install ffmpeg`) or bundle a notarized binary.
- [ ] `FfmpegInstaller.cs` becomes OS-aware: Windows downloads the gyan.dev zip as today;
  Linux/macOS surface a "Install via your package manager" instruction with copy-paste
  commands (`apt install ffmpeg` / `brew install ffmpeg`).
- [ ] Bootstrap picks `Win32MP4Writer` on Windows when MF resolves, else `FfmpegVideoWriter`.
  Video tab disables itself with a one-line banner when no writer resolves.
- [ ] `Hosting/FfmpegSetupDialog.cs` rewritten as an Avalonia dialog (or split: Win-only
  WinForms version stays in the legacy WinExe; Avalonia version added to `UI.Avalonia/`).

**Exit criteria:** a 100-frame slideshow export succeeds on Linux via bundled (or PATH)
ffmpeg.

### Phase X.3 — P/Invoke and `IsOSPlatform` gating sweep

- [ ] Audit every `[DllImport]` outside `Rendering.D3D` and `Rendering.Silk/Platform`. Wrap
  the call site in `if (OperatingSystem.IsWindows()) …` and provide a no-op or alternative on
  non-Win.
  - `ServerHost/ServerEntry.cs` `AttachConsole`/`AllocConsole` → no-op on Linux (stdout/stderr
    already inherited).
  - `Batch/BatchEntry.cs` ditto.
  - `Benchmarks/MandelbrotBench.cs` — gate or move under `#if WINDOWS`.
  - `Hosting/NativeMouseForwarder.cs` already gated; move file into the Windows-only host
    fragment so non-Win builds don't even compile it.
  - **NEW:** `UI.Avalonia/Views/MainWindow.axaml.cs` Toy-mode HWND drag — replace
    `ReleaseCapture` + `WM_NCLBUTTONDOWN` with Avalonia's `BeginMoveDrag(PointerPressedEventArgs)`.
    No `[DllImport]` left after the rewrite. (Catches the crash on Linux/macOS Toy-mode
    drag.)
  - **NEW:** `Hosting/AvaloniaShellBootstrap.cs` `ClientToScreen` — gate behind
    `OperatingSystem.IsWindows()`; on non-Win, use Avalonia's `Control.PointToScreen` for the
    inspect-click coordinate.
- [ ] Sweep `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` and switch to
  `OperatingSystem.IsWindows()` (compile-time analyzable, plays nice with trimming).
- [ ] Add `[SupportedOSPlatform("windows")]` attributes to genuinely-Windows-only types so the
  analyzer (`CA1416`) catches drift.

**Exit criteria:** `dotnet build` on Linux emits zero `CA1416` warnings from
`FracturingFog.App`, `Engine`, `Hosting`, `UI.Avalonia`, `Audio`.

### Phase X.4 — Renderer selection + bootstrap polish

- [ ] `AvaloniaShellBootstrap` static ctor: on non-Windows, register `NonWin32Backend = Silk`
  by default with Skia as the documented fallback (`--renderer skia` CLI flag). On Windows,
  keep DX as the default and let `--renderer silk` / `--renderer skia` override for parity
  testing.
- [ ] Verify `IGpuSurface.Kind` covers Win32Hwnd, X11Window, WaylandSurface,
  CAMetalLayer/NSView, with bootstrap diagnostics that name the missing adapter when init
  fails.
- [ ] Settle the macOS adapter: the current `SilkCglContextAdapter` requests a 3.2 core
  profile token (macOS caps; 4.1 in practice). Confirm the Silk fragment shader still compiles
  (`#version 330 core` works under that token).
- [ ] Wayland: the EGL adapter is in but the Linux CI leg still runs the smoke under xvfb
  (X11/GLX). Add a parallel `linux-wayland` smoke that runs the EGL path under `weston
  --backend=headless`.

**Exit criteria:** Avalonia shell paints a fractal on:
1. Win10/11 x64 (DX)
2. Win10/11 x64 with `--renderer silk` (WGL)
3. Ubuntu 22.04 x64 + X11 (GLX)
4. Ubuntu 22.04 x64 + Wayland (EGL)
5. macOS 14 arm64 (CGL)
6. Fedora 39 x64 with `--renderer skia` (no GL driver case)

### Phase X.5 — Compute fallbacks

- [ ] On Apple Silicon, ILGPU has neither CUDA nor OpenCL. Confirm `UserBulbGpuCalculator`,
  the new `UserBulbSandboxGpuCompiler`, and `Rendering/MandelbrotGpuKernel.cs` all fall
  through to the managed CPU device cleanly; add a smoke test asserting the chosen device
  kind on each RID.
- [ ] Document that AVX2/AVX-512 lanes are x64-only at runtime; on osx-arm64 / linux-arm64 the
  scalar path runs. The calculator gating (`Avx2.IsSupported && Fma.IsSupported`) already
  handles this — publish the expectation in user-facing docs (Help → Hardware tab).
- [ ] **Out of scope for this roadmap:** ARM64 NEON SIMD lane in `CalculatorGen`. File as
  `CalculatorGen-Roadmap.md` follow-up.

### Phase X.6 — Packaging + distribution

- [ ] Define publish profiles (`Properties/PublishProfiles/*.pubxml`) for: `win-x64`,
  `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`. All `--self-contained true`,
  `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`.
- [ ] Linux: produce both a tarball and a `.AppImage` (use `appimagetool`). Optional Flatpak
  manifest as a follow-up.
- [ ] macOS: produce `.app` bundle via `dotnet publish` + custom `Info.plist`. Code-signing
  + notarization needs an Apple Developer cert — defer until binary verified.
- [ ] Windows: keep the existing MSIX/zip flow (no change required since the legacy WinExe
  still ships).
- [ ] CI: extend `cross-platform-build.yml` to publish + upload artifacts on tag pushes.

### Phase X.7 — Documentation + UX

- [ ] Update `FEATURES.md` with platform matrix table.
- [ ] Add a `Help → Hardware` tab section listing detected backend (DX / Silk / Skia), SIMD
  width, ILGPU device list, audio backend — most of this exists, verify labels match new
  selection logic.
- [ ] `README.md`: install instructions per OS.
- [ ] `Docs/User/CrossPlatform-UserGuide.md` (new): known limitations per OS — Wayland +
  NVIDIA proprietary driver caveats, macOS notarization status, Linux video export ffmpeg
  requirement, audio-reactive loopback gap.

---

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Vortice DX leaks into Engine via implicit ref | Med | Audit `Engine.csproj`'s transitive deps; add CI assertion that `Engine.dll` has no `runtime.win-*` natives in its publish folder. |
| `System.Drawing.Common` 8+ throws on non-Win unless explicit opt-in | High | Phase X.A removes it entirely from the engine surface; do not opt in via runtimeconfig. |
| SkiaSharp watermark text differs visually from GDI+ output on existing posters | Med | A/B render a fixed test poster pre- and post-swap; allow ±2 px metric drift but no missing glyphs. Pin SkiaSharp font fallback to Inter (already bundled via Avalonia). |
| NAudio cross-platform claim doesn't survive WASAPI loopback removal | High | Audit which NAudio assemblies actually load on Linux at runtime; design the IAudioCaptureBackend so the WASAPI path is gated behind `OperatingSystem.IsWindows()` and never reached on other RIDs. |
| Avalonia 11.3.x XAML compiler regression on macOS arm64 | Low | Pin Avalonia version; CI runs `dotnet publish` per RID and exercises one window-open smoke per OS. |
| Silk.NET GL ctx flakes on hybrid GPU laptops (Linux) | Med | Bootstrap falls back to Skia CPU renderer with a banner; document `--renderer skia` escape hatch. |
| ILGPU CPU device perf cliff vs CUDA path | Low | Calculator already exposes scalar/AVX2/AVX-512 lanes; CPU device path is for parity, not perf parity. |
| ffmpeg licensing on bundled Linux binary | Med | Ship system-ffmpeg lookup first; only bundle if licensing audit clears. |
| Avalonia BeginMoveDrag on Linux/Wayland tied to seat focus loss bugs (compositor-specific) | Low | Acceptable degradation: if BeginMoveDrag throws, swallow and log; Toy-mode loses drag on broken compositors but doesn't crash. |

---

## Definition of done

A user on a fresh Ubuntu 22.04, Fedora 39, macOS 14 arm64, and Windows 11 x64 install can:
1. Download a single self-contained archive for their OS.
2. Launch `FracturingFog` (no `dotnet runtime` install required).
3. See an interactive Mandelbrot render within 5 seconds.
4. Pan/zoom, switch fractal type, switch colour theme, export PNG.
5. Export an MP4 zoom video (Win + Linux must; macOS allowed to defer if ffmpeg notarisation
   slips).
6. Open the audio-reactive slideshow dialog without crashing. System-loopback option is
   greyed out on Linux/macOS with a banner; file-playback and synth options work everywhere.

Anything beyond that (deep-zoom perturbation parity, GPU path on Apple Silicon, perfect HiDPI
on every Linux compositor, OpenAL system-audio capture) is follow-up, not launch.
