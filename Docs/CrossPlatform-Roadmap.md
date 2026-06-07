# Cross-Platform Roadmap

**Branch baseline:** `feature/phase2-avalonia-ui` (Phase 2 Avalonia migration mostly complete).
**Goal:** ship a runnable FracturingFog binary on **win-x64**, **linux-x64**, **linux-arm64**, **osx-arm64**, **osx-x64**.
**Non-goal (this doc):** mobile, Wasm browser host, touch UI, GPU compute parity on Apple Silicon Metal.

This file is a forward-looking checklist; it does not retell Phase 2 history (see `PHASE2_AVALONIA_MIGRATION.md`). It picks up after Phase 2.4 (Silk + Skia backends already merged, CI matrix green for the platform-neutral assemblies) and lists what still blocks an end-user launching the full app on a non-Windows host.

---

## Current state (snapshot)

What already works on Linux/macOS:
- `Abstractions`, `UI.Avalonia`, `Rendering.Silk`, `Rendering.Skia` build + publish under win-x64 / linux-x64 / osx-arm64 in CI.
- `Rendering.Silk.Smoke` renders one frame on win/linux/macOS (Linux uses xvfb; macOS uses invisible-window FBO).
- Silk context adapters present: WGL (Win32), GLX (X11), EGL (Wayland), CGL/NSOpenGL (macOS).
- ILGPU `AcceleratorProbe` exposes CPU fallback when no CUDA/OpenCL device exists.
- `RendererFactory.NonWin32Backend` slot lets the bootstrap pick Silk or Skia on non-HWND surfaces.

What still pins the shipped binary to Windows x64:
| Blocker | File / artefact | Impact |
|---|---|---|
| WinExe TFM = `net10.0-windows` + `UseWindowsForms=true` | `FracturingFogCLD.csproj` | Whole exe refuses to build off Windows. |
| Legacy `MainForm` + `Views/*.cs` still compiled into the WinExe | `MainForm.cs`, `Views/**`, `Slideshow.cs`, `VideoZoom.cs` | WinForms types drag the `-windows` TFM along with them. |
| PaletteBuilder.Lib `net10.0-windows` + `PDFsharp-gdi` (PDF exporter only) | `PaletteBuilder/PaletteBuilder.Lib.csproj`, `PaletteBuilder/Services/PdfPaletteExporter.cs` | Decode + sampling now SkiaSharp; only `PdfPaletteExporter` still drags GDI through `PDFsharp-gdi`. Drop or swap to QuestPDF → Lib lands on `net10.0`. |
| WinForms `ImagePaletteDialog` keeps a `System.Drawing.Bitmap` for `PictureBox` display | `Views/ImagePaletteDialog.cs` | Bridges to `SKBitmap` at the sampler boundary (deprecation-tail dialog). Goes away when WinForms shell is removed; not on the cross-platform critical path. |
| Vortice DX11/12 hard-referenced in WinExe | `FracturingFogCLD.csproj` `<PackageReference>` block | Brings `runtime.win-x64.*` natives; ref'd by DX renderer classes that live in the WinExe today. |
| MP4 export via Media Foundation P/Invoke | `Imaging/MP4Writer.cs` | `mfplat.dll` / `mfreadwrite.dll` are Win-only. |
| `NativeMouseForwarder` HWND subclass (`comctl32`, `user32`) | `Hosting/NativeMouseForwarder.cs` | Already guarded by `OSPlatform.Windows`, but file still lives in the host. |
| Console attach P/Invoke in batch + server entry points | `ServerHost/ServerEntry.cs`, `Batch/BatchEntry.cs` | `kernel32!AttachConsole`/`AllocConsole`. Cosmetic on non-Windows but currently unguarded. |
| `RuntimeInformation.IsOSPlatform` checks scattered ad-hoc | `Hosting/HostHelpContentProvider.cs`, `MainForm.cs`, etc. | Inconsistent fallbacks; some still throw on non-Win. |
| `BenchmarkDotNet` pulls `kernel32` P/Invokes via `Benchmarks/MandelbrotBench.cs` | `Benchmarks/MandelbrotBench.cs` | Dev-only; safe to gate but currently in main project. |
| Tools/ffmpeg.exe copied to output | `FracturingFogCLD.csproj` `<Tools/ffmpeg.exe>` | Windows binary in output dir; need RID-keyed copy. |
| ILGPU CUDA path assumes NVIDIA driver | `Calculators/*GpuCalculator*.cs` | Already falls back to CPU device, but needs verification on Apple Silicon (no OpenCL on macOS 14+, CUDA absent). |

---

## Phase order

### Phase X.0 — Project geometry split (prereq for everything else)

**Goal:** introduce a separate cross-platform exe target without breaking the existing Windows WinExe.

- [ ] Add `FracturingFog.App` (`net10.0`, `WinExe` on Win / `Exe` on other RIDs) as the new cross-platform entry point. References: `Abstractions`, `UI.Avalonia`, `Rendering.Silk`, `Rendering.Skia`, `Server`, `Client`, `CalculatorGen`, `ColorGen`. No WinForms, no Vortice, no `System.Drawing.Common`, no `PaletteBuilder.Lib`.
- [ ] Move `Hosting/AvaloniaShellBootstrap.cs`, `Hosting/AvaloniaDialogs.cs`, `Hosting/HostColorThemeService.cs`, `Hosting/HostHelpContentProvider.cs`, `Hosting/ColorThemeDefAdapter.cs`, `Hosting/HostPaletteExtractionService.cs` into `FracturingFog.App` (or into a new `FracturingFog.Hosting` `net10.0` lib both shells reference).
- [ ] Pull `Rendering/FractalRenderHost.cs` + `.Video.cs` + the 11 calculator types into a new `FracturingFog.Engine` `net10.0` project. Calculators have no UI deps already; the move just severs them from `net10.0-windows`. Keep `Rendering/DirectXRenderer.cs`, `DirectX12Renderer.cs`, `RenderFactory.cs` in a separate `FracturingFog.Rendering.D3D` `net10.0-windows` project (deferred since Phase 2.1; do it now).
- [ ] `FracturingFog.App` references DX only via a Windows-conditional `ProjectReference`:
  ```xml
  <ProjectReference Include="..\Rendering.D3D\FracturingFog.Rendering.D3D.csproj"
                    Condition="'$(TargetFramework)' == 'net10.0-windows' OR $([MSBuild]::IsOSPlatform('Windows'))" />
  ```
- [ ] Existing `FracturingFogCLD.csproj` stays as the Windows-legacy WinExe (keeps `MainForm` + `Vortice` + `PaletteBuilder.Lib`) so nothing regresses for current users. Mark it `Obsolete` in the sln description.
- [ ] sln gains `FracturingFog.App`, `FracturingFog.Engine`, `FracturingFog.Rendering.D3D`, optional `FracturingFog.Hosting`.

**Exit criteria:** `dotnet build FracturingFog.App.csproj -r linux-x64 --self-contained false` succeeds in CI without referencing any `*-windows` TFM project.

### Phase X.1 — Palette engine demotion

**Goal:** strip Windows-only deps from the palette extraction path so the cross-platform host can keep `IPaletteExtractionService`.

**Status (2026-06):** SkiaSharp swap landed on `feature/palette-builder-image-pipeline`. The image-pipeline GDI surface is now narrow enough to be tackled with surgical follow-up work — see "Remaining GDI usage" below for the exact map.

- [x] `Imaging/PaletteExtraction/BitmapSampler.cs` — every `System.Drawing.*` reference replaced with `SkiaSharp` (`SKBitmap`, `SKCodec`, `SKImageInfo`, `SKEncodedOrigin`). Pixel layout forced to `SKColorType.Bgra8888` so the BGRA byte iteration in `ExtractPixels` is unchanged. EXIF orientation flows through `SKCodec.EncodedOrigin` rather than the GDI `PropertyItem` path; the prior 1..8 rotate/flip switch ports 1:1 because `SKEncodedOrigin` numeric values match the EXIF tag.
- [x] `PaletteBuilder/Services/PaletteExtractionService.cs` — `_sources: List<SKBitmap>`, decode via `SKCodec.GetPixels`. No `System.Drawing` imports remain. Public `IPaletteExtractionService` surface is byte-identical so downstream extractors (`KMeans*` / `MedianCut` / `Octree` / `Histogram` / `Wu` / `MiniBatchKMeans` / `Material` / `MeanShift` / `Dbscan` / `Gmm` / `SpatialKMeans`) are untouched.
- [x] `Hosting/HostPaletteExtractionService.cs` — same SkiaSharp swap; same cache-key hardening (key now covers full filter set + ROI so cached pixels invalidate when any extraction option moves).
- [x] File pickers extended: `.webp`, `.heic`, `.heif` added to `PaletteBuilder/Views/MainWindow.axaml.cs` patterns + folder enumeration.
- [ ] **Remove `PDFsharp-gdi` + `System.Drawing.Common`** from `PaletteBuilder.Lib.csproj`. Currently both packages survive only to feed `PaletteBuilder/Services/PdfPaletteExporter.cs` (the one remaining `using PdfSharp.Drawing;` consumer). Two options:
  - **Drop PDF export from the cross-platform host** and keep it in the Windows WinExe wrapper.
  - **Swap to `QuestPDF`** — cross-platform, MIT-friendly with optional commercial license. Rewrite `PdfPaletteExporter` against `QuestPDF.Fluent`. Honour the existing `PdfExportOptions` (page size, columns, cover page, CVD rows, etc.).
- [ ] **Once `PDFsharp-gdi` is gone**, flip `PaletteBuilder/PaletteBuilder.Lib.csproj` TFM from `net10.0-windows` → `net10.0`. Verify `PaletteBuilder/PaletteBuilder.csproj` (WinExe wrapper) and `FracturingFogCLD.csproj` (Windows host) still resolve the Lib via TFM downcompat — both windows-targeted parents can ref a `net10.0` Lib unchanged.
- [ ] Optional split: `PaletteBuilder.Engine` (`net10.0`, extractors + BitmapSampler + stop builder) carved out from the UI shell so `FracturingFog.App` references only the engine. Low priority once the TFM lands on `net10.0`.
- [ ] Once the engine is portable, retire the `GdiToSkia` bridge in `Views/ImagePaletteDialog.cs` (deletes with the WinForms shell — same gate as the broader WinForms deprecation tail).

**Exit criteria:** `From Image…` palette flow round-trips an input PNG → ColorStopDef list on Linux + macOS in a manual smoke. `PaletteBuilder.Lib` TFM is `net10.0` and `dotnet build` succeeds on linux-x64.

#### Remaining GDI usage (post-SkiaSharp swap)

| Site | Symbol | Why it's still GDI | Notes |
|---|---|---|---|
| `PaletteBuilder/Services/PdfPaletteExporter.cs` | `using PdfSharp.Drawing` (XGraphics, XPdfFontOptions, etc.) | `PdfSharp-gdi` 6.2.4 dependency; binds the whole Lib to `net10.0-windows`. | Swap to `QuestPDF` *or* drop PDF export from cross-platform Lib. Sole reason `PaletteBuilder.Lib.csproj` still keeps `<TargetFramework>net10.0-windows</TargetFramework>`. |
| `Views/ImagePaletteDialog.cs` | `private Bitmap? _sourceImage`, `PictureBox`, `LockBits`/`PixelFormat`, `Image.FromFile` | WinForms shell, deprecated per `CLAUDE.md`. Kept buildable; `GdiToSkia` helper bridges to the new `SKBitmap` sampler at the call boundary. | Goes away with the rest of the WinForms shell; not a cross-platform blocker. |
| `Hosting/HostPaletteExtractionService.cs` | None — was `Bitmap _source`, now `SKBitmap _source`. | — | Done. Cache key now covers the full filter set; ROI changes invalidate cleanly. |
| `Imaging/PaletteExtraction/BitmapSampler.cs` | None — was `using System.Drawing.*`, now `using SkiaSharp;`. | — | Done. `ApplyExifOrientation(Bitmap)` retired; `ApplyOrigin(SKBitmap, SKEncodedOrigin)` ported the 1..8 switch verbatim. |
| `Imaging/MP4Writer.cs` | Media Foundation P/Invoke | Separate concern (Phase X.2); never used GDI. | — |
| `Imaging/ImageCapture.cs` | `System.Drawing.Bitmap`, `Graphics.CopyFromScreen` | Windows screen-capture path; unrelated to PaletteBuilder. | Phase X.0 splits this out into a Windows-only fragment. |

#### Why the SkiaSharp swap was scoped this way

- `SkiaSharp 3.119.4` is already shipping via `Rendering.Skia.csproj`. Adding it to `PaletteBuilder.Lib.csproj` reuses Avalonia's transitive native (`libSkiaSharp.{so,dylib,dll}`) — no new RID-specific binaries land in publish output.
- Forcing `SKColorType.Bgra8888 + SKAlphaType.Premul` everywhere preserved the existing `b, g, r, a` byte iteration in `ExtractPixels` so the swap is bit-identical to the prior GDI path for palette output (verified by visual A/B on a smoke set).
- Webp / HEIC decode is "free" via `SKCodec` — no per-format branches needed in the loader.
- EXIF orientation values 1..8 are identical between `SKCodec.EncodedOrigin` and the EXIF tag, so the orientation switch is unchanged. TIFF parity is acceptable-with-caveat: `SKCodec.EncodedOrigin` returns `Default` for some TIFFs, matching the prior `System.Drawing.GetPropertyItem` reliability for the same format.

### Phase X.2 — Video export portability

**Goal:** MP4 writer either works on every RID or fails gracefully.

- [ ] Introduce `IVideoWriter` in `Abstractions/Imaging/`.
- [ ] Keep current `MP4Writer` (Media Foundation P/Invoke) as `Win32MP4Writer` in a Windows-conditional sub-project.
- [ ] Add `FfmpegMP4Writer` that shells out to `ffmpeg` via `System.Diagnostics.Process` for non-Windows hosts. Strategy:
  - Detect bundled `Tools/ffmpeg` (or `ffmpeg.exe` on Win) first, else fall through to `PATH`.
  - On Linux: ship `Tools/linux-x64/ffmpeg` + `Tools/linux-arm64/ffmpeg`; flagged via `RuntimeIdentifier` `<None Include>` blocks.
  - On macOS: rely on PATH (`brew install ffmpeg`) or bundle a notarized binary.
- [ ] Bootstrap picks `Win32MP4Writer` on Windows when available, else `FfmpegMP4Writer`. Video tab disables itself with a one-line banner when no writer resolves.

**Exit criteria:** a 100-frame slideshow export succeeds on Linux via bundled ffmpeg.

### Phase X.3 — P/Invoke and `IsOSPlatform` gating sweep

- [ ] Audit every `[DllImport]` outside `Rendering.D3D` and `Rendering.Silk/Platform`. Wrap the call site in `if (OperatingSystem.IsWindows()) …` and provide a no-op or alternative on non-Win.
  - `ServerHost/ServerEntry.cs` `AttachConsole`/`AllocConsole` → no-op on Linux (stdout/stderr already inherited).
  - `Batch/BatchEntry.cs` ditto.
  - `Benchmarks/MandelbrotBench.cs` — gate or move under `#if WINDOWS`.
  - `Hosting/NativeMouseForwarder.cs` already gated; move file into the Windows-only host fragment so non-Win builds don't even compile it.
- [ ] Sweep `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` and switch to `OperatingSystem.IsWindows()` (compile-time analyzable, plays nice with trimming).
- [ ] Add `[SupportedOSPlatform("windows")]` attributes to genuinely-Windows-only types so the analyzer (`CA1416`) catches drift.

**Exit criteria:** `dotnet build` on Linux emits zero `CA1416` warnings from `FracturingFog.App`, `Engine`, `Hosting`.

### Phase X.4 — Renderer selection + bootstrap polish

- [ ] `AvaloniaShellBootstrap` static ctor: on non-Windows, register `NonWin32Backend = Silk` by default with Skia as the documented fallback (`--renderer skia` CLI flag). On Windows, keep DX as the default and let `--renderer silk` / `--renderer skia` override for parity testing.
- [ ] Verify `IGpuSurface.Kind` covers Win32Hwnd, X11Window, WaylandSurface, CAMetalLayer/NSView, with bootstrap diagnostics that name the missing adapter when init fails.
- [ ] Settle the macOS adapter: the current `SilkCglContextAdapter` requests a 3.2 core profile token (macOS caps; 4.1 in practice). Confirm the Silk fragment shader still compiles (`#version 330 core` works under that token).
- [ ] Wayland: the EGL adapter is in but the Linux CI leg still runs the smoke under xvfb (X11/GLX). Add a parallel `linux-wayland` smoke that runs the EGL path under `weston --backend=headless`.

**Exit criteria:** Avalonia shell paints a fractal on:
1. Win10/11 x64 (DX)
2. Win10/11 x64 with `--renderer silk` (WGL)
3. Ubuntu 22.04 x64 + X11 (GLX)
4. Ubuntu 22.04 x64 + Wayland (EGL)
5. macOS 14 arm64 (CGL)
6. Fedora 39 x64 with `--renderer skia` (no GL driver case)

### Phase X.5 — Compute fallbacks

- [ ] On Apple Silicon, ILGPU has neither CUDA nor OpenCL. Confirm `UserBulbGpuCalculator` falls through to the managed CPU device cleanly; add a smoke test asserting the chosen device kind on each RID.
- [ ] Document that AVX2/AVX-512 lanes are x64-only at runtime; on osx-arm64 / linux-arm64 the scalar path runs. The calculator gating (`Avx2.IsSupported && Fma.IsSupported`) already handles this — just publish the expectation in user-facing docs (Help → Hardware tab).
- [ ] Add ARM64 NEON SIMD lane to `CalculatorGen` emitters? **Out of scope for this roadmap** — file as `CalculatorGen-Roadmap.md` follow-up, not a blocker for X-platform launch.

### Phase X.6 — Packaging + distribution

- [ ] Define publish profiles (`Properties/PublishProfiles/*.pubxml`) for: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`. All `--self-contained true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`.
- [ ] Linux: produce both a tarball and a `.AppImage` (use `appimagetool`). Optional Flatpak manifest as a follow-up.
- [ ] macOS: produce `.app` bundle via `dotnet publish` + custom `Info.plist`. Code-signing + notarization needs an Apple Developer cert — defer until binary verified.
- [ ] Windows: keep the existing MSIX/zip flow (no change required since the legacy WinExe still ships).
- [ ] CI: extend `cross-platform-build.yml` to publish + upload artifacts on tag pushes.

### Phase X.7 — Documentation + UX

- [ ] Update `FEATURES.md` with platform matrix table.
- [ ] Add a `Help → Hardware` tab section listing detected backend (DX / Silk / Skia), SIMD width, and ILGPU device list — most of this exists, verify the labels match the new selection logic.
- [ ] `README.md`: install instructions per OS.
- [ ] `Docs/CrossPlatform-UserGuide.md` (new): known limitations per OS — e.g. Wayland + NVIDIA proprietary driver caveats, macOS notarization status, Linux video export ffmpeg requirement.

---

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Vortice DX leaks into Engine via implicit ref | Med | Audit `Engine.csproj`'s transitive deps; add CI assertion that `Engine.dll` has no `runtime.win-*` natives in its publish folder. |
| `System.Drawing.Common` 8+ throws on non-Win unless explicit opt-in | High | Phase X.1 removes it entirely; do not opt in via runtimeconfig. |
| Avalonia 11.3.x XAML compiler regression on macOS arm64 | Low | Pin Avalonia version; CI runs `dotnet publish` per RID and exercises one window-open smoke per OS. |
| Silk.NET GL ctx flakes on hybrid GPU laptops (Linux) | Med | Bootstrap falls back to Skia CPU renderer with a banner; document `--renderer skia` escape hatch. |
| ILGPU CPU device perf cliff vs CUDA path | Low | Calculator already exposes scalar/AVX2/AVX-512 lanes; CPU device path is for parity, not perf parity. |
| ffmpeg licensing on bundled Linux binary | Med | Ship system-ffmpeg lookup first; only bundle if licensing audit clears. |

---

## Definition of done

A user on a fresh Ubuntu 22.04, Fedora 39, macOS 14 arm64, and Windows 11 x64 install can:
1. Download a single self-contained archive for their OS.
2. Launch `FracturingFog` (no `dotnet runtime` install required).
3. See an interactive Mandelbrot render within 5 seconds.
4. Pan/zoom, switch fractal type, switch colour theme, export PNG.
5. Export an MP4 zoom video (Win + Linux must; macOS allowed to defer if ffmpeg notarisation slips).

Anything beyond that (deep-zoom perturbation parity, GPU path on Apple Silicon, perfect HiDPI on every Linux compositor) is follow-up, not launch.
