# Open Work Plan — 2026-06-20

Master execution plan rolling up every open item across the project's roadmap docs.
Source roadmaps surveyed:

- `Docs/Performance-Roadmap.md`
- `Docs/Lighting-FX-Roadmap.md`
- `Docs/Fractal-Expansion-Roadmap.md`
- `Docs/Technical/CalculatorGen-Roadmap.md`
- `Docs/Technical/Performance-DevelopmentPlan.md`
- `Docs/Technical/PHASE2_AVALONIA_MIGRATION.md`
- `Docs/Technical/CrossPlatform-Roadmap.md`
- `Docs/Technical/CrossPlatform-ImplementationPlan.md`
- `Docs/Technical/UserBulb3D-DevelopmentPlan.md`
- `Docs/Technical/UserBulbSandbox-DevPlan.md`
- `Docs/Documentation-Plan.md`

Strategy: ship-blockers first → cheap perf wins → feature lifts → polish.
Critical path = Cross-Platform roadmap (whole roadmap blocks Linux/macOS launch).

---

## Wave 0 — Quick wins (cheap, low-risk, < 1 day each)

Land first to (a) collect cheap CPU/IO wins, (b) give every later wave a
measurement baseline, (c) shake out renderer hooks needed elsewhere.

| # | Item | Status |
|---|------|--------|
| 0.1 | Perf T1.1 — strip `new StackTrace()` from `Calculate` | ✅ Already shipped — `Engine/Calculators/MandelbrotCalculator.cs:371` gated `#if DEBUG` |
| 0.2 | Perf T1.2 — VSync toggle on `DirectXRenderer.Render` | ✅ Already shipped — `IFractalRenderer.VSync` + `Rendering.D3D/DirectXRenderer.cs:449,475` |
| 0.3 | Perf T1.3 — single-shot `MemoryCopy` fast path in `UpdateTexture` | ✅ Already shipped — `Rendering.D3D/DirectXRenderer.cs:397-415` |
| 0.4 | Perf T1.4 — `EscapeTimeCalculator` concrete-colormap dispatch | ✅ Already shipped — concrete switch on both `DispatchByColorMap` + `DispatchByColorMapSimd` (~21 cases each) |
| 0.5 | Visual-regression harness — `--batch` per-fractal SHA256 baseline | ✅ Tool shipped (`Tools/VisualRegression/`). Baseline-record run is a follow-up (full app rebuild + 22 renders) |
| 0.6 | Per-stage frame-time HUD microbar | ✅ Shipped — `PostStage` enum + `StagePerf` static hook + 5 `Apply*` sites wrapped + `PerfStats.RecordStage` ring + HUD micro-rows |

> **Re-survey note (2026-06-20):** Repo geometry already split per Phase X.0
> (Engine/, FracturingFog.App/, FracturingFog.Win/, Hosting/, Rendering.D3D/,
> Audio/, Audio.Win/, Rendering.Silk/, Rendering.Skia/). Update Wave 1
> tracking after auditing what X.A/X.B/X.1/X.2/X.3 still need.

Exit: Release build, FPS up, no visual regression on the 8 stock regions.

---

## Wave 1 — Cross-Platform foundation (BLOCKS LAUNCH)

> **Re-audit 2026-06-20:** All code-side phases shipped on `feature/cross-platform-full`.
> Outstanding items are manual smoke runs on real Linux/macOS hardware + two cleanup
> items (ToyDragWindow Avalonia rewrite, FfmpegSetupDialog Avalonia rewrite). CI builds
> green for every cross-platform assembly; release workflow drafts artifacts per RID
> (zip / AppImage / .app). NETSDK1150 publish blocker resolved.

| # | Phase | Item | Status |
|---|-------|------|--------|
| 1.1 | X.0 Slice 0.1 | Carve `FracturingFog.Engine` csproj (`net10.0`) | ✅ Shipped |
| 1.2 | X.0 Slice 0.2 | Carve `FracturingFog.Audio` csproj | ✅ Shipped (`Audio/` + Win fragment `Audio.Win/`) |
| 1.3 | X.0 Slice 0.3 | Carve `FracturingFog.Hosting` csproj | ✅ Shipped |
| 1.4 | X.0 Slice 0.4 | Carve `FracturingFog.Rendering.D3D` Win fragment | ✅ Shipped (incl. `MP4Writer.cs` move) |
| 1.5 | X.0 Slice 0.5 | New `FracturingFog.App` exe target | ✅ Shipped |
| 1.6 | X.A | Image I/O SkiaSharp swap | ✅ Shipped — Engine.csproj comment confirms Slice A.2 + A.7. `System.Drawing.Common` dropped; engine uses `System.Drawing.Primitives` (BCL, x-plat) only. GDI tail moved to `FracturingFog.Win.ImageExportGdi` |
| 1.7 | X.B | Audio backend abstraction | ✅ Shipped — `Abstractions/Audio/IAudioCaptureBackend.cs` + `Audio/NoopAudioBackend.cs` + `Audio.Win/WindowsNAudioBackend.cs` + `Audio/AudioCaptureDriver.cs` |
| 1.8 | X.1 | Palette demotion | ✅ Shipped — `PaletteBuilder.Lib.csproj` TFM = `net10.0`, QuestPDF replaces PDFsharp-gdi, `System.Drawing.Common` dropped |
| 1.9 | X.2 | Video export — `IVideoWriter` | ✅ Shipped — `Engine/Imaging/IVideoWriter.cs` + `FfmpegVideoWriter.cs`; `MP4Writer.cs` moved to `Rendering.D3D` Win fragment |
| 1.10 | X.3 | P/Invoke + IsOSPlatform sweep | ✅ Mostly shipped — `BatchEntry.cs` / `ServerEntry.cs` / `MandelbrotBench.cs` / `MainWindow.ToyDragWindow` all gated `OperatingSystem.IsWindows()`. CA1416 clean. **Open:** ToyDragWindow still uses Win32 `ReleaseCapture`+`WM_NCLBUTTONDOWN` because the NativeMouseForwarder HWND callback can't synthesise `PointerPressedEventArgs`; Linux/macOS Toy-mode drag is parked — file as follow-up |
| 1.11 | X.4 | Bootstrap polish — `--renderer` CLI | ✅ Shipped — `--renderer dx\|silk\|skia\|auto` in `Program.cs:241`; CI has Linux Wayland leg via `weston --backend=headless` |
| 1.12 | X.5 | Compute fallback smoke on Apple Silicon | ⚠️ Code path shipped (`AcceleratorProbe` CPU fallback). **Open:** per-RID device-kind smoke assert + manual run on real Apple Silicon |
| 1.13 | X.6 | Packaging | ✅ Shipped — 5 pubxml profiles under `FracturingFog.App/Properties/PublishProfiles/`; `release.yml` workflow zips Win + AppImage Linux + `.app` macOS, sha256-sums, drafts GH release |
| 1.14 | X.7 | Docs | ✅ Shipped — `FEATURES.md` (cross-platform overview), `Docs/User/CrossPlatform-UserGuide.md`, README; `CrossPlatform-SmokeTests.md` enumerates per-phase manual checks |

**Remaining Wave 1 work** (not blockers for code-complete):

| # | Item | Effort |
|---|------|--------|
| 1.S1 | Run `CrossPlatform-SmokeTests.md` manual checks on real Ubuntu 24.04 (X11 + Wayland), macOS Sonoma arm64, Raspberry Pi OS arm64 | 1 d per host |
| 1.S2 | Tag a `v0.7.0-rc1` to fire `release.yml`; triage drafted artifacts; verify install + launch on each host | 1 d |
| 1.C1 | Avalonia `FfmpegSetupDialog` rewrite — remove WinForms drag from cross-platform `Hosting/` (currently WinForms shell only) | ½ d |
| 1.C2 | Toy-mode drag — synth `PointerPressedEventArgs` from NativeMouseForwarder callback so `BeginMoveDrag` works on every RID, retire `ReleaseCapture`+`WM_NCLBUTTONDOWN` | ½ d |
| 1.C3 | X.5 per-RID device-kind smoke — assert `AcceleratorProbe.Chosen.Kind` matches expectation in `--batch --self-test` | ½ d |
| 0.5b | Wave 0.5 follow-up — `dotnet run --project Tools/VisualRegression -- record` to populate `baseline.json` | ½ d (long build + 22 batch renders) |

Exit per `CrossPlatform-Roadmap.md` Definition of Done: install on Ubuntu /
Fedora / macOS 14 / Win11, launch self-contained, render Mandelbrot in 5 s,
pan/zoom/switch, export PNG, export MP4 (Win+Linux required), open
audio-reactive dialog without crash.

---

## Wave 2 — CalcGen high-impact

| # | Item | Lift |
|---|------|------|
| 2.1 | D-2.8 — SA orders 8 → 16 | 1 d |
| 2.2 | D-3.16 — Phoenix proper DE + perturbation (δ_prev + dprev/dc) | ✅ Shipped 2026-06-21 |
| 2.3 | D-6.26 — Save hot-loaded calc to permanent `.cs` | ½ d |
| 2.4 | D-6.24 — Live equation preview (AST + dz/dc + SA flag as user types) | ✅ Shipped 2026-06-21 |
| 2.5 | D-5.20 — Progressive rendering ¼→½→full | 2 d |
| 2.6 | D-5.19 — Anti-aliasing 2×2/4×4 (Quality gate) | 1 d |
| 2.7 | D-5.21 — TAA temporal accumulation | ✅ Shipped 2026-06-21 |
| 2.8 | D-6.23 — Equation cookbook + gallery | ✅ Shipped 2026-06-21 |
| 2.9 | D-6.25 — Animation: morph equations | 2 d |
| 2.10 | D-4.18 — DD-precision BLA tables | 3 d |
| 2.11 | D-4.17 — Octuple-double (OD) ref orbit — past 1e50 zoom | 5+ d |
| 2.12 | D-6.27 — GPU reference orbit (QD on GPU) | 5+ d |
| 2.13 | D-7.29 — Roslyn source generator | 1 wk |

---

## Wave 3 — Perf Tier 2 + 3 tail

| # | Item |
|---|------|
| 3.1 | T2.1 — SIMD brightness/contrast |
| 3.2 | T2.2 — Suppress pre-overlay snapshot during video record |
| 3.3 | T2.3 — `EscapeTimeCalculator` SIMD inner loop (Mandelbrot/Julia/BurningShip/Tricorn/Multibrot) |
| 3.4 | T3.3 — non-temporal `Avx.Store*` writes |
| 3.5 | T3.2 — ref-orbit recycling across video frames |
| 3.6 | T3.1 ext — HLSL palette codegen for hand-written `IColorMap`; GPU `ColorBuffer` for orbit-aware themes |
| 3.7 | Finding D — Adaptive HE crossfade lerp |
| 3.8 | Pan/keyboard input fails at zoom ≥ 1e24 — QD-limb update in pan-zoom command pipeline |

---

## Wave 4 — Lighting/FX + UserBulb features

| # | Item |
|---|------|
| 4.1 | Lighting-FX 21b GPU port — HDR DoF skewed blurs on ILGPU |
| 4.2 | Lighting-FX 16b GGX importance sampling per bounce |
| 4.3 | Lighting-FX — HDRI auto-preload on param change |
| 4.4 | Sandbox 3C — interpreter perf (opcode-flat dispatch or DynamicMethod IL emit) |
| 4.5 | Sandbox chain mode GPU dispatch |
| 4.6 | Sandbox Quat-mode Julia + numerical-Jacobian DE on GPU |
| 4.7 | UserBulb 3.4 — time global `t` + animate bar |
| 4.8 | UserBulb 3.7 — color drivers |
| 4.9 | UserBulb 3.9 — FOV / DoF / clip / SS + viewport orbit |
| 4.10 | UserBulb 3.6 — multi-equation chain w/ named outputs |
| 4.11 | UserBulb 3.10 — preset library seed |
| 4.12 | UserBulb 3.11 — marching-cubes mesh export OBJ/STL |
| 4.13 | UserBulb 3.12 — `.fbulb` import/export |
| 4.14 | UserBulb 3.5 — Julia mode Vec3 path |

---

## Wave 5 — Fractal Expansion polish

| # | Item |
|---|------|
| 5.1 | Theme compatibility matrix audit (A.1–D.2 new families) |
| 5.2 | Region preset coverage audit |
| 5.3 | CalcGen reach verification (A.1/A.2/A.5/A.6 5-path) |
| 5.4 | Math help 2-level grouping (>25 sub-tabs) |
| 5.5 | `FEATURES.md` "20+ families" → ~38; README badge counter |
| 5.6 | Allowlist negative tests for 19 new types |
| 5.7 | Headless visual-regression baseline (golden PNG per type) |
| 5.8 | `FractalParamsView.axaml` per-type extract |
| 5.9 | B.2 KIFS new folds (Mandelbox-rot / Octahedron / Dodecahedron) |
| 5.10 | D.5 L-System 5 more presets |
| 5.11 | D.4 Flame next 8-16 Apophysis variations |
| 5.12 | B.4 Kleinian user-editable sphere list + Möbius composition + analytic DE |
| 5.13 | D.2 DLA cached-blit pan/zoom + multi-seed + sticky-coef |
| 5.14 | C.3 Bicomplex 2nd-slice-axis + split-complex variant |
| 5.15 | D.1 Apollonian sub-gasket filled rendering (low pri) |

---

## Wave 6 — Multi-cluster glitch rebase

| # | Item |
|---|------|
| 6.1 | Multi-cluster spatial partitioning for perturbation rebase |

---

## Wave 7 — Docs

| # | Item |
|---|------|
| 7.1 | Top-level `Docs/_Index.md` landing page for both audiences |

---

## Wave 8 — WinForms retirement (USER-GATED)

Do not start without explicit user signal — current note: "user wants legacy intact."

| # | Item |
|---|------|
| 8.1 | Phase 2.3 G — delete `MainForm.cs` + `Slideshow.cs` + `VideoZoom.cs` + `MainForm.resx` + WinForms entry |
| 8.2 | Phase 2.1 deferred — move `DirectXRenderer.cs` to `Rendering.D3D` + `HwndGpuSurface` |
| 8.3 | B4 — retire `Views/ImagePaletteDialog.cs` `GdiToSkia` bridge |

---

## Parallelisation map

```
Track A (lead dev):
    Wave 0 → Wave 1.1-1.5 → Wave 1.6-1.9 → Wave 1.10-1.14

Track B (second dev, after Wave 0):
    Wave 2 CalcGen (independent files)

Track C (third dev, after Wave 0):
    Wave 4 UserBulb + Sandbox (independent files)

Convergence after Wave 1:
    Wave 3 perf → Wave 5 polish → Wave 6 → Wave 7
```

## Estimates (single dev)

| Wave | Days | Notes |
|------|------|-------|
| 0 | 3 → **0** | Shipped |
| 1 | 25-30 → **~4** | Re-audit: code shipped; cleanup + smoke only (1.S1/1.S2/1.C1-3/0.5b) |
| 2 | 20-25 | CalcGen high-impact |
| 3 | 10 | Perf Tier 2 + 3 tail |
| 4 | 15 | Lighting/FX + UserBulb |
| 5 | 10 | Fractal Expansion polish |
| 6 | 3 | Multi-cluster glitch rebase |
| 7 | 1 | Docs landing page |
| 8 (when greenlit) | 2 | WinForms retirement |
| **Total** | **~65 dev-days** | Down from 90 after Wave 0+1 re-audit |

3 parallel tracks → ~9 cal weeks. Single dev → ~13 weeks.

---

## Status log

- 2026-06-20 — Plan drafted from full roadmap survey. Wave 0 in progress.
- 2026-06-20 — Wave 0 complete. 0.1–0.4 found already shipped (re-survey).
  0.5 visual-regression tool added at `Tools/VisualRegression/`. 0.6
  per-stage HUD microbar wired via `StagePerf` static hook +
  `PerfStats.RecordStage` ring + 5 `Apply*` sites + HUD micro-rows.
  Full solution builds clean (`dotnet build FracturingFogCLD.sln`).
- 2026-06-20 — Repo re-survey shows Phase X.0 geometry split largely
  shipped already (Engine/, FracturingFog.App/, FracturingFog.Win/,
  Hosting/, Rendering.D3D/, Audio/, Audio.Win/, Rendering.Silk/,
  Rendering.Skia/). Wave 1 needs re-audit before resuming.
- 2026-06-20 — Wave 1 re-audit complete. Code-side **all 9 phases shipped**
  (X.0 / X.A / X.B / X.1 / X.2 / X.3 / X.4 / X.6 / X.7). X.5 has code path
  via `AcceleratorProbe` CPU fallback, missing per-RID device-kind smoke
  assert. CI green for all cross-platform assemblies; `release.yml`
  draft-publishes per RID (zip / AppImage / `.app`). Remaining: manual
  smoke runs (1.S1/1.S2), two Avalonia rewrites (1.C1 FfmpegSetupDialog,
  1.C2 ToyDragWindow), X.5 device-kind assert (1.C3), Wave 0.5
  baseline-record (0.5b). Wave 1 effort drops from 25-30 d to ~4 d of
  cleanup + smoke. Wave 1 is no longer the launch blocker it was
  estimated as; **Waves 2-7 unblocked**.
- 2026-06-20 — Wave 1 cleanup + Wave 2 partial complete.
  * 1.C3 ✅ `--ilgpu-probe` flag wired in both `Program.cs` (WinExe) and
    `FracturingFog.App/Program.cs`; runs `AcceleratorProbe.RunSmoke` to
    enumerate devices + assert CPU fallback; per-RID expectation gates
    CUDA-on-ARM as a packaging bug.
  * 1.C2 ✅ Toy-mode drag wires `BeginMoveDrag(e)` via InputSponge
    `PointerPressed` handler (`AttachToySpongeDrag` in `MainWindow.axaml.cs`).
    Win-only Win32 trick stays as fallback for the DX-HWND case where
    Avalonia events don't reach the sponge.
  * 1.C1 ✅ partial — `FfmpegSetupDialog.cs` re-included in cross-platform
    `FracturingFog.Hosting.dll` (Avalonia, no Audio/Views/Palette deps).
    `AvaloniaDialogs.cs` deferred — pulls `FracturingFog.Views.*`,
    `PaletteBuilder.*`, and `AvaloniaShellBootstrap` statics; needs
    broader carve.
  * 2.3 ✅ `CalculatorGenHotLoad.PersistAndLoad` + `LoadAllPersisted`;
    `HotLoadAndPersistRequested` event in VM; "Compile + Save" button in
    `UserEquationView.axaml`; host warm-loads persisted .cs on boot from
    `%LOCALAPPDATA%/FracturingFog/UserCalculators/`.
  * 2.1 ✅ SA orders bumped 8 → 16 in template + emitter; all 10
    generated calculators (`MandelbrotZ{2..5}`, `MandelbrotPhoenix`,
    `MandelbrotTricorn`, `MandelbrotBurningShip`, `BurningShip`,
    `Tricorn`, `UserDslEquation`) regenerated. `--gentest MandelbrotZ2`
    PASS; `--saprobe` shows healthy distribution at zoom 1e9-1e16
    (gen vs legacy color counts within ±10).
  * 2.6 ✅ `QualityPreset.AaSamples` field (Standard=1, High=4,
    Ultra/Extreme=16) + render-host `RunMsaaAccumulateMandelbrot`
    averages N sub-pixel jittered passes for the canonical
    `MandelbrotCalculator` path. Alt calcs (user-equation / sandbox)
    currently skip AA pending interface broadening.
- 2026-06-21 — Wave 2.2 (D-3.16) shipped — Phoenix proper DE + scalar
  perturbation tier.
  * `PhoenixKernel.StepWithPrevDeriv` carries `(dr, di, dprev_r, dprev_i)`
    via recurrence `D_{n+1} = 2·z·D + 1 + p·Dp`. Init (0,0,0,0).
  * `EscapeTimeCalculator.CalculatePhoenix` now passes real derivative
    to `FillAuxAndColor` — distance estimate + normal shading active
    for Phoenix where they were zeroed before (visible: edge glow,
    proper DE-based contrast).
  * New `CalculatePhoenixPerturb` tier gated at `Zoom >= 1e10`. Builds
    double-precision reference orbit at frame centre, then per-pixel
    `δ + δ_prev` recurrence:  `δ_{n+1} = 2·Z·δ + δ² + ε + p · δ_prev`.
    Reconstructs true `(z, prev_z)` each step so derivative tracking
    stays exact. Scalar only — no AVX2 / QD / BLA / glitch detection
    (deferred follow-on); enough headroom to unstick the SP-only
    iteration-count collapse Phoenix hits at deep zoom.
  * Generated `MandelbrotPhoenixCalculator.cs` left untouched — unused
    by dispatch (`FractalType.Phoenix` → `EscapeTimeCalculator`); banner
    still says "DO NOT HAND-EDIT — copy + rename for divergent math".
    Future Roslyn-source-gen path (Wave 2.13) will regenerate it
    correctly once the emitter handles prev-z as a function of c.
- 2026-06-21 — Wave 2.7 (D-5.21) shipped — TAA temporal accumulation.
  * `QualityPreset.TaaMaxSamples` field — 1 = off (Draft/Standard), 8 (High),
    16 (Ultra), 32 (Extreme). Caps total accumulated samples per still-camera
    spell so the loop terminates.
  * `FractalRenderHost` carries per-pixel R/G/B/A `long[]` accumulator + view
    fingerprint (Cx/Cy/Zoom/Iter/Width/Height/FractalType). First frame of a
    given view seeds the accumulator from the freshly-computed (and
    optionally MSAA-averaged) `ColorBuffer`. After each upload, if the
    fingerprint still matches and count < TaaMaxSamples, the host enqueues a
    new `FrameJob` with `TaaSampleIndex > 0`. The calc thread branches: jitter
    `Center{X,Y}` by Halton(2,3) sub-pixel, `Calculate()`, restore Center,
    blend the new colors into the sums, average → `ColorBuffer`, hand off to
    the standard upload path. Brightness/contrast + grid/watermark composite
    still apply to the averaged buffer.
  * `Resize` drops the accumulator (sized for old buffers). View changes
    self-invalidate via the fingerprint check; any user `Trigger()` drains
    the bounded(1) calc queue, so a queued TAA continuation gets replaced by
    the fresh frame before it runs.
  * Bypassed when `useAlt` is set (Burning Ship / Tricorn / user-equation hot
    load all run alt calcs). Only the canonical Mandelbrot path gets TAA;
    extending to alt calcs needs each one's `Center{X,Y}` plumbed the same
    way — deferred follow-on.
  * Build clean (0 errors, 4 pre-existing AVLN5001 Watermark warnings).
- 2026-06-21 — Wave 2.8 (D-6.23) shipped — Equation cookbook + gallery.
  * `UI.Avalonia/ViewModels/EquationCookbook.cs` — 14 curated `CookbookEntry`
    rows: Mandelbrot z²/z³/z⁴/z⁵, Tricorn, Burning Ship, Phoenix, Sin / Cos /
    Exp Mandelbrot, Lambda (Logistic), Newton z³−1, Magnet 1, mixed
    quadratic. Each carries a hand-tuned (centre, zoom) framing.
  * `CookbookViewModel` + `CookbookView.axaml` — modeless picker dialog. Left
    column lists entries by name; right column shows DSL source + centre/zoom
    + description. Enter / "Use this equation" accepts; Escape / Cancel
    closes. Selection-driven properties (`SelectedName` / `SelectedDescription`
    / `SelectedSourceDisplay` / `SelectedCentreDisplay`) keep XAML bindings
    off the nullable struct, which Avalonia x:DataType doesn't traverse.
  * `UserEquationViewModel` — `OpenCookbookCommand` + `CookbookRequested`
    event opens the picker; `ApplyCookbookEntry(entry)` writes the source
    into `DslSource`, snaps to the DSL tab, and fires
    `CookbookCentreRequested(cx, cy, zoom)` so the host re-centres the view.
    Editor preview panel picks up the new source via the existing
    DSL-validate path (no new wiring).
  * `AvaloniaShellBootstrap.OpenUserEquationEditor` — wires both events; the
    cookbook window is shown modeless as a child of the equation editor, and
    `CookbookCentreRequested` writes `ViewState.{CenterX,CenterY,Zoom}` then
    calls `Trigger()`.
  * `UserEquationView.axaml` — new "Cookbook…" button in the action row.
  * Build clean (0 errors).
- 2026-06-21 — Wave 2.4 (D-6.24) shipped — Live equation preview.
  * New `CalculatorGenApi.Preview(equation) → PreviewResult` returns the
    parsed AST in printed form (`AstPrinter.Print`), symbolic `dz/dc` and
    `dz/dz` (`AstDifferentiator.{DpDc,DpDz}`), SA gating (both fast and
    generic detectors), perturbation + DE feature flags, plus per-node
    presence flags (prev / iter / conj / fold / div / trans / cond). Same
    analysis pass the generator runs, exposed as a read-only projection
    with no file I/O so it's safe per keystroke.
  * `UserEquationViewModel` adds observable preview state and calls
    `UpdatePreview` from both the UE-tab CalcGen validator and the
    DSL-tab validator after a successful parse. Last-good values stay
    pinned on transient parse failures so the panel doesn't flicker.
    Dialog seeds the preview from the current source on open.
  * `UserEquationView.axaml` adds an `Expander` between the editor and
    the status row showing AST / dz/dc / dz/dz / SA / Perturbation / DE
    / Flags. SelectableTextBlocks, monospace. Window grew to 700×680 to
    accommodate.
  * Build clean (0 errors, only pre-existing AVLN5001 Watermark warnings).
