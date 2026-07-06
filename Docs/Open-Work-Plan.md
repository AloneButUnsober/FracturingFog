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
| 0.5 | Visual-regression harness — `--batch` per-fractal SHA256 baseline | ✅ Tool shipped (`Tools/VisualRegression/`). Baseline recorded 2026-06-22 (0.5b) |
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
| 1.10 | X.3 | P/Invoke + IsOSPlatform sweep | ✅ Shipped — `BatchEntry.cs` / `ServerEntry.cs` / `MandelbrotBench.cs` / `MainWindow.ToyDragWindow` all gated `OperatingSystem.IsWindows()`. CA1416 clean. Linux/macOS Toy-mode drag covered by the Avalonia `BeginMoveDrag` path (see 1.C2); Win+DX retains the `WM_NCLBUTTONDOWN` trick as the fallback for the swap-chain HWND case. |
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
| 1.C2 | Toy-mode drag cross-platform — `BeginMoveDrag(e)` via InputSponge `PointerPressed` handles Linux/macOS + Windows-under-Skia. Win+DX retains the Win32 `WM_NCLBUTTONDOWN` fallback because the swap-chain HWND swallows Avalonia pointer events before the sponge sees them. | ✅ Shipped 2026-06-23 (S-X5) |
| 1.C3 | X.5 per-RID device-kind smoke — assert `AcceleratorProbe.Chosen.Kind` matches expectation in `--batch --self-test` | ½ d |
| 0.5b | Wave 0.5 follow-up — `dotnet run --project Tools/VisualRegression -- record` to populate `baseline.json` | ✅ Shipped 2026-06-22 |

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
| 2.6 | D-5.19 — Anti-aliasing 2×2/4×4 (Quality gate) | ✅ Shipped 2026-06-22 (broadened to alt calcs gated on `SupportsZoomPan`) |
| 2.7 | D-5.21 — TAA temporal accumulation | ✅ Shipped 2026-06-21 |
| 2.8 | D-6.23 — Equation cookbook + gallery | ✅ Shipped 2026-06-21 |
| 2.9 | D-6.25 — Animation: morph equations | ✅ Shipped 2026-06-21 |
| 2.10 | D-4.18 — DD-precision BLA tables | ✅ Shipped 2026-06-21 |
| 2.11 | D-4.17 — Octuple-double (OD) ref orbit — past 1e50 zoom | ✅ Shipped 2026-06-21; OD arithmetic fixed + re-enabled 2026-06-22 (op* rewrite + 23 xUnit OD parity tests in `Server.Tests/OctupleDoubleTests.cs`). UI navigation past 1e58 still pending — see status log |
| 2.12 | D-6.27 — GPU reference orbit (QD on GPU) | 🟡 Scaffold shipped 2026-06-22 (Hi-only kernel works on CUDA). QD upgrade + perf-win analysis **deferred** as non-blocking follow-on — toggle off by default, no other wave depends on it |
| 2.13 | D-7.29 — Roslyn source generator | ✅ Shipped 2026-06-22 |
| 2.14 | D-4.19 — QD δ-chain precision floor — fix pixelation at zoom 1e40–1e58 | ⚫ **Closed obsolete 2026-07-05** — premise disproven by `--qdfloorsweep` (QD separates 128/128 pixels through 1e64; no arithmetic floor in the 1e40–1e58 band). Original report predates the 2.11 OD-arith fix + SM-1 iter-cap finding. Intent folded into **SM-2** (rebasing). See status log. |
| 2.15 | D-4.20 — OD-aware UI navigation — populate `CenterX4..X7` from pan/zoom | ✅ Shipped 2026-06-22 (`FractalInputController.cs` — 6 pan/zoom sites + OD pan-start cache + `StoreOD` helper) |

---

## Wave 3 — Perf Tier 2 + 3 tail

| # | Item |
|---|------|
| 3.1 | T2.1 — SIMD brightness/contrast | ✅ Already shipped — `FractalRenderHost.cs:2181` `ProcessRowSimd` Vector256 (8 BGRA pixels/step), chunked Partitioner, pooled LOH dst buffer |
| 3.2 | T2.2 — Suppress pre-overlay snapshot during video record | ✅ Already shipped — `FractalRenderHost.cs:2085` `if (!_recordingActive)` gate; pooled `_uploadPrePool` LOH |
| 3.3 | T2.3 — `EscapeTimeCalculator` SIMD inner loop (Mandelbrot/Julia/BurningShip/Tricorn/Multibrot) | ✅ Shipped 2026-06-22 — Mandelbrot/Julia/BurningShip/Tricorn already SIMD; Multibrot d∈{3,4,5} added (direct complex-mul scalar + `StepSimd`); d≥6 keeps polar fallback. `SimdSupported` flag drives dispatch in `EscapeTimeCalculator.Calculate` |
| 3.4 | T3.3 — non-temporal `Avx.Store*` writes | ✅ Shipped 2026-06-22 — `ProcessRowSimd` gains `StoreAlignedNonTemporal` fast-path when dst is 32-byte aligned; pre-loop alignment check splits two hot loops |
| 3.5 | T3.2 — ref-orbit recycling across video frames | 🟢 Shipped opt-in 2026-07-05 — `MandelbrotCalculator.AllowRefOrbitRecycle` (default OFF). `TryRecycleReferenceOrbit` keeps the cached orbit when the centre moved < 25% of the frame corner (same tier + maxIter-covered); Δc injected into the SIMD PT dc (`_refRecycleDx/Dy`), DD/QD/OD glitch fallbacks stay exact via `absoluteWorldCoord − storedRefCentre`; the cheap BLA/SA tables rebuild for the widened dc, the expensive orbit build is skipped. Default path bit-identical (`x + 0.0`). Headless gate `--reforbitrecycle` (fresh-vs-recycled parity) PASS: ≤3/16384 boundary-flip pixels, 0 large-area divergence. **Remaining before production-on:** wire into the video pipeline + deep-zoom visual flicker sign-off; QD/OD tiers use the identical code path but are probe-covered only at DD tier so far |
| 3.6 | T3.1 ext — HLSL palette codegen for hand-written `IColorMap`; GPU `ColorBuffer` for orbit-aware themes | ✅ Shipped 2026-06-22 — `HsvPalette` + all 19 sibling hand-written themes now implement `IGpuHlslPalette`. Shared HLSL prelude in `Engine/Models/HlslPaletteHelpers.cs` (cg_mods + cg_hsv_to_rgb mirroring Fractals.HsvToRgb). Auto-picked by `EscapeTimeCalculator.TryDispatchGpu` |
| 3.7 | Finding D — Adaptive HE crossfade lerp | ✅ Shipped 2026-06-22 — `RecolorActiveToBuffer` now bakes HE into the recolor target via `BuildHistogramCdf` + `ApplyHistogramEqualizationWithCdf` when `ViewState.HistogramEq > 0`; eliminates the post-fade snap |
| 3.8 | Pan/keyboard input fails at zoom ≥ 1e24 — QD-limb update in pan-zoom command pipeline | ✅ Superseded by Wave 2.15 (2026-06-22) — `FractalInputController.cs` all 6 pan/zoom sites carry OD/QD/DD/SP branches with `StoreOD`/`StoreQD`/`StoreDD` writing all limbs |

---

## Wave 4 — Lighting/FX + UserBulb features

| # | Item |
|---|------|
| 4.1 | Lighting-FX 21b GPU port — HDR DoF skewed blurs on ILGPU | ✅ Shipped 2026-06-22 |
| 4.2 | Lighting-FX 16b GGX importance sampling per bounce | ✅ Shipped 2026-06-22 |
| 4.3 | Lighting-FX — HDRI auto-preload on param change | ✅ Shipped 2026-06-22 |
| 4.4 | Sandbox 3C — interpreter perf (opcode-flat dispatch or DynamicMethod IL emit) | ✅ Shipped 2026-06-22 |
| 4.5 | Sandbox chain mode GPU dispatch | ✅ Shipped 2026-06-22 |
| 4.6 | Sandbox Quat-mode Julia + numerical-Jacobian DE on GPU | ✅ Shipped 2026-06-22 |
| 4.7 | UserBulb 3.4 — time global `t` + animate bar | ✅ Shipped 2026-06-22 |
| 4.8 | UserBulb 3.7 — color drivers | ✅ Shipped (audited 2026-06-22) |
| 4.9 | UserBulb 3.9 — FOV / DoF / clip / SS + viewport orbit | ✅ Shipped (audited 2026-06-22) |
| 4.10 | UserBulb 3.6 — multi-equation chain w/ named outputs | ✅ Shipped (audited 2026-06-22) |
| 4.11 | UserBulb 3.10 — preset library seed | ✅ Shipped 2026-06-22 |
| 4.12 | UserBulb 3.11 — marching-cubes mesh export OBJ/STL | ✅ Shipped 2026-06-23 |
| 4.13 | UserBulb 3.12 — `.fbulb` import/export | ✅ Shipped 2026-06-22 |
| 4.14 | UserBulb 3.5 — Julia mode Vec3 path | ✅ Shipped (audited 2026-06-22) |

---

## Wave 5 — Fractal Expansion polish

| # | Item |
|---|------|
| 5.1 | Theme compatibility matrix audit (A.1–D.2 new families) | ✅ Shipped 2026-06-23 — implicit feature-bit gating, no central tag table needs adding |
| 5.2 | Region preset coverage audit | ✅ Shipped 2026-06-23 — +5 built-in regions (Plasma / Flame / Logistic / TearDrop / Mandelbulb) |
| 5.3 | CalcGen reach verification (A.1/A.2/A.5/A.6 5-path) | ✅ Shipped 2026-06-23 — Magnet 1/2 / Glynn / Spider stay hand-written (DSL lacks pole-clamp / fractional-pow / c-mutate) |
| 5.4 | Math help 2-level grouping (>25 sub-tabs) | ✅ Shipped 2026-06-23 — `HelpSubTabGroup` + 7-group layout in `HostHelpContentProvider`; AXAML nested TabControl |
| 5.5 | `FEATURES.md` "20+ families" → ~38; README badge counter | ✅ Shipped 2026-06-23 |
| 5.6 | Allowlist negative tests for 19 new types | ✅ Shipped 2026-06-23 — +16 tests in `FractalTypeAllowlistTests.cs`; new enum-classification coverage assertion |
| 5.7 | Headless visual-regression baseline (golden PNG per type) | ✅ Shipped 2026-06-23 — case set extended 22 → 41; baseline re-record deferred to user run |
| 5.8 | `FractalParamsView.axaml` per-type extract | 🟡 Deferred — low pri per roadmap |
| 5.9 | B.2 KIFS new folds (Mandelbox-rot / Octahedron / Dodecahedron) | 🔴 Bugged 2026-06-23 — enum values + DE bodies land but all three render incorrectly (Octa → cube, Dodeca → all-black, MandelboxRot → stepped-ridge cube). Fix deferred. See status log. |
| 5.10 | D.5 L-System 5 more presets | ✅ Shipped 2026-06-23 — Crystal / Quadratic Koch Island / Twindragon / Bush / Sierpinski Carpet |
| 5.11 | D.4 Flame next 8-16 Apophysis variations | ✅ Shipped 2026-06-23 — +10: Horseshoe / Spiral / Hyperbolic / Diamond / Ex / Bent / Fisheye / Exponential / Power / Cosine |
| 5.12 | B.4 Kleinian user-editable sphere list + Möbius composition + analytic DE | 🟡 Deferred — heavy multi-file refactor |
| 5.13 | D.2 DLA cached-blit pan/zoom + multi-seed + sticky-coef | 🟡 Deferred — needs new render-pipeline state |
| 5.14 | C.3 Bicomplex 2nd-slice-axis + split-complex variant | ✅ Shipped 2026-06-23 — `BicomplexSliceAxis` enum {K/J/I/R}; split-complex / coquaternion stays deferred |
| 5.15 | D.1 Apollonian sub-gasket filled rendering (low pri) | 🟡 Deferred — low pri |

---

## Wave 6 — Multi-cluster glitch rebase

| # | Item | Status |
|---|------|--------|
| 6.1 | Multi-cluster spatial partitioning for perturbation rebase | ✅ Shipped 2026-06-23 — `CalculatorGen/Templates/Calculator.template.cs` carries MVP (Item 7) + guards A/B/C + multi-cluster D (16×16-cell occupancy grid + 8-conn BFS flood-fill) + cross-frame cache E (4-slot LRU). Multi-cluster + cache landed in commits 2912309 + bcea672; default flipped to `UseClusterRebase = true` this turn (AVX-2 perturbation lane parity reached, commits cea9cc8 + b0abc68). Legacy `Engine/Calculators/MandelbrotCalculator.cs` port filed as 6.1.f1 follow-up. |
| 6.1.f1 | Port cluster rebase pipeline (Item 7 MVP + A/B/C/D/E) into legacy `Engine/Calculators/MandelbrotCalculator.cs` — canonical Mandelbrot path glitched lanes currently fall straight to per-pixel HP-direct (`ComputePixelOD/QD/HP`). ~2-3 d: needs SP/DD/QD/OD precision-tier branches, 4 perturbation-path glitch-enqueue sites (scalar + PT4 + PT8 + PT8-512), OD-aware rebase orbit build (template only handles DD+QD). | 🟡 Deferred — non-blocking |

---

## Wave 7 — Docs

| # | Item | Status |
|---|------|--------|
| 7.1 | Top-level `Docs/_Index.md` landing page for both audiences | ✅ Shipped 2026-06-23 — top-level router page routes by audience (User → `User/_Index.md`, Technical → `Technical/_Index.md`) plus project-wide roadmap quick-links. Root `README.md` + both sub-indices wired to point at the new landing page. |

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
| 6 | 3 → **0** | Shipped in template (Wave 6 closeout); legacy port = 6.1.f1 deferred |
| 7 | 1 → **0** | Shipped 2026-06-23 |
| 8 (when greenlit) | 2 | WinForms retirement |
| **Total** | **~65 dev-days** | Down from 90 after Wave 0+1 re-audit |

3 parallel tracks → ~9 cal weeks. Single dev → ~13 weeks.

---

## Deferred follow-ups — deep-region smoke test (2026-07-05)

| ID | Item | Status |
|----|------|--------|
| SM-1 | Deep regions render solid when saved/auto `MaxIterations` < escape band | 🟡 Deferred 2026-07-05 — root-caused via `--regionprobe`: not a precision bug, purely iter-count (regions need 379–3940 iters; rendered under band → 100% in-set → flat). Fix = trace region-load iteration path (where a loaded region sets `MaxIterations`) and either raise the saved values or add auto-iter that climbs until in-set fraction stabilises. Verify with `--regionprobe 20000` (all clean) vs `--regionprobe 300` (all SOLID). |
| SM-3 | Status bar reports "done" while TAA refinement is still running | 🟡 Deferred 2026-07-05 — surfaced once rebasing went default-ON: only the first full-res sample (`TaaSampleIndex==0`) updates the status bar (S-X8, to avoid per-sample ms oscillation); pre-rebasing that sample was slow so status tracked the real work, now it finishes fast while ≤15 TAA continuations keep computing. Fix = keep "Calculating…" (or "Refining n/N") while continuations are pending and fire the authoritative `FrameCompleted` when TAA settles (`_taaSampleCount >= taaMax`). Touches the TAA/upload timing loop — needs the running GUI to verify. |
| SM-4 | Right-click selection-box outline redraws slowly at deep zoom | 🟡 Deferred 2026-07-05 — also a rebasing-default side effect: the overlay repaint (`RepaintWithPostFx`) shares `_uploadGate` with `Calculate`; rebasing lets deep frames complete and spawn TAA continuations that hold the gate, so overlay repaints block behind them. Real fix = decouple the overlay repaint from the calc gate (or suppress TAA continuations while a box-select drag is active). |
| SM-8 | Detail-warning bounced the status bar; render-context overlay; deep-zoom TAA no-op churn | 🟢 **2026-07-06.** (a) The SM-7 detail notice appended to the status bar wrapped and oscillated the panel height (image edge bounced) — **removed from the status bar**. (b) Added a **render-context block to the perf HUD** (`ShowPerfHud`): fractal type, centre, zoom, iter, reference-orbit escape/length, max-detail-zoom estimate, active toggles, and the detail-limit warning in yellow (#FFCC00). Host builds the lines (`FractalRenderHost.BuildRenderContextOverlay`), compositor draws them (`CompositePerfHud` gained `contextLines` + `warningLine`). Status bar stays simple. (c) **Deep-zoom TAA no-op guard**: `RunOneTaaSample` jitters only the DOUBLE `CenterX/CenterY` by ~scale, which rounds away once scale < centre ULP (~past zoom 1e10), so every continuation renders a byte-identical frame — 31 wasted passes that re-run the upload/HUD path (the "status flushing after render finished" at deep zoom). `TryScheduleNextTaaSample` now skips continuations there. **Open:** real deep-zoom TAA needs jitter through the OD centre / per-pixel offset (SM-9); the E+64 status-flash and outline-zoom/pan-at-depth need user retest with the new overlay. |
| SM-9 | Deep-zoom TAA disabled (jitter below double ULP) — no sub-pixel AA past ~1e10 | 🟡 Deferred 2026-07-06 — TAA sub-pixel jitter is added to the double `CenterX/CenterY`; past ~1e10 it is lost, so the SM-8 guard now skips it (was silently producing identical samples). To restore anti-aliasing at depth, feed the Halton jitter through the OD centre limbs or the per-pixel `dc` offset (both carry precision below the double ULP) instead of the top double limb. |
| SM-7 | "Controls break past ~1e63" — actually the POINT's detail-depth limit | 🟢 **Diagnosed + guarded 2026-07-06.** User reported double-click missing / pan overshooting just past 1e63. NOT an input bug: the new `--focusprobe` (end-to-end render + double-click focus + patch-match) shows focus is **pixel-perfect (0.00px) through 1e70** and `--inputprobe` extended to 1e70 shows **0.00px** anchor drift. The real cause: past ~1e60 the RENDER collapses to a flat frame (frameDistinct 163→3→1, inSet 0%), so navigation has no visible structure to land on and *reads* as broken. Root: perturbation resolves detail only while the pixel offset δ (∝1/zoom), amplified by ∏\|2·Zₙ\| over the reference orbit, reaches O(1). The user's centre's orbit **escapes at iter 4060** ⇒ only ~62 decades of amplification ⇒ intrinsic depth floor ~1e62 for THAT point (matched the collapse exactly). Property of the location, not precision — to go deeper you must recentre on a longer-orbit point. Guard: `MandelbrotCalculator.MaxUsefulZoomLog10` (Σlog₁₀\|2·Zₙ\| to the \|Z\|=2 crossing, +∞ if bounded) computed free during every ref-orbit build (OD/QD/DD), carried on `RenderFrameInfo`, and surfaced as a status-bar notice ("⚠ detail limit for this point (~1eNN) — recenter on visible structure to zoom deeper") when the live zoom passes it. Gate: `--focusprobe` (maxUseful≈1e62 vs collapse 1e62). |
| SM-5 | Extreme-tier zoom wall (5e58 → 1e63 → 1e100) | 🟢 **Superseded by SM-6 — cap now 1e100 on 2026-07-05.** User hit a hard wall at exactly 5e58 (top-tier `QualityPreset.Extreme.ZoomMax`); wheel/outline zoom stopped. First raised to 1e63 assuming a QD floor — but the OD ref orbit + OD per-pixel coords already engage above 1e50 (`ODZoomThreshold`), so the true limiter was the OD path, not QD (see SM-6). Cap now 1e100. Comments in `MandelbrotCalculator` (135, 2040) corrected. |
| SM-6 | Zoom beyond ~1e64 needs the OD coordinate path fixed | 🟢 **Fixed on 2026-07-05 — cap raised to 1e100.** The 1e64 wall was NOT the QD ref-orbit floor (OD ref orbit + OD per-pixel coords already run above 1e50). Real bug: `OD.FromCenterOffset` added the pixel offset via the **OD+OD sloppy operator**, whose 3-level carry cascade only propagates a residual ~3 limbs; a deep offset landing at limb X4+ (`\|off\|` ~1e-64, zoom > ~1e64) got parked against X3 and rounded away → every pixel collapsed to the centre (the `--qdfloorsweep` OD column's 37/128 at 1e66 — real, not a probe artifact). Fix = route through the **OD+double full-cascade** operator (`(center + offHi) + offLo`), which places the offset at its true limb through all 8 limbs. `--qdfloorsweep` now shows OD **128/128 through 1e120** (top of sweep). Raised `Extreme.ZoomMax = 1e100` (20 decades under the measured-clean coord floor; OD ref-orbit accuracy is the soft limit past there, chaos-dominated as on QD). Regression test `OctupleDoubleTests.FromCenterOffset_DeepOffset_SeparatesAdjacentPixels` (128 distinct + pixel-step recovered at zoom 1e70). **Remaining:** direct OD ref-orbit accuracy at 1e70+ over ~100k iters is unmeasured (no 16-double ground truth) — a future `--odorbitprobe` could cross-check OD-vs-QD at a shared depth. |
| SM-2 | Deep-QD extreme-region render is slow (minutes at full window × AA16) | 🟢 **Rebasing shipped opt-in 2026-07-05 — `--rebaseprobe` PASS.** Root cause: SIMD PT δ-loop bails ~1e30 (glitch check `z==Z && δ!=0`), so deep frames ran per-pixel **direct-QD** `ComputePixelQD`. Fix: `ComputePixelPTRebased` (Zhuoran rebasing) — ref index `m` tracked separately, `z = Z[m] + δ` reconstructed, rebase `δ := z; m := 0` when `\|z\| < \|δ\|` or ref exhausted. Replaces the QD/OD/HP glitch fallback (all 6 sites) when `AllowPtRebasing` on. Stays in **double** — a DD δ/ref/dc variant gave byte-identical iteration counts (precision is not the limiter; the ~50 % divergence from a QD render is chaotic sensitivity the QD path shares with itself, `QDself ≈ reb-vs-QD`). Probe: **91–142× speedup, rebasing tracks QD within 0.05 % of QD's own SA-off/SA-on reproducibility.** **Default flipped ON 2026-07-05** after user confirmed the speed win; the debug toggle is now "Bypass Rebasing" (checked = off) to A/B against legacy QD/OD. Later mitigations: SIMD rebasing (reclaim vector throughput), lower AA for preview, adaptive iter cap (ties to SM-1). |

## Status log

- 2026-07-06 — **Deep-zoom nav evidence tooling: `--navrepro` + overlay limb
  counts (SM-10).** User still reports outline-zoom / pan / double-click "close
  but not exact" past ~E+64, but words alone haven't pinned it and the synthetic
  probes are self-referential (controller OD vs OD truth) or can't measure on a
  flat frame. Added `--navrepro [file]`: reads a coordinate file the user fills
  straight from the floating menu (full-limb `cx=`/`cy=`, `zoom=`, `dim=`,
  `click=`), renders the frame, runs the real `FractalInputController`
  double-click focus, re-renders, and patch-matches the clicked feature to report
  focus error IN PIXELS (0 = perfect, >1 = the reported bug reproduced). Validated
  on the user's own 3E47 coordinate: focus 0.00px at 5e58 (textured), and at 1e64
  the frame is flat (distinctIters=1, maxUseful=1e62) — i.e. that coordinate's
  reported "past-E+64 breakage" IS the SM-7 detail floor, not an input fault. To
  catch a genuine error the user must supply a coordinate that still has DETAIL
  past E+64. Also added centre limb-counts (`limbs X:n/8 Y:n/8`) + render px to
  the perf-HUD render-context block — a truncated centre would show instantly.
  `Docs/Nav-Repro-Template.txt` is the fill-in form. Awaiting a detailed-frame
  repro coordinate.
- 2026-07-06 — **"Controls break past 1e63" root-caused: it's the point's detail
  floor, not input (SM-7).** New `--focusprobe` renders a deep frame, double-click
  focuses via the real `FractalInputController`, re-renders, and patch-matches to
  measure where the clicked feature lands. Result: focus is **0.00px through
  1e70**; input is provably exact (also confirmed by `--inputprobe` extended to
  1e70, 0.00px anchor drift). What breaks is the RENDER — past ~1e60 the frame
  collapses to flat (frameDistinct 163→3→1, inSet 0%). Localised through
  rebasing-on/off and accel-on/off (all identical) to the shared δ core, then to
  the reference orbit: the user's centre **escapes at iteration 4060**, giving
  only ~62 decades of ∏|2·Zₙ| δ-amplification, so its intrinsic perturbation
  depth is ~1e62 — matched the collapse exactly. This is a property of the POINT
  (must recentre on a longer-orbit location to go deeper), the same limit every
  perturbation zoomer has. Shipped a guard: `MaxUsefulZoomLog10` computed free in
  all three ref-orbit builds, carried on `RenderFrameInfo`, surfaced as a
  status-bar notice when the live zoom exceeds it — so a flat deep frame reads as
  a location depth limit, not broken navigation. Note the earlier `--qdfloorsweep`
  (SM-6) validated OD.FromCenterOffset coordinate separation, but the live deep
  render uses the double-δ path, so coordinate separation was necessary but the
  amplification floor is the operative limit for a given point. 547/547 tests pass.
- 2026-07-05 — **OD coordinate path fixed → Extreme cap 1e63 → 1e100 (SM-6).**
  Follow-up to SM-5: the 5e58/1e64 walls were NOT a QD limit — OD ref orbit + OD
  per-pixel coordinates already engage above `ODZoomThreshold` (1e50). The real
  bug was `OD.FromCenterOffset` adding the pixel offset through the OD+OD sloppy
  operator, whose 3-level carry cascade can't reach limb X4+; a deep offset
  (zoom > ~1e64) was parked against X3 and rounded away, collapsing all pixels
  to the centre. That is exactly the `--qdfloorsweep` OD column's 37/128 at 1e66
  (real, not the probe's zeroed-limb artifact I'd assumed). Fix = route through
  the OD+double full-cascade add (`(center + offHi) + offLo`). Sweep now: OD
  **128/128 through 1e120**. Cap raised to **1e100** (20 decades of coordinate
  margin; OD ref-orbit accuracy is the soft limit past there, chaos-dominated as
  on QD). Regression `FromCenterOffset_DeepOffset_SeparatesAdjacentPixels` added;
  all 24 OD tests pass. Note SM-5's 1e63 was a wrong-mechanism stopgap, now
  superseded.
- 2026-07-05 — **Extreme zoom wall raised 5e58 → 1e63 (SM-5).** User navigated
  to exactly 5e58 and could zoom no further (neither wheel nor box). Root cause:
  that value is `QualityPreset.Extreme.ZoomMax`, the top tier's hard cap — a
  stale-conservative number ~5 decades below the real QD floor. Reran
  `--qdfloorsweep`: QD coords stay 128/128 distinct through **1e64**, collapse to
  3/128 at 1e66. Raised the cap to **1e63** (one-decade margin under the clean
  1e64 for QD reference-orbit accuracy in deep filaments; coordinate separation
  is necessary-not-sufficient). Iter budget fine (66 560 < 131 072 cap at 1e63).
  Cap propagates automatically — the input promotion sites read
  `Extreme.ZoomMax` directly. Going past 1e64 filed as **SM-6** (OD reference
  orbit + new tier). Stale QD-ceiling comments corrected in `MandelbrotCalculator`.
- 2026-07-05 — **Input rework CONFIRMED FIXED after a clean rebuild** (commit
  168eedc). Smoke retest initially still failed — root cause was a **stale
  binary**: `dotnet build` kept reporting success without relinking the exe, so
  the tested build lacked the ViewCamera change (rebasing, a runtime static
  flag, was present — hence its effects showed but the input fix did not). A
  `--no-incremental -t:Rebuild` resolved it. Also fixed a real render bug found
  in passing: `ApplyView` copied only the QD centre limbs (X0..X3) into the
  render calculator, dropping OD (X4..X7) — past 1e50 the render sat at a
  QD-truncated centre while the view state held full OD, so deep frames rendered
  at a wrong centre and navigation compounded against the mis-placed image; now
  copies all eight limbs (`MirrorMandelbrotState` already did). Rebasing default
  flipped ON (see SM-2); two rebasing-default side effects filed as SM-3
  (status-bar premature done) + SM-4 (outline redraw slow), deferred.
- 2026-07-05 — **Deep-zoom input rework — ViewCamera + DeepComplex (commit
  901b641).** User report: approaching ~9e49, keyboard/mouse lose precision
  (double-click mis-focuses, drag pans the wrong amount, box-zoom lands wrong).
  Recurring — the input layer had been rewritten per precision tier several
  times. `--inputprobe` (new headless gate) root-caused it: single-op anchoring
  is exact, but a cumulative wheel zoom-in DRIFTS — the centre was carried in
  plain double until the HP threshold (1e12), where promotion froze a ~1e-16
  world error that bloomed ∝ zoom (3000px off at 1e17, astronomically off by
  1e49). Fix (chosen: full ViewCamera, OD-always): `DeepComplex` (OD-backed
  complex, precision is an internal detail) + `ViewCamera` (single screen↔world
  authority) in Abstractions; `FractalViewState.GetCenter/SetCenter` typed
  accessor. All six `FractalInputController` sites now delegate to ViewCamera —
  the per-tier cascades + DD/QD/OD pan-start caches + Store* deleted (~150
  lines). New precision tiers extend `DeepComplex` only, never the input
  handlers. Gate: anchor drift **0.00px through 1e6→8e49** (was 3000px@1e17);
  546/546 tests pass. Follow-up: unify the render onto ViewCamera + reconcile
  logical-vs-device pixel dims (HiDPI) — separate constant-offset concern.
- 2026-07-05 — **SM-2 rebasing shipped opt-in — `--rebaseprobe` PASS.** New
  `MandelbrotCalculator.ComputePixelPTRebased` (Zhuoran rebasing) resolves any
  pixel in double precision from the single shared reference orbit at any depth,
  replacing the per-pixel QD/OD/HP glitch fallback at all six sites (scalar row +
  PT4/PT8 vector-extract + PT4/PT8 scalar tail) when `AllowPtRebasing` is set.
  Default OFF ⇒ the render path is bit-identical to pre-SM-2 (`if (AllowPtRebasing)
  … else <existing fallback>`). Probe A/Bs it against the per-pixel QD truth on
  the deep smoke regions:
  * **91–142× faster** (3E47: 12740 ms → 101 ms at 128²/20 k iters).
  * **Accuracy = QD.** reb-vs-QD tracks QDself (QD SA-off vs SA-on) to within
    0.05 pt (51.63/51.68, 58.98/58.97, 96.77/96.77, 100/100). The ~50 % "miss"
    on the two deepest regions is chaotic sensitivity of deep filamentary
    structure at high iter — the QD render disagrees with itself by the same
    amount, so there is no tighter truth to hit.
  * A DD δ-chain + DD reference + DD dc variant was tried and produced
    **byte-identical iteration counts** to the double path (and 50× slower):
    precision is not the limiter here, so the fix stays in double. This is why
    2.14's "DD δ-chain" would have bought nothing.
  Remaining: flip `AllowPtRebasing` on after a visual sign-off (3.5-style gate);
  optional SIMD rebasing later to reclaim vector throughput at deep zoom.
- 2026-07-05 — **Wave 2.14 investigated — premise not reproducible; recommend
  reframe/close.** Two headless probes added to `Program.cs`:
  * `--qdfloorprobe [maxIter]` — renders each QD-band smoke region twice (SA on
    vs off) and reports a neighbour-collapse metric. Result: SA on/off is
    near-identical (41.0/39.3 %, 24.4/24.7 %, 83.6/83.6 %) ⇒ the double SA seed
    is **not** the pixelation floor. Also confirmed the SIMD PT δ-loop bails
    ~1e30 (glitch check at `ComputePixelPT`), so deep frames run per-pixel
    direct-QD — that is the SM-2 slowness, not a δ-chain the plan's "DD δ"
    framing assumed.
  * `--qdfloorsweep` — builds the 128 per-pixel X coords the QD/OD path uses
    (`QD.FromCenterOffset`, |c|≈2 centre) across a zoom sweep and counts
    bit-distinct values. **QD separates all 128/128 pixels through 1e64**,
    cliffing only at 1e66. So there is **no QD arithmetic pixelation floor in
    the stated 1e40–1e58 band** — QD headroom runs ~6 orders past the band and
    ~37 orders past the video zoom cap (5e27).
  Conclusion: the 2026-06-22 "pixelation 1e40–1e58" report predates the 2.11
  OD-arithmetic fix and the SM-1 iteration-cap finding; it is not a live QD
  precision bug. The real remaining deep-zoom lever is **SM-2** (PT δ-loop bails
  ~1e30 → slow per-pixel direct-QD; fix = rebasing to keep cheap SIMD PT viable
  at any depth). Recommend closing 2.14 as obsolete and folding its intent into
  SM-2. Probes: commits `baade13` (qdfloorprobe) + this entry's sweep.
- 2026-07-05 — Deep-region smoke-test triage + `--regionprobe` diagnostic.
  New headless renderer (`Program.cs --regionprobe [maxIter]`) renders the
  reported deep regions at 128² single-sample and reports tier / wall-clock /
  in-set% / distinct-iter / distinct-colour. Dispositions of the smoke report:
  * **"renders solid colour"** (Deeper and Deeper, Deep Lightning in Space) —
    **iteration-count issue, NOT a precision bug.** At maxIter=8192 all four deep
    regions render valid fractals (distColour 550–1527, 0% in-set); at maxIter=300
    all collapse to 100% in-set / one colour. Escape bands are 328–3940 iters, so
    any effective cap below a region's band paints it flat. The reported-solid
    regions (need 379 / 808 iters) were rendered under their band. Fix path: the
    region's saved MaxIterations (or the app's per-region auto-iter) is too low —
    not the QD path.
  * **"takes minutes"** (3E47, E45Test04) — inherent deep-QD-perturbation cost:
    3E47 = 11.2 s at 128² single-sample → minutes at full window × AA16 (Extreme).
    Not a defect.
  * **Video zoom clamp at E+27** — by design (`VideoZoom.cs:241`, Ultra cap;
    Extreme-regime pixelation). Not a bug.
  * **Video Settings phantom modal (Windows)** — nested modal-of-a-modal failed
    to front on Win32 (+ ShowInTaskbar=false → unreachable). Fixed 43168a9:
    Activate-on-Opened for the Video + Audio dialogs. Needs Windows verification.
  * Wave 3.5 confirmed inert in production (flag set only in the probe).
- 2026-07-05 — Wave 3.5 shipped opt-in — reference-orbit recycling across
  frames. `MandelbrotCalculator.AllowRefOrbitRecycle` (static, default **OFF**)
  gates `TryRecycleReferenceOrbit`: when the view centre moved by less than
  `RecycleMaxShiftFactor` (0.25) of the frame's corner-dc — same precision tier,
  cached maxIter covers the frame — the cached reference orbit is reused instead
  of rebuilt. The centre shift Δc = newCentre − cachedCentre (computed at the
  tier's DD/QD/OD precision, rounded to double) is injected into the SIMD PT dc
  via `_refRecycleDx/_refRecycleDy` in the scalar / PT4 / PT8 paths; the DD/QD/OD
  glitch fallbacks need no change because they already derive
  δc = absoluteWorldCoord − storedRefCentre. The expensive orbit build is
  skipped; the cheap BLA/SA tables rebuild for the widened dc (the kept orbit is
  a valid perturbation base well past the BLA linearisation radius). Default path
  is bit-identical (`x + 0.0 == x`). New headless gate `--reforbitrecycle`
  (Program.cs) renders each target centre twice — a fresh orbit vs a recycled one
  — and PASSes: recycling engages every case, ≤ 3 / 16384 pixels differ (escape-
  boundary flips, inherent to any reference change), **zero** large-area
  divergence at DD-tier zooms 1e13–1e22. Public `RefRecycleHits`/`RefRecycleMisses`
  diagnostics. **Not yet production-on** — needs the video pipeline to opt in +
  a deep-zoom visual flicker sign-off; QD/OD tiers run the identical code path
  but the probe only exercises DD tier (representable pan). See item 3.5.
- 2026-07-05 — Wave 5.9.f1 attempted — KIFS fold fixes + headless probe.
  Added `--kifsprobe` (Program.cs) + `KifsCalculator.ProbeDE` test hook: a
  headless geometric self-test that sphere-traces the DE inward along a
  Fibonacci direction set and reports hit-fraction + surface radii (axis /
  face-diagonal / body-diagonal), detecting the two documented failure modes
  (all-black = hitFrac≈0; cube = radius signature 1:√2:√3) without a GUI.
  Faithful ports of all three broken folds were tried and **the probe
  disproved each**:
  * **Octahedron** — Mandelbulber2 apex-fold port → solid cube (hitFrac 1.0,
    radii 1:√2:√3). Reverted to the shipped rotated-Menger approximation.
  * **Dodecahedron** — exact Coxeter [5,3] icosahedral mirror fold → all-black
    (hitFrac 0.0; scale-from-vertex diverges because the user offset isn't an
    icosahedron vertex). Reverted to the shipped rotated-Sierpinski (which at
    least renders a visible shape — the icosa port was a regression).
  * **MandelboxRot** — the documented dr-accumulator fix (DE = length/dr) →
    object spans to radius ~6, past this fold's camera setRadius (3.5), so the
    camera sits inside the body. Reverted rather than ship an unverifiable
    framing regression.
  Net: all three fold bodies stay at their shipped state; the probe + hook +
  honest per-fold NOTE doc-comments land so the eventual fix has a headless
  gate. **5.9.f1 remains open** — a correct fix needs reference-sourced
  formulas (Octahedron / IcosaFold) + a matched camera retune (MandelboxRot),
  verified visually, which is not reliably doable headlessly.
- 2026-06-23 — Wave 7.1 shipped — Top-level `Docs/_Index.md` landing page.
  Routes by audience (User → `User/_Index.md`, Technical → `Technical/
  _Index.md`) and surfaces the project-wide roadmap layer (Open-Work-Plan,
  Performance-Roadmap, Lighting-FX-Roadmap, Fractal-Expansion-Roadmap,
  CalculatorGen-Roadmap, Documentation-Plan, Resources-Bibliography) as
  a third bucket — the existing sub-indices only show their own audience's
  pages, so cross-cutting roadmap docs had no canonical entry. Style matches
  the two sub-indices (table-of-routes pattern, terse one-line "what it
  covers" cells). Wires:
  * `README.md` — new "Documentation landing page" line between feature
    tour and per-OS install caveats so the top-level entry is one click
    from the project landing page.
  * `Docs/User/_Index.md` + `Docs/Technical/_Index.md` — opening paragraph
    extended to point upward at the new landing page so readers who
    arrive mid-tree can navigate up to the bridge.
  * Roadmap effort table bumped 1 → 0 day. Wave 7 closed.
- 2026-06-23 — Wave 6.1 shipped — Multi-cluster spatial partitioning for
  perturbation rebase. Audit found the work already in the CalculatorGen
  Roslyn template (`CalculatorGen/Templates/Calculator.template.cs`):
  Item 7 MVP (commit f54ffe7), guards A+B+C hardening (8d7cf75),
  multi-cluster D (2912309), cross-frame orbit cache E (bcea672) — all
  shipped pre-Wave-6 but never tracked in `Open-Work-Plan.md`. Plus
  Wave 2.13 source-gen (f5935c3) deleted the in-tree generated calcs;
  template now emits at build time via Roslyn analyzer, so multi-cluster
  flows to all 10 generated calcs (MandelbrotZ2..5, Tricorn,
  MandelbrotTricorn, BurningShip, MandelbrotBurningShip, MandelbrotPhoenix,
  UserDslEquation).
  * `ProcessClusterRebase` (template line 2108) — spatial-partitions the
    `ConcurrentBag<(int x, int y)>` from the main perturbation pass via
    a 16×16-cell occupancy grid + 8-conn BFS flood-fill on occupied
    cells. 1920×1080 frame → ~8K cells; cluster count typically 1-20 per
    deep-zoom frame. Sequential per-cluster dispatch (inner rebase pass
    is itself Parallel.For — nesting would oversubscribe threadpool).
  * `ProcessSingleCluster` (template line 2234) — zoom gate (skip below
    `QdDirectZoomThreshold = 1e25`, DD-direct cheaper there), bbox-
    cohesion guard (skip long-thin tendrils with density &lt; 2%), centroid
    build of shared QD reference orbit via `BuildRebaseRefOrbitQd` (no
    BLA, no SA), 4-slot LRU cache lookup via `TryGetCachedRebaseOrbit`
    keyed by centroid within `scale·16` tolerance + maxIt, sample-probe
    of first 8 pixels (commit only when ≥ 50% land), parallel
    `TryIterateRebasePixel` over remainder. Failures route to
    `HpDirectGlitchPixel`.
  * `UseClusterRebase` default flipped `false → true` this turn. Original
    off-by-default per commit 6d6db7f "Default UseClusterRebase off until
    AVX-2 perturbation lane lands"; AVX-2 lane shipped commits cea9cc8 +
    b0abc68 so the guard is stale. XML doc rewritten to reflect Wave 6
    closeout state (multi-cluster + cache reduce wasted work, three guards
    kill bad-fit clusters early).
  * Smoke: full solution build clean (0 errors, 24 baseline warnings —
    CS0219 in generator output + AVLN5001 obsolete). 156/156 Server.Tests
    pass. `--gentest MandelbrotZ2` 0-diff scalar↔AVX2↔GPU↔perturbation↔
    BLA↔QD-ref-orbit at 4096 pixels. `--saprobe` histogram across
    1e9 → 1e60 zoom tiers: gen-vs-legacy colour counts within ±10 across
    SP / QD-PT-SA range (saprobe coords stay on main cardioid so don't
    fire rebase, but no regression in non-glitch path).
  * Legacy `Engine/Calculators/MandelbrotCalculator.cs` (canonical
    `FractalType.Mandelbrot` path) has no cluster rebase at all — glitched
    lanes in PT4 / PT8 / PT8-512 fall straight to per-pixel `ComputePixelOD
    /QD/HP`. Filed as 6.1.f1 follow-up: needs SP/DD/QD/OD precision-tier
    branches, 4 glitch-enqueue sites across the 4 perturbation paths, and
    OD-aware `BuildRebaseRefOrbitOD` extension (template only handles
    DD+QD). ~2-3 d when prioritised; non-blocking — canonical Mandelbrot's
    own per-pixel HP-direct fallback stays correct, just slower than
    cluster rebase would be on cohesive mini-Julia scenes.
  * `Docs/Technical/CalculatorGen-Roadmap.md` Item 7 + Known-Issues entry
    updated to reflect the multi-cluster + cache state (was still
    documented as MVP single-centroid with multi-cluster as follow-up).
- 2026-06-23 — Wave 5.9 KIFS folds **bugged + deferred**. All three new folds
  (Octahedron, Dodecahedron, MandelboxRot) render incorrectly. Iteration
  + rebuild + smoke confirmed code path runs; math itself wrong. Three
  successive rewrites all failed to produce correct shapes. Deferring
  rather than burning more time on derivation.
  * **Octahedron** — current impl is Menger sort-3 abs-fold with a 30°
    Y-axis pre-rotation. User reports solid cube. Earlier variant
    (Menger minus z-mirror) also rendered cube — both leave the orbit
    bounded to roughly the unit cube under abs+sort+scale, no
    octahedral self-similarity emerges. True octahedron IFS needs a
    face-fold across the (1,1,1)/√3 face-normal plane that actually
    fires for typical iterates; my versions either folded too rarely
    (`y+z > 1` after sort puts max in x → rarely true) or used the
    wrong scale/offset combination that collapsed orbits to origin.
  * **Dodecahedron** — current impl is Sierpinski tetra fold with 36°
    rotation around (1,1,1) diagonal — should render *something* but
    not the intended dodecahedral / icosahedral shape. Earlier variants
    using Knighty's three φ-derived mirror planes (n1=(-φ,-1,φ-1),
    n2=(-1,φ-1,-φ), n3=(φ-1,-φ,-1)) diverged for every traced pixel —
    `if (d < 0)` reflections never produced a bounded attractor, so DE
    stayed huge-positive everywhere → no ray hits → all-black render.
    Adding the canonical abs(z) first-octant prefix made the +++ octant
    all-positive against any (-,-,+) normal, so mirrors fired the wrong
    way. The Wave 5.9 "next 36° rotated Sierp" tactic ships but does NOT
    match the dodecahedral spec.
  * **MandelboxRot** — current impl is box-fold-at-±1 + sphere-fold +
    π/48 Y-axis rotation + scale. User reports cube-like shape with
    stepped ridges along oblong slightly curved sides. The fixed-dr
    KIFS DE scheme (no per-iter |dz| magnitude tracking) is the root
    cause — real Mandelbox DE needs the dr update from the sphere-fold
    factored into the distance return. Without it, the DE produced is
    geometrically incorrect → only the bounding-cube approximation
    renders. The proper sphere-fold + dr-magnitude update lives in
    `MandelboxCalculator`, separate code path.
  * UI hooks shipped: `KifsFoldKind.Octahedron / Dodecahedron /
    MandelboxRot` enum values, ComboBox row in `FractalParamsView.axaml`,
    `DispatchDE` switch in `KifsCalculator.cs`. GPU path correctly gates
    new folds to CPU fallback. Build clean, 156/156 tests pass — the
    bug is mathematical, not structural.
  * **Fix plan (deferred)** — proper fixes require porting battle-tested
    formulas from Mandelbulber2's `fractal_formulas.cpp` (specifically
    its "Octahedron", "IcosaFold", and "AmazingBoxMod1" entries), each
    of which is ~50 lines of carefully-tuned axis swaps + plane mirrors
    + dr-tracking arithmetic. None of those formulas fit the simple
    fixed-dr KIFS DE scheme used by the existing Menger / Sierpinski
    paths — they need a dr accumulator and bailout management closer
    to the `MandelboxCalculator` shape. Filing as 5.9.f1: replace the
    three current DE bodies with Mandelbulber-ported versions and
    extend `KifsCalculator` with a dr-magnitude accumulator threaded
    through `DispatchDE`. Estimated 1-2 days.
  * Interim user-facing behaviour: the three new fold options remain
    pickable in the ComboBox and render *something* (their distinct
    incorrect shapes), so they don't crash or block. Recommend leaving
    selection on Menger / Sierpinski until 5.9.f1 lands.

- 2026-06-23 — Wave 5 closeout — 10 of 15 items shipped (5.1–5.7, 5.10, 5.11,
  5.14). Five items deferred — 5.8 / 5.12 / 5.13 / 5.15 are heavier than
  polish-wave scope; 5.9 (KIFS folds) ships UI/enum scaffolding but DE
  bodies bugged — see separate 5.9 entry. Fix tracked as 5.9.f1.
  * 5.5 / 5.6 / 5.7 — doc + test polish. FEATURES.md / README bumped to
    "~38 families" with category breakdown; README gained shields.io
    badges (fractals / themes / platforms / .NET). Allowlist test suite
    grew from 39 → 55 with full enum-classification coverage assertion.
    Visual-regression case set 22 → 41 (every new FractalType + every
    Generated variant + every 3D raymarcher). Baseline.json stays the
    earlier 22-entry record; user runs `record` when ready to absorb the
    ~10 min cold-build cost.
  * 5.1 audit conclusion — theme gating is implicit (per-calculator
    capability + `EquationProfile` feature-bit recommender), not a
    central FractalType→tag registry. New families pick up sane defaults
    via interface gating (e.g. `IInteriorAwareColorMap` only runs inside
    `MandelbrotCalculator.RunInteriorPass`; alt calcs silently skip).
    No new tag table needed. Documented + closed.
  * 5.2 — `Engine/Models/FractalRegion.cs` `_builtIns` extended with 5
    new entries: Plasma (default seed framing), Flame (default chaos
    framing), Logistic (r ∈ [2.9, 4.0] bifurcation window), TearDrop
    (default centre), Mandelbulb power-8 (default camera). Roadmap
    target was ≥1 built-in per family; the four families above were
    bare. Other families already had ≥1.
  * 5.3 — CalcGen DSL inspected. Magnet 1/2 rational expressions are
    DSL-expressible (`/`, `^2` work) but the pole-clamp on the
    denominator has no DSL operator → NaN blow-up on pole pixels.
    Glynn needs fractional `z^1.5` — DSL `^` is integer-only. Spider
    mutates `c` per iteration — DSL state model assumes constant c.
    All four stay hand-written scalar / SIMD kernels in
    `Engine/Models/FractalKernels/`. No `[assembly: GeneratedCalculator]`
    entries added.
  * 5.4 — Two-level Mathematics-tab grouping. New `HelpSubTabGroup`
    record in `Abstractions/Help/IHelpContentProvider.cs`. Default
    interface impl wraps the flat `MathSubTabs` into a single "All"
    group so legacy hosts stay compatible. `HostHelpContentProvider`
    overrides with a 7-group layout: Overview / 2D escape-time /
    Histogram / Procedural / 3D + 4D / Authoring / Generated.
    `FloatingHelpView.axaml` now renders an outer TabControl over the
    groups and an inner TabControl (TabStripPlacement="Left") over each
    group's sub-tabs. Tab strip no longer wraps; total 35 sub-tabs split
    across the 7 groups.
  * 5.9 — UI / enum scaffolding shipped (KifsFoldKind.Octahedron /
    Dodecahedron / MandelboxRot, ComboBox row, DispatchDE switch, GPU
    gate to CPU fallback) but DE math bugged across all three variants.
    Three rewrite attempts failed to produce correct shapes. **Marked
    🔴 bugged + deferred — see dedicated 5.9 status-log entry above for
    diagnosis and 5.9.f1 fix plan.**
  * 5.10 — `LSystemPresets.cs` gained 5 new entries: Crystal (Koch-square
    variant), Quadratic Koch Island, Twindragon, Bush (Plant variant),
    Sierpinski Carpet. Built-in library now 16 entries.
  * 5.11 — Flame gained 10 new Apophysis stock variations: Horseshoe (v4),
    Spiral (v9), Hyperbolic (v10), Diamond (v11), Ex (v12), Bent (v14),
    Fisheye (v16), Exponential (v18), Power (v19), Cosine (v20). Enum
    values use Apophysis-canonical IDs; the `ApplyVariation` switch
    arm guards origin-singular variations with the same r ≥ 1e-12
    bailout the existing Spherical / Julia / Disc arms use.
  * 5.14 — Bicomplex Mandelbrot 4D slice-axis selector. New
    `BicomplexSliceAxis` enum {K, J, I, R}. The DE now packs
    (sx, sy, sz, sliceW) into (c1..c4) according to the selected axis,
    routing the slice constant to the chosen algebra basis vector.
    Default K preserves legacy behaviour bit-exactly. GPU kernel
    still hardcodes K-axis assignment — non-K selections fall back to
    CPU. UI gains a ComboBox row in `FractalParamsView.axaml`. The
    coquaternion / split-complex variant from the roadmap stays
    deferred — needs a new calculator with swapped product table.
  * Bug fix piggyback in `FractalParamsView.axaml`: the Bicomplex
    "Cam dist" NumericUpDown's `Grid.Row` was "4" against a TextBlock
    at row "5" → cam-dist value overlapped cam-φ. Fixed to row "5".
  * Build clean (0 errors, 20 pre-existing warnings — same baseline as
    Wave 4 closeout). 156/156 Server.Tests pass (140 baseline + 16 new
    allowlist tests).

- 2026-06-23 — Wave 4.12 shipped — Marching Cubes mesh export
  (OBJ smooth + binary STL).
  * `Export/UserBulbMeshExporter.cs` rewritten. Public surface now:
    `ExportMarchingCubes(filePath, sample, cx, cy, cz, range, n, ct)`
    — dispatches on file extension (`.stl` → binary STL with per-face
    normals; anything else → OBJ with smooth per-vertex normals + `v` /
    `vn` / `f a//a b//b c//c` lines). Legacy `ExportObjVoxelSurface`
    kept verbatim for the WinForms shell wire-up + as a low-N fallback.
  * Lorensen-Cline tables embedded inline: 256-entry `EdgeTable[int]`
    (12-bit crossed-edge mask per cube index) and `TriTable[int,16]`
    (-1-terminated triangle edge lists, up to 5 tris per cube). Layout
    matches Paul Bourke's reference.
  * Sampling: uniform (n+1)³ scalar field on a cube of side 2·range,
    centred on (cx,cy,cz). Iso-level held at `step·0.5` to match the
    surface band the raymarcher considers solid (the User Bulb DE
    estimator clamps positive outside; matches the legacy voxel path's
    `surfaceEps`).
  * MC sweep over the n³ cells: 8-corner sign comparison against iso
    → `ci ∈ [0,256)`, fetch `em = EdgeTable[ci]`, materialise the up-to-12
    edge vertices via linear interp toward iso, then emit triangles by
    indexing through `TriTable[ci,*]` in triples until the -1 sentinel.
  * Edge-vertex dedup: each MC edge is uniquely owned by the
    lower-coord corner + an axis (X/Y/Z). Packed into a single
    `int[(n+1)³ · 3]` -1-seeded lookup, keyed
    `((i·side + j)·side + k)·3 + axis`. Each crossed edge produces
    exactly one shared vertex regardless of how many of the (up to 4)
    incident cells reach it.
  * Smooth normals: per-vertex normal = sum of incident triangle face
    normals (computed as `(b-a) × (c-a)`, which already carries area
    weighting in its magnitude), normalised at the end. Zero-length
    vertices fall back to `(0,0,1)` to keep OBJ readers happy.
  * OBJ writer emits `v` + `vn` lines (1-indexed) and faces as
    `f a//a b//b c//c` so DCC tools (Blender, MeshLab) pick the smooth
    normals up directly. STL writer emits the standard 80-byte header
    (`FracturingFog UserBulb MC`), `uint32` triangle count, then 50
    bytes per triangle (12 B face normal + 3×12 B verts + 2 B attribute
    word = 0). Output is little-endian, matches Wikipedia STL spec.
  * Host gate updated in `Hosting/AvaloniaShellBootstrap.cs:1741` to
    call `ExportMarchingCubes` (extension-dispatched) instead of
    `ExportObjVoxelSurface`. `UserBulbViewModel.OnExportMesh` widened
    its save-filter to `"OBJ (smooth)|*.obj|STL (binary)|*.stl"`. The
    WinForms shell (`MainForm.cs:1841` / `Views/UserBulbDialog.cs:682`)
    kept on the legacy voxel path per the CLAUDE.md "WinForms
    deprecated" guidance — no new features land there.
  * `Span<int> edgeIdx = stackalloc int[12]` hoisted out of the
    `i/j/k` triple loop (CA2014 "potential stack overflow"). Unset
    entries from prior cells are never read because `TriTable` only
    indexes edges marked in `EdgeTable[ci]`, which is exactly the
    bitmap that gates the conditional `GetOrCreateEdgeVert` writes.
  * Build clean (0 errors, 0 warnings — the 4 pre-existing
    `AVLN5001 TextBox.Watermark` Avalonia warnings stay on
    UI.Avalonia / PaletteBuilder.Lib, unrelated). 140/140
    `Server.Tests` pass. No new unit-test fixture: Server.Tests doesn't
    reference Engine / Export, and adding a project reference for one
    smoke is heavier than the standard-table verification is worth.
  * Wave 4 backlog now empty.

- 2026-06-22 — Wave 4.6 shipped — Sandbox quat Julia + numerical-
  Jacobian DE on GPU.
  * `GpuRenderParams` extended with `JuliaMode` (int 0/1),
    `JuliaCW/CX/CY/CZ` (double), `JacH` (double), `UseAnalyticDE`
    (int 0/1). Default-zero values are inert for legacy
    `UserBulbGpuCalculator.BulbKernel` (it never reads them), so the
    Roslyn-source / hand-written GPU path keeps its bit-identity.
  * `UserBulbSandboxGpuCompiler` kernel scaffolding refactored. The
    DE + Kernel string bodies extracted to constants
    (`VecSandboxDESource`, `QuatSandboxDESource`, `KernelBodySource`)
    + helpers (`AppendKernelPrelude`, `AppendStepFn`). Both
    `BuildKernelSource` and `BuildChainKernelSource` now compose
    from the same parts; previously the chain version was a verbatim
    duplicate of the single-step kernel scaffolding.
  * Unified Quat DE: branches on `p.UseAnalyticDE` (power-map vs
    5-trajectory Jacobian) and on `p.JuliaMode` (per-pixel c vs Julia
    parameter from `p.JuliaC*` with z₀ taking the pixel coord).
    Numerical-Jacobian mirrors CPU `UserBulbQuatDE`: four perturbed
    trajectories along {W, X, Y, Z}; max |z_pert − z|/h gives the
    conservative spectral-radius proxy. Vec mode kept analytic-only —
    vec-Julia / vec-numerical GPU support is out of scope for 4.6.
  * `SandboxDE` signature collapsed to
    `(double cx, double cy, double cz, GpuRenderParams p, ArrayView<double> __p)`
    so the branch fields live on the struct, not the param list. All
    `KernelBodySource` callsites updated.
  * `UserBulbCalculator.Calculate` GPU gate now allows the Sandbox-quat
    route through Julia mode + non-analytic sources. Old gate:
    `!juliaMode && analyticPattern != None`; new gate: `sandboxQuatGpu ||
    vecAnalyticGpuOk`. Julia + analytic-power case still uses the analytic
    branch (matches CPU `useAnalytic` gate of `!juliaMode && analytic`,
    so Julia falls into the numerical branch).
  * `GpuRenderParams` populated with the new fields at the same site:
    `JuliaMode`, `JuliaC{W,X,Y,Z}`, `JacH`, `UseAnalyticDE`.
  * Build clean (0 errors, 24 pre-existing warnings — same baseline as 4.5).
    140/140 Server.Tests pass. `--ubtest` quat compile + chain + triplex
    + emitter parity scenarios all green.
  * Wave 4 remaining: 4.12 follow-on (real MC + STL).
- 2026-06-22 — Wave 4.5 shipped — Sandbox chain GPU dispatch.
  * Prior gate in `UserBulbCalculator.Calculate` GPU branch was
    `_compiledCompiler == Sandbox && !useChainPath` — chain mode
    locked to CPU. Now `useChainPath` selects between
    `UserBulbSandboxGpuCompiler.TryCompileChain(...)` and the
    existing `TryCompile(...)`.
  * `UserBulbSandboxEmitter.Emit` gained a 5-arg overload accepting
    `IReadOnlyDictionary<int, (string Name, SbxEmitKind Kind)>
    extraSlots`. `EmitCtx` stores the map and `EmitSlot` checks it
    after the let-substitution probe and after the
    z/c/n/params/`t` lookup — slots that survive to the fall-through
    branch are chain prior-step outputs. The original "extras past
    paramCount collapse to `t`" bug fixed; unbound slots now throw
    `NotSupportedException` rather than silently aliasing `t`.
    Slot kind also seeded into `_slotKinds` so `step0.x` member
    access infers Real correctly.
  * `UserBulbSandboxGpuCompiler.TryCompileChain(steps, paramNames,
    quatMode)` — new entry point parallel to `TryCompile`. Reuses
    `SandboxBulbChain.Parse` (shares scope across steps + assigns
    output slots). Per-step emit walks each step's root AST through
    the 5-arg emitter overload, populating the slot map after each
    step with `(localName, stepKind)`. Local name comes from
    `steps[i].OutputName` sanitised via `SanitizeIdent` (leading
    char letter/underscore, non-ident chars replaced with `_`).
  * `BuildChainKernelSource` mirrors `BuildKernelSource` but the
    `Step` body inlines each emitted step expression into a typed
    local (`Vec3 step0 = (body);` / `Vec3 myname = (body);` …) so
    later steps reference earlier ones by their declared name.
    Returns the last step's local. Surrounding `SandboxDE` +
    `Kernel` raymarch scaffolding identical to single-step path,
    so existing fp64 fallback + Roslyn compile + ILGPU JIT plumbing
    + Render shim all reused unchanged.
  * Chain key includes `CHAIN|` prefix so the kernel cache distinguishes
    chain compiles from single-step with the same source string.
  * Build clean (0 errors, 24 pre-existing warnings — same baseline as
    4.4). 140/140 Server.Tests pass. `--ubtest` chain compile +
    chain-analytic detect both green (`Pattern=MandelbulbN power=8`).
  * Wave 4 remaining: 4.6 Sandbox Quat-mode Julia + numerical-Jacobian
    DE on GPU; 4.12 follow-on (real MC + STL).
- 2026-06-22 — Wave 4.4 shipped — Sandbox interpreter opcode-flat
  dispatch.
  * Hot path was `Sbx3Binary.Eval` / `Sbx3Call.Eval` running `string`
    switches on `Op` (`"+"`, `"&&"`, `"<="`, …) and `Name` (`"vec"`,
    `"triplex"`, `"qmul"`, …) per AST step. C# string switches lower
    to chained equality probes, not jump tables — every interior node
    paid an O(n) string-compare cost per Eval call. With Sandbox at
    ~10–15× slower than Roslyn for non-analytic sources per Stage 3C
    notes, this was the dominant tax.
  * Added two internal enums in
    `Engine/Models/SandboxBulbExpression.cs`: `SbxBinOp` (13 values
    Add/Sub/Mul/Div/Pow/Lt/Gt/Le/Ge/Eq/Ne/And/Or) and `SbxFuncId`
    (34 values covering every built-in vec/quat/scalar function).
    Resolved at AST construction time — `Sbx3Binary` ctor calls
    `ResolveOp(op)` once, `Sbx3Call` ctor calls `ResolveFunc(name)`
    once. Original `string Op` / `string Name` fields retained so
    `UserBulbAnalyticDE.DetectSandbox` pattern matchers
    (`Sbx3Binary { Op: "+" }` / `call.Name == "triplex"`) and
    `UserBulbSandboxEmitter.EmitBinary` / `EmitCall` (which read
    `b.Op` / `call.Name`) continue to work bit-identically.
  * `Sbx3Binary.Eval` now switches on `OpKind` (SbxBinOp). 13-arm
    enum switch lowers to a dense jump table.
  * `Sbx3Call.Eval` now switches on `Func` (SbxFuncId). Two-stage
    structure preserved: multi-arg ops in the first `switch`, scalar
    unary transcendentals in the trailing enum switch (handles
    Quat-rejection via `ApplyScalar`).
  * Out of scope for this slice — DynamicMethod IL emit. Opcode-flat
    dispatch was the cheaper of the two options on Stage 3C's list
    and removes the bulk of the string-compare tax without
    introducing a JIT-emit pipeline. If sandbox perf needs another
    pass after this, the next move is `Eval(env)` virtual call
    flattening (compile AST into a linear opcode array
    walked by a single while loop) or DynamicMethod IL.
  * Build clean (0 errors, 24 pre-existing warnings — same baseline
    as 4.2). 140/140 Server.Tests pass. `--ubtest` runs full sandbox
    + chain + quat + emitter parity matrix, all green.
  * Wave 4 remaining: 4.5 Sandbox chain GPU dispatch; 4.6 Sandbox
    Quat-mode Julia + numerical-Jacobian DE on GPU; 4.12 follow-on
    (real MC + STL).
- 2026-06-22 — Wave 4.1 + 4.2 shipped.
  * 4.1 — `ScreenSpacePost.ApplyHdrDof` now gates on `fx.UseGpuPost` and
    routes the three skewed-box blurs through new ILGPU kernels in
    `GpuPostKernels.cs`. Kernels: `DofCocKernel` (per-pixel CoC from
    depth + focus + cocScale), `DofSkewedBoxKernel` (1D box blur along
    (dx, dy) with width = per-pixel CoC and the same bleed-control
    behaviour as the CPU `SkewedBoxBlur` — foreground neighbours
    contribute only when their CoC reaches the centre), and
    `DofMinBlendKernel` (composites three skewed passes back into
    `hdrBuffer` via per-channel min, skipping sub-CoC and sky pixels).
    Float-precision NaN sky detection uses the self-compare trick the
    bloom kernels already use (ILGPU doesn't accept `float.IsNaN`).
    Falls back to the CPU path on any init / OOM / kernel throw — no
    new toggle, the existing `UseGpuPost` knob covers it.
  * 4.2 — `LightingFxData.UseGgxSampling` (default `false` preserves
    16b mirror-reflect bit-identity). When on, both reflect-direction
    sites in `ShadingPipeline.Shade<TDe>` (initial mirror from view ray
    + per-bounce re-reflect against newly-hit normal) replace the
    mirror reflect with a GGX VNDF sample (Heitz 2018). New helpers
    in `ShadingPipeline.cs`: `HashPair(x, y, z, bounce)` returns two
    deterministic Wang-hashed uniforms seeded by world position + bounce
    index (stable across frames, decorrelated per pixel); and
    `SampleGgxReflect(V, N, roughness, u1, u2)` builds a Frisvad TBN,
    stretches view dir to the unit hemisphere per Heitz, samples the
    visible-normal lobe, unstretches, and returns the reflected
    direction. One sample per bounce — temporal/spatial decorrelation
    spreads the lobe across the screen so we don't need Monte Carlo
    averaging. Below-horizon samples (g·n ≤ 0) fall back to mirror.
    Knob ties into the existing `Roughness` field (alpha = roughness²);
    `Roughness = 0` collapses to mirror by definition.
  * Build clean (0 errors, 24 pre-existing warnings — same baseline as
    4.3). 140/140 Server.Tests pass. No commit made.
  * Wave 4 remaining: 4.4 Sandbox interp perf; 4.5/4.6 Sandbox chain +
    Quat-Julia GPU dispatch; 4.12 follow-on (real MC + STL).
- 2026-06-22 — Wave 4 audit + Wave 4.3 shipped.
  * Audit: 4.8 color drivers, 4.9 FOV/DoF/clip/SS, 4.10 multi-equation
    chain (`UserBulbChain` + `CompileSandboxChain` + `WrapUserSourceChain`),
    4.14 Julia mode Vec3 (UserBulbDE juliaMode branch) all found
    wired in code but unmarked in plan — marked ✅ Shipped (audited
    2026-06-22). 4.12 (mesh export) re-classified 🟡: voxel-cube OBJ
    exporter at `Export/UserBulbMeshExporter.cs` shipped, real
    Marching Cubes (256-entry triangulation table) + STL writer +
    normal smoothing still open.
  * 4.3 HDRI auto-preload — `HdriProbe.Preload` action added
    (`Abstractions/Rendering/Lighting/HdriProbe.cs`). `HdriRegistry`
    static ctor wires it to `Task.Run(() => TryLoadFromFile(...))`.
    `HdriRegistry.TryLoadFromFile` rerouted through a `_parseGate`
    `ConcurrentDictionary<string, Lazy<HdriImage?>>` so concurrent
    first-hits funnel through a single Lazy parse instead of N
    pixel-worker threads each opening the file + parsing the RGBE
    stream + writing to the cache. Gate entry removed after parse so
    a fixed file can be retried.
  * Preload kicked from three seams covering every parameter-change
    route: (a) `FractalParamsViewModel.EnvironmentName` setter
    (UI-driven changes); (b) `ShellViewModel` theme + region preset
    apply sites that assign `FractalParameters.Lighting` wholesale
    (bypass the VM setter); (c) `LightingFxPresetData.ApplyTo`
    (preset DTO apply path that fires for theme JSON loads). Fire-
    and-forget on background thread; render trigger continues
    concurrently, but with the per-path lock guaranteeing single
    parse the worst-case race is `parse_ms + 1 frame` not
    `parse_ms · N_threads`.
  * Build clean (0 errors, 24 pre-existing warnings — same baseline
    as 4.13). 140/140 Server.Tests pass. No commit made.
  * Wave 4 remaining: 4.1 Lighting-FX GPU port (big — ILGPU port of
    HDR DoF skewed blurs); 4.2 GGX importance sampling per bounce;
    4.4 Sandbox interpreter perf (opcode-flat dispatch or
    DynamicMethod IL emit); 4.5/4.6 Sandbox chain + Quat-Julia GPU
    dispatch; 4.12 follow-on (real MC + STL).
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
- 2026-06-22 — Wave 4.13 shipped — `.fbulb` snapshot import/export.
  Pre-4.13 `UserBulbStore.ExportEntry`/`ImportEntry` only round-tripped
  `UserBulbEntry` (Name/Source/Promoted/Chain) — the preset's axis mode /
  Julia c / camera / lights / colour driver / render budget were lost on
  export. Per the Wave 4.11 follow-on, `.fbulb` is the schema-extension
  point that finalises per-entry persistence so Quaternion-Julia + KIFS
  presets reload exactly the way they were saved.
  * `Abstractions/Models/UserBulbSnapshot.cs` — new versioned envelope.
    `Version = 1`, `Entry` (UserBulbEntry), plus ~30 nullable knobs
    mirroring `FractalParameters.UserBulb*`: axis mode, compiler, DE mode,
    backend, QuatSliceW, Julia mode + (Cx,Cy,Cz,Cw), camera distance /
    theta / phi, light theta / phi, light 1-3 intensity, AO samples, fog
    density, colour driver, orbit-trap (X,Y,Z), iter-component axis,
    iterations, max steps, epsilon, bailout, Jacobian h, cull radius,
    FOV, clip-plane, super-sample, time, named params list. Every knob
    nullable → missing fields leave the target slot untouched on import,
    so older + newer producers interoperate without breaking changes.
  * `UserBulbStore.ExportSnapshot(snapshot, path)` — writes the envelope
    with `JsonIgnoreCondition.WhenWritingNull` so emitted JSON only
    contains what the producer actually set; keeps `.fbulb` files small.
    `ImportSnapshot(path)` reads the envelope, merges the entry into the
    store (collision rename `(N)`), returns the parsed snapshot so the
    caller can apply the runtime knobs.
  * Legacy fallback in `TryParseSnapshot`: `JsonDocument`-peeks the root
    for `Version` + `Entry`; absent → parses as bare `UserBulbEntry`,
    wraps in a snapshot with `Version = 0` (sentinel for "legacy, no
    knobs to apply"). Pre-4.13 `.fbulb` files written by `ExportEntry`
    round-trip unchanged.
  * `UserBulbViewModel.OnExport` builds the snapshot from `_params` via
    new `BuildSnapshotFromParams(entry)`. `OnImport` calls `ImportSnapshot`,
    applies non-null knobs through `ApplySnapshotToParams`, then
    `SyncMirrorFromParams` re-pulls every VM mirror field + raises
    `PropertyChanged` for the bound views — suppress flag stays on
    through the bulk update so no per-property render fires; the
    final `LoadEquationByName` triggers one compile + render.
  * `ExportEntry`/`ImportEntry` retained for legacy callers (none in
    tree besides the now-rewritten VM). Doc-comments flag them as
    pre-4.13.
  * Build clean (0 errors, 24 pre-existing warnings). 140/140 Server.Tests
    pass.
  * Follow-on: `.fbulb` registered as an OS file-association handler
    (double-click → open in FF). Not in 4.13 scope — file format works
    end-to-end via the editor's Import/Export buttons. Filed as 4.13.f1.
- 2026-06-22 — Wave 4.11 shipped — UserBulb preset library seed.
  Audit of `Abstractions/Models/UserBulbStore.cs:SeedDefaults` found 6/10 of
  the spec list (`Docs/Technical/UserBulb3D-DevelopmentPlan.md:387-398`)
  already seeded — Mandelbulb p=8, Square triplex (squared variant), Sin-bulb,
  Abs-bulb p=8, Mandelbox, Animated breathing bulb. Four missing: Menger
  sponge step, Sierpinski tetrahedron, Kaleidoscopic IFS chain, Quaternion
  Julia. Added this turn:
  * `UserBulbStore.SeedDefaults` — 4 new `Equations.Add` calls. Menger /
    Sierpinski reuse the bodies already centralised in
    `UserBulbChainPrimitives.GetById(Id{Menger,Sierpinski})` so the
    standalone preset and the hybrid-chain step are bit-equal. Quaternion
    Julia uses the same triplex-squared body as `Square triplex` with an
    inline `// Switch Axis Mode → Quat + Julia Mode` comment — neither
    `Source` nor `Chain` carries axis-mode / Julia-mode flags today, so
    the user toggles them in the editor; per-entry persistence is the
    Wave 4.13 (.fbulb) schema work.
  * `UserBulbStore.TopUpBuiltins` — 4 matching `Ensure(name, factory)`
    calls so pre-existing `%APPDATA%/FracturingFog/userbulbs.json` files
    pick up the new entries on next launch (mirrors how the B.3 hybrid
    chains were retro-fitted).
  * `UserBulbChainPrimitives.KaleidoscopicIfsChain()` — new 3-step factory
    (Sierpinski fold → Y-axis rotation → scale-2 + translation), matching
    the spec's "chain: fold → rot → scale, 3 steps". Two new id consts
    `IdKifsRot` / `IdKifsScale` so the kernel caches per step. The
    Kaleidoscopic IFS preset's `Source` is a single-pass fallback for
    legacy chain-less loaders; `Chain` is the canonical form and
    overrides at runtime per the `UserBulbEntry` doc-comment contract.
  * Build clean (0 errors, 24 pre-existing warnings — CS0219 in
    generator output + AVLN5001 Watermark obsolete). 140/140
    Server.Tests pass.
  * Follow-on: per-entry axis-mode / Julia-c / camera persistence —
    Wave 4.13 (.fbulb single-equation import/export) is the place for the
    schema extension; Wave 4.11 deliberately scoped to chain-source seeds
    only to avoid churning UserBulbEntry mid-wave.
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
- 2026-06-22 — Wave 0.5b shipped — Visual-regression baseline recorded.
  `Tools/VisualRegression/Program.cs` needed four pre-flight fixes before the
  record run could succeed:
  * Shelled the cross-platform `FracturingFog.App` stub which doesn't yet
    handle `--batch` (the BatchEntry CLI lives in the WinExe). Constant
    `BatchProject = "FracturingFogCLD.csproj"` flips the target.
  * `MagnetOne` / `MagnetTwo` corrected to `Magnet1` / `Magnet2` matching
    `FractalType` enum literals in `Abstractions/Models/Enums.cs`.
  * Each case now passes `--x 0 --y 0 --zoom 0.5` (BatchOptions validator
    requires region or coords; procedural / non-escape-time families ignore
    them and use family-internal framing).
  * Per-case timeout bumped 120 s → 600 s to absorb the cold-cache rebuild
    on the first invocation; subsequent cases skip rebuild and render in
    seconds at 256² Standard.
  * Redirected stdout/stderr drained via `BeginOutputReadLine` /
    `BeginErrorReadLine` (no-op handlers). Initial record-attempt wedged
    on the Flame case — child blocked writing into a full pipe buffer
    because the harness never read the redirected streams. Standalone
    Flame batch completed in seconds; only the un-drained pipeline
    blocked. Other 21 cases passed at first attempt because their
    per-case chatter stayed under the pipe-buffer ceiling.
  Recorded 22 SHA256 entries to `Tools/VisualRegression/baseline.json`.
  Curiosity: `newton-default` and `nova-default` share a hash at the (0,0)
  centre — either Nova falls back to Newton-rendering at default knobs or
  both produce identical output at this framing. Baseline pins current
  behaviour; future regression run will fire if either changes. Filed
  separately as an audit (not in 0.5b scope).
  Non-determinism note: Buddhabrot + IFS hashes shifted across two record
  runs (RNG-driven Monte Carlo). These two cases won't gate as
  bit-equality regressions until their RNG is explicitly seeded — separate
  follow-on (deferred). Plasma uses `PlasmaSeed` field default so its
  output stayed stable.
- 2026-06-22 — Wave 2.6 broadening shipped — AA for alt calcs.
  Wave 2.6 originally landed canonical Mandelbrot AA only ("alt calcs (user-
  equation / sandbox) currently skip AA pending interface broadening" per
  status entry 2026-06-20). Now extended to every alt calc whose
  `IFractalCalculator.SupportsZoomPan` is true — escape-time families
  (Newton/Nova/Halley/Secant/Magnet1/Magnet2/Glynn/Spider, Phoenix via
  `EscapeTimeCalculator`), user-equation hot-load path, sandbox, and 3D
  raymarchers (Mandelbulb/UserBulb/Mandelbox/KIFS/QuaternionJulia/
  QuaternionMandelbrot/Bicomplex/Kleinian). Procedural / non-escape-time
  families (IFS/LSystem/Plasma/Flame/DLA/Apollonian/StrangeAttractor/
  Buddhabrot/Nebulabrot/Anti*) gate out at the `SupportsZoomPan` check —
  their `Calculate()` ignores centre+zoom so jitter would just re-roll noise.
  * New `RunMsaaAccumulateAlt(IFractalCalculator, aaSamples, token)` helper
    mirrors `RunMsaaAccumulateMandelbrot` shape: sub-pixel jitter on
    (CenterX, CenterY) over a √N×√N grid at 3.5/max(W,H)/Zoom pixel scale,
    re-runs `Calculate(token)` per sample, accumulates BGRA channel sums in
    pinned int arrays, writes the weighted-mean colour back to ColorBuffer.
    MandelbrotCalculator stays on the typed helper because it isn't an
    `IFractalCalculator` (carries QD/DD/OD limb fields the interface doesn't
    expose).
  * Call-site at `FractalRenderHost.cs:1038` branches: `!useAlt` → existing
    Mandelbrot helper; `useAlt && altCalc.SupportsZoomPan` → new alt helper;
    `useAlt && !SupportsZoomPan` → AA skipped (procedural family).
  * Heavy delegate overhead on UserEquation/Sandbox/UserBulb Calculate()
    remains; user opts into 4×/16× cost by picking High/Ultra/Extreme
    QualityPreset. Standard stays 1× AA across the board (unchanged
    default).
  * Build clean (0 errors). 140/140 Server.Tests pass.
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
