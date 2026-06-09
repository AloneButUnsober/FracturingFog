# Performance Development Plan

Push C# / .NET to the limit on fractal compute, deep zoom, video FPS, and
single-image render time. Ordered cheap → expensive. Each item is
self-contained; land in order, verify each, then move on.

Baseline already optimized:
- `MandelbrotCalculator` — SIMD DD (AVX2+FMA), AVX-512 perturbation lane,
  BLA, SA prelude, cardioid skip, block periodicity, devirtualized
  `IColorMap` via concrete-type generic dispatch, inline color, ref-orbit
  cache, CDF cache.
- `FractalRenderHost` — pooled BGRA scratch, `Parallel.For` post-FX,
  `_d3dGate` serialisation, stale-frame re-upload.
- `DirectXRenderer` — dynamic texture + `WriteDiscard` map, full-screen
  triangle (no vbuf), opaque blend.

---

## Tier 1 — Cheap, high ROI

### T1.1 — Strip `new StackTrace()` from `MandelbrotCalculator.Calculate`

**Location:** `Calculators/MandelbrotCalculator.cs:287-288`

Current:

```csharp
public void Calculate(CancellationToken ct = default)
{
    var callingMethod = new StackTrace().GetFrame(1)?.GetMethod();
    Debug.WriteLine($"Calculate() called from {callingMethod?.DeclaringType?.Name}.{callingMethod?.Name}{Environment.NewLine} ...");
    ...
}
```

Problem: `Debug.WriteLine` is `[Conditional("DEBUG")]` so the arg-eval
disappears in Release — BUT `new StackTrace().GetFrame(1)?.GetMethod()` is
evaluated outside the `[Conditional]` call and still runs in Release.
Allocates `StackTrace` object + walks frames + reflects MethodBase. At 60
fps video that is 60 wasted allocs + walks per second plus GC pressure.

**Fix:** Wrap in `#if DEBUG` or extract into a `[Conditional("DEBUG")]`
static helper so the entire block compiles out in Release. 5-minute change.

**Verify:** Build Release, run video zoom 30 s. GC counters before/after
(`GC.CollectionCount(0)`).

---

### T1.2 — VSync toggle on `DirectXRenderer.Render`

**Location:** `Rendering/DirectXRenderer.cs:442`

Current:

```csharp
_swapChain.Present(1, PresentFlags.None);
```

Hard-locked to monitor refresh. Caps live preview to 60/120/240 Hz, caps
video render to display refresh, and adds frame-pacing latency to
single-image renders.

**Fix:**
1. Add `public bool VSync { get; set; } = true;` on `IFractalRenderer` /
   `DirectXRenderer` / `DirectX12Renderer`.
2. Replace the literal `1` with `(VSync ? 1u : 0u)`.
3. Wire `FractalRenderHost` to set `VSync = false` while
   `IVideoZoomController` is recording or while a single-image render is
   blocking on the calc-continuation Present.
4. Optional: enable `DXGI_PRESENT_ALLOW_TEARING` on the swap-chain creation
   path so `Present(0)` actually tears (otherwise driver still waits on
   non-flip-discard chains). Requires
   `DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING` at chain creation.

**Verify:** Run video zoom, observe FPS counter unhitched from refresh
rate. Live preview still vsync'd (no tearing in idle browsing).

---

### T1.3 — Single-shot `MemoryCopy` fast path in `UpdateTexture`

**Location:** `Rendering/DirectXRenderer.cs:362-391`

Current: `for (row = 0; row < height; row++) Buffer.MemoryCopy(...)`.
Per-row branch + per-row call overhead.

**Fix:** Detect `mapped.RowPitch == width * 4`. When equal, single
`Buffer.MemoryCopy(srcPtr, dst, width*height*4, width*height*4)`. Fall back
to row loop only when GPU adds padding.

```csharp
long rowBytes = (long)width * 4;
if (mapped.RowPitch == rowBytes)
{
    Buffer.MemoryCopy(srcPtr, dst, rowBytes * height, rowBytes * height);
}
else
{
    for (int row = 0; row < height; row++) { ... }
}
```

**Verify:** 4K render upload time. Single copy will be ~10-30% faster than
2160 row-copies.

---

### T1.4 — `EscapeTimeCalculator` concrete-colormap dispatch

**Location:** `Calculators/EscapeTimeCalculator.cs:134-138`

Current:

```csharp
private void DispatchByColorMap<TKernel>(TKernel kernel, CancellationToken ct)
    where TKernel : struct, IFractalKernel
{
    CalculateCore<TKernel, IColorMap>(kernel, ColorMap, ct);
}
```

`TMap` is constrained to interface, so JIT cannot devirtualize
`colorMap.Map(...)` inside `FillAuxAndColor`. Every pixel pays a vtable
lookup. Hits Julia / BurningShip / Tricorn / Multibrot / Phoenix — half the
fractal catalogue.

**Fix:** Mirror `MandelbrotCalculator.Calculate`'s concrete-type switch.
Build a `DispatchByColorMapConcrete<TKernel>` that switches on `ColorMap`'s
runtime type and calls `CalculateCore<TKernel, ConcretePalette>(...)` so
the JIT specialises Map() per-palette and inlines it.

The list mirrors `MandelbrotCalculator.cs:358-560` cases (skip the
3D/normal-aware themes that are Mandelbrot-only — fall back to the
interface-generic path for those).

**Verify:** Benchmark Julia render at maxIter=512, 1080p. Expect 1.5-2x
speedup.

---

## Tier 2 — Medium effort

### T2.1 — Vectorize brightness/contrast post-FX

**Location:** `Rendering/FractalRenderHost.cs:989-1023`

Current: per-pixel unpack BGRA → 3 floats → scale → clamp → repack inside
`Parallel.For`. Scalar.

**Fix:** Process 4 pixels at a time with `Vector128<byte>`:
1. Load 16 bytes (4 BGRA pixels) into `Vector128<byte>`.
2. Widen to two `Vector128<short>` (low / high halves).
3. Widen each to `Vector128<float>` (4 lanes).
4. Apply `(v - 128) * contrast + 128 + brightness*255`, clamp to [0,255].
5. Narrow back to byte, repack.

Use `Sse41.Pack*`/`Avx2.Permute*` for the narrow path. Alpha lane masked
out + restored to 0xFF.

**Verify:** Adaptive-slider tick latency at 4K. Expect 4-8x on the post-FX
pass.

---

### T2.2 — Suppress pre-overlay snapshot during video recording

**Location:** `Rendering/FractalRenderHost.cs:1033-1038`

Current: every `UploadProcessedBuffer` does
`Array.Copy(dst, pre, n)` → 8 MB at 1080p, 33 MB at 4K. Only
`SaveLastFrameToPng` consumes the snapshot.

**Fix:** Add `_recordingActive` flag set by the video controller's
recording start/stop. When true, skip the snapshot. The save path
(`SaveLastFrameToPng`) only runs from interactive UI and is gated by user
action — no race with video record.

```csharp
if (!_recordingActive)
{
    if (_uploadPrePool == null || _uploadPrePool.Length < n)
        _uploadPrePool = new uint[n];
    var pre = _uploadPrePool;
    Array.Copy(dst, pre, n);
    _lastPreOverlayBuffer = pre;
}
else
{
    _lastPreOverlayBuffer = null;
}
```

**Verify:** Video record at 1080p60. Per-frame upload time. Expect 5-10%
frame-time cut.

---

### T2.3 — `EscapeTimeCalculator` SIMD inner loop

**Location:** `Calculators/EscapeTimeCalculator.cs:142-185`

Current: scalar per-pixel loop. No SIMD, no cardioid skip, no periodicity.

**Fix:** Port the `MandelbrotCalculator.ComputeRowSP` SIMD lane structure
to a `IFractalKernel.StepSimd(ref Vector<double> zr, ref Vector<double> zi, ...)`
struct method. Each kernel struct implements it inline so the JIT
specialises.

Kernels in scope (escape-time `z² + c`-family, no Phoenix):
- `MandelbrotKernel` — already trivially vectorizable
- `JuliaKernel` — same SIMD shape, c-constant broadcast
- `BurningShipKernel` — needs `Vector.Abs` for `|zr|`, `|zi|`
- `TricornKernel` — needs `Vector.Negate` on `zi`
- `MultibrotKernel` (z^n) — power = 3/4/5: unrolled SIMD; power = 2: same
  as Mandelbrot

Phoenix uses two-step memory; keep scalar path.

**Verify:** Julia/BurningShip frame time at 1080p. Expect 3-4x on SP path.

---

### T2.4 — Dedicated calc thread + bounded queue

**Location:** `Rendering/FractalRenderHost.cs:554-570`

Current: every `Trigger()` does `Task.Run(...).ContinueWith(...)`. New
Task + continuation + threadpool scheduling each frame.

**Fix:**
1. One background `Thread` started in `FractalRenderHost` ctor.
2. `BlockingCollection<FrameJob>` of capacity 1 (latest-only semantics).
3. `Trigger()` clears + enqueues; the thread dequeues + runs Calculate
   inline.
4. Completion fires `FrameCompleted` via thread-pool callback (one
   ThreadPool dispatch per frame, instead of a Task + ContinueWith).

**Verify:** Sustained video at 60 fps. ETW threadpool counters before /
after. Saves 0.1-0.5 ms per frame plus GC pressure on Task allocations.

---

### T2.5 — Cached `ParallelOptions` + `RangePartitioner`

**Locations:**
- `Calculators/MandelbrotCalculator.cs:693, 1082, 1250, 2508, 2590, 2644`
- `Calculators/EscapeTimeCalculator.cs:155, 200`
- `Rendering/FractalRenderHost.cs:999`

Current: `new ParallelOptions { CancellationToken = ct }` every Calculate.
And `Parallel.For(0, height, ...)` partitions row-at-a-time which spawns
one work-item per row — `height` task creations per Calculate.

**Fix:**
1. Field `private readonly ParallelOptions _po = new()`. Set `_po.CancellationToken = ct` per Calculate.
2. Replace `Parallel.For(0, height, _po, body)` with
   `Parallel.ForEach(Partitioner.Create(0, height, height / (Environment.ProcessorCount * 4)), _po, range => { for (int y = range.Item1; y < range.Item2; y++) body(y); })`.
   Chunk count = `procCount * 4` gives good load balancing without per-row
   scheduling overhead.

**Verify:** Per-Calculate overhead on small frames (480p). Expect 5-15%
improvement at low maxIter where Parallel scheduling dominates.

---

## Tier 3 — Large effort, large payoff

### T3.1 — GPU compute path for SP escape-time

Move the SP escape-time inner loop to a D3D11 compute shader (HLSL CS 5.0)
or extend the existing ILGPU translator (`UserBulbIlgpuTranslator`) to the
escape-time family.

**Scope:**
- SP path only (zoom < ~1e15). DD/QD perturbation stays CPU — BLA tables +
  reference orbit don't port cleanly without rewrite.
- Compute shader writes directly to a `RWTexture2D<unorm float4>` shared
  with the existing display SRV → zero CPU↔GPU round-trip per frame.
- IColorMap → HLSL: code-gen palette evaluation per-theme (the
  `ColorGen` pipeline already does compile-time C# codegen; extend to
  HLSL emit). Or for first cut: pass smooth + dist + normals back to CPU
  for color (loses round-trip but still wins on the iteration loop).

**Expected gain:** 10-30x on integrated GPU, 50-200x on discrete. Video
runs at native refresh at maxIter 4096. Single-image render at 8K becomes
sub-second on discrete.

**Risks:** Driver-specific perf cliffs at high maxIter (long-shader timeout
on Windows TDR — work around with iteration-bucket dispatch). Float-only
on most consumer GPUs (no FP64 SIMD lanes), so DD stays CPU.

**Phases:**
1. HLSL CS for `MandelbrotKernel` only, palette = on CPU.
2. Generate HLSL palette evaluation from `ColorGen`. End-to-end GPU.
3. Extend to `JuliaKernel`, `BurningShipKernel`, `TricornKernel`,
   `MultibrotKernel`.
4. Move `ColorBuffer` to GPU-resident `StructuredBuffer<uint>`; eliminate
   the BGRA upload (renderer reads directly).
5. Investigate FP64 path via ILGPU/CUDA on discrete NVIDIA for DD on GPU.

---

### T3.2 — Reference-orbit recycling across video frames

**Location:** `Calculators/MandelbrotCalculator.cs:213-216` (cache key)

Video zoom continuously recentres. Any centre delta busts the orbit cache
→ full reference-orbit recompute (single-thread, can be 100k+ iters at
deep zoom).

**Fix:** Add an orbit-validity check before invalidating:
1. Re-evaluate cached `_refZr`, `_refZi` against the new centre delta `dc`.
2. If `|δ_n|` stays within BLA validity radius at every checkpoint, reuse
   the orbit + rebuild only BLA / SA caches (cheaper than re-running the
   orbit).
3. Otherwise full rebuild.

Most video frames at deep zoom drift by sub-pixel amounts → orbit stays
valid. Expected 30-70% reduction in HP video frame time at zoom > 1e25.

---

### T3.3 — Non-temporal stores + buffer pinning

**Location:** color/iter buffer write paths in `MandelbrotCalculator` and
`EscapeTimeCalculator`.

`ColorBuffer` is consumed by GPU upload immediately after Calculate — no
CPU re-read. `Avx.StoreAlignedNonTemporal` bypasses cache pollution and
saves the cache-line eviction cost.

Requires the buffers to be 32-byte aligned. Allocate via `GC.AllocateArray<uint>(n, pinned: true)` or `NativeMemory.AlignedAlloc`. Pinned alloc also
avoids GC scan cost for the giant frame buffers.

**Verify:** L2/L3 cache miss counters via VTune / `perf stat`. Expect 5-15%
on 4K renders where buffers exceed L2.

---

### T3.4 — `<PublishReadyToRun>true</PublishReadyToRun>`

**Location:** `FracturingFogCLD.csproj` and dependent calc projects.

Adds R2R native code alongside IL. Kills JIT-warmup tax on first frame
after launch and on first frame after assembly reload (UserEquation /
Sandbox hot-load).

**Risk:** Larger binary size (~30-50% per assembly).

**Verify:** Time-to-first-frame on cold launch.

---

## Cross-cutting

- All Tier-1 changes are isolated to one file each. Land independently.
- Tier-2 has interdependencies: T2.3 (EscapeTime SIMD) lands cleanly only
  after T1.4 (concrete dispatch) — same file, same dispatch path.
- Tier-3 GPU path (T3.1) is the biggest single FPS win. Schedule its phase
  1 immediately after Tier-1 + Tier-2 land, in parallel with T3.2 / T3.3.
- Add a benchmark harness in `Benchmarks/` covering: Mandelbrot SP @ 1080p,
  Mandelbrot HP @ 1e20 zoom, Julia SP @ 1080p, BurningShip SP @ 1080p,
  Adaptive-slider repaint latency, Video frame time @ 1080p60. Run before
  and after each tier to keep gains visible.

## Execution order

1. **Tier 1** — T1.1 → T1.2 → T1.3 → T1.4. One PR each, verify each.
2. **Tier 2** — T2.1 → T2.2 → T2.3 (after T1.4) → T2.4 → T2.5.
3. **Tier 3 phase planning** — design doc for T3.1 GPU compute. Begin T3.2
   ref-orbit recycling and T3.3 non-temporal stores in parallel.
4. **Tier 3 build-out** — T3.1 phases 1 → 5.

---

## Tier 2 landed-so-far

- **T2.4** — `FractalRenderHost` now owns a dedicated background `Thread`
  ("FractalCalc") + a `BlockingCollection<FrameJob>` with `boundedCapacity: 1`.
  `Trigger()` drains any queued-but-unstarted job and enqueues the freshest
  one (latest-only semantics), so bursts of wheel / key-repeat triggers
  collapse before the calc thread sees them. The thread runs the
  stale-frame re-upload + `Calculate(token)` inline, then hands the
  post-calc upload (CDF build + `UploadProcessedBuffer` + `FrameCompleted`)
  off to the threadpool via `ThreadPool.UnsafeQueueUserWorkItem` + cached
  `Action<UploadCtx>`. Removes the per-frame `Task.Run` + `ContinueWith`
  pair (~4 allocs/frame) and removes the per-trigger threadpool dispatch
  hops. `Dispose()` calls `CompleteAdding()` + `Join(2000)` before tearing
  down the renderer.
- **T2.5** — chunked `Partitioner.Create(0, h, chunk)` replaces
  `Parallel.For(0, h, ...)` everywhere in `MandelbrotCalculator` (7 sites),
  `EscapeTimeCalculator` (3 sites), and `FractalRenderHost.UploadProcessedBuffer`
  (1 site). `chunk = max(1, h / (procCount * 4))` so workers grab
  contiguous row blocks instead of single rows — collapses scheduling
  dispatch count from `h` to `~procCount * 4` per Calculate. Three stray
  `new ParallelOptions()` sites in `MandelbrotCalculator` (Adaptive HE,
  band-dither recolor, plain recolor) now use the cached `_po` field.
  Largest win on small frames + low maxIter where scheduling overhead
  dominated row body cost.
- **Video iter-cap dialog picker (Phase 1)** — `VideoIterCapMode { Off,
  Global, PerTile }` enum added to `Abstractions/Models/SlideshowConfig.cs`,
  persisted on `VideoSettingsConfig.IterCapMode` (default `Global` =
  prior auto-adaptive behaviour). Wired through `VideoZoomRequest`,
  `VideoSettingsViewModel` (`IterCapModes` list + `IterCapMode` string),
  the embedded `VideoSettingsView` (ComboBox row under "Adaptive iter
  cap (perf vs quality)"), and `ShellViewModel.StartVideoFromConfig`.
  `FractalRenderHost.Video.cs` honours the mode in the adaptive ratchet
  (`Off` keeps `_videoIterCap` pinned at 1.0 so the calculator always
  runs at full maxIter — strong-HW path) and in the per-frame iter
  application. `PerTile` is a Phase-1 stub: `StartVideo` / `StartSlideshow`
  emit a one-time `Console.Error` warning and the runtime treats it as
  `Global`. Phase 2 (true per-tile cap) requires either multi-call
  Calculate on sub-rects or pushing a per-tile cap array into the
  iteration loop in every color-map specialisation — out of scope for
  the Phase 1 commit, tracked as a follow-up.
- **Video iter-cap dialog picker (Phase 2)** — `MandelbrotCalculator`
  gains `int[]? PerRowMaxIter`; when non-null and sized to `Height`,
  row `y` uses `PerRowMaxIter[y]` instead of the global
  `MaxIterations`. Only the SP path
  (`CalculateDoublePrecision`/`ComputeRowSP`) honours it; HP (DD/QD)
  perturbation paths still use the global cap (Phase 2.1 follow-up).
  `FractalRenderHost.Video.cs` divides the frame into `TileBands = 8`
  vertical row bands. After each successful Calculate the SP path
  samples `IterationBuffer` along a single near-mid-band row at
  `samplesPerBand = 32` strides per band — `O(TileBands * 32)` per
  frame. `BuildPerRowMaxIterCap` smoothsteps the normalised band avg
  dwell over `[TileInteriorLo=0.50, TileInteriorHi=0.90]` and lerps
  the per-band cap multiplier from `1.0` (full quality, boundary
  detail) down to `TileMinCapMult=0.40` (interior-dominated, capped).
  Caps floor at 64 iter. The per-row array is pooled + reused across
  frames; cleared at video end + slideshow end + run start so
  interactive `Trigger()` reverts to the global cap path. PerTile no
  longer routes to Global — the Phase 1 fallback warning + one-shot
  guard field are removed.
- **Video iter-cap dialog picker (Phase 2.1)** — extends `PerRowMaxIter`
  honouring to the HP (DD/QD) perturbation path
  (`CalculateHighPrecision` dispatching `ComputeRowPT8`/`PT4`/`Scalar`)
  and the `CalculateOrbitAware` path (orbit traps, stripe average,
  TIA, etc.). Deep-zoom Mandelbrot regions (Deep Julias visual class)
  use the HP path; without this, PerTile mode produced no win past
  the HP-promote threshold. Each row body reads `PerRowMaxIter[y]`
  once, falls back to `MaxIterations` when null or zero. Reference
  orbit length is pixel-cap independent so BLA/SA tables are not
  invalidated by per-row caps. Adds an in-set rewrite post-row: any
  pixel whose `IterationBuffer` entry hit the row cap (`iters >=
  rowMaxIt < MaxIterations`) is overwritten with `MaxIterations` so
  the recolor's `iters >= maxIter` in-set gate classifies it
  correctly — semantically right for unescaped pixels and prevents
  visible iter-banding at band boundaries.
- **Video iter-cap dialog picker (single-shot Video Zoom dialog)** —
  `AvaloniaDialogs.ShowVideoAsync` (single-shot Video Zoom dialog under
  the FloatingMenu) gains the same Adaptive iter-cap ComboBox the
  embedded VideoSettingsView already had. Both `startBtn.Click` and
  `slideshowBtn.Click` populate `IterCapMode` from the picker. Default
  Global preserves the prior behaviour.
- **Video iter-cap dialog picker (alt-calc extension)** —
  `EscapeTimeCalculator` (used by Julia / BurningShip / Tricorn /
  Multibrot / Phoenix) gains `int[]? PerRowMaxIter` with the same
  semantics as `MandelbrotCalculator`. All three calc paths
  (`CalculateCoreSimd`, `CalculateCore`, `CalculatePhoenix`) honour
  the per-row cap and run the in-set rewrite post-row.
  `SyncAltCalculatorForVideoFrame` copies the per-row array onto an
  `EscapeTimeCalculator` cast. Generated calcs under
  `Calculators/Generated/` are template-driven by `CalculatorGen` and
  don't honour per-row caps yet — listed as a follow-up requiring
  template + regen.
- **Video iter-cap dialog picker (band-stat smoothing)** —
  `SampleBandDwellStats` now averages `BandRowsPerBand = 4` evenly-
  spaced rows per band at `samplesPerBand = 32` strides per row
  (1024 IterationBuffer reads per frame total) instead of a single
  near-midpoint row. Adds an EMA (`emaAlpha = 0.40`) so per-band
  stats refresh fully over ~5 frames — fast enough to track region
  changes during a zoom, slow enough that a single noisy frame
  doesn't whip the cap. First frame after StartVideo/StartSlideshow
  still records the raw average (no EMA prior).

## Tier 3 landed-so-far

- **T3.3 (light)** — pinned LOH alloc via `GC.AllocateUninitializedArray<T>(n, pinned: true)`
  on all `MandelbrotCalculator` + `EscapeTimeCalculator` output buffers and
  the `FractalRenderHost` upload-pool buffers. Eliminates per-frame
  `GCHandle.Alloc/Free` (LOH pin is built into the allocation) and removes
  the buffers from the GC mark-and-compact scan. Non-temporal `Avx.Store*`
  writes deferred — needs the SIMD write paths refactored to use raw
  pointers, which is a larger touch.
- **T3.4** — `<PublishReadyToRun>true</PublishReadyToRun>` +
  `<PublishReadyToRunComposite>true</PublishReadyToRunComposite>` set on the
  `Publish|x64` + `Publish|AnyCPU` configurations of `FracturingFogCLD.csproj`.
  No effect on `dotnet build` Debug/Release; takes effect on
  `dotnet publish -c Publish` which now emits native AOT-precompiled code
  alongside IL for cold-start + first-frame perf.

## Tier 3 deferred (need dedicated branches)

### T3.2 ref-orbit recycling — deferred

**Why deferred:** correct mathematical recycling requires per-pixel `dc`
plumbing into three SIMD row paths (`ComputeRowPT8`, `ComputeRowPT4`,
`ComputeRowPTScalar`) so the perturbation loop sees `dc' = (pixel - displayCenter) + (displayCenter - cachedCenter)`.
Each row path is ~150 lines of tightly-tuned AVX2/AVX-512 with its own SA
prelude + BLA skip; adding a `(ΔcR, ΔcI)` parameter and threading it through
the iteration loop without breaking the BLA/SA validity bounds needs a
dedicated session with side-by-side render verification.

**Concrete steps for the follow-up branch:**
1. Add `_refRecycleOffsetX/Y` fields. Populated when
   `ComputeReferenceOrbit*` detects centre drift below
   `recycleTolerance = pixelScale * 0.5`.
2. Pass `(ΔcR, ΔcI)` into `ComputeRowPT8` / `ComputeRowPT4` /
   `ComputeRowPTScalar` as scalar arguments (broadcast inside the row).
3. Per-pixel `dcR = (x - halfW) * scale + ΔcR; dcY = (y - halfH) * scale + ΔcI`.
4. Force `EnsureBlaTable` + `EnsureSeriesApproximation` rebuild when Δ
   non-zero (BLA coefficients depend on `dcMaxAbs` which grew by `|Δ|`).
5. Visual regression test: render a video zoom at zoom > 1e30, compare
   recycled vs full-rebuild frames. Expect identical output to within
   colour-LSB tolerance.

Expected gain on hit: skip the single-threaded reference-orbit loop
(~100k+ iters of DD/QD math per frame at zoom > 1e25). 30-70% video-frame
time reduction at deep zoom.

### T3.1 GPU compute (phase 1, kernel land)

`Rendering/MandelbrotGpuKernel.cs` — D3D11 compute shader (HLSL CS 5.0)
for the SP Mandelbrot escape-time inner loop. Owns its own `cs_5_0`
blob compiled via `Vortice.D3DCompiler`, a 48-byte cbuffer for
per-frame params (split centre + split scale for ~6 extra mantissa
bits past plain FP32), and two `StructuredBuffer<uint>` / `<float>` UAVs
for the iter + smooth outputs. `Run()` is synchronous: Dispatch →
`CopyResource` → `Map(Read)` → memcpy into the caller's pinned
`int[]` + `float[]`. 8×8 threadgroup, one thread per pixel,
whole-cardioid + period-2 bulb early-out matches the CPU SIMD path.

Phase 1 host integration — landed.
- `DirectXRenderer.TryGetD3D11(out device, out context)` exposes the
  device + immediate context the swap chain is bound to. Returns false
  on non-Windows / non-D3D11 backends (GL / Skia) — caller falls back
  to CPU.
- `MandelbrotCalculator.UseGpuCompute` + `GpuKernel` properties.
  `CalculateDoublePrecision` branches to `GpuKernel.Run(...)` when the
  toggle is on + kernel present + `PerRowMaxIter` null. The CPU palette
  stage runs as a `Parallel.ForEach` over rows with `FillAuxAndColorSP`
  per pixel (consuming the GPU's iter + smooth writeback). On kernel
  exception the call falls through to the CPU SIMD path with a
  `Debug.WriteLine` — user still gets a frame.
- `IFractalRenderHost.UseGpuCompute` lazy-constructs the kernel on
  first true assignment; null when the renderer isn't D3D11. Cleaned
  up in `Dispose` before tearing the renderer down.
- `MandelbrotGpuKernel` ctor takes the host's `_d3dGate` lock; `Run()`
  wraps its entire dispatch + readback in `lock (_d3dGate)` so the
  kernel never overlaps the swap-chain Render. ID3D11DeviceContext
  (immediate) is not thread-safe.
- UI: Ctrl+G toggles GPU compute. `MainViewModel.UseGpuCompute`
  delegates to the host and re-reads after assignment so the property
  reflects "didn't engage" when the renderer isn't D3D11. Perf HUD's
  precision label appends "(GPU)" when the kernel actually ran this
  frame.

Phase 1.b landed:
- **PerRowMaxIter on GPU.** `MandelbrotGpuKernel` gains a
  `StructuredBuffer<uint>` SRV at t0 and a `gUsePerRow` cbuffer flag.
  Shader loop bound becomes `rowMaxIt = gUsePerRow ? gPerRow[y] :
  gMaxIter`; row-capped unescaped pixels write `gMaxIter` to iter +
  `0` to smooth (same in-set rewrite as the CPU Phase 2.1 post-row
  pass). `Run()` takes `int[]? perRowMaxIter`; `MandelbrotCalculator`
  passes `PerRowMaxIter` directly — no more CPU fallback when both
  PerTile + GPU are active.
- **z+dz UAVs.** Kernel writes a packed `RWStructuredBuffer<float4>`
  at u2 holding final `zr, zi, dr, di` per pixel. Tracks derivative
  through the iteration loop with the standard `d_{n+1} = 2 z_n d_n + 1`
  recurrence (init `(1, 0)` matching the CPU SIMD convention). CPU
  writeback now drives distance-estimate via the standard
  `|z|·log|z|/|dz|` formula and the existing `FillNormal` helper, so
  Phong / distance / orbit-glow themes work under GPU compute.
- **Split GPU timing.** `MandelbrotGpuKernel.LastDispatchMs` covers
  cbuffer + per-row upload + Dispatch submission + implicit flush
  (the first Map blocks until the GPU finishes); `LastReadbackMs`
  isolates the three staging Map+memcpy passes. `PerfStats` gains
  `RecordGpuDispatch` / `RecordGpuReadback` + a snapshot row. HUD
  shows a "gpu  dis X  rb Y ms" line when `GpuSampleCount > 0`,
  hidden otherwise so the CPU-only case isn't cluttered.

Phase 1.b open items:
- Orbit-aware themes: `CalculateOrbitAware` keeps CPU dispatch
  unconditionally — the kernel doesn't sample z per iteration. Per-
  orbit reductions inside the shader (stripe sum, TIA accumulator)
  are a phase 2 candidate.
- GPU-resident colour. Phase 2 of the original plan: code-gen
  IColorMap → HLSL emit so palette runs on GPU and ColorBuffer
  stays GPU-side. Eliminates the per-frame readback.

### T3.1 phase 3 — alt-fractal kinds on GPU

`MandelbrotGpuKernel` gains a `FractalKind { Mandelbrot, Julia,
BurningShip, Tricorn }` enum and three new cbuffer fields
(`gFractalKind`, `gParam0`, `gParam1`). Shader switches on `gFractalKind`
inside `CSMain`:
- **Mandelbrot (0)** — unchanged. Cardioid + period-2 bulb early-out
  only fires for this kind (other shapes have different in-sets).
- **Julia (1)** — `z_0` initialised to the pixel coord; iteration `c`
  is the constant `(gParam0, gParam1)`.
- **BurningShip (2)** — pre-step transform `z := |Re(z)| + i|Im(z)|`.
- **Tricorn (3)** — pre-step `z := conj(z)`.

`EscapeTimeCalculator` gains `UseGpuCompute` + `GpuKernel` (shared
instance, set by the host alongside the Mandelbrot one) and a
`TryDispatchGpu` helper. Called at the top of `Calculate` — when the
fractal type is shader-supported, dispatches the kernel, runs the
same CPU writeback as the Mandelbrot path (smooth + distance estimate
+ normal from final z+dz), and skips the CPU switch entirely. Returns
false for Multibrot (pow, deferred) and Phoenix (two-step memory,
out of scope for the polynomial path).

The cbuffer grew from 64 to 64 bytes — the four new ints fit in the
existing pad slots.

### T3.1 — FP32 zoom-ceiling fix + future precision uplifts

`MandelbrotCalculator.MaxGpuZoom = 1e4` gates GPU dispatch off above the
band where the shader's split-centre + split-scale FP32 reconstruction
hits catastrophic cancellation. `cx = cxHi + fx*scaleHi + cxLo + fx*scaleLo`
needs an FP32 ULP smaller than the pixel-size; pixel-size at zoom 1e4
≈ 4.8e-7 already brushes the ULP of centres near 1, so 1e4 is the
conservative ceiling before users see pixelation + cardioid/bulb
mispredicates that paint the whole frame as in-set. CPU SP path
(double precision) handles cleanly through ~1e12 and HP (DD/QD) takes
over from there. HUD precision label drops the `(GPU)` suffix when the
gate engages so the user sees the path switch.

**Deferred precision-uplift options** (only land if profiling shows GPU
matters at deep zoom on real user HW):

1. **Full double-double in HLSL.** Re-emit the centre reconstruction
   + every iteration's z² + c step as DD arithmetic (Dekker TwoSum +
   Veltkamp split, two FP32 components per scalar). Lifts the ceiling
   toward the CPU's ~1e12 limit. Cost: roughly 5× shader work — every
   add becomes a 6-op TwoSum, every multiply becomes a 17-op DD mul.
   Likely loses GPU throughput against the calc-thread CPU SIMD path
   for the maxIter ranges modest HW runs, so the user-visible win is
   only on the small zoom band 1e4..1e8 where CPU SP still works but
   slower than DD-GPU. Implementation = full new HLSL file + DD helper
   inline; ~400 lines of careful shader code. Verify against the CPU
   path bit-for-bit before shipping.

2. **GPU perturbation.** Mirror the CPU HP path: one reference orbit
   computed CPU-side in DD/QD, uploaded as a `StructuredBuffer<float2>`
   of (refZr, refZi) per iteration; shader iterates only the FP32
   delta `δ_n = z_n − Z_n` against the reference. Lifts the ceiling
   past 1e15 to wherever the reference orbit precision holds. Cost is
   high: needs the BLA table + series-approximation prelude ported
   too (or the CPU computes them and ships skip tables to GPU); also
   needs glitch detection per-pixel to spot pixels whose δ_n diverges
   from the reference and re-iterate them with a fresh nearby
   reference. ~800 lines of new HLSL + CPU-side orbit-upload pipeline.
   Real engineering month, not a session-scoped task.

Neither option is scheduled. Phase 2 (HLSL palette emit) and phase 4
(GPU-resident ColorBuffer) are the next worthwhile GPU lifts — they
cut the per-frame readback cost regardless of zoom and benefit every
GPU-eligible frame.

### T3.1 phase 2 + 4 — HLSL palette emit + GPU-resident ColorBuffer (landed)

ColorGen now emits a HLSL twin of every theme's Map() body. Generated
themes implement `IGpuHlslPalette` (in `Interefaces/IGpuHlslPalette.cs`)
returning three strings:
- `HlslPaletteBody` — the body of a `float3 EvalPalette(...)` HLSL
  function with the 15 DSL inputs surfaced as `float` args
  (canonical order in `GpuPaletteInputOrder.FloatInputs`).
- `HlslPrelude` — helper definitions (`cg_mods`, `cg_hash`,
  `cg_fromHsv/Hsl`, plus per-arity `cg_paletteN`).
- `PaletteId` — short SHA-256 fingerprint for kernel shader cache.

`MandelbrotGpuKernel` keeps two shader variants: `_csBase` (no colour
write — for non-IGpuHlslPalette themes) and `_csByPaletteId[id]`
(emits the GPU colour write to a new `RWStructuredBuffer<uint> gColor
: register(u3)`). The kernel composes its shader source per-variant at
`SetPalette` time, splicing the prelude before EvalPalette and the
EvalPalette body inside a generated wrapper. Compile failures fall
back to `_csBase` so the CPU palette path still works.

`Run` gained an optional `colorDst: uint[]?` arg. When provided AND
`HasGpuPalette` is true, the kernel binds the colour UAV, dispatches
the colour-emitting CS, copies the colour staging buffer into the
caller's `ColorBuffer` via `Buffer.MemoryCopy`, and the calculator
skips the CPU palette pass entirely. When `colorDst == null` (legacy
themes or non-IGpuHlslPalette colour maps), behaviour is identical to
phase 1.b.

`MandelbrotCalculator` and `EscapeTimeCalculator` both detect
`IGpuHlslPalette` on the active `ColorMap`, call `SetPalette`, and
forward `ColorBuffer` as `colorDst` when GPU palette is live. Aux
buffers (distance / normal) stay zero on the GPU palette path — the
emitted DSL inputs `in_dist`, `in_nx`, `in_ny` get 0 inside the
shader (degrades gracefully for themes that read them; honest themes
that ship through the DSL pipeline either drive colour from `smooth`
+ `t` + `arg` + `mag` or accept the degradation).

Limitations:
- Only ColorGen-emitted themes get the GPU palette. Hand-written
  `IColorMap` impls (HsvPalette, FirePalette, RainbowColorMap, etc.)
  stay on the CPU palette path. Adding GPU palette to a hand-written
  theme is a manual translation job — implement `IGpuHlslPalette` on
  the type.
- Orbit-aware themes (`IOrbitAwareColorMap`) and interior-aware themes
  (`IInteriorAwareColorMap`) need per-step samples the kernel doesn't
  produce; they remain CPU-bound regardless.
- HP/DD paths still go through CPU (zoom gate `<= 1e4` on the GPU
  branch). HP zoom paints the CPU palette pass.

Expected gain: at 1080p with a ColorGen theme active, the per-frame
readback drops from 4 buffers (iter + smooth + finalZD) at ~3-5 MB
total to 1 buffer (color uint[]) at 8 MB but avoids the CPU palette
loop's ~5-15 ms per Mp pass. Net wins biggest on themes whose CPU
`Map()` does heavy work (palette interpolation, `cg_palette`,
multiple `Hsv` calls); marginal on trivial themes whose CPU eval is
already cache-resident.

### T3.1 GPU compute (phase 5) — remaining

**Why deferred:** new HLSL compute shader + new D3D11 dispatch path + new
GPU↔CPU sync model. Phase 1 alone (Mandelbrot SP kernel, palette on CPU)
needs roughly:
- ~200 lines of HLSL CS 5.0 (escape loop, smooth iter, derivative
  tracking for normal/distance estimate).
- New `D3D11ComputeRenderer` (or extend `DirectXRenderer`) with
  `CreateComputeShader`, two `StructuredBuffer<float>` outputs
  (smoothIter + distance), `Dispatch((W+15)/16, (H+15)/16, 1)` per frame.
- CPU readback for palette eval (StagingResource + Map/Unmap), or move
  palette to HLSL too.
- Integration into `FractalRenderHost.Trigger` selecting GPU vs CPU
  based on a quality / zoom gate (SP only — zoom < 1e15; DD/QD stays CPU).
- Validation pass: pixel-by-pixel compare GPU vs CPU output across
  a regression set of regions to catch float precision drift.

**Concrete steps for the follow-up branch (phase 1 only):**
1. New file `Rendering/MandelbrotComputeShader.hlsl` with the
   `[numthreads(16,16,1)]` Mandelbrot SP escape loop emitting
   `RWStructuredBuffer<float>` smoothIter + dist.
2. New `Rendering/D3D11ComputeBackend.cs` owning the CS, the
   `ID3D11ComputeShader`, and the dispatch + readback path. Reuses
   `DirectXRenderer._device` / `_context`.
3. Extend `IFractalRenderer` with `bool SupportsGpuCompute { get; }` so
   the host can gate dispatch per backend.
4. `FractalRenderHost.Trigger` decision: `ViewState.UseGpuCompute && zoom < 1e15 && fractalType == Mandelbrot && !IsHighPrecisionActive` → dispatch CS;
   else current CPU path.
5. Status string: surface `"GPU-CS"` precision label so deep-zoom paths
   stay diagnosable.
6. Benchmark vs current CPU SIMD path on 1080p / 4K / 8K at maxIter 256 /
   1024 / 4096. Document gain in `Benchmarks/`.

Phases 2-5 (HLSL palette codegen, GPU-resident colour buffer, kernel
extension to Julia/BurningShip/Tricorn/Multibrot, FP64 ILGPU/CUDA path)
each get their own follow-up branches once phase 1 is verified.

---

## Empirical findings — post-Tier 1-3 land (2026-06-09)

Mixed results from BAB's hands-on testing at 1280x763. Quality and
slideshow good across the board; specific regressions / pain points below.

### Finding A — Status-bar `Calculating...` lag *and* render-start lag

**Symptom:** Sporadic delay between user-initiated region pick / manual
zoom and (a) the `"Calculating..."` status string appearing, **and** (b)
the render itself starting. Both lag, not just the status indicator.

**Likely cause:** Both lags point to dispatch-path latency, not just a
display-thread issue. Candidates:
- `_d3dGate` semaphore held by prior frame's GPU upload → next `Trigger`
  waits before even setting status.
- `_calcCts.Cancel()` on the prior calc blocks until prior `Parallel.For`
  unrolls (cancellation is cooperative; row workers check token mid-loop).
  Heavy frames at deep zoom can sit 50-200 ms before cancel observed.
- Status update + render start both fire from the same
  `Dispatcher.UIThread.Post` continuation — UI thread queue backlog
  delays both together.

**Next steps (separate branch):**
1. Audit `FractalRenderHost.Trigger` / `Hosting/AvaloniaShellBootstrap`
   for the order of (a) status-string assignment, (b) calc dispatch,
   (c) `Dispatcher.UIThread.Post`.
2. Move status flip to a synchronous UI-thread set *before* `Trigger`.
3. Add a `_calcInFlight` int + `Interlocked.Increment/Decrement` so the
   status binding shows `Calculating...` while count > 0, hides at 0.

### Finding B — Shallow-zoom manual-wheel choppiness

**Symptom:** Multiple wheel clicks at the surface / shallow zoom feel
choppy. User suspects renderer thrash from queued wheel events triggering
back-to-back calcs.

**Likely cause:** Wheel handler calls `Trigger()` per click without
coalescing. Each click cancels the in-flight calc (CancellationToken from
`_calcCts`) and restarts. At shallow zoom the calc itself is fast (~5-20 ms),
so the wasted work isn't iters — it's `Task.Run` + ContinueWith + GPU
upload + present cycling 5-10x in a 200 ms window.

**Next steps (separate branch):**
1. Add a wheel-event debouncer: accumulate `Δzoom` over a sliding
   ~30-50 ms window, fire one `Trigger()` on debounce expiry.
2. Or: keep per-click trigger but skip the GPU upload + present on cancelled
   calcs (currently the cancelled calc still falls through to the upload
   path because the cancel signal arrives after the row loop bails but
   before the present logic gates on it).
3. T2.4 (dedicated calc thread + bounded queue cap 1, latest-only) would
   collapse the queued work naturally — currently moot per the earlier
   determination because the video loop is single-threaded, but the
   interactive path *does* benefit from queue coalescing under burst input.
   Reconsider T2.4 scope.

### Finding C — Video zoom choppiness varies by region (Feigenbaum point)

**Symptom:**
- Region "Blackhole Sun" → visually/empirically smoother video zoom; some
  choppy frames; overall good.
- Region "Feigenbaum Point" → very choppy video zoom; overall poor.

**User hypothesis:** Large inset count (minibrots + cardioids) drives the
slowdown — Feigenbaum-point neighbourhood has dense small-feature structure
at every zoom level, Blackhole-Sun-class regions are mostly inside-set
(black) with sparser features.

**Likely cause (matches hypothesis):**
1. Inside-set pixels short-circuit on cardioid-skip; minibrot interiors
   *don't* — period-doubling cascades around the Feigenbaum point produce
   dense families of minibrots whose interiors run the full maxIter loop.
2. Block periodicity detection only kicks in mid-loop; small minibrots
   below the block threshold pay full iter cost.
3. BLA/SA at deep zoom: small minibrots near the Feigenbaum point have
   tight BLA-validity radii, so the BLA-skip rate drops — more pixels fall
   back to per-iteration perturbation.

**Next steps (separate branch / Tier 3 scope):**
1. Profile a 5 s capture of the Feigenbaum-point video zoom with PerfView /
   dotnet-trace. Hot spots expected: `ComputeRowPT8` inner iteration loop +
   colour map evaluation.
2. T3.1 GPU compute is the natural fix — moves the per-pixel inner loop
   off CPU entirely. Bumps the budget so dense-feature regions stay above
   30 fps.
3. Interim: investigate adaptive maxIter — drop maxIter dynamically during
   video record when frame budget runs hot, fade back up when budget
   recovers. Visual cost is mild iter-banding at the edges of minibrots
   during fast pan; acceptable for live preview, not for final encode.
4. Investigate per-tile maxIter heuristic: track per-tile escape histogram
   from prior frame, cap maxIter at p99 of the previous frame's
   actual-iter histogram. Saves wasted iters on the inside-set lake.

### Findings A/B/C — landed 2026-06-09

- **A (status + render-start lag)** — `FractalRenderHost.Trigger` reordered.
  `StatusRequested` now fires at the very top of `Trigger`, before
  `InvalidateAdaptiveCdf` / cancel / `ApplyView` / alt-switch. The
  stale-frame re-upload moved off the UI thread into the same `Task.Run`
  that owns `Calculate` (runs before the calc on the threadpool slot),
  so the calling thread doesn't block on a 5-15 ms GPU upload before
  the calc kicks off. Order vs the calc-completion upload is still
  guaranteed by `_d3dGate`.
- **B (wheel / key-repeat coalesce)** — `MainViewModel.OnInputViewChanged`
  rate-limits `RenderHint.Full` hints. First Full in a burst fires
  immediately (instant feedback); subsequent Fulls within a 50 ms
  window arm a trailing `_fullCoalesceTimer` that fires one final
  `Trigger()` after the burst settles. Covers wheel-zoom AND keyboard
  W/S key-repeat at shallow zoom. Single-click feedback unaffected
  because the leading edge always fires.
- **C (Feigenbaum-class chop interim)** — `FractalRenderHost.Video.cs`
  added `_videoIterCap` adaptive multiplier (range
  `VideoIterCapMin`=0.40 to `VideoIterCapMax`=1.00). Each
  `RenderVideoFrame` measures elapsed and ratchets the cap by
  `VideoIterCapDown`=0.92 if over 1.5× a 33 ms budget, or by
  `VideoIterCapUp`=1.05 if under 0.9× budget. `ApplyVideoFrameState`
  applies the cap to the computed iter count (with a 64-iter floor)
  when iter is not user-locked. Ratchet is gentle so iter banding
  ramps smoothly across the frame sequence rather than snapping.
  Master switch `VideoAdaptiveIterEnabled` (public bool, default true);
  wire to UI later if banding becomes a complaint.

### Finding D — Adaptive HE applies in one shot at end of slideshow crossfade

**Symptom:** When Adaptive Histogram Equalisation strength > 0, the
adaptive-HE effect is applied to the colour theme *once it is fully
faded-in* in a single jump — a jarring "just on" visual snap. User
expects Adaptive HE to apply continuously during the crossfade, ramping
with the fade.

**Status:** **Documented + deferred.** Not a perf bug; it's a
visual-correctness bug in the slideshow crossfade pipeline. Captured here
to avoid losing track; will be addressed in its own branch.

**Likely cause (to verify):** Adaptive-HE strength is read from the
*incoming* slide's settings and applied unconditionally at the end of the
crossfade transition, instead of being lerped from the outgoing slide's
strength to the incoming slide's strength across the crossfade `t`.

**Next steps (separate branch):**
1. Locate the crossfade tick path in `UI.Avalonia/` slideshow code.
2. Find where Adaptive-HE strength is fetched per frame.
3. Lerp `heStrength = Lerp(prev.heStrength, next.heStrength, fadeT)` for
   `fadeT ∈ [0,1]` across the crossfade window.
4. Verify visually: Adaptive-HE strength should ramp continuously across
   the crossfade with no "snap-on" at the end.
