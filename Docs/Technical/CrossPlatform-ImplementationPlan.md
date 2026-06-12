# Cross-Platform Implementation Plan

> Companion: [Cross-Platform Roadmap](CrossPlatform-Roadmap.md) · [Technical Index](_Index.md) · [Architecture Overview](Architecture-Overview.md)

> **Created 2026-06-12** on branch `feature/cross-platform-full` (forked from
> `main` @ `b8c7312`). This document is the execution plan for the phases listed in
> `CrossPlatform-Roadmap.md`; the roadmap is the "what" and "why", this is the "how"
> and "in what order".

---

## Branch + commit strategy

- **Tracking branch:** `feature/cross-platform-full`. All slices below land on this branch
  via small commits with the slice number in the subject (e.g. `XPlat S0.1 — add
  FracturingFog.Engine csproj`).
- **PR cadence:** open a PR per phase (X.0, X.A, X.B, …) once the phase exit criteria are
  green in CI. Slices land directly on the tracking branch; the PR is the rollup.
- **WinExe regression guard:** every slice must keep `dotnet build FracturingFogCLD.csproj`
  green on Windows. The legacy WinExe is the production binary today; nothing here
  removes it until the new App reaches parity in a later branch.
- **CI:** `.github/workflows/cross-platform-build.yml` already runs on `feature/**`
  branches. Each slice that adds a new csproj or moves files adds the corresponding
  `dotnet build -r <rid>` step to the matrix.

---

## Phase X.0 — Project geometry split

> **Exit:** `dotnet build FracturingFog.App.csproj -r linux-x64` succeeds on a
> Linux CI runner. The legacy WinExe still builds and runs unchanged on Windows.

### Slice 0.1 — Carve `FracturingFog.Engine` (`net10.0`)

**Files to add:**
- `Engine/FracturingFog.Engine.csproj` — `<TargetFramework>net10.0</TargetFramework>`,
  `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, ProjectReferences to `Abstractions`,
  `CalculatorGen`, `ColorGen`.

**Files to move (git mv) into `Engine/`:**
- `Rendering/FractalRenderHost.cs`, `Rendering/FractalRenderHost.Video.cs`
- `Rendering/MandelbrotGpuKernel.cs` (new GPU compute)
- `Rendering/PerfStats.cs`
- `Rendering/FractalOverlayCompositor.cs` (will be SkiaSharp-swapped in Phase X.A)
- `Calculators/**` (all of them — they have no UI deps)
- `Models/**` (the engine-side model types; ColorScheme files that reference
  `System.Drawing.Color` need the swap from Phase X.A, but Phase X.0 just moves them
  and the build will fail with CS0246 — fix in X.A)
- `Math/**`
- `Interefaces/**` (typo'd folder, leave the name to minimise churn)

**`FracturingFogCLD.csproj` change:** delete the in-tree compile glob and add
`<ProjectReference Include="..\Engine\FracturingFog.Engine.csproj" />`. The
strip-globbing pattern (`<Compile Remove="…\**" />`) already in the csproj makes this a
near-mechanical edit.

**Validation:** `dotnet build FracturingFogCLD.csproj` on Windows. Worktree dirty state
in the legacy WinExe is OK; expect only ref-resolution errors that fix themselves once
all calc + model files are inside `Engine/`.

### Slice 0.2 — Carve `FracturingFog.Audio` (`net10.0`)

**Files to add:**
- `Audio/FracturingFog.Audio.csproj` — `<TargetFramework>net10.0</TargetFramework>`,
  references `Abstractions`. **Does not reference NAudio** at csproj level; the NAudio
  backend lands in a sibling Win-only project (Slice 0.5 / Phase X.B).
- `Abstractions/Audio/IAudioCaptureBackend.cs` — minimum surface (Start/Stop/DataAvailable
  event + Capabilities enum).
- `Abstractions/Audio/AudioFormat.cs` — sample rate, channels, bit depth.

**Files to move:**
- `Audio/AudioEngine.cs` — split into backend-neutral driver (`AudioCaptureDriver` in
  `Audio/`) + the NAudio-specific source pump (moves to a new `Audio.Win/` project in
  Slice 0.5). For Phase X.0, the move is mechanical; the backend abstraction is
  introduced in Phase X.B.
- `Audio/BeatAnalyzer.cs` — uses `NAudio.Dsp.Fft`. Either swap to `MathNet.Numerics`
  (already referenced by PaletteBuilder.Lib) or copy the tiny `Fft.Forward` we need
  into `Audio/Dsp.cs`. Defer the swap to X.B; for X.0 keep the NAudio reference
  (the project is Win-only until X.B finishes).
- `Audio/FractalSynth.cs` — uses `NAudio.Wave.WaveFormat` + `ISampleProvider`. Same
  treatment: keep NAudio ref temporarily.
- `Audio/AudioSettingsStore.cs` — POCO, no NAudio dep. Moves clean.

**Temporary TFM:** because the move keeps NAudio refs, the new `Audio.csproj` will
target `net10.0-windows` for one slice. Phase X.B flips it to `net10.0`.

### Slice 0.3 — Carve `FracturingFog.Hosting` (`net10.0`)

**Files to add:**
- `Hosting/FracturingFog.Hosting.csproj` — `<TargetFramework>net10.0</TargetFramework>`.
  References `Abstractions`, `Engine`, `UI.Avalonia`, `Rendering.Silk`, `Rendering.Skia`.

**Files to move:**
- `Hosting/AvaloniaShellBootstrap.cs`, `Hosting/AvaloniaDialogs.cs`
- `Hosting/HostColorThemeService.cs`, `Hosting/HostHelpContentProvider.cs`
- `Hosting/ColorThemeDefAdapter.cs`, `Hosting/HostPaletteExtractionService.cs`
- `Hosting/PngSlideshowFrameRecorder.cs`

**Files that stay in the Windows fragment (Slice 0.5):**
- `Hosting/NativeMouseForwarder.cs` (comctl32 subclass)
- `Hosting/FfmpegSetupDialog.cs` (WinForms dialog — until Phase X.2 rewrites it)

### Slice 0.4 — Carve `FracturingFog.Rendering.D3D` (`net10.0-windows`)

**Files to add:**
- `Rendering.D3D/FracturingFog.Rendering.D3D.csproj` —
  `<TargetFramework>net10.0-windows</TargetFramework>`, `<UseWindowsForms>false</UseWindowsForms>`.
  References `Abstractions`, `Engine`, **all Vortice packages** (moved out of
  `FracturingFogCLD.csproj`).

**Files to move:**
- `Rendering/DirectXRenderer.cs`, `Rendering/DirectX12Renderer.cs`,
  `Rendering/RendererFactory.cs`
- `Imaging/MP4Writer.cs` (Media Foundation P/Invoke — also Win-only by construction)

### Slice 0.5 — Create the Windows-only fragment + add `FracturingFog.App`

**New: `FracturingFog.Win/FracturingFog.Win.csproj` (`net10.0-windows`)** holding the
remaining Windows-only-but-not-WinForms surface:
- `Hosting/NativeMouseForwarder.cs`
- `Audio.Win/NAudioCaptureBackend.cs` (extracted from old AudioEngine)

**New: `FracturingFog.App/FracturingFog.App.csproj`** — the cross-platform exe.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>FracturingFog</AssemblyName>
    <RootNamespace>FracturingFog</RootNamespace>
    <ApplicationIcon>..\Resources\FracturingFog.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Abstractions\FracturingFog.Abstractions.csproj" />
    <ProjectReference Include="..\Engine\FracturingFog.Engine.csproj" />
    <ProjectReference Include="..\Audio\FracturingFog.Audio.csproj" />
    <ProjectReference Include="..\Hosting\FracturingFog.Hosting.csproj" />
    <ProjectReference Include="..\UI.Avalonia\FracturingFog.UI.Avalonia.csproj">
      <ExcludeAssets>analyzers</ExcludeAssets>
    </ProjectReference>
    <ProjectReference Include="..\Rendering.Silk\FracturingFog.Rendering.Silk.csproj" />
    <ProjectReference Include="..\Rendering.Skia\FracturingFog.Rendering.Skia.csproj" />
    <ProjectReference Include="..\Server\FracturingFog.Server.csproj" />
    <ProjectReference Include="..\Client\FracturingFog.Client.csproj" />
    <ProjectReference Include="..\CalculatorGen\CalculatorGen.csproj" />
    <ProjectReference Include="..\ColorGen\ColorGen.csproj" />
  </ItemGroup>
  <!-- Windows-only project refs (DX + MP4Writer + NAudio backend + native mouse forwarder) -->
  <ItemGroup Condition="'$([MSBuild]::IsOSPlatform(`Windows`))' == 'true'">
    <ProjectReference Include="..\Rendering.D3D\FracturingFog.Rendering.D3D.csproj" />
    <ProjectReference Include="..\FracturingFog.Win\FracturingFog.Win.csproj" />
    <ProjectReference Include="..\Audio.Win\FracturingFog.Audio.Win.csproj" />
    <!-- PaletteBuilder.Lib still net10.0-windows in Phase X.0; pulled in unconditionally
         after Phase X.1 flips its TFM. -->
    <ProjectReference Include="..\PaletteBuilder\PaletteBuilder.Lib.csproj">
      <ExcludeAssets>analyzers</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

**`Program.cs` move:** the entry point moves into `FracturingFog.App/Program.cs`. The
benchmark / saprobe / gentest CLI arms come with it. The `--winforms` arm stays in
`FracturingFogCLD.csproj` (the legacy WinExe keeps its own entry point so existing
users running the old `.exe` see no behaviour change).

### Slice 0.6 — Wire the solution + CI matrix

- `FracturingFogCLD.sln` adds: `FracturingFog.App`, `FracturingFog.Engine`,
  `FracturingFog.Audio`, `FracturingFog.Audio.Win`, `FracturingFog.Hosting`,
  `FracturingFog.Rendering.D3D`, `FracturingFog.Win`.
- `cross-platform-build.yml` adds:
  - On every leg: `dotnet build FracturingFog.App -r ${{ matrix.rid }} --self-contained false`
  - On every leg: `dotnet build FracturingFog.Engine -r ${{ matrix.rid }}`
  - On every leg: `dotnet build FracturingFog.Hosting -r ${{ matrix.rid }}`
  - Windows leg only: `dotnet build FracturingFog.Rendering.D3D`, `FracturingFog.Win`,
    `FracturingFog.Audio.Win`
- Solution filter `FracturingFog-XPlat.slnf` listing only the cross-platform projects
  (so Rider / VS can open the slim graph on macOS/Linux without choking on the
  `*-windows` TFM projects).

---

## Phase X.A — Image I/O SkiaSharp swap

> **Exit:** `FracturingFog.Engine` and `FracturingFog.App` build under
> `-r linux-x64 --self-contained false` with zero `System.Drawing` references, and the
> headless `--batch` PNG flow round-trips on Linux CI.

### Slice A.1 — `Abstractions/Imaging/Rgba32` + `IImageFormat` enum

Introduce a tiny `Rgba32` struct (4-byte BGRA pack identical to the existing `uint`
buffers — but typed) and an `IImageFormat` enum (`Png | Jpeg | Tiff | Bmp | Webp`).
All call sites currently pass `ImageFormat.Png` etc. via `System.Drawing.Imaging`;
swap to the new enum and adapt at the encode site.

### Slice A.2 — `Imaging/ImageExport.cs` → SkiaSharp

- Add `<PackageReference Include="SkiaSharp" Version="3.119.4" />` to
  `FracturingFog.Engine.csproj`. (Already shipping via Avalonia + Rendering.Skia, so no
  new RID-specific native lands.)
- `SavePixelsToFile` becomes:
  ```csharp
  using var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
  using var bitmap = new SKBitmap(info);
  unsafe {
      fixed (uint* src = pixels) {
          Buffer.MemoryCopy(src, (void*)bitmap.GetPixels(), w*h*4, w*h*4);
      }
  }
  if (dpi > 0f) { /* no SKBitmap DPI; SKDocument or PNG pHYs chunk via SkiaSharp.SKData */ }
  using var image = SKImage.FromBitmap(bitmap);
  using var data = image.Encode(SkiaFormat(format), quality: 100);
  using var fs = File.OpenWrite(path);
  data.SaveTo(fs);
  ```
- Watermark composition: load the bundled Inter font (already shipping via
  `Avalonia.Fonts.Inter`) via `SKTypeface.FromFamilyName("Inter")` with a fallback chain.
  Outline = `SKPaint { Style=Stroke, StrokeWidth=2 }`; fill = `SKPaint { Style=Fill }`.
- Contrast colour sampler: keep the pixel-loop math as-is; only the `Color` type changes
  (`Rgba32` from Slice A.1).
- DPI metadata: PNG's `pHYs` chunk is reachable via `SKPngEncoderOptions.zLibLevel` plus a
  manual pixel-density write. For TIFF/BMP, SkiaSharp doesn't expose DPI directly — accept
  this as a feature gap on the cross-platform path (Windows path via `MP4Writer`-adjacent
  GDI keeps DPI; cross-platform PNG keeps it via pHYs; TIFF/BMP DPI degrades to "the saved
  file declares 96 DPI").

### Slice A.3 — `Imaging/PngSequenceWriter.cs` → SkiaSharp

- `Bitmap` ctor + `LockBits` block becomes `SKBitmap.InstallPixels` over the existing
  `uint[]` copy.
- `SavePng` becomes `SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).SaveTo(stream)`.
- The semaphore-gated background save preserves exactly. Per-frame allocation count is
  unchanged (one `SKBitmap` per frame instead of one `Bitmap`).

### Slice A.4 — `Imaging/PosterRenderer.cs` → SkiaSharp

- Same swap as Slice A.2 at the save tail.
- Rotation (`PosterRequest.Rotate`) — use `SKBitmap.Resize` + `SKCanvas.RotateDegrees(90)`
  or pre-rotate via a 2D index transform during the BGRA copy (faster, no extra alloc).
- Custom watermark composition: `WatermarkResolver.Resolve` already returns a
  `WatermarkDef` POCO; the `Render` call swaps from `Graphics.DrawString` to
  `SKCanvas.DrawText` over `SKPaint`.

### Slice A.5 — `Rendering/FractalOverlayCompositor.cs` → SkiaSharp

- Halo grid + ROI outline composition becomes `SKCanvas.DrawLine` + `DrawPath`.
- Existing `Graphics.SmoothingMode = AntiAlias` maps to `SKPaint.IsAntialias = true`.
- Used by both interactive (overlay on framebuffer) and headless export (slideshow
  recording) paths; behaviour preserved bit-identical for grid alignment.

### Slice A.6 — `Models/ColorSchemes/**` + `Models/ColorThemeCsExporter.cs` `System.Drawing.Color` → `Rgba32`

- All these files use `Color.FromArgb(a,r,g,b)` as a struct constructor — they never call
  GDI+ APIs. Two options:
  - **Adopt `System.Drawing.Primitives`**: shipping in the BCL, no GDI+ dependency. The
    `Color` struct works on every RID. This is the smallest diff: change nothing in source,
    just stop shipping `System.Drawing.Common`.
  - **Adopt `Rgba32` from Slice A.1**: enforces zero `System.Drawing` references engine-wide.
    Bigger diff (one-line per `Color.FromArgb` call) but cleaner.

  **Recommendation:** go with `System.Drawing.Primitives` for X.A (small diff, fast). Park a
  follow-up to migrate to `Rgba32` after the rest of the cross-platform launch lands.

### Slice A.7 — Remove `System.Drawing.Common` from `FracturingFog.App` closure

- Strip `<PackageReference Include="System.Drawing.Common">` from any project still
  referencing it inside the App closure. Add a CI assertion (PowerShell + grep) that fails
  the Linux leg if any closure project pulls it transitively.

---

## Phase X.B — Audio capture abstraction

> **Exit:** opening the audio-reactive slideshow dialog on Linux/macOS shows the source
> picker, picks "File" or "Synth" without crashing, drives the beat analyzer. System
> loopback is greyed with a banner.

### Slice B.1 — `IAudioCaptureBackend` + `AudioCaptureDriver`

- `Abstractions/Audio/IAudioCaptureBackend.cs`:
  ```csharp
  public interface IAudioCaptureBackend : IDisposable
  {
      AudioBackendCapabilities Capabilities { get; }
      void Start(AudioSourceKind source, AudioFormat preferredFormat, string? filePath);
      void Stop();
      event Action<ReadOnlyMemory<float>, AudioFormat>? DataAvailable;
      event Action<Exception>? Failed;
  }
  [Flags] public enum AudioBackendCapabilities { None=0, SystemLoopback=1, Microphone=2, FilePlayback=4, SynthPlayback=8 }
  ```
- `Audio/AudioCaptureDriver.cs` (in `FracturingFog.Audio`) — owns the backend, wraps the
  beat analyzer, exposes `IBeatSource` (existing) to consumers.

### Slice B.2 — `WindowsNAudioBackend` (in `Audio.Win/`)

- Extract the WASAPI loopback + WaveOutEvent path from the old `AudioEngine.StartCore`
  switch into a clean `IAudioCaptureBackend` impl. `Audio.Win.csproj` targets
  `net10.0-windows`, references NAudio, ProjectReferences `FracturingFog.Audio`
  (for the interface).
- `Capabilities = SystemLoopback | Microphone | FilePlayback | SynthPlayback`.

### Slice B.3 — `NoopAudioBackend` (in `Audio/`)

- Cross-platform fallback. Supports only `FilePlayback` (via `NAudio.AudioFileReader`
  — yes this works cross-platform because file decode doesn't touch WASAPI/MM) and
  `SynthPlayback` (analyzer-only, no speaker output — host doesn't push samples to a
  speaker, only to the analyzer).
- Spawn a `System.Threading.Channels` based pump that decodes the file in chunks and
  pushes float samples to `DataAvailable`. No platform-specific API.
- `Capabilities = FilePlayback | SynthPlayback`.

### Slice B.4 — Backend selection + UI glue

- `AvaloniaShellBootstrap` picks `WindowsNAudioBackend` on Windows (via reflection
  load to avoid Windows-only ref leaking into the cross-platform host — or via the
  Win-only ProjectReference in `App.csproj` resolving the type at load time).
  Otherwise picks `NoopAudioBackend`.
- `UI.Avalonia/Views/AudioSettingsView.axaml` — grey + tooltip the SystemLoopback /
  Microphone options when `backend.Capabilities` lacks the flag. Add a one-line banner
  at the top: "System audio capture is not supported on this OS."
- Audio-reactive sweep settings persist either way (saved to the user store); the running
  sweep receives a flat beat stream on hosts without capture.

### Slice B.5 — `BeatAnalyzer` NAudio dep removal

- Replace `NAudio.Dsp.Fft.FFT` with `MathNet.Numerics.IntegralTransforms.Fourier.Forward`
  (MathNet is already in the PaletteBuilder.Lib closure, easy to add to
  `FracturingFog.Audio`). Verify spectrum bins match within 1e-6 of the prior NAudio
  output on a sine sweep test.
- `FractalSynth.cs`'s `ISampleProvider` interface and `WaveFormat` POCO are pulled into
  `Abstractions/Audio/` as local copies so the synth survives without NAudio.

### Slice B.6 — Flip `FracturingFog.Audio` TFM to `net10.0`

- Remove the temporary `net10.0-windows` TFM from `Audio.csproj`. CI Linux leg builds it
  clean.

---

## Phase X.1 — Palette engine demotion

> **Exit:** `From Image…` round-trips an input PNG on Linux. `PaletteBuilder.Lib` TFM is
> `net10.0`.

### Slice 1.1 — `PdfPaletteExporter` → QuestPDF

- Add `<PackageReference Include="QuestPDF" Version="2026.x.x" />` to
  `PaletteBuilder.Lib.csproj`. Remove `<PackageReference Include="PDFsharp-gdi" />` and
  `<PackageReference Include="System.Drawing.Common" />`.
- Rewrite `PaletteBuilder/Services/PdfPaletteExporter.cs` against `QuestPDF.Fluent`:
  - `Document.Create(container => { container.Page(page => { … }); })`
  - Page size / margin from existing `PdfExportOptions`.
  - Cover page, swatch grid, CVD rows ported one-to-one. The QuestPDF API is closer to
    Razor than to PDFsharp, so the rewrite is mostly structural.
- Honour `QuestPDF.Settings.License = LicenseType.Community` for the open-source license.

### Slice 1.2 — Flip TFM + verify Windows downcompat

- `PaletteBuilder.Lib.csproj`: `<TargetFramework>net10.0-windows</TargetFramework>` →
  `<TargetFramework>net10.0</TargetFramework>`. Remove `<UseWindowsForms>false</UseWindowsForms>`
  (default already).
- `PaletteBuilder.csproj` (Avalonia WinExe wrapper) and `FracturingFogCLD.csproj`
  reference the Lib via TFM downcompat — both Windows-targeted parents can reference a
  `net10.0` Lib. Build both on Windows to confirm.

### Slice 1.3 — Manual PNG round-trip smoke on Linux + macOS

- A short doc in `Docs/Technical/CrossPlatform-SmokeTests.md` listing the manual
  steps. Not blocking on automation — CI builds the lib; user smoke confirms the UI flow.

---

## Phase X.2 — Video export portability

> **Exit:** 100-frame slideshow export succeeds on Linux via bundled or PATH ffmpeg.

### Slice 2.1 — `IVideoWriter` + `FfmpegVideoWriter` adapter

- `Abstractions/Imaging/IVideoWriter.cs` — `Init(width, height, fps)`, `WriteFrame(uint[])`,
  `Finish()`. Pure abstraction.
- Wrap the existing `Imaging/FfmpegEncoder.cs` (which is already process-based) in
  `Imaging/FfmpegVideoWriter.cs` adapter exposing `IVideoWriter`. Internally it spools
  PNGs to a temp directory and calls `EncodeAsync` at `Finish()` — or streams BGRA to
  ffmpeg's stdin via a pipe for lower disk I/O. **Recommendation:** PNG-on-disk first
  (mirrors today's behaviour), pipe streaming as a later optimisation.

### Slice 2.2 — `Win32MP4Writer` lives in `Rendering.D3D/` (or `FracturingFog.Win/`)

- `Imaging/MP4Writer.cs` already in the Win-only fragment from Phase X.0 (Slice 0.4).
  Wrap it in an `IVideoWriter` adapter (`Win32Mp4VideoWriter`) at the same boundary.

### Slice 2.3 — `FindFfmpeg` rename for cross-platform

- `FfmpegEncoder.FindFfmpeg`:
  - Probe `ffmpeg.exe` (Windows) and `ffmpeg` (Linux/macOS).
  - Probe `Tools/<rid>/ffmpeg{.exe}` based on `RuntimeInformation.RuntimeIdentifier`.
  - PATH fallback already handles `Path.PathSeparator` correctly.

### Slice 2.4 — Bundle ffmpeg per RID

- `FracturingFog.App.csproj`:
  ```xml
  <ItemGroup>
    <None Include="..\Tools\win-x64\ffmpeg.exe"     Pack="false" CopyToOutputDirectory="PreserveNewest" Condition="'$(RuntimeIdentifier)' == 'win-x64'" />
    <None Include="..\Tools\linux-x64\ffmpeg"       Pack="false" CopyToOutputDirectory="PreserveNewest" Condition="'$(RuntimeIdentifier)' == 'linux-x64'" />
    <None Include="..\Tools\linux-arm64\ffmpeg"     Pack="false" CopyToOutputDirectory="PreserveNewest" Condition="'$(RuntimeIdentifier)' == 'linux-arm64'" />
    <None Include="..\Tools\osx-arm64\ffmpeg"       Pack="false" CopyToOutputDirectory="PreserveNewest" Condition="'$(RuntimeIdentifier)' == 'osx-arm64'" />
    <None Include="..\Tools\osx-x64\ffmpeg"         Pack="false" CopyToOutputDirectory="PreserveNewest" Condition="'$(RuntimeIdentifier)' == 'osx-x64'" />
  </ItemGroup>
  ```
- **Licensing audit:** ffmpeg builds vary in license (GPL vs LGPL, codec set). Recommend
  *not* bundling on Linux/macOS for the first launch — rely on `apt`/`brew` install. Ship
  bundled ffmpeg only on Windows (matches today). Document this in the user guide.

### Slice 2.5 — `FfmpegSetupDialog` → Avalonia

- Move the WinForms `Hosting/FfmpegSetupDialog.cs` into a Win-only subproject as
  `FfmpegSetupDialog.WinForms` (kept for the legacy `--winforms` shell).
- Add `UI.Avalonia/Views/FfmpegSetupView.axaml` with OS-aware copy:
  - Windows: existing gyan.dev auto-download.
  - Linux/macOS: instructions panel — copy-pasteable `sudo apt install ffmpeg` /
    `brew install ffmpeg`. "I've installed it" button rescans PATH.
- `FfmpegInstaller.cs`: gate the download path behind `OperatingSystem.IsWindows()`.

### Slice 2.6 — Bootstrap wires the writer

- `AvaloniaShellBootstrap.OnSurfaceReady`: probe Media Foundation (on Win) →
  `Win32Mp4VideoWriter`; else probe ffmpeg → `FfmpegVideoWriter`; else null.
- Video-export UI surfaces "Not available — install ffmpeg" banner when null.

---

## Phase X.3 — P/Invoke + IsOSPlatform sweep

> **Exit:** `dotnet build` on Linux emits zero `CA1416` warnings from `FracturingFog.App`,
> `Engine`, `Hosting`, `UI.Avalonia`, `Audio`.

### Slice 3.1 — Avalonia shell P/Invoke removal

- `UI.Avalonia/Views/MainWindow.axaml.cs` `ToyDragWindow`:
  ```csharp
  private void OnPointerPressedForToyDrag(object? sender, PointerPressedEventArgs e)
  {
      if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
          BeginMoveDrag(e);
  }
  ```
  Hook to the title-bar `PointerPressed`. Delete `[DllImport("user32.dll")]
  ReleaseCapture`, `SendMessage`, and the `WM_NCLBUTTONDOWN` / `HTCAPTION` constants.
- `Hosting/AvaloniaShellBootstrap.cs` `ClientToScreen`:
  ```csharp
  if (OperatingSystem.IsWindows())
      ClientToScreen(handle, ref pt);
  else
  {
      var screenPt = control.PointToScreen(new Point(pt.X, pt.Y));
      pt.X = (int)screenPt.X; pt.Y = (int)screenPt.Y;
  }
  ```
  Or unconditionally use `Control.PointToScreen` if the Win32 path doesn't drift.

### Slice 3.2 — Console attach gating

- `ServerHost/ServerEntry.cs`, `Batch/BatchEntry.cs`, `Benchmarks/MandelbrotBench.cs`:
  wrap every `AttachConsole`/`AllocConsole` call in `if (OperatingSystem.IsWindows())`.

### Slice 3.3 — `[SupportedOSPlatform("windows")]` annotations

- Add to all types in `Rendering.D3D`, `FracturingFog.Win`, `Audio.Win`, and to the
  Win-only fragment of `Hosting` (NativeMouseForwarder + FfmpegSetupDialog while it lives).
- `OperatingSystem.IsWindows()` replaces `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`
  at every site so the analyzer can prove the gating.

### Slice 3.4 — `MainForm` exclusion from CA1416 scan

- `FracturingFogCLD.csproj` already net10.0-windows, so `CA1416` is silent there. Keep
  the `--winforms` legacy shell out of scope for this phase.

---

## Phase X.4 — Renderer selection + bootstrap polish

> **Exit:** Avalonia paints a fractal on the 6-RID matrix from the roadmap.

### Slice 4.1 — `--renderer` CLI flag

- Parse in `Program.cs`. Default: DX on Win, Silk on Linux/macOS. Override values:
  `dx`, `silk`, `skia`.
- `RendererFactory` picks based on the flag + `OperatingSystem.IsWindows()` + the
  presence of a Win32 HWND surface.

### Slice 4.2 — Linux Wayland CI leg

- `cross-platform-build.yml` adds `linux-wayland` matrix entry:
  - `sudo apt install weston libwayland-egl1`
  - `weston --backend=headless &`
  - Run the Silk smoke under `WAYLAND_DISPLAY=wayland-0`

### Slice 4.3 — macOS CGL token verification

- Open one Silk window on macOS CI (already passing for FBO smoke), confirm the GL
  fragment shader compiles. If `#version 330 core` is rejected, fall back to
  `#version 410 core`.

---

## Phase X.5 — Compute fallbacks

> **Exit:** smoke test asserts ILGPU device kind per RID; CPU device used on Apple
> Silicon without crash.

### Slice 5.1 — ILGPU device-probe smoke

- `Rendering.Silk.Smoke` already runs the Silk path. Add a parallel `Compute.Smoke`
  CLI that constructs `ILGPU.Context`, picks an accelerator, runs a 64×64 Mandelbrot
  kernel, asserts no crash. Compare device kind against the expected per-RID set.

### Slice 5.2 — Help → Hardware tab docs

- `Hosting/HostHelpContentProvider.cs` Hardware text already mentions DXGI adapters +
  CPU SIMD. Add audio backend (`backend.GetType().Name + Capabilities`) and ILGPU
  device kind to the tab.

---

## Phase X.6 — Packaging + distribution

> **Exit:** GitHub Release on tag push has 5 self-contained archives.

### Slice 6.1 — Publish profiles

- `FracturingFog.App/Properties/PublishProfiles/{win-x64,linux-x64,linux-arm64,osx-arm64,osx-x64}.pubxml`
  with `SelfContained=true`, `PublishSingleFile=true`,
  `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`.

### Slice 6.2 — Linux AppImage

- `Tools/Packaging/build-appimage.sh` — `appimagetool` over the publish output. Optional
  `.desktop` file in `Resources/Linux/`.

### Slice 6.3 — macOS `.app` bundle

- `Tools/Packaging/build-mac-app.sh` — `Info.plist` template in `Resources/macOS/`,
  copies publish output into `FracturingFog.app/Contents/MacOS/`. Code-signing is a
  separate manual step until Apple Developer cert lands.

### Slice 6.4 — CI release workflow

- New `.github/workflows/release.yml` triggered on `v*` tags. Matrix runs publish per
  RID, uploads artifacts. Optional: GitHub Release auto-create with checksums.

---

## Phase X.7 — Documentation + UX

### Slice 7.1 — `Docs/User/CrossPlatform-UserGuide.md`

Sections:
- Install per OS (Win exe, Linux AppImage, macOS .app).
- Renderer selection — when to use `--renderer skia` or `--renderer silk`.
- Audio capability matrix.
- Video export — ffmpeg install for Linux/macOS.
- Known limitations: Apple Silicon GPU compute via CPU, Wayland + NVIDIA caveats.

### Slice 7.2 — `README.md` install paragraph

Replace "Windows-only" with the per-OS install table.

### Slice 7.3 — `FEATURES.md` platform matrix

Three columns: Win / Linux / macOS. Rows: every feature group (Render, Compute, Audio,
Video Export, Slideshow, Palette Builder, etc.). Tick / N/A / Limited.

---

## Critical files index

Files most likely to need careful review or non-mechanical edits:

| Phase | File | Why |
|---|---|---|
| X.0 | `FracturingFogCLD.csproj` | The Compile-glob strip pattern is fragile; moving Hosting/Imaging/Rendering out shrinks the strip list. Keep the analyzer-strip target. |
| X.0 | `Hosting/AvaloniaShellBootstrap.cs` | Single static class holds the whole boot graph. Moving it splits ref closure cleanly but check no internal types leak. |
| X.A | `Imaging/ImageExport.cs` | The unsafe BGRA copy and DPI metadata are easy to get wrong; pin a golden-pixel A/B against a known render before merging. |
| X.A | `Rendering/FractalOverlayCompositor.cs` | Pixel-aligned grid lines drift if SkiaSharp anti-aliasing differs from GDI+. Use `SKPaint.IsAntialias=false` for the grid path; AA is fine for the watermark. |
| X.B | `Audio/AudioEngine.cs` | The state machine across StartCore/StopCore/Reconfigure is intricate; preserve the lock + Stopped event contract when refactoring into the driver + backend split. |
| X.B | `Audio/BeatAnalyzer.cs` | FFT swap from NAudio to MathNet. Window function, hop size, magnitude scaling must match — write a spectrum comparison test first. |
| X.1 | `PaletteBuilder/Services/PdfPaletteExporter.cs` | QuestPDF API is fluent; the cover page + CVD row logic needs structural rethink, not line-by-line port. |
| X.2 | `Imaging/FfmpegEncoder.cs` | Already process-based — easy adapter. The only landmine is the `frame_%06d.png` glob and `-start_number 1` which must survive. |
| X.3 | `UI.Avalonia/Views/MainWindow.axaml.cs` | Toy-mode drag rewrite touches event handlers; verify the title-bar drag still triggers cleanly on Avalonia's Pointer events vs the previous WM_NCLBUTTONDOWN trick. |
| X.6 | `FracturingFog.App.csproj` | Publish-profile interaction with `<ProjectReference Condition="…IsOSPlatform…">` can break on `dotnet publish -r osx-arm64` from a Win runner; test cross-RID publish on every leg. |

---

## Execution order

1. **Phase X.0** (project geometry) — week of 2026-06-12.
   No behavioural change visible to users. Pure refactor.
2. **Phase X.A** (Skia swap) — week of 2026-06-19.
   PNG export A/B testing. WinForms shell unchanged (it still uses GDI+ paths in the
   legacy partials).
3. **Phase X.B** (audio abstraction) — week of 2026-06-26.
4. **Phase X.3** (P/Invoke sweep — done first across X.B / X.A overlap) — runs in
   parallel where possible.
5. **Phase X.1** (palette PDF) — week of 2026-07-03.
6. **Phase X.2** (video) — week of 2026-07-03 (parallel with X.1).
7. **Phase X.4** (renderer polish + Wayland CI) — week of 2026-07-10.
8. **Phase X.5** (compute smoke) — same week.
9. **Phase X.6** (packaging) — week of 2026-07-17.
10. **Phase X.7** (docs) — week of 2026-07-24.

Total: ~6-7 calendar weeks of focused work for one developer. Phases X.A and X.B are the
deepest cuts; X.6 / X.7 are mostly mechanical.

---

## Out of scope (for this branch)

- Removing the legacy WinForms shell. Tracked separately; happens once
  `FracturingFog.App` reaches parity and is the default on Windows installs.
- ARM64 NEON SIMD lane in CalculatorGen — `CalculatorGen-Roadmap.md` follow-up.
- Metal GPU compute on Apple Silicon — needs ILGPU's not-yet-merged Metal backend.
- OpenAL system-audio capture (Linux/macOS WASAPI-loopback equivalent) — Phase X.B
  ships with a noop backend; OpenAL backend is a follow-up enhancement.
- Mobile (iOS / Android) and Wasm browser host.
- Touch UI affordances.
