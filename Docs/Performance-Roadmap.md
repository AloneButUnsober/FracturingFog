# Performance Roadmap

Companion to `Lighting-FX-Roadmap.md` and `Fractal-Expansion-Roadmap.md`.
Tracks interactive-render performance work after the second deferred-wave
landed (GPU tonemap+bloom+edge, HDR DoF in 7 calculators).

## Status (2026-06-15)

P0–P6 shipped. P7 (DE-GPU port) remains the open 10-day refactor.

| Phase | Status | Notes |
|-------|--------|-------|
| P0 — Buffer pooling                     | ✅ shipped | `PostPassBufferPool` + `GpuBufferPool` |
| P1 — Raymarch micro-opts                | ✅ shipped | `1L<<k` + `ExpNegSmall` Padé |
| P2 — Low-res interactive preview        | ✅ shipped | `LowResPreview` helper + 7 calculators wired |
| P3 — `IDistanceEstimator` struct generic| ✅ shipped | Generic `Shade<TDe>` + 5 concrete DE structs (Mandelbulb, Mandelbox, QJulia, QMandel, Bicomplex, Kleinian). KIFS + UserBulb still route through `DelegateDeAdapter`; complex closures, lower-priority follow-up |
| P4 — Adaptive volumetric LOD            | ✅ shipped | `VolumeStepsFalloff` knob (default 0.5) |
| P5 — Bloom blur SIMD                    | ✅ shipped | Vector&lt;float&gt; horizontal + vertical interior pass; scalar edge tails |
| P6 — Bundle GPU dispatch single sync    | ✅ shipped | `ScreenSpacePost.BeginGpuFrame` / `EndGpuFrame` + shared device color buffer across SSAO/tonemap/edge |
| P7 — DE-GPU port                         | ⏸ deferred | Multi-day refactor; unlocks 12b-volumetric, 16b, 20b |

Current state — frame budget at 1920×1080 on a representative scene
(Mandelbulb, key+fill light, SSAO 16 samples, soft shadow 24 steps, AO
on, bloom on):

| Stage                                  | Approx CPU cost | Notes |
|----------------------------------------|-----------------|-------|
| Primary raymarch (`Calculate` + Shade) | 80–95 %         | DE-bound; `double` math; delegate-dispatched DE |
| `ApplyToneMapBloom` (CPU path)         | 3–8 %           | GPU path roughly 0.5–1 ms |
| `ApplyHdrDof` (CPU)                    | 0–4 %           | Only when DoF aperture > 0 |
| `ApplySsao` (CPU)                      | 1–3 %           | GPU path ~0.3 ms |
| `ApplyEdgeInk`                         | <1 %            | GPU path negligible |
| `ApplyLensPost`                        | <1 %            | One full-res snapshot clone per frame |

After GPU tonemap+bloom landed, the raymarch is the dominant cost on every
non-trivial 3D scene. Post-pass work is no longer the bottleneck; raymarch
+ DE delegate dispatch is.

---

## Phase ordering (ascending lift, descending impact)

1. **P0** — Buffer pooling (post-pass scratch + GPU device buffers)
2. **P1** — Cheap raymarch micro-opts (`Math.Pow(2,k)`, `Math.Exp` approx)
3. **P2** — Resolution-scaled interactive preview in 6 raymarchers
4. **P3** — `IDistanceEstimator` struct generic — devirtualize DE inner loops
5. **P4** — Adaptive volumetric LOD (step count by depth)
6. **P5** — Bloom blur SIMD
7. **P6** — Bundle GPU dispatch into single device sync
8. **P7** — **DE-GPU port** (the big one — unblocks 12b-volumetric, 16b, 20b)

---

## P0 — Buffer pooling

**Current state.** `BuildBloomPyramid` allocates 4× `float[3*n]` per frame
(emissive, two mip levels, output buffer). `ApplyHdrDof` allocates 3×
more for the skewed-blur passes plus one CoC buffer. `ApplyLensPost` and
byte `ApplyDof` clone the entire `ColorBuffer`. At 1920×1080 that's
~150 MB of fresh allocation every frame.

`GpuPostKernels.TryApplySsao` / `TryApplyToneMapBloom` / `TryApplyEdgeInk`
each call `_acc.Allocate1D` 3–7× per dispatch. CUDA's allocator is fast
but not free; OpenCL/Velocity allocators are ~10× slower.

**Why.** GC stutter at interactive framerates. Visible as periodic frame
hitches under heavy effect stacks. Also wastes ~3× memory bandwidth
reading/writing freshly-zeroed pages.

**Scope.**

1. Add `Engine/Rendering/Lighting/PostPassBufferPool.cs`:
   - `ThreadStatic` pool keyed by `(byteSize, kind)`.
   - `Rent(int n3)` returns `float[]`; `Return(float[])` reclaims.
   - Bound pool size to 4 buffers per size class so resolution changes
     don't permanently retain old buffers.
2. Replace `new float[3 * n]` sites in `ScreenSpacePost.cs` with
   `using var buf = PostPassBufferPool.RentScope(3 * n)` (struct that
   returns on dispose).
3. GPU side: add `GpuBufferPool` in `GpuPostKernels.cs` keyed by
   `(typeSize, elementCount)`. Rent on dispatch, return on
   `Synchronize`. Survives one frame inside the pool, recycled next.

**Touch points.** `Engine/Rendering/Lighting/ScreenSpacePost.cs`,
`Engine/Rendering/Lighting/GpuPostKernels.cs`. New file
`PostPassBufferPool.cs`.

**Risk.** Pool retains memory across resolution changes — bound the pool
size and clear on `ClearGBuffer` to avoid leak on resize.

**Expected win.** Eliminates ~150 MB/frame GC pressure; smooths
interactive frametimes by 5–15 % under heavy effect stacks.

---

## P1 — Raymarch micro-opts

**Current state.**

- `ShadingPipeline.cs:169` and `:390`:
  `double d = i.Epsilon * Math.Pow(2, k);` in DE-cone AO. Called
  `AoSamples` times per pixel. `Math.Pow(2, k)` is ~25 ns on x64;
  `1 << k` cast to double is ~1 ns.
- `ShadingPipeline.cs:236` and `:578`: `T *= Math.Exp(-density * stepSize)`
  in volumetric in-scatter. Called `VolumeSteps` times per pixel.
  `Math.Exp` is ~15 ns; a Padé approximation valid for small arguments
  is ~3 ns.

**Why.** Hot inner loops. At 1920×1080 with AO=8 + Volume=32, that's
8M `Math.Pow` calls + 32M `Math.Exp` calls per frame.

**Scope.**

1. Replace `Math.Pow(2, k)` with `(double)(1 << k)` in both AO sites.
2. Introduce `ExpNegSmall(x)` static helper using Padé (2,2) approx for
   `x ∈ [0, 0.5]`, fall back to `Math.Exp` outside. Call site:
   ```
   double a = density * stepSize;
   T *= a < 0.5 ? ExpNegSmall(a) : Math.Exp(-a);
   ```

**Touch points.** `Engine/Rendering/Lighting/ShadingPipeline.cs`.

**Risk.** Visual divergence at extreme densities. Padé(2,2) accuracy
~1e-4 in the valid range — well below visible.

**Expected win.** 2–5 % on AO-heavy scenes, 8–15 % on volumetric-heavy
scenes.

---

## P2 — Resolution-scaled interactive preview

**Current state.** `UserBulbCalculator` already implements a low-res
preview path (`lowRes` branch in `Calculate`, renders at half-res then
upscales). The other six 3D raymarchers (Mandelbulb, Mandelbox, KIFS,
QJulia, QMandel, Bicomplex, Kleinian) render at full resolution
unconditionally.

**Why.** Interactive UI (rotate / pan / zoom) needs sub-100 ms
frametimes for smooth navigation. At 1920×1080 a Mandelbulb full-res
render is 200–500 ms on a 16-core CPU. Half-res preview cuts that to
50–125 ms with no perceptible quality loss during motion.

**Scope.**

1. Extract `UserBulbCalculator`'s low-res render → upscale pattern into
   `Engine/Rendering/LowResPreview.cs` static helper. API:
   ```
   public static (uint[] buffer, float[] depth, float[] normal, float[] hdr)
       AllocatePreviewBuffers(int width, int height, double scale);
   public static void UpscaleToFullRes(
       uint[] preview, int pw, int ph,
       uint[] full, int fw, int fh);
   ```
2. Wire each of the six remaining 3D calculators to call the helper when
   `IsInteractive` is true. Final render goes through the existing
   full-res path.
3. Tunable: `LowResPreview.ScaleFactor` field on `FractalParameters`
   (default 0.5). Mirrors the existing UserBulb knob.

**Touch points.** All seven 3D calculators' `Calculate` methods. New
file `LowResPreview.cs`. UI binding: `FractalParamsView` numeric input.

**Risk.** Preview can mask aliasing or detail issues that only show
up at full res. Mitigated by always doing a deferred full-res pass when
the user stops interacting (existing `FractalRenderHost` debounce).

**Expected win.** 2–4× interactive framerate on the six raymarchers
that don't have preview today.

---

## P3 — `IDistanceEstimator` struct generic

**Current state.** `Engine/Rendering/Lighting/ShadingPipeline.cs:30`:
```csharp
public delegate double DistanceEstimator(double x, double y, double z);
```

Every call to `de(...)` inside `SoftShadow`, AO loop, reflection march,
and volumetric in-scatter is an indirect virtual dispatch (~3 ns
overhead vs ~1 ns for a direct call). On a heavy scene that's
`(Shadow=24 + AO=8 + Refl=24 + Vol=32×Shadow=24) = ~830 DE calls /
pixel`. At 1920×1080 = 1.7 billion indirect calls/frame.

**Why.** JIT cannot inline through a delegate. Each indirect dispatch
is a cache-miss-prone instruction-pointer chase. The DE itself is
typically 30–80 ns of math; the dispatch overhead is 3–5 % of that.
Devirtualized + inlined, the DE becomes part of the calling function's
hot loop body and benefits from cross-function CSE.

**Scope.**

1. Introduce `IDistanceEstimator` interface in
   `Abstractions/Rendering/Lighting/`:
   ```csharp
   public interface IDistanceEstimator
   {
       double Evaluate(double x, double y, double z);
   }
   ```
2. Refactor each calculator's DE closure into a `readonly struct`
   implementing the interface. Captured parameters become struct
   fields:
   ```csharp
   public readonly struct MandelbulbDe : IDistanceEstimator
   {
       public readonly double Power;
       public readonly int Iter;
       public MandelbulbDe(double power, int iter) { Power = power; Iter = iter; }
       public double Evaluate(double x, double y, double z) =>
           MandelbulbCalculator.MandelbulbDE(x, y, z, Power, Iter, out _);
   }
   ```
3. Re-sign `ShadingPipeline.Shade`, `SoftShadow`, etc. as generic on
   `TDe : struct, IDistanceEstimator`:
   ```csharp
   public static uint Shade<TDe>(in ShadingInputs i, …, in TDe de)
       where TDe : struct, IDistanceEstimator { … }
   ```
4. Call sites pass the struct directly:
   ```csharp
   var de = new MandelbulbDe(power, iter);
   ColorBuffer[idx] = ShadingPipeline.Shade(in inputs, baseColor, in fx,
       in de, idx, depthBuf, normalBuf, hdrBuf);
   ```
5. Keep the delegate-based `Shade` overload as a thin wrapper that
   boxes the delegate into an `IDistanceEstimator` adapter struct, so
   migration is incremental.

**Touch points.** `Engine/Rendering/Lighting/ShadingPipeline.cs`
(signature changes + helper generics), all seven 3D calculators
(define DE struct + switch call site), `Abstractions/Rendering/Lighting/`
(new interface).

**Risk.** Generic instantiation blowup — each calculator triggers a
fresh `Shade<TDe>` codegen. ~7 instantiations × ~3 KB IL each = small.
JIT compile time increases by ~50 ms on first frame per fractal —
amortized after warmup.

**Expected win.** 8–15 % raymarch speedup. Biggest single CPU-side
lever before DE-GPU port.

---

## P4 — Adaptive volumetric LOD

**Current state.** `ShadingPipeline.cs:206`:
```csharp
double stepSize = i.TotalT / vs;
```

Volumetric in-scatter walks `VolumeSteps` samples uniformly across the
ray length, regardless of distance to camera. Distant pixels with long
ray total-T get the same scattering quality as near pixels but require
the same expensive DE-shadow probe per step.

**Why.** Visual contribution of in-scatter on distant pixels is
already attenuated by `T` falling toward zero. Past a certain depth,
extra samples produce no visible difference but cost full DE+shadow.

**Scope.**

1. Adaptive step count:
   ```csharp
   int vs = fx.VolumeSteps;
   if (i.TotalT > 4.0)
       vs = Math.Max(4, (int)(vs / (1.0 + (i.TotalT - 4.0) * 0.5)));
   ```
2. Add `VolumeStepsFalloff` knob to `LightingFxData` (default 0.5,
   0 = no LOD = legacy bit-identical).

**Touch points.** `Engine/Rendering/Lighting/ShadingPipeline.cs`,
`Abstractions/Rendering/Lighting/LightingFxData.cs`,
`UI.Avalonia/ViewModels/FractalParamsViewModel.Lighting.cs`.

**Risk.** Visible banding if falloff is too aggressive on scenes with
distant dense fog. Default conservative; expose knob.

**Expected win.** 30–60 % on volumetric-heavy scenes with deep depth
range.

---

## P5 — Bloom blur SIMD

**Current state.** `ScreenSpacePost.DownsampleAndBlur` runs scalar
5-tap separable Gaussian inside `Parallel.For`. Each tap reads 3
floats per pixel. AVX2 `Vector<float>` carries 8 floats / op on
modern x64.

**Why.** Blur is purely sequential per-pixel math — perfect SIMD fit.
Currently ~2–3 ms per bloom build at 1920×1080. SIMD'd: ~0.5–0.8 ms.

**Scope.**

1. Rewrite horizontal blur loop using `Vector<float>` over the
   interleaved BGR layout. Care needed: 3-float stride doesn't divide
   evenly into 8-float lanes; pack `float[w*h*4]` (RGBA) for SIMD
   path, unpack on output.
2. Vertical pass same pattern.
3. Keep scalar fallback for `Vector.IsHardwareAccelerated == false`.

**Touch points.** `Engine/Rendering/Lighting/ScreenSpacePost.cs`
(`DownsampleAndBlur`).

**Risk.** Layout change (3-float → 4-float interleaved internally)
adds a copy in / copy out. Net win only when blur cost dominates the
copy; verified on AVX2 desktops.

**Expected win.** ~4× faster bloom build on CPU path. Mostly relevant
when GPU bloom is unavailable.

---

## P6 — Bundle GPU dispatch into single device sync

**Current state.** `GpuPostKernels.TryApplySsao`,
`TryApplyToneMapBloom`, and `TryApplyEdgeInk` each call
`_acc.Synchronize()` internally and copy results back to host before
returning. With all three enabled the host stalls 3× per frame waiting
for device.

**Why.** Each `Synchronize` is a full host-device round trip
(~100–500 µs depending on driver). Bundling all three into a single
device queue with one final sync removes two round trips.

**Scope.**

1. Add `GpuPostKernels.BeginFrame()` / `EndFrame()` API. Inside, all
   `TryApply*` calls queue kernels and copy-out commands but skip
   `Synchronize`. `EndFrame` does the single sync + copy-out.
2. `ScreenSpacePost` dispatches all GPU-eligible passes inside the
   begin/end pair. CPU-only passes (lens, HUD) wait until after
   `EndFrame`.
3. Keep current per-call API for callers that don't want to batch.

**Touch points.** `Engine/Rendering/Lighting/GpuPostKernels.cs`,
`Engine/Rendering/Lighting/ScreenSpacePost.cs`, each calculator's
post-pass sequence (one optional wrap).

**Risk.** Error handling — a failure in one pass currently falls back
to CPU for that pass only. Batched mode needs to either fall back to
CPU for the whole batch on any kernel failure, or track per-pass
failures and recover.

**Expected win.** 0.5–1.5 ms saved per frame when all GPU passes
active. Bigger on slow accelerators.

---

## P7 — DE-GPU port (the big one)

**Current state.** Each fractal's distance estimator runs as a C# method
called per-pixel from the calculator's `Calculate` loop on CPU.
`MandelbulbDE`, `MandelboxDE`, `MengerSpongeDE`, `QuatJuliaDE`,
`QuatMandelDE`, `BicomplexDE`, `KleinianDE`, `UserBulbDE` — eight DEs
totalling ~600 lines of math, all `double` precision.

`UserBulbGpuCalculator` already runs the UserBulb DE on ILGPU as a
proof-of-concept. The pattern there is the template for the rest.

**Why.** Three open phases blocked on this:

- **12b-volumetric** — GPU port of in-scatter requires DE callable from
  inside a kernel. Currently impossible (managed delegate).
- **16b** — recursive reflection bounces with GGX importance sampling.
  Cost scales linearly with `MaxBounces` × DE evals; on GPU it's
  affordable, on CPU it isn't.
- **20b** — true per-eye stereo doubles the raymarch cost. On GPU
  that's a non-issue; on CPU it halves framerate.

Plus the raymarch hot loop itself is the 80–95 % cost bucket and the
only remaining big CPU lever.

**Scope (the multi-day refactor).**

1. **Define GPU DE convention.** Each DE rewritten as a `static` method
   marked `[ILGPUKernel]`-compatible (no `Math.Pow` with non-const
   exponent, no `out` params, `float` precision only on GPU and `double`
   on CPU). Match the `UserBulbGpuCalculator` pattern.
2. **Per-fractal GPU kernel.** Rewrite each calculator's `Calculate`
   inner loop as an ILGPU kernel. Mirror existing CPU pattern: ray
   construction → sphere-trace loop → on-hit compute normal via
   gradient-tetra → write color/depth/normal/HDR.
3. **Shading on GPU.** Lift `ShadingPipeline.Shade` to a static GPU
   helper that takes the DE as a `struct` generic argument (mirrors
   the P3 CPU plan). Single source compiles to CPU + GPU.
4. **G-buffer co-resident on device.** Once Shade runs on GPU, the
   depth + normal + HDR buffers stay device-resident and feed directly
   into the 12b GPU post-pass without a host copy.
5. **CPU fallback path retained.** Every calculator keeps its CPU
   `Calculate` for headless servers without an accelerator and for
   bit-identity regression testing.
6. **Calculator dispatch.** `FractalRenderHost` selects GPU calculator
   when `UseGpuRender` flag is on and accelerator is available;
   otherwise CPU. Mirrors the existing `UseGpuPost` pattern.

**Touch points.** Roughly:

- New: `Engine/Calculators/Gpu/*GpuCalculator.cs` — seven new files,
  one per 3D fractal (UserBulb already exists).
- Modified: `Engine/Rendering/Lighting/ShadingPipeline.cs` — generic
  TDe parameter, GPU-friendly math (no `Math.Pow` with var exponent).
- Modified: `Engine/Rendering/Lighting/GpuPostKernels.cs` — accept
  device-resident G-buffers as input instead of copying from host.
- Modified: `Engine/Rendering/FractalRenderHost.cs` — GPU calculator
  dispatch routing.
- New: `Abstractions/Rendering/Lighting/IDistanceEstimator.cs` —
  shared with P3.

**Risk.**

- **Precision divergence.** GPU runs `float`; CPU runs `double`. The
  CPU and GPU paths will not be bit-identical even with the same DE.
  Document expected magnitude; visual regression tests need a
  tolerance band.
- **Driver fragmentation.** ILGPU's CUDA + OpenCL backends handle the
  same kernel differently. Validate on at least one of each before
  shipping default-on.
- **Maintenance burden.** Two implementations of every DE going
  forward. Mitigated by keeping the math in a single `static` helper
  that compiles for both — the GPU calculator just calls the same
  helper from inside a kernel.
- **Recursive calls.** ILGPU kernels can't recurse. Any DE that uses
  recursion (Kleinian's inversion cascade is iterative — fine; check
  KIFS, Mandelbox — fine) is portable. Document any DE that isn't.

**Expected win.** 10–30× raymarch speedup on a mid-range GPU vs CPU.
Unlocks 12b-volumetric, 16b, 20b. Eliminates the hardware-bound
ceiling.

**Time estimate.** One DE per day for the first three (Mandelbulb,
Mandelbox, KIFS — established patterns), faster after that. Plus 1–2
days for shading + dispatch refactor. Total: ~10 working days for the
full set + plumbing.

---

## Cross-cutting work

- **Visual regression harness.** P3 / P5 / P7 all change pixel output.
  A scripted `--batch --headless` render → SHA256 on each of the 7
  raymarchers at default knob values, with a tolerance band for the
  GPU paths, would catch regressions early.
- **Frame-time HUD.** Phase 19's debug HUD already shows light directions
  and param bars. Extend with a per-stage frame-time microbar
  (raymarch / SSAO / tonemap / bloom / DoF / lens / edge) so the
  user can see at a glance where time is going. Cheap;
  `Stopwatch.GetTimestamp()` deltas around each `Apply*` call.
- **Pool telemetry.** P0 + P6 add pools. Expose pool hit/miss counts
  via a debug flag so undersized pools don't silently degrade perf.
- **Default-zero gating remains the rule.** Every new perf knob
  (`VolumeStepsFalloff`, `LowResPreview.ScaleFactor`, `UseGpuRender`)
  defaults to the legacy value so old scenes render identically.

---

## Summary table

| Phase | Lift     | Impact (interactive) | Unlocks |
|-------|----------|----------------------|---------|
| P0    | ½ day    | 5–15 % smoother      | —       |
| P1    | 1 hour   | 2–15 %               | —       |
| P2    | 2 days   | 2–4×                 | —       |
| P3    | 1 day    | 8–15 %               | P7      |
| P4    | 2 hours  | 30–60 % (vol scenes) | —       |
| P5    | 1 day    | 4× bloom CPU         | —       |
| P6    | ½ day    | 0.5–1.5 ms / frame   | —       |
| P7    | ~10 days | 10–30× raymarch      | 12b-vol, 16b, 20b |

After P0–P6 ships, you are CPU-raymarch-bound until P7 lands. After P7,
you are GPU-shader-bound — orders of magnitude headroom for higher
sample counts, recursive bounces, true stereo, and the remaining
deferred Lighting/FX work.
