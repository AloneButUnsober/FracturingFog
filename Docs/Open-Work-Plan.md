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
| 1.C1 | Avalonia `FfmpegSetupDialog` rewrite — remove WinForms drag from cross-platform `Hosting/` (currently WinForms shell only) | ✅ Shipped 2026-06-22 |
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
| 2.5 | D-5.20 — Progressive rendering ¼→½→full | ✅ Shipped 2026-06-22 |
| 2.6 | D-5.19 — Anti-aliasing 2×2/4×4 (Quality gate) | 1 d |
| 2.7 | D-5.21 — TAA temporal accumulation | ✅ Shipped 2026-06-21 |
| 2.8 | D-6.23 — Equation cookbook + gallery | ✅ Shipped 2026-06-21 |
| 2.9 | D-6.25 — Animation: morph equations | ✅ Shipped 2026-06-21 |
| 2.10 | D-4.18 — DD-precision BLA tables | ✅ Shipped 2026-06-21 |
| 2.11 | D-4.17 — Octuple-double (OD) ref orbit — past 1e50 zoom | ✅ Shipped 2026-06-21; OD arithmetic fixed + re-enabled 2026-06-22 (op* rewrite + 23 xUnit OD parity tests in `Server.Tests/OctupleDoubleTests.cs`). UI navigation past 1e58 still pending — see status log |
| 2.12 | D-6.27 — GPU reference orbit (QD on GPU) | 🟡 Scaffold shipped 2026-06-22 (Hi-only kernel works on CUDA). QD upgrade + perf-win analysis **deferred** as non-blocking follow-on — toggle off by default, no other wave depends on it |
| 2.13 | D-7.29 — Roslyn source generator | ✅ Shipped 2026-06-22 |
| 2.14 | D-4.19 — QD δ-chain precision floor — fix pixelation at zoom 1e40–1e58 | 🟡 Deferred 2026-06-22 — 3–5 d, no other wave depends on it; revisit after Wave 3 perf tail |
| 2.15 | D-4.20 — OD-aware UI navigation — populate `CenterX4..X7` from pan/zoom | ✅ Shipped 2026-06-22 (`FractalInputController.cs` — 6 pan/zoom sites + OD pan-start cache + `StoreOD` helper) |

---

## Wave 3 — Perf Tier 2 + 3 tail

| # | Item |
|---|------|
| 3.1 | T2.1 — SIMD brightness/contrast | ✅ Already shipped — `FractalRenderHost.cs:2181` `ProcessRowSimd` Vector256 (8 BGRA pixels/step), chunked Partitioner, pooled LOH dst buffer |
| 3.2 | T2.2 — Suppress pre-overlay snapshot during video record | ✅ Already shipped — `FractalRenderHost.cs:2085` `if (!_recordingActive)` gate; pooled `_uploadPrePool` LOH |
| 3.3 | T2.3 — `EscapeTimeCalculator` SIMD inner loop (Mandelbrot/Julia/BurningShip/Tricorn/Multibrot) | ✅ Shipped 2026-06-22 — Mandelbrot/Julia/BurningShip/Tricorn already SIMD; Multibrot d∈{3,4,5} added (direct complex-mul scalar + `StepSimd`); d≥6 keeps polar fallback. `SimdSupported` flag drives dispatch in `EscapeTimeCalculator.Calculate` |
| 3.4 | T3.3 — non-temporal `Avx.Store*` writes | ✅ Shipped 2026-06-22 — `ProcessRowSimd` gains `StoreAlignedNonTemporal` fast-path when dst is 32-byte aligned; pre-loop alignment check splits two hot loops |
| 3.5 | T3.2 — ref-orbit recycling across video frames | 🟡 Deferred 2026-06-22 — needs orbit-validity checkpoint infrastructure (re-eval cached orbit vs new dc, reuse when |δ_n| stays within BLA radius); ~1-2 d, revisit after Wave 4 |
| 3.6 | T3.1 ext — HLSL palette codegen for hand-written `IColorMap`; GPU `ColorBuffer` for orbit-aware themes | ✅ Shipped 2026-06-22 — `HsvPalette` + all 19 sibling hand-written themes now implement `IGpuHlslPalette`. Shared HLSL prelude in `Engine/Models/HlslPaletteHelpers.cs` (cg_mods + cg_hsv_to_rgb mirroring Fractals.HsvToRgb). Auto-picked by `EscapeTimeCalculator.TryDispatchGpu` |
| 3.7 | Finding D — Adaptive HE crossfade lerp | ✅ Shipped 2026-06-22 — `RecolorActiveToBuffer` now bakes HE into the recolor target via `BuildHistogramCdf` + `ApplyHistogramEqualizationWithCdf` when `ViewState.HistogramEq > 0`; eliminates the post-fade snap |
| 3.8 | Pan/keyboard input fails at zoom ≥ 1e24 — QD-limb update in pan-zoom command pipeline | ✅ Superseded by Wave 2.15 (2026-06-22) — `FractalInputController.cs` all 6 pan/zoom sites carry OD/QD/DD/SP branches with `StoreOD`/`StoreQD`/`StoreDD` writing all limbs |

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
| 4.7 | UserBulb 3.4 — time global `t` + animate bar | ✅ Shipped 2026-06-22 |
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
- 2026-06-22 — DD-BLA perf gating. User report: "performance at medium
  depth seems empirically worse once DD kicks in"; right-click outline-
  to-zoom lagging at ~1e18. Root cause: Wave 2.10 ran the DD-precision
  BLA merge unconditionally across the entire DD tier (1e12 → 1e25).
  `MergeDd` is ~7× the flops of `MergeDouble` (4 DdComplexMul + 2 DdAdd
  vs 10 muls/adds), and BLA rebuilds on every centre / orbit change —
  every pan event paid the full DD merge cost. Below ~1e15 the merged
  A_n ULP error hasn't yet accumulated to visible iteration banding.
  Fix: added `DdBlaZoomThreshold = 1e15` constant in
  `MandelbrotCalculator.cs`. `EnsureBlaTable` now picks DD merge only
  when `Zoom > 1e15 && !DisableDdBla`; below that, legacy single-
  precision merge (restoring pre-2.10 pan-responsive perf at the
  Standard / shallow-zoom tier). Above 1e15 DD merge fires unchanged.
  Outline lag at 1e18 is a downstream symptom: `SetSelectionBox` →
  `RepaintWithPostFx` shares the `_uploadGate` lock with `Calculate` —
  while a slow deep-zoom Calculate is in flight, every drag-event
  overlay update blocks behind it. Real fix is a perf budget that
  keeps Calculate under ~50 ms; tracked as 2.14 (DD δ-chain) +
  separate per-pixel QD/OD optimization wave.
- 2026-06-22 — Wave 2.15 shipped — OD-aware UI navigation. User report:
  click-drag pan at deep QD/OD zoom collapsed CX/CY to 2 limbs in the
  main menu. Root cause: `FractalInputController.cs` had `RequiresQD`
  + `RequiresDD` pan/zoom branches but no `RequiresOD` branch — at
  zoom > 1e50 (OD threshold) the pan fell into the plain-double `else`
  arm and `ClearLowLimbs()` wiped CenterX/Y_Lo..X3. Even at QD zoom
  the `StoreQD` helper never touched CenterX/Y_4..7, so stale OD
  limbs from a Copy/Paste survived a pan-zoom cycle. Fix:
  * Added `_panStartODCX/CY` cache, initialised in `OnPointerDown`.
  * Added `OD` branch as first arm of all six pan/zoom sites (pan
    move, box zoom, double-click pan, wheel zoom, key-pan, all
    threading through the same RequiresOD → QD → DD → SP precedence).
  * New `StoreOD(OD cx, OD cy)` helper writes all 8 limbs from an OD.
  * Extended `ClearLowLimbs` / `StoreDD` / `StoreQD` to also clear
    X4..X7 (consistent with their tier — DD has no X2/3 either).
  * Build clean, 140/140 tests pass.
- 2026-06-22 — Wave 2.14 filed. Path B (DD-precision PT δ chain)
  required to push usable zoom past 1e58. Path A (OD arithmetic fix)
  + Path C (OD-aware navigation, shipped today as 2.15) are necessary
  but not sufficient by themselves — see 2.11 entry below.
- 2026-06-22 — User test of OD path. Coord:
  ```
  CX = -1.9918151296901943|-7.8219844803880472E-17|1.6601399303929208E-34|-5.8601391417687406E-51
  CY = -5.5240415753972429E-06|-2.8659813126937928E-22|6.6910924132216174E-39|-2.0109018297360669E-55
  Zoom = 1.0E+51
  ```
  Threshold engagement: zoom > 1e50 enters OD path. Expected: no
  solid-colour render, no NaN, no all-black; visual quality similar
  to QD at same zoom (Path A only fixes the OD-arithmetic regression,
  not the underlying QD precision floor — those need 2.14 / 2.15).
- 2026-06-22 — Wave 2.11 OD arithmetic fix (Path A) — `operator *` rewritten.
  Root cause: original tier-by-tier accumulator reused `ThreeSum` residual
  variable names (`r1a/r1b` overwritten by tier-2 `ThreeSum`, `r3a/r3b` by
  tier-3 `TwoSum`), silently dropping tier-1 + tier-3 residual mass on every
  multiply. ~1e-32 noise per multiply compounded through the ref orbit and
  bubbled into X0 at iter ~127, collapsing every pixel to one colour at
  zoom ≥ 1e40. Fix: replaced with stackalloc 9-slot expansion accumulator
  (`AddPair` / `AddProduct` push partial products into the expansion via
  TwoSum cascade, residuals propagate forward without name reuse).
  * Threshold restored: `ODZoomThreshold = 1e50` in both
    `MandelbrotCalculator.cs` and `FractalViewState.cs`.
  * Tests: 23 xUnit OD parity + stress tests in
    `Server.Tests/OctupleDoubleTests.cs`. Key invariant — at user's
    pixelating coord (zoom 7.14E48), OD/QD agree to better than 1e-55
    on X0 through iter 200 (beyond which QD's own precision floor
    swamps the comparison; deep-iter test verifies OD stays finite to
    iter 5000).
  * Saprobe smoke (1e9 → 1e60) runs without crash / NaN. Visual parity
    past 1e50 requires UI navigation populating CenterX4..X7 — the
    saprobe coord has zero X2..X7, so OD behaves identically to QD
    there regardless of fix.
  * Pre-existing QD-floor pixelation at user's 7.14E48 coord remains
    open. OD threshold engages only past 1e50, so QD path still runs
    at that exact zoom; user-reported pixelation there is the QD
    precision wall on a specific orbit (chaos amplifies QD's 1e-62 ULP
    over ~600 iters to swamp the 5e-52 pixel scale). Path B (DD-precision
    PT δ chain) or full OD navigation pipeline needed — separate wave.
- 2026-06-21 — Wave 2.11 regression. OD engagement at zoom 7e48 (after
  threshold lowered from 1e50 → 1e40) produced solid-colour render at
  user's prior-working 1e58 location. Suspect bug in `OD operator+`
  carry cascade or `Renorm9` redistribution. Restored prior behaviour
  by setting `ODZoomThreshold = 1e100` in both
  `MandelbrotCalculator.cs` and `FractalViewState.cs` — OD code stays
  compiled but inert; QD path runs at any practical zoom (verified
  user-side to ~1e58). **Resolved 2026-06-22 — see entry above.**
- 2026-06-21 — Wave 2.11 (D-4.17) shipped (engine MVP) — Octuple-double
  (OD) reference orbit, 8-limb extended precision, ~124 decimal digits.
  Pushes the legacy `MandelbrotCalculator` zoom ceiling past 1e50 toward
  the ~10¹¹⁶ OD limit. `OdEmitter` for generated calcs is a deferred
  follow-up (large mechanical port of the QD path; not blocking).
  * `Abstractions/Math/OctupleDouble.cs` — new `OD` readonly struct
    (X0..X7), mirrors `QD` API. HLB primitives `TwoSum / QuickTwoSum /
    TwoProduct / ThreeSum` carried over. `Renorm9` — 9-term QuickTwoSum
    cascade reducing to canonical 8-term form. Add (sloppy two-pass
    residual sweep), Sub, Mul (diagonal-by-diagonal partial product
    accumulation across 8 tiers — tier 6 keeps Hi-only on outer terms,
    tier 7 collapses to scalar mul; sufficient for ~124-digit retention
    after renormalize), Square (= this·this), Div (long-division by 8×
    Newton refinement on Hi limb). Implicit `OD ← double`, explicit
    `(double)OD`, `ToDD()`, `ToQD()`, `FromCenterOffset(center, pixel,
    scale)` for the per-pixel coord factory.
  * `Abstractions/ViewState/FractalViewState.cs` — `CenterX4..X7` /
    `CenterY4..Y7` properties, `ODZoomThreshold = 1e50` const,
    `RequiresOD` flag. `ResetView` / `SnapToFractalDefault` clear all
    8 limbs. `RequiresQD` now gated `&& !RequiresOD` so QD doesn't
    fire when OD is required.
  * `Engine/Calculators/MandelbrotCalculator.cs` — adds the same 4-limb
    center props (`CenterX4..X7` / `CenterY4..Y7`), `ODZoomThreshold`
    const, OD limbs 4..7 of the reference orbit storage (`_refZrX4..X7`
    / `_refZiX4..X7`), and `_refCx4..X7` / `_refCy4..Y7` for the
    centerSame cache check. New `EnsureRefOrbitCapacity(maxIter)` —
    single allocation point for all 8-limb arrays so QD/OD paths share
    storage. DD and QD `centerSame` updated to also require X4..X7
    zero (avoids stale OD orbit reuse when zoom drops back through
    1e50). New `ComputeReferenceOrbitOD(OD cx, OD cy, maxIter)` writes
    all 8 limbs per slot. New `ComputePixelOD` mirrors `ComputePixelQD`
    with OD subtraction in the SA prelude + OD inner iteration (per-
    pixel HP fallback when PT glitches at zoom > 1e50). `Calculate()`
    branches three-way: OD > 1e50 → QD > 1e25 → DD ≤ 1e25.
    `ComputeRowPTScalar` / `ComputeRowPT4` / `ComputeRowPT8` all
    detect `useOD` separately, build `cy_od`, and route glitched/tail
    pixels through OD instead of QD when active.
  * `Program.cs` — `--saprobe` ladder extended with 1e30 + 1e60 cases
    to exercise QD and OD code paths through the legacy calculator.
    At those zooms the probe coords (DD-only precision) are below
    pixel scale, so the result collapses to a single colour as
    expected; the verification is that the code path runs without
    crash, NaN, or memory issue. Real visual verification past 1e50
    waits on the pan-zoom OD limb plumbing (out of scope this wave —
    requires UI cursor → OD-limb propagation, similar to the existing
    QD limb pan handling).
  * Build clean (0 errors, 24 pre-existing warnings).
  * Known limitation: pan/zoom UI does not yet promote a screen
    cursor to non-zero CenterX4..X7. Until that lands, OD ref orbit
    runs at DD-precision center → output collapses past zoom 1e16.
    Filing as follow-up wave (similar shape to the existing QD limb
    pan handling at `Engine/Calculators/MandelbrotCalculator.cs:1450`).
  * Known limitation: generated calcs (`Engine/Calculators/Generated/*`)
    still cap at QD. `OdEmitter` is the next sub-task — large mechanical
    port mirroring `QdEmitter` + `QdDirectEmitter`. Deferred to keep
    Wave 2.11 shippable.
- 2026-06-21 — Wave 2.10 (D-4.18) shipped — DD-precision BLA tables.
  * `Engine/Math/Bla.cs` — `Bla` struct now stores A, B as double-double
    pairs (`AReHi/AReLo, AImHi/AImLo, BReHi/BReLo, BImHi/BImLo`). Public
    `ARe / AIm / BRe / BIm` properties return `Hi + Lo` collapsed, so all
    11 apply sites (SIMD broadcasts + scalar reads) work unchanged — one
    add per skip, negligible vs the merge-precision win.
  * New `BlaTable(refZr, refZrLo, refZi, refZiLo, refLen, dcMaxAbs)`
    constructor seeds level-0 from the DD reference orbit: `A = 2·Z`
    with `A.Lo = 2·refZLo` (multiply by 2 is exact in FP, so Lo
    carries through unchanged), `B = 1` exactly. Merge math
    (`MergeDd`) runs in DD throughout using `TwoSum`/`TwoProduct`
    primitives mirroring `Abstractions/Math/DoubleDouble.cs` — complex
    DD × DD for `A_m = A2·A1` and `B_m = A2·B1 + B2`. Validity radius
    uses collapsed magnitudes (radius precision not load-bearing).
  * Legacy single-precision ctor still emits `Lo=0` for all limbs; the
    generic `BlaTable(Bla[] level0, …)` overload picks the
    single-precision merge path (`MergeDouble`) so generated calcs are
    bit-identical to pre-2.10. New `DdPrecision` flag exposes which
    merge ran for the diagnostic log (`BLA-DD:` vs `BLA:`).
  * `MandelbrotCalculator.EnsureBlaTable` always picks the DD ctor when
    in the HP path — `_refZrLo / _refZiLo` are populated unconditionally
    (DD low limb for Zoom ≤ 1e25, QD X1 limb for Zoom > 1e25) so DD-BLA
    fires across the entire perturbation regime, not just near the
    QD threshold.
  * Smoke: `--saprobe` deep-zoom histogram at z=1.08e12…1e16 — distinct
    colour counts stable, legacy calc tracks generated `MandelbrotZ2`
    within ~5%, no iteration banding or solid-blob collapse.
  * Build clean (0 errors).
- 2026-06-21 — Wave 2.9 (D-6.25) shipped — Animation: morph equations.
  * `UI.Avalonia/ViewModels/EquationMorph.cs` — synth helper. Wraps two DSL
    sources A and B into `(1-t)*(A) + t*(B)` with `t` baked as a numeric
    literal. Endpoint shortcut (`t=0` → A verbatim, `t=1` → B verbatim) skips
    the wrap-around so the parser never sees `0.0 * (foo)` noise. Validate
    helper parses A, B, and the mid-morph string (defensive — catches the rare
    case where both sides parse but the wrap trips a limit).
  * `UI.Avalonia/ViewModels/EquationMorphViewModel.cs` + `Views/EquationMorphView.axaml` —
    modeless dialog. Two cookbook combos populate A + B (also free-edit
    TextBox), `FrameCount` (default 60, clamp 2..600), `OutputDir` (defaults
    to `%PICTURES%/FracturingFog/Morph`). Start/Stop + progress bar +
    cancellation. VM drives the loop; per-frame work delegated to host via
    `RenderAndSaveRequested(synth, outPath, ct) → Task<string?>` event.
  * `Hosting/AvaloniaShellBootstrap` — wires `MorphRequested` to open
    `EquationMorphView` modeless under the UE editor. `RenderAndSaveRequested`
    handler hot-compiles the synth DSL via `CalculatorGenHotLoad.TryCompileAndLoad`,
    installs the result via `SetDynamicAltCalculator` (which triggers a
    render internally), subscribes a one-shot `AnimationFrameUploaded`
    handler to await upload (30 s timeout per frame), then calls
    `SaveLastFrameToPng(outPath)`. Output sequence is `morph_0000.png` …
    `morph_NNNN.png`; user assembles into MP4/GIF with ffmpeg/OBS.
  * `UserEquationViewModel` — `OpenMorphCommand` + `MorphRequested` event.
  * `UserEquationView.axaml` — new "Morph…" button in the action row, between
    "Cookbook…" and "Compile & Load".
  * SA is implicitly off across the sweep — the cross term `(1-t)*(A) + t*(B)`
    almost always trips at least one of CalcGen's SA gates (conj/sin/fold/div
    etc.) once either A or B contains a non-polynomial op, and the polynomial
    structure changes per frame anyway. Spec calls this out as required;
    enforcement falls out of existing gating.
  * Build clean (0 errors).
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
- 2026-06-22 — Wave 2.5 (D-5.20) shipped — Progressive rendering ¼ → ½ → full.
  * `Engine/Rendering/FractalRenderHost.cs` — two new sidecar
    `MandelbrotCalculator` instances (`_previewCalcQuarter`,
    `_previewCalcHalf`) permanently sized to (W/4, H/4) and (W/2, H/2),
    floor 64×64. Memory cost ~25 MB pinned LOH on top of the main
    calc's ~80 MB at 1080p. `Resize` updates both in step.
  * `FrameJob.ProgressiveStage` int (0 = final, 2 = half, 4 = quarter).
    `Trigger(progressive: true)` enqueues a quarter-stage job; the
    upload tail schedules the next stage (4 → 2 → 0). Gated on the
    canonical Mandelbrot path (no `useAlt`, no `_dynamicAltCalculator`)
    and surface ≥ 256×256 — alt calcs and tiny windows fall back to
    a single full render.
  * `MirrorMandelbrotState(src, dst)` copies all 8 centre limbs +
    zoom + iter + quality + colour map + acceleration flags onto the
    sidecar so the preview reproduces the main view at downsample.
  * Preview upload pushes the sidecar's `ColorBuffer` at its smaller
    dims; `DirectXRenderer.EnsureTexture` recreates the texture at
    those dims and the full-screen quad sampler stretches to the
    back buffer. No overlay composite, no TAA seed, no MSAA, no SSAO,
    no CDF rebuild, no `FrameCompleted` event for non-final stages —
    those run only on the final full-res stage as before.
  * `UI.Avalonia/ViewModels/MainViewModel.cs` — `RenderHint.Fast`
    now calls `Trigger(progressive: true)` instead of `TriggerFast()`.
    Each pan / wheel event cancels the in-flight chain (shared CTS)
    and restarts at ¼ res. Pan-stop debounce kept as backstop for
    single-Fast callers that don't follow up with a Full hint.
  * Build clean (0 errors). 140/140 server tests pass.
- 2026-06-22 — Wave 2.12 (D-6.27) — GPU reference orbit scaffold landed.
  Hi-only kernel runs end-to-end on CUDA; QD upgrade deferred to next slice.
  * `Engine/Calculators/Gpu/GpuQD.cs` — ILGPU-friendly QD math (mirror of
    `Abstractions/Math/QuadDouble.cs`). Tuple-returning primitives,
    `AggressiveInlining`. Uses Dekker split-based `TwoProduct` instead of
    `Math.FusedMultiplyAdd` — ILGPU 1.5.3 doesn't intercept the BCL FMA
    intrinsic, so the FMA form throws "internal compiler error" during
    JIT. Built but not yet invoked by the kernel.
  * `Engine/Calculators/Gpu/MandelbrotRefOrbitGpu.cs` — single-thread
    sequential kernel + host shim. Packs the 8 output limbs per slot
    into `RefOrbitSlot` so the typed kernel loader stays at 4 generic
    params (8 parallel `ArrayView<double>` blew past the loader's
    practical ceiling — kernel JIT failed before any math ran). First
    cut iterates Hi-only doubles; QD body wired via `GpuQDMath` lives
    behind a TODO because ILGPU 1.5.3's IR-inliner trips on
    `Renorm5`/`ThreeSum`'s deep `(s, e1, e2) = ThreeSum(...)`
    deconstruction cascades (kernel JIT failed identically to the FMA
    case). Two options for the QD upgrade slice: (a) rewrite GpuQDMath
    primitives to return mutable struct outputs instead of value tuples;
    (b) bump ILGPU to 2.x (different IR pipeline, tuples handled).
  * `Engine/Calculators/Gpu/MandelbrotRefOrbitGpu.cs` — private FP64-
    capable accelerator (`TryAcquireFp64`). Walks devices CUDA → CPU.
    Bypasses `GpuAcceleratorHost` for the ref orbit because that picks
    ILGPU's preferred non-CPU device, which on this dev machine landed
    on Intel UHD OpenCL — "Float64 (double) type is not supported on
    this device", kernel can't compile. Skips OpenCL entirely (no
    cheap pre-flight FP64 probe; CPU is the only universally-FP64
    fallback). Exposes `SelectedDeviceLabel` for `--gpurefprobe`.
  * `Engine/Calculators/MandelbrotCalculator.cs` — `UseGpuReferenceOrbit`
    static toggle (default off). When on, `CalculateHighPrecision` QD
    branch routes through `TryComputeReferenceOrbitQDGpu` which mirrors
    the centre-cache short-circuit in `ComputeReferenceOrbitQD`, runs
    the GPU compute, and updates `_refZr/_refZrLo/_refZrX2/_refZrX3`
    (and zi counterparts) plus the cache fields. Failure falls back
    silently to the CPU path; failure reason logged via `Debug.WriteLine`.
    Default off keeps every existing call site bit-identical to pre-2.12.
  * `Program.cs` — `--gpurefprobe` flag. Runs three implementations
    side-by-side at QD-tier coord/zoom (1e15 + 1e30 saprobe coords):
    CPU-QD (truth), CPU-Hi (Hi-only baseline matching kernel math),
    GPU-Hi (kernel). Reports ms + Δ(GPU vs CPU-Hi) — should be FP64
    round-off; Δ(GPU vs CPU-QD) — chaos-amplified, expected large
    until QD kernel slice lands. Writes `gpurefprobe.out`.
  * Smoke result on dev hardware (GeForce GT 710, FP64 1/24-rate):
    CUDA picked; kernel JITs (~490 ms first call, cached after);
    second call 1.54 ms vs CPU-QD 0.54 ms. Δ(GPU-Hi vs CPU-Hi)=346 —
    differs by CUDA's fused-mul-add rounding vs x86's two-step
    mul+add; not a bug, IEEE-FP64 semantics differ between
    backends. GT 710 isn't the target perf hardware — Wave 6 multi-
    cluster will need a modern CUDA card for the GPU path to beat
    CPU on sequential ref-orbit work; current win is offload + CPU
    overlap potential, not raw throughput.
  * Build clean (0 errors); 140/140 server tests pass (toggle off,
    no behaviour change to default path).
  * Open follow-ons: QD-body kernel (struct-output rewrite or ILGPU
    upgrade); benchmark on RTX-class CUDA card; integrate toggle into
    a host-side perf decision (auto-enable when measured GPU < CPU at
    rebuild time); promote to OD ref orbit once QD lands.
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
- 2026-06-22 — Wave 3 kickoff. 2.14 (QD δ-chain) deferred — 3-5 d, no
  blocker. 3.1 (SIMD brightness/contrast) + 3.2 (suppress pre-overlay
  snapshot) found already shipped — `FractalRenderHost.cs` has
  Vector256 `ProcessRowSimd` (8 BGRA pixels/step, chunked Partitioner,
  pooled LOH) + `_recordingActive` gate at the snapshot site. Closed
  both as completed.
- 2026-06-22 — Wave 3.8 superseded. `FractalInputController.cs` already
  carries OD/QD/DD/SP branches on all 6 pan/zoom sites
  (`OnPointerMove`, `OnPointerDoubleClick`, `OnWheel`, `ApplyBoxZoom`,
  `PanByPixels`) with `StoreOD`/`StoreQD`/`StoreDD` writing every limb —
  Wave 2.15's work covered the perf-plan finding. `QDZoomThreshold` =
  1e25; DD branch handles 1e12–1e25 via Hi+Lo. Pan no longer collapses
  limbs in any tier.
- 2026-06-22 — Wave 3.6 follow-on shipped — 19 remaining hand-written
  themes ported to `IGpuHlslPalette`. New shared file
  `Engine/Models/HlslPaletteHelpers.cs` exposes two static prelude
  strings: `HsvAndMods` (cg_mods + cg_hsv_to_rgb that mirrors
  `Fractals.HsvToRgb` exactly — no input clamping; `cg_pack_bgra`
  saturates at pack time) and `ModsOnly` (for themes that need only
  the GLSL-style mod helper). Themes ported: `GrayscalePalette`,
  `RainbowColorMap`, `FirePalette`, `Painted`, `PaintedReversed`,
  `Pastelly`, `WarpedHsvMap`, `GoldenRatioMap`, `MonoBandMap`,
  `BernsteinMap`, `RedAndBlack` (Radio Interference), `NebulaDustMap`,
  `DigitalMatrixMap`, `PsychedelicMap`, `TwilightCyclicMap`,
  `SolarWindMap`, `SolarWindMapMOD`, `CopperSheenMap`, `VintageSepiaMap`,
  `DistanceGlowMap`. Each carries unique `PaletteId` so the kernel
  caches its compiled shader per theme. End-to-end GPU palette path
  (`EscapeTimeCalculator.TryDispatchGpu` → `GpuKernel.SetPalette` →
  `gColor` UAV write) now applies to every algorithmic theme in the
  EscapeTime dispatch table. Build clean, 140/140 tests pass.
- 2026-06-22 — Wave 3.6 (T3.1 ext) shipped — HLSL palette codegen for
  hand-written `IColorMap`. `HsvPalette` now implements `IGpuHlslPalette`
  as the canonical hand-port template. `HlslPaletteBody` mirrors `Map()`
  but with saturation=1 baked in, so the per-sector colour blend
  simplifies to (v,t,0)/(q,v,0)/(0,v,t)/(0,q,v)/(t,0,v)/(v,0,q) and the
  shader needs no helpers — `HlslPrelude` returns empty. `PaletteId`
  = `"HsvPalette/v1"` so the kernel caches the compiled shader per id.
  Picked up automatically by `EscapeTimeCalculator.TryDispatchGpu`'s
  `ColorMap as IGpuHlslPalette` check (and the matching
  `MandelbrotCalculator` path); when the toggle is on and the kernel
  attached, the GPU writes `ColorBuffer` end-to-end and the CPU
  writeback skips. 3.5 deferred per user — needs orbit-validity
  infrastructure (~1-2 d).
  Follow-on (mechanical port using HsvPalette as template):
  GrayscalePalette, RainbowColorMap, FirePalette, Painted,
  PaintedReversed, Pastelly, WarpedHsvMap, GoldenRatioMap, MonoBandMap,
  BernsteinMap, RedAndBlack, NebulaDustMap, DigitalMatrixMap,
  PsychedelicMap, TwilightCyclicMap, SolarWindMap, SolarWindMapMOD,
  CopperSheenMap, VintageSepiaMap, DistanceGlowMap. Build clean,
  140/140 tests pass.
- 2026-06-22 — Wave 3.7 (Finding D) shipped — Adaptive HE crossfade.
  Root cause: slideshow `_lastUploadedBuffer` snapshot source had HE
  applied (calc-completion path bakes HE before `UploadProcessedBuffer`)
  but `RecolorActiveToBuffer` returned the raw post-Calculate /
  post-`ApplyBandDitherRecolor` buffer with no HE. `FadeAsync`
  per-pixel lerp ended in the pre-HE target state; the post-fade
  `RepaintWithPostFx` then snapped HE onto the visible frame — the
  "HE pops on at end" jump. Fix: after the recolor compute,
  `BuildHistogramCdf` + `ApplyHistogramEqualizationWithCdf` apply HE
  to the recolor target when `ViewState.HistogramEq > 0`, matching
  the upload-path output so both fade endpoints are post-HE and the
  RepaintWithPostFx is now a no-op visually. Mandelbrot path only
  (alt calcs already bypass HE in the upload path). Build clean,
  140/140 tests pass.
- 2026-06-22 — Wave 4.7 shipped — UserBulb 3.4 time global + animate bar.
  Audit revealed engine-side + ViewModel-side already shipped earlier:
  `FractalParameters.UserBulbTime` (cloned), `UserBulbCalculator` compile
  sig appends `double t = __p[__p.Length - 1]`, `UserBulbView.axaml`
  animation row (Play/Pause / Speed / t), `UserBulbViewModel.AnimationTick`
  + `NotifyRenderDone` gating, and `AvaloniaShellBootstrap` 30 Hz
  `DispatcherTimer` pumping `vm.AnimationTick(dt)` while gated on
  `AnimationFrameUploaded`. Outstanding piece per the original 3.4 spec
  (`Docs/Technical/UserBulb3D-DevelopmentPlan.md:250`): loop-length knob.
  Added this turn:
  * `UserBulbViewModel.AnimLoopSeconds` (clamp 0..600). When > 0,
    `AnimationTick` wraps `t` into `[0, L)` via `next -= L * floor(next/L)`.
    Default 0 = no loop, preserving the prior monotonic-advance behaviour.
  * `UserBulbView.axaml` animation row gains "Loop s:" NumericUpDown
    between Speed and t.
  * Build clean (0 errors, 4 pre-existing AVLN5001 Watermark warnings).
    140/140 Server.Tests pass.
  * Open follow-on: video time-sweep mode (spec 3.4 line 263) — wire
    `BulbTimeSweepEnabled` / `BulbTimeStart` / `BulbTimeEnd` into
    `VideoZoomRequest` so the video pipeline can lerp `UserBulbTime`
    per frame. Not blocking; filed as 4.7.f1.
- 2026-06-22 — Wave 1.C1 closure shipped — AvaloniaDialogs.cs carved into
  cross-platform `FracturingFog.Hosting.dll`. Three blockers resolved:
  * QD coord codec (FormatCoordSingle / TryParseCoordSingle / TryParseCoordAny
    + DecomposeDouble / ExactSum / RationalToDouble) relocated from
    `Views/Controls.cs` FormHelpers (WinForms-bound) to new
    `Abstractions/Math/QdCoordCodec.cs`. FormHelpers retains the legacy
    `FracturingFog.Views.FormHelpers` API as thin delegating shims for
    in-tree WinForms callers.
  * `AvaloniaShellBootstrap.AudioCapabilities` static dependency replaced
    with `FracturingFog.Audio.AudioCapabilityProbe.Detect()` in the
    cross-platform Audio assembly. AvaloniaShellBootstrap now delegates to
    the probe; AvaloniaDialogs calls the probe directly so the carve has
    no remaining reference to the WinExe-pinned bootstrap.
  * `PaletteBuilder.Views.MainWindow` available cross-platform since
    Wave 1.8 (PaletteBuilder.Lib TFM = net10.0). Added
    `PaletteBuilder.Lib.csproj` ProjectReference to
    `FracturingFog.Hosting.csproj`.
  * `FracturingFog.Hosting.csproj` drops `<Compile Remove="AvaloniaDialogs.cs" />`;
    `FracturingFogCLD.csproj` adds it (WinExe consumes across the
    ProjectReference). AvaloniaDialogs visibility flipped `internal →
    public` because AvaloniaShellBootstrap (still WinExe-only) calls into
    it across the new assembly boundary.
  * Full solution builds clean (0 errors, 0 warnings). 140/140
    Server.Tests pass. Wave 1 launch blockers now reduce to manual smoke
    runs (1.S1/1.S2) + per-RID device-kind assert wired (1.C3) + Wave 0.5b
    baseline-record.
- 2026-06-22 — Wave 3.4 (T3.3) shipped — Non-temporal AVX writes.
  `FractalRenderHost.ProcessRowSimd` (brightness/contrast Vector256 inner
  loop) now uses `Vector256<uint>.StoreAlignedNonTemporal(uint*)` when
  the dst buffer (pinned POH `_uploadDstPool`) is 32-byte aligned at
  start. Each step writes 32 bytes (vecLen=8 uints) so alignment is
  preserved across the loop — one pre-loop alignment check splits the
  hot path into NT vs fallback `StoreUnsafe` loops, no per-iteration
  branch. Bypasses L2/L3 cache eviction for the post-FX buffer that GPU
  upload consumes immediately without CPU re-read. Build clean,
  140/140 tests pass.
- 2026-06-22 — Wave 3.3 (T2.3) shipped — Multibrot SIMD.
  Mandelbrot/Julia/BurningShip/Tricorn already SIMD via
  `ISimdFractalKernel`. Multibrot previously stayed scalar with polar
  form (`Math.Atan2`/`Pow`/`Cos`/`Sin` per step). Added direct
  complex-multiplication scalar + SIMD paths for d∈{3,4,5}:
  * d=3: `z³ = zr(zr² − 3 zi²) + i zi(3 zr² − zi²)`; 3·z² derivative.
  * d=4: `z² = u + iv`, `z⁴ = (u² − v²) + 2 u v i`; 4·z³ derivative.
  * d=5: `z⁴ = U + iV`, `z⁵ = (zr·U − zi·V) + i(zr·V + zi·U)`; 5·z⁴.
  Each Step/StepSimd branches on `_d` — predictable, hoist-friendly.
  d≥6 keeps polar fallback. `SimdSupported` flag drives dispatch:
  `EscapeTimeCalculator.Calculate` picks `DispatchByColorMapSimd` for
  d∈{3,4,5}, scalar `DispatchByColorMap` otherwise. Interface comment
  in `IFractalKernel.cs` updated to reflect the new coverage.
  Build clean (0 errors), 140/140 server tests pass.
- 2026-06-22 — Wave 2.13 (D-7.29) shipped — Roslyn source generator.
  Replaces the legacy `dotnet run -p CalculatorGen` step with a
  compile-time `IIncrementalGenerator`. Deleted ~33 K lines of
  hand-checked-in generated source.
  * New `CalculatorGen.SourceGen.csproj` — netstandard2.0 Roslyn analyzer.
    Compile-includes the existing Parser/Emitters/Api tree from
    `..\CalculatorGen\` (excluding `Program.cs` and `CalculatorGenHotLoad.cs`
    which need net10.0 + Roslyn Scripting). `Polyfills.cs` shims
    `IsExternalInit` + `System.Index/Range` for the netstandard2.0 TFM
    so the original net10.0 source compiles unmodified.
    Diagnostics: CG001 empty-equation, CG002 parse-fail, CG003 missing-name.
  * `[assembly: GeneratedCalculator(equation, name, IncludeSelfTest?, Bailout?)]`
    attribute injected via `RegisterPostInitializationOutput`. Assembly-level
    + `AllowMultiple=true` so one registry file can declare every calc.
    Generator pulls all attribute instances from `Compilation.Assembly.GetAttributes()`,
    runs `CalculatorGenApi.Generate`, emits `{Name}.g.cs` (and
    `{Name}SelfTest.g.cs` when requested) via `context.AddSource`.
  * Templates updated: `// <auto-generated />` + `#nullable enable` as
    lines 1-2 of both `Calculator.template.cs` and `SelfTest.template.cs`.
    Suppresses CS8669 on emitted source; applies equally to any legacy CLI
    regeneration.
  * `Engine/FracturingFog.Engine.csproj` references the SourceGen project
    with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` — runs
    inside the compiler, no runtime dep added.
  * `Engine/Calculators/Generated/GeneratedCalculatorAttributes.cs` —
    central registry. 10 `[assembly:]` declarations cover all stock calcs:
    MandelbrotZ2..5, Tricorn, MandelbrotTricorn, BurningShip,
    MandelbrotBurningShip, MandelbrotPhoenix, UserDslEquation.
  * Deleted 20 files (10 calc + 10 selftest) = ~33 K lines. Build artifacts
    now flow `obj/.../FracturingFog.CalculatorGen.SourceGen/.../*.g.cs`.
  * WinExe `FracturingFogCLD.csproj` got `<Compile Remove="CalculatorGen.SourceGen\**" />`
    matching the sibling Lib exclusions — sidesteps the CS0579
    duplicate-AssemblyInfo cascade that hits every sibling project under
    the WinExe root.
  * Smoke: full solution build clean (0 errors, 0 warnings). 140/140
    Server.Tests pass. `FracturingFog.exe --gentest MandelbrotZ2` reports
    PASS across scalar↔AVX2↔GPU↔perturbation↔BLA↔QD-ref-orbit; 0
    mismatches at 4096 pixels.
  * Legacy `dotnet run -p CalculatorGen --equation … --name …` CLI still
    works (sibling Lib unchanged) and writes byte-equivalent source to disk
    — useful for inspecting generator output or hot-loading user equations.
    `CalculatorGenHotLoad` (user-equation Compile-&-Load runtime path) also
    unaffected.
