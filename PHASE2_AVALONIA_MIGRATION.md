# Phase 2 — Avalonia UI Migration + GPU Renderer Abstraction

**Branch**: `feature/phase2-avalonia-ui`
**Goal**: Replace WinForms UI with Avalonia 11 for proper DPI scaling and cross-platform readiness (Win/Mac/Linux). Introduce a renderer abstraction so the Vortice DirectX 11/12 path stays on Windows while future Skia/Vulkan/Metal backends can slot in.
**Scope warning**: Large multi-week effort. Touches `MainForm.cs` (183 KB monolith), 17 dialog/view files, all event wiring, and the `DirectXRenderer` / `DirectX12Renderer` HWND hosting model.

## Why

- Current UI uses hardcoded pixel `ClientSize`, manual `Left/Top/Width` arithmetic, no `TableLayoutPanel`/`FlowLayoutPanel`/`AutoSize`. Breaks at non-100% DPI and unusual resolutions.
- WinForms `PerMonitorV2` only helps if controls cooperate — they don't here.
- `Vortice.Direct3D11/12` locks renderer to Windows. To open Mac/Linux later we need an `IGpuSurface` boundary.
- `MainForm.cs` mixes view, view-model, and controller logic. Avalonia's MVVM-friendly bindings require a split anyway, so this is the natural cut point.

## Architecture target

```
FracturingFog.Core           (netstandard2.1 / net10.0)
  ├── Calculators/           (existing, no UI deps)
  ├── Models/                (existing)
  ├── Math/                  (existing)
  ├── Interefaces/
  │     ├── IFractalRenderer.cs   (existing)
  │     └── IGpuSurface.cs        (NEW — abstracts swapchain/HWND/CAMetalLayer/VkSurface)
  └── ViewModels/            (NEW — extracted from MainForm.cs)
        ├── MainViewModel.cs
        ├── FloatingMenuViewModel.cs
        ├── ColorThemeEditorViewModel.cs
        └── ...

FracturingFog.Rendering.D3D  (net10.0-windows)
  ├── DirectXRenderer.cs     (moved, implements IGpuSurface)
  ├── DirectX12Renderer.cs
  └── HwndGpuSurface.cs      (NEW — wraps HWND)

FracturingFog.UI.Avalonia    (net10.0, cross-platform)
  ├── App.axaml + App.axaml.cs
  ├── Views/
  │     ├── MainWindow.axaml
  │     ├── FloatingMenu.axaml
  │     ├── ColorThemeEditor.axaml
  │     └── ... (one per current Views/*.cs)
  ├── Controls/
  │     └── GpuSurfaceControl.cs   (NativeControlHost wrapper around IGpuSurface)
  └── Program.cs             (BuildAvaloniaApp — replaces existing Program.cs)

FracturingFog.Legacy.WinForms (net10.0-windows, deletable when port complete)
  └── MainForm.cs            (kept building during transition behind a build flag)
```

## Phase 2 step list

### 2.0 — Setup (this session)
- [x] Create branch `feature/phase2-avalonia-ui`
- [x] Add `FracturingFog.Abstractions` project (UI-free shared contracts; replaces the original `Core` plan for the bootstrap step)
- [x] Add `FracturingFog.UI.Avalonia` project (Avalonia 11.2.3, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.ReactiveUI`)
- [x] Update `.sln` to include both new projects
- [x] Define `IGpuSurface.cs` in Abstractions — abstraction over native window handle + resize events
- [x] Stub `GpuSurfaceControl` in Avalonia project using `NativeControlHost`
- [x] Wire minimal Avalonia `MainWindow` that hosts `GpuSurfaceControl` (renderer wiring deferred to step 2.1)
- [x] Add `--avalonia` CLI flag to `Program.cs` so both UIs build during transition
- [x] All three projects build clean (`dotnet build FracturingFogCLD.sln` → 0 errors)

#### Build gotchas resolved during 2.0
- Avalonia XAML NameGenerator analyzer leaks transitively from `UI.Avalonia` into the WinExe even with `ExcludeAssets`/`PrivateAssets` set. Worked around with a `StripAvaloniaAnalyzers` MSBuild target in the WinExe csproj that removes any `@(Analyzer)` whose path contains "Avalonia".
- Avalonia XAML compiler also auto-globs `*.axaml` under the project root. WinExe csproj now explicitly `Remove`s `AvaloniaResource`, `AvaloniaXaml`, `ApplicationDefinition`, `Page`, and `AdditionalFiles` under `UI.Avalonia\**`.
- WinExe also `Remove`s `Compile`/`None`/`EmbeddedResource`/`Content` under `Abstractions\**` and `UI.Avalonia\**` to stop the implicit SDK glob from compiling sibling-project sources into the WinExe twice.

### 2.1 — Renderer abstraction
- [ ] Move `Rendering/DirectXRenderer.cs` + `DirectX12Renderer.cs` into `FracturingFog.Rendering.D3D` project (deferred — not required for proof of life; will move when WinForms shell is retired in step 2.3)
- [ ] `HwndGpuSurface : IGpuSurface` wraps current HWND-based init (deferred — only needed once the WinForms shell also speaks IGpuSurface; the legacy MainForm still uses raw HWND directly)
- [x] `RendererFactory` returns surface-aware renderer via `Create(IGpuSurface)` overload that validates surface kind, clamps size, and wires Resized / HandleLost
- [x] WinForms `MainForm` path untouched — legacy HWND-based `Create(IntPtr, int, int)` overload preserved; full solution builds green
- [x] Avalonia shell renders animated test pattern through the live DX renderer (`AvaloniaBootstrap.cs` in WinExe; `AvaloniaShell.OnSurfaceReady` hook in UI.Avalonia keeps the shell renderer-agnostic). Real fractal output arrives with the calculator wiring in step 2.3.

### 2.2 — Dialog ports (incremental, one PR per dialog)
Priority order is now by **line count ascending** (re-ordered after measuring the WinForms files — `FloatingHelp.cs` is 3,431 lines of static help text and not the easiest target despite being a "help" dialog).

- [x] `SlideshowSettingsView.axaml` (was `SlideshowSettingsDialog.cs`, 223 lines)
- [x] `MiniDepthControl.axaml` (was `MiniDepthPanel.cs`, 307 lines)
- [x] `FractalParamsView.axaml` (was `FractalParamsDialog.cs`, 349 lines)
- [x] `UserEquationView.axaml` (was `UserEquationDialog.cs`, 435 lines)
- [x] `AudioSettingsView.axaml` (was `AudioSettingsDialog.cs`, 437 lines)
- [x] `SandboxView.axaml` (was `SandboxDialog.cs`, 483 lines)
- [x] `MiniMapControl.axaml` (was `MiniMapPanel.cs`, 512 lines — render-only Avalonia control consuming host-supplied thumbnail bitmaps; bg calculator pipeline stays in main project)
- [x] `ImagePaletteView.axaml` (was `ImagePaletteDialog.cs`, 801 lines — added `IPaletteExtractionService` + neutral DTOs in Abstractions so UI.Avalonia stays free of System.Drawing and the palette-extractor classes; host wires the impl)
- [x] `UserBulbView.axaml` (was `UserBulbDialog.cs`, 1,203 lines — VM exposes split CompileRequested/RenderRequested channels; host drives `AnimationTick(dt)` from its own 30 Hz timer and calls `NotifyRenderDone()` on upload to gate ticks against long raymarches)
- [x] `ColorThemeEditorView.axaml` (was `ColorThemeEditor.cs`, 1,448 lines — neutral `ColorThemeDef`/`LightSourceDef`/`PbrMaterialBandDef`/`InSetColorDef`/`PbrLightingModeDef` DTOs added to Abstractions alongside legacy `ColorThemeData` so UI.Avalonia stays free of System.Drawing and the runtime `LightSource`/`PbrLightingMode` classes; new `IColorThemeService` interface lets the host bridge to `ColorPalette`/`DataDrivenColorThemes`/`UserColorThemeLibrary` and own JSON+C# serialization; VM holds three `LightSourceRowVm` instances + `ObservableCollection<ColorStopRowVm>` + `ObservableCollection<MaterialBandRowVm>` with shared 150 ms debounce → PreviewRequested; host wires PreviewRequested, RegionRequested, EditorThemeSelected, ThemeSavedToLibrary, HelpRequested, ThemeMessageEventArgs, ThemeSaveFileEventArgs, ThemeFromImageEventArgs)
- [x] `FloatingMenuView.axaml` (was `FloatingMenu.cs`, 1,541 lines — VM is a thin command/state surface: 22 ReactiveCommands bubble button clicks to host as events, four ObservableCollection<string> combo lists (regions/themes/resolutions/qualities) get populated by host via `SetRegions`/`SetThemes`/etc., parallel `SetXxxSilent` variants suppress change notifications for cross-shell mirroring; post-FX sliders expose both round-tripping setters (`Brightness` → `BrightnessSlide` event) and silent setters (`SetBrightnessSilent` for theme-switch snap); `IterLockEventArgs` carries current iter count when lock toggles)
- [x] `FloatingHelpView.axaml` (was `FloatingHelp.cs`, 3,431 lines — defined `IHelpContentProvider` (+ `HelpSubTab`, `HelpLink` records) in `FracturingFog.Help` so the ~2,500 lines of static text + Math sub-tabs + live DXGI/D3D11 enumeration stay in the main project and UI.Avalonia just renders tab bodies; VM exposes one string per tab + `ObservableCollection<HelpSubTab>` for the nested Math `TabControl`; About-tab `HelpLink` buttons raise `LinkRequested(url)` for the host to launch in the system browser; Refresh button re-fetches `HardwareText`; Esc closes)
- [x] `SlideshowVcrControl.axaml` (was `SlideshowVcrPanel.cs`, 152 lines)
- [x] `MiniMapDefaults` moved to Abstractions (66 lines; namespace now `FracturingFog.Models`, visibility `public` for cross-shell use)

Models migrate to the shared `FracturingFog.Abstractions` assembly **as each dialog needs them** (namespace stays `FracturingFog.Models` so legacy WinForms code compiles untouched). Done so far: `SlideshowSettings`, `FractalParameters` + `AffineMap` + `UserBulbParam` + `UserBulbChainStep` + `UserBulbStore` + `UserEquationStore` + `SandboxEquationStore` + `Enums.cs` (`FractalType`, `QualityLevel`, `RenderProfile`). Plus `AudioSettings` + `IBeatSource` + `MiniMapDefaults` and the host-only `IPaletteExtractionService` / DTOs in `FracturingFog.Imaging`.

Each port:
1. Extract view-model class from current code-behind (commands, observable props).
2. Build `.axaml` with `Grid`/`StackPanel`/`DockPanel` — no pixel literals; use `*` / `Auto` sizing and `Margin`/`Padding` in DIPs.
3. Bind to view-model. Unit test the VM.
4. Wire from new `MainViewModel`.
5. Remove old `Views/*.cs` once parity confirmed.

### 2.3 — MainForm decomposition

Survey done. Total monolith = 4,247 lines (`MainForm.cs`) + 818 (`Slideshow.cs`) + 1,850 (`VideoZoom.cs`) — all `sealed partial class MainForm`, 110 methods in the main file. Cut plan:

**A. Pure view state → Abstractions** (no UI, no renderer)
- `Abstractions/ViewState/FractalViewState.cs` — POCO holding `CenterX/Y` quad-precision limbs, `Zoom`, `QualityPreset`, `FractalType`, brightness/contrast/adaptive, iter lock state, and a reference to the existing `FractalParameters` (already in Abstractions). 3D camera state already lives on `FractalParameters`.

**B. Input → Abstractions** (mouse + keyboard, precision-aware pan/zoom math)
- `Abstractions/Input/InputEvents.cs` — neutral event records (`PointerInput`, `WheelInput`, `KeyInput`) so the input layer is shell-agnostic.
- `Abstractions/Input/IFractalInputController.cs` + `FractalInputController.cs` — owns pan/zoom state, picks DD/QD/double math tier from `_zoom`, handles 2D and 3D key bindings (W/S zoom, A/D/Q/E pan, arrows for 3D camera, PgUp/PgDn/Home/End for 3D light). Raises `ViewChanged` so the renderer host re-triggers.

**C. Render orchestration → main project** (renderer + 11 calculators)
- `FractalRenderHost.cs` (stays in main; depends on all calculator types + `IFractalRenderer`) — wraps `TriggerCalculation` / `TriggerCalculationFast` / `UploadProcessedBuffer` / `BlendWatermarkOverlay` / `BlendGridOverlay` / `SelectAltCalculator` / `ApplyViewState`. Surface: `void ApplyView(FractalViewState)`, `void Trigger(bool progressive)`, `void TriggerFast()`, `void Resize(int,int)`, `event Action<RenderFrameInfo> FrameCompleted`.

**D. `MainViewModel` → UI.Avalonia/ViewModels/** — top-level: holds `FractalViewState`, drives `FractalRenderHost`, owns `FractalInputController`, mirrors selected region/theme/quality/fractal-type into combos, manages brightness/contrast/adaptive + lock flags.

**E. `ShellViewModel` → UI.Avalonia/ViewModels/** — owns `FloatingMenuViewModel`, lazy `ColorThemeEditorViewModel`, lazy `FloatingHelpViewModel`, mini-map + mini-depth panels, VCR + slideshow settings. Glues child VMs to the `MainViewModel`.

**F.** Avalonia `MainWindow.axaml` binds to `ShellViewModel` with the existing `GpuSurfaceControl` as the render surface.

**G.** Delete `MainForm.cs` + `Slideshow.cs` + `VideoZoom.cs` (or carve `Slideshow` + `VideoZoom` into engines that the `ShellViewModel` orchestrates), `MainForm.resx`, the WinForms project entry point.

WinForms shell stays green during steps A–E by having MainForm consume the new objects; only step G removes it.

- [x] Survey + cut plan written (above)
- [x] A. Extract `FractalViewState` POCO to Abstractions (also moved `QualityPreset` + `QualityTier` from `Models/` to `Abstractions/Models/` since it's pure POCO; `FromName` raised from `internal` to `public` so the cross-assembly caller in `FractalRegion.cs` still compiles)
- [x] B. Extract `IFractalInputController` + neutral input events to Abstractions (`InputEvents.cs` defines `PointerInput`/`WheelInput`/`KeyInput` records + `PointerButton`/`InputModifiers`/`InputKey`/`InputCursor` enums; `FractalInputController.cs` ports the precision-aware pan/zoom math from MainForm verbatim — double/DD/QD tiers, cursor-anchor wheel zoom, 3D right-drag camera rotation, 2D+3D key bindings. Also moved `Math/DoubleDouble.cs` + `Math/QuadDouble.cs` to `Abstractions/Math/` since the input controller references them. Controller raises `ViewChanged(RenderHint)` (Full or Fast), `StatusRequested` for quality auto-promotion notices, `CursorRequested` for drag-state cursor changes. WinForms shell still unchanged — adapter glue lands in step C.)
- [x] C. Extract `FractalRenderHost` to main project (`Abstractions/Render/IFractalRenderHost.cs` defines the shell-neutral surface + `RenderFrameInfo` record; concrete `Rendering/FractalRenderHost.cs` lives in main since it depends on all 11 calculator types + the Vortice `IFractalRenderer`. Ports `TriggerCalculation` / `TriggerCalculationFast` / `ApplyViewState` / `Resize` / `UploadProcessedBuffer` / `RepaintWithBrightnessContrast` / `SelectAltCalculator` from MainForm verbatim, reading from the shared `FractalViewState`. Brightness + contrast pure-CPU pass kept; grid + watermark overlays (System.Drawing-based) intentionally skipped — they will redraw via Avalonia.Media in step F. WinForms shell still untouched; MainForm continues with its own private renderer + calculator instances during the transition.)
- [x] D. Extract `MainViewModel` to UI.Avalonia (thin facade over `FractalViewState` + `IFractalInputController` + `IFractalRenderHost`. Wires `ViewChanged(Full)` → `Trigger()` and `ViewChanged(Fast)` → `TriggerFast()` + 300 ms pan-stop debounce that fires `Trigger()` once motion ends. Brightness/Contrast write through to view state and trigger `RepaintWithPostFx` (no recalc); Adaptive triggers a full recalc because it lives on the calculator's escape buffer. Mirrors `FrameCompleted` into the legacy MainForm status string. Exposes `QualityPresets` / `FractalTypes` observable collections, `SelectedRegion` / `SelectedTheme` / `SelectedQuality` / `SelectedFractalType`, post-FX with lock flags, IterLocked + LockedIterations, `ResetViewCommand`. Dialog ownership stays in `ShellViewModel` (step E).)
- [x] E. Extract `ShellViewModel` to UI.Avalonia (top-level composition VM: owns `MainViewModel` + `FloatingMenuViewModel` + lazy `ColorThemeEditorViewModel` / `FloatingHelpViewModel`. Constructor takes host-provided services — `IFractalRenderHost`, `IFractalInputController`, `IColorThemeService`, `IHelpContentProvider`, optional `IPaletteExtractionService`. Wires FloatingMenu events into Main (region/theme combos, reset, post-FX sliders) and bubbles ColorThemeEditor + FloatingHelp events back up to the host for the System.Drawing-bound bits (`ColorThemePreviewRequested`, `FromImageRequested`, `SaveFileRequested`, `MessageRequested`, `LinkRequested`). Visibility flags (`IsFloatingMenuVisible` etc.) bind directly to Window.IsVisible in MainWindow.axaml.)
- [x] F.1 Avalonia input adapter (`UI.Avalonia/Input/AvaloniaInputAdapter.cs` — bridges PointerPressed/Moved/Released/DoubleTapped/PointerWheelChanged/KeyDown into IFractalInputController; wheel delta scaled ×120 to match WinForms; Ctrl+Shift+S/A diag toggles; cursor translation from InputCursor → Avalonia StandardCursorType)
- [x] F.2 `MainWindow.axaml` toolbar + status + render surface (top toolbar bound to ShellViewModel — FractalType/Quality combos from Main, Region/Theme combos from FloatingMenu, Reset/Edit Theme/Menu/Help buttons; status bar bound to Main.StatusText; center hosts GpuSurfaceControl with transparent InputSponge Border above it since native HWND children don't forward pointer events back into Avalonia; code-behind tracks IsXxxVisible flags + lazily shows/hides FloatingMenuView, ColorThemeEditorView, FloatingHelpView; each child cancels its OS Close and flips the shell flag; shutdown flag suppresses cancel during app exit)
- [x] F.3 Host service impls + bootstrap (`Hosting/HostColorThemeService.cs` bridges ColorPalette + UserColorThemeLibrary + DataDrivenColorThemes.Export via new `Hosting/ColorThemeDefAdapter.cs` for full Def↔Data translation; `Hosting/HostHelpContentProvider.cs` stubs the 7-tab help with short placeholders + environment-derived system info (full ~2,500 lines of FloatingHelp text migration queued as follow-up); `Hosting/AvaloniaShellBootstrap.cs` replaces the proof-of-life AvaloniaBootstrap: constructs FractalRenderHost + FractalInputController + services + ShellViewModel, wires host-handled events (ColorThemePreview → IColorMap → render host; LinkRequested → ProcessStartInfo with UseShellExecute; SaveFileRequested → temp file write; MessageRequested → console), assigns DataContext to MainWindow on UI thread once surface ready, 60 Hz System.Threading.Timer drives swap-chain presents. Program.cs --avalonia path routes through the new bootstrap.)
- [ ] G. Delete `MainForm.cs` + `Slideshow.cs` + `VideoZoom.cs` + `MainForm.resx` + WinForms entry point *(deferred — user wants legacy intact)*

#### F.3 follow-ups (deferred; not blockers for parity testing)
- [x] Real Avalonia `SaveFileDialog` via `TopLevel.StorageProvider` *(done — `Hosting/AvaloniaDialogs.SaveFileAsync` parses WinForms `Name (*.ext)|*.ext|...` filters into `FilePickerFileType`, calls `TopLevel.StorageProvider.SaveFilePickerAsync`, writes via `StreamWriter`. `AvaloniaShellBootstrap.SaveFileRequested` routes through it and fills `args.Saved`)*
- [x] Avalonia `MessageBox` impl *(done — `AvaloniaDialogs.ShowMessageAsync` builds 480-dip modal Avalonia `Window` with OK or Yes/No buttons + `TaskCompletionSource<bool>`; marshals onto UI thread for worker-thread callers. `AvaloniaShellBootstrap.MessageRequested` routes through it)*
- [x] `IPaletteExtractionService` concrete wiring through `Hosting/HostPaletteExtractionService.cs` *(done — bridges BitmapSampler + 4 extractors + PaletteStopBuilder; AvaloniaShellBootstrap defaults `PaletteService` to it and `FromImageRequested` now pops `ImagePaletteView` with browse + drag-drop, returning ColorStopDef list to the editor)*
- [x] Full FloatingHelp text migration (~2,500 lines) — extract from `Views/FloatingHelp.cs` into shared resource bundle both shells read *(done — `Abstractions/Help/HelpTextBundle.cs` now holds every tab `IHelpContentProvider` exposes plus the full 17-entry Math sub-tab list: Overview / Mandelbrot / Julia / Burning Ship / Tricorn / Multibrot / Phoenix / Newton / Nova / Buddhabrot / IFS / L-System / Attractor / Mandelbulb / User Equation / User Bulb 3D / Sandbox. `HostHelpContentProvider.MathSubTabs` reads them in legacy display order so both shells render identical content. Legacy `Views/FloatingHelp.cs` keeps its inline copies until step G lands)*
- [x] DXGI / D3D11 adapter enumeration in Hardware tab (currently env-info only) *(done — `HostHelpContentProvider.GetSystemInfoText` now mirrors legacy `FloatingHelp.BuildSystemInfoText`: DXGI adapter table + D3D11 feature level + CPU/OS + memory + SIMD width. Windows-only branches gated with `OperatingSystem.IsWindows()` so Linux/macOS shells render a friendly fallback)*
- [x] Extract `BuildCSharpSource` from `Views/ColorThemeEditor.cs` into a shared helper so `HostColorThemeService.GenerateCSharp` emits real class source instead of a JSON-comment stub *(done — `Models/ColorThemeCsExporter.cs`, both shells call it; legacy editor + `HostColorThemeService.GenerateCSharp` swapped over)*
- [x] Grid + watermark overlays via Avalonia.Media (FractalRenderHost intentionally skipped these from the legacy MainForm) *(done — `UI.Avalonia/Controls/FractalOverlayControl.cs`; toolbar `Grid` + `Watermark` ToggleButtons bind `Main.ShowGrid`/`Main.ShowWatermark`. Overlay sits in the render Grid cell with `IsHitTestVisible="False"` so input still flows to the sponge. Contrast colour is hardcoded white until `IFractalRenderHost` surfaces the active `IColorMap` — minor follow-up)*

### 2.4 — Cross-platform renderer (deferred, optional)
- [ ] Add `FracturingFog.Rendering.Skia` (SkiaSharp GPU backend) OR `FracturingFog.Rendering.Silk` (Vulkan/OpenGL via Silk.NET)
- [ ] CI build matrix: win-x64, linux-x64, osx-arm64
- [ ] ILGPU compute path validated on Linux/Mac (CUDA optional, CPU fallback required)

## Non-goals

- Mobile/touch UI (defer until cross-platform desktop ships).
- Rewriting `MandelbrotCalculator` / kernels — they are UI-agnostic already.
- Theming overhaul — match current dark theme; cosmetic redesign is a separate task.
- Removing Vortice — it stays as the Windows renderer.

## Risks

- **DirectX hosting in Avalonia**: `NativeControlHost` works but resize/devicelost handling needs care. Validate early in step 2.0.
- **MVVM extraction depth**: `MainForm.cs` has tight coupling between input, view, and renderer state. Expect leaky abstractions during transition.
- **Build time**: split projects increase first-build time. Acceptable tradeoff.
- **DPI on multi-monitor mixed scaling**: Avalonia handles per-monitor DPI natively; verify on a 100% + 150% dual-monitor setup.
- **Vortice swapchain rebuild on resize**: must hook Avalonia's `SizeChanged` not WinForms `Resize`.

## Commit cadence

- One commit per step (or sub-step) above. Format per repo convention: `<imperative summary> - BAB <yyyymmdd>`.
- Keep WinForms build green at every commit until step 2.3 deletes it.
- Tag `phase2-avalonia-bootstrap` after step 2.0 completes.

## Reference

Phase 1 (low-effort WinForms scaling fixes) is a separate parallel track on a different branch and is not blocked by this work.
