<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<!-- SPDX-FileCopyrightText: 2026 Bradley Brown -->

# GPU Deep-Zoom — Session Handoff

**Branch:** `feature/vulkan-compute` · **Tip at handoff:** `468859c` ·
**Next task:** SA/BLA-on-GPU spike ([#88](https://github.com/AloneButUnsober/FracturingFog/issues/88)) — jump to
[§6](#6-start-here-next-session--sabla-on-gpu-spike-88). **Update 2026-10-07 (GPU parity plan G4.5a):** SA is
wired into production on both backends — see the note at the top of §6; BLA remains.
**Update 2026-10-14:** this handoff's thread is complete. SA (G4.5a, PR #1211) and BLA (G4.5b, PR #1215)
both run on D3D11 and Vulkan. #82, #84, #85 and #87 are closed. What remains is the strong-fp64 speed
sign-off and the `MaxGpuPerturbZoom` decision, tracked in #1265. §4 below shows current issue states;
the rest of this page is history.

---

## 1. TL;DR — where things stand

- **V6 GPU deep-zoom perturbation ([#82](https://github.com/AloneButUnsober/FracturingFog/issues/82)) is
  functionally complete on BOTH backends** (Vulkan + D3D11), off by default, gated, tiled for TDR safety, with a
  perf auto-fallback. Correctness is **bit-exact vs the CPU deep path** and validated headless.
- **No fast-FP64 hardware is available in this environment for a perf sign-off.** Both test GPUs are too weak at
  FP64, so the perf auto-fallback correctly disables the GPU deep path and everything runs on the CPU. The feature
  is **correctness-proven, perf-unproven** — it only pays off on a strong-FP64 GPU (workstation / compute card).
- **Next work (SA/BLA on GPU, #88) can START on weak hardware** because correctness gates are speed-independent;
  only the eventual perf sign-off needs capable hardware.

---

## 2. Hardware reality (READ THIS before expecting GPU speedups)

Deep-zoom perturbation runs the δ loop in **FP64**. Consumer GPUs cripple FP64:

| GPU | Backend | FP64 rate | Deep-GPU result |
|-----|---------|-----------|-----------------|
| GeForce GT710 (Kepler) | D3D11 (Windows) | 1/24 | Slower than CPU. One un-tiled dispatch tripped the Windows **TDR** watchdog → `DXGI_ERROR_DEVICE_REMOVED` (now tiled + perf-fallback → CPU). |
| Intel UHD 630 | Vulkan (Linux) | weak/slow | Perf-fallback **disables at very shallow HP zoom and never re-engages** (session-disable is intentional). Deep GPU never beneficial here. |

**Conclusion:** on both available GPUs the CPU deep path (multi-threaded SIMD PT + SA/BLA) is faster than GPU FP64.
The GPU path's value is real **only on strong-FP64 hardware**. Don't chase GPU speedups on these two cards — validate
**correctness** on them, defer **perf** to capable hardware.

The Linux "engages vs DISABLED — slower" log line is the perf verdict for any given box. On the UHD 630 it says
DISABLED.

---

## 3. What shipped (V6 map)

### Behaviour / gating
- `MandelbrotCalculator.UseGpuPerturbation` (static, **default OFF**) — master toggle.
- Gate in `CalculateHighPrecision` (`Engine/Calculators/MandelbrotCalculator.cs`): `!recycled && UseGpuPerturbation
  && GpuKernel != null && GpuKernel.SupportsPerturbation && AllowPtRebasing && !UseDdRebaseReference &&
  !ForceScalarPtPath && !tileCap && Zoom <= MaxGpuPerturbZoom && _refOrbitLen >= 1` → `TryRunGpuPerturbation`.
- `MaxGpuPerturbZoom = ODZoomThreshold (1e50)`, conservative. **Deep-dc recheck proved single-double `dc` is
  bit-exact vs DD across 1e6→1e40** (`--vulkanpturbdc`), so this ceiling *can* lift toward the `scale`-denormal
  limit — held pending a strong-FP64 sign-off.
- **Enable seams:** Vulkan auto-enables on `--renderer vulkan` when `shaderFloat64` present
  (`Hosting/AvaloniaShellBootstrap.cs`). D3D is behind env **`FF_GPU_PERTURB=1`** (deep-only — attaches the kernel
  but leaves the shallow FP32 GPU path off).

### TDR row-band tiling (commit `911a77b`)
- Shared HLSL `MandelbrotKernelSource.BuildPerturb()`: cbuffer reordered **doubles-first** (64 bytes, clean
  8-byte alignment) + new `gRowBase`; pixel row = `gRowBase + tid.y`.
- `MandelbrotKernelSource.PerturbBandRows(w,h,maxIter)` — band height from a 40M iter-pixel budget.
- Both backends dispatch the frame in **row bands**, each its own GPU packet (Vulkan: submit+`QueueWaitIdle`;
  D3D: `Flush` per band), so no single packet exceeds the ~2 s TDR budget.

### Perf auto-fallback (commit `468859c`, #87)
- After band 0 (real GPU-synced time), extrapolate `band0 × bandCount`. If `> PerturbBudgetMs` (default 3000, env
  `FF_GPU_PERTURB_BUDGET_MS`) → throw `GPU-PERTURB-TOO-SLOW`.
- Calculator catch treats that (and any device-lost) as **disable `UseGpuPerturbation` for the session** + CPU deep
  path. This is why weak GPUs "fall off and never re-engage" — by design.

### Files
- `Rendering.D3D/MandelbrotKernelSource.cs` — shared HLSL + band/budget helpers (compile-linked into Vulkan).
- `Rendering.D3D/MandelbrotGpuKernel.cs` — D3D `RunPerturb` (tiling + band-0 perf sync via `CopyResource`+`Map`).
- `Rendering.Vulkan/VulkanComputeKernel.cs` — Vulkan `RunPerturb` (tiling + band-0 perf via `QueueWaitIdle`).
- `Engine/Calculators/MandelbrotCalculator.cs` — gate, `TryRunGpuPerturbation`, too-slow/device-lost handling,
  `LastFrameUsedGpuPerturbation` (HUD marker), `#86` file-diagnostics.
- `Engine/Interefaces/IGpuKernel.cs` — `SupportsPerturbation` + `RunPerturb` default-interface members.
- `Engine/Rendering/FractalRenderHost.cs` — `DD (GPU)` HUD marker; newest-wins present guard; `#86` trace.
- `Rendering.Vulkan.Smoke/Perturb*Probe.cs` — the headless gates (below).

### Gates (headless — run on GT710/lavapipe, speed-independent)
```
dotnet run --project Rendering.Vulkan.Smoke/FracturingFog.Rendering.Vulkan.Smoke.csproj -- --vulkanpturbcalc  # end-to-end calc parity: 0/16384 exact
dotnet run -c Release --project FracturingFogCLD.csproj -- --d3dpturbcalc  # D3D11 twin (Windows, #82): headless D3D11 device, 1e14 → 3e47, 0/16384 exact on a GT 710
dotnet run --project Rendering.Vulkan.Smoke/FracturingFog.Rendering.Vulkan.Smoke.csproj -- --vulkanpturbprobe  # kernel-vs-CPU at noise floor
dotnet run --project Rendering.Vulkan.Smoke/FracturingFog.Rendering.Vulkan.Smoke.csproj -- --vulkanpturbdc    # deep-dc precision sweep 1e6→1e50
dotnet run -c Release --project FracturingFogCLD.csproj -- --gpuperiodicityprobe  # #1342 shallow SP kernel (D3D11): periodicity on == off bit for bit, fires, timing
dotnet run -c Release --project FracturingFogCLD.csproj -- --vkpipelinecacheprobe  # #1341 Vulkan pipeline cache: cold / warm / off / corrupt / foreign sessions (child processes), vkCreateComputePipelines time, same frames
dotnet run -c Release --project FracturingFogCLD.csproj -- --gpugroupshapeprobe  # #1339 thread-group shapes (D3D11 + Vulkan): tuning timings + winner per slot, tuned vs 8x8 at full size, byte-identical
dotnet run -c Release --project FracturingFogCLD.csproj -- --gpubandprobe  # #1334 deep perturbation bands (D3D11 + Vulkan): banded == one band bit for bit, band sizes, wall + GPU time
dotnet run -c Release --project FracturingFogCLD.csproj -- --gpudeferprobe  # #1332 shallow SP kernel (D3D11): deferred readback == eager bit for bit, flush, GPU escape count, timing
```

### Env vars
| Var | Effect |
|-----|--------|
| `FF_GPU_PERTURB=1` | D3D/Windows: opt in to deep GPU perturbation (deep-only). |
| `FF_GPU_PERTURB_PRECISION` | #1331 — `auto` (default: the fp32 kernels only on a device without fp64), `fp32` (opt in anywhere: 2–8× faster on weak-fp64 cards, ~0.1–0.4% of pixels wrong near deep minibrots), `double` (never fp32). See Deep-Zoom-Perturbation.md §8. |
| `FF_GPU_PERTURB_BUDGET_MS` | Perf-fallback threshold, ms (default 3000). Raise it, or set `0` (or a negative value) to turn the abort off, to keep the GPU on for measuring. Before #1490, `0` and negatives fell back to 3000. Unparseable values give the default. |
| `FF_PERTURB_BANDROWS` | Force band height (test hook — stress multi-band; e.g. `8`). Rounded down to whole 8-row thread groups (#1334), like the budget-derived height. |
| `FF_GPU_GROUP_SHAPE` | #1339 — compute thread-group shape of the 2D kernels (D3D11 + Vulkan). `auto` (default): each slot (`sp`, `perturb`, `perturb-fp32`) is timed in 8×8 / 16×8 / 16×16 / 32×4 / 32×8 / 16×4 on its first frame, once per device + driver + kernel source, and the winner is stored in `ShaderCache/gpu-group-shapes.json`. 8×8 stays unless another shape is ≥ 3% faster. `off` = 8×8, `16x8` = that shape everywhere, `sp=16x8,perturb=8x8` = per slot. Output is identical in every shape. Band heights follow the group height. #1377 adds the slots `relief` (the Relief 3D raymarch; its tiles are whole groups) and `froxel-integrate` / `froxel-composite` (the two froxel passes), tuned the same way on a fixed relief + froxel frame. `relief` is opt-in: `auto` keeps it 8×8 (8×8 wins on the GT 710 and tuning compiles the relief shader per candidate); `relief=auto` tunes it. |
| `FF_VK_PIPELINE_CACHE=0` | #1341 — Vulkan: don't load or save the persisted `VkPipelineCache` (one file per kernel type, `ShaderCache/vk-pipelines-<compute|relief|froxel>-<vendor>-<device>-<uuid>.bin`). The SPIR-V cache (#456) is unaffected. `FF_NO_SHADER_CACHE=1` turns both off. A file is only handed to the driver if our SHA-256 envelope and its Vulkan header (vendor, device, pipelineCacheUUID) match. |
| `FF_VULKAN_SERIAL_BANDS=1` | #1334 — Vulkan: wait for the queue after every perturbation band, as before #1334. Default: each band gets its own UBO slice, only band 0 is waited on (the too-slow abort), and the frame ends in one wait; output identical either way. Tests use `VulkanComputeKernel.ForceSerialBands`. |
| `FF_GPU_WAVEFRONT=0` | #1340 — the SP escape-time kernel and the deep fp64 perturbation kernels (plain + SA / BLA, D3D11 + Vulkan) run one thread per pixel; #1384 adds the SP orbit colour kernel, the deep orbit kernel and the fp32 deep kernels (float-exp / scaled fp32), which follow the same switches. Default: SP runs the wavefront (persistent-thread) schedule — threads pull pixels from an atomic counter (`gWork`) and iterate 32 steps at a time; the deep kernels follow `FF_GPU_WAVEFRONT_PERTURB`. Output identical either way (one compiled shader, the cbuffer flag `gWave`). Tests and probes use `GpuWavefront.Mode` / `.Threads`. `FF_GPU_WAVEFRONT_THREADS` = threads of an SP dispatch (default 2^18). |
| `FF_GPU_WAVEFRONT_SEED=1` | #1386 — under `FF_GPU_WAVEFRONT_PERTURB=auto`, a deep frame with no warp-waste hint for a nearby view (the first frame, a jump past one frame width or 2× zoom, another iteration cap, a batch still) first runs its kernel at ¼ resolution per axis (same orbit, SA, BLA) and picks its schedule from `GpuWavefront.EstimateFullWaste`. Off by default: on the GT 710 it costs 5–12% of a first frame and pays back only on boundary-heavy views (Marina 1.65×). Without it, GPU deep drag previews still seed the full-resolution frame (free). Output is identical either way. |
| `FF_GPU_WAVEFRONT_PERTURB` | #1340 — deep perturbation schedule: `auto` (default; the calculator measures each GPU deep frame's warp waste, `GpuWavefront.WarpWaste`, and runs the next one in the wavefront schedule when it is ≥ 1.4), `1` = always, `0` = never. Each band pulls from its own counter, in 8×8-tile order, a quarter of its pixels' worth of threads. `--gpuwavefrontprobe deep` times the shipped deep presets both ways. |
| `FF_GPU_PERIODICITY=0` | #1342 — shallow SP kernels (D3D11 + Vulkan): compile the plain loop, without the periodicity early-out (default on; output identical either way, only the time differs). Tests and probes use `GpuPeriodicity.Mode`. |
| `FF_GPU_DEFER_READBACK=0` | #1332 — GPU-coloured SP frames (D3D11 + Vulkan) read every output back eagerly again. Default: the iteration / smooth / final-z outputs (and the CPU distance fill) stay on the GPU until something reads them (`Calculators/Gpu/GpuDeferredAux`); readers get the same bytes either way. Tests and probes use `GpuDeferredReadback.Enabled`. |
| `FF_VULKAN_MEMORY` | Vulkan buffer layout (#1228): `auto` (default: orbit + SA / BLA in VRAM, outputs host-cached), `host` (pre-#1228, every buffer host-visible), `device-outputs` (outputs in VRAM too, copied out). |
| `FF_GPU_PERTURB_DEBUG=1` | Write `#86` present/gate trace to `%TEMP%/ff_gpu_perturb_86.log` (WinExe has no console). |

---

## 4. Open issues

| # | Title | State |
|---|-------|-------|
| [#82](https://github.com/AloneButUnsober/FracturingFog/issues/82) | V6 GPU perturbation | **Closed.** The `MaxGpuPerturbZoom` lift decision moved to the modern-GPU sign-off, #1265. |
| [#88](https://github.com/AloneButUnsober/FracturingFog/issues/88) | **SA/BLA on GPU** | **Closed** (SA PR #1211, BLA PR #1215, both backends; the shader BLA lookup starts at the aligned level like the CPU since #1346, ~5–9% on the GT 710). The strong-fp64 speed sign-off is #1265. |
| [#85](https://github.com/AloneButUnsober/FracturingFog/issues/85) | Resize-during-calc buffer realloc race | **Closed.** |
| [#84](https://github.com/AloneButUnsober/FracturingFog/issues/84) | LightingFX HUD gone post-Vulkan | **Closed.** |
| [#44](https://github.com/AloneButUnsober/FracturingFog/issues/44) | V5 macOS via MoltenVK | Stretch — needs a **Mac**, not FP64. |

Closed this arc: #86 (stale deep frame → was TDR device-removed, fixed by tiling), #87 (perf-fallback, done).

### Pending cleanup
- `#86` diagnostics (`FF_GPU_PERTURB_DEBUG` file trace in `FractalRenderHost` + `MandelbrotCalculator`) are **still
  in** — opt-in and harmless, kept as they help debug the SA/BLA spike. Strip when the deep-GPU work is fully signed
  off.

---

## 5. Key facts / gotchas (don't re-derive)

- **The δ loop is PLAIN DOUBLE**, even at extreme zoom — the default CPU `ComputePixelPTRebased` reads only the
  Hi-limb reference orbit (`_refZr`/`_refZi`) + single-rounded `dc`. QD/OD precision is spent BUILDING the ref
  orbit + carrying the centre, **not** in the per-pixel loop. So no in-shader limb (DD/QD) math is needed for the
  base kernel. (Proven again by `--vulkanpturbdc`.)
- **Parity metric = disagreement FRACTION vs the CPU precision noise floor**, NOT `maxΔiter`. Filament pixels at
  the escape-time knife-edge flip by many iters under sub-ULP rounding (boundary chaos) — the CPU-vs-DD oracle
  disagrees by the same order. Gate on the fraction. (See `Docs/Deep-Zoom-Perturbation.md`.)
- **One HLSL source, two compilers:** `MandelbrotKernelSource` feeds FXC (`cs_5_0`, D3D) and DXC (`cs_6_0 -spirv`,
  Vulkan). No `[[vk::binding]]` — DXC shift flags map registers (`-fvk-b/t/u-shift`). Bindings: `b0`=params,
  `t0/t1`=refZr/refZi, `u0/u1/u2`=iter/smooth/finalZD.
- **cbuffer layout is doubles-first, 64 bytes** — both C# blobs (`PerturbParams` D3D, `PerturbParamsBlob` Vulkan)
  must match the HLSL byte-for-byte or you get silent garbage.
- **D3D kernel shares the renderer's device/immediate-context** (via the `GpuKernelFactoryHook` downcast). A long
  or crashing dispatch therefore takes down presentation too — that's why TDR froze the whole app. A dedicated
  deferred context / separate compute device would decouple them (noted on #87, not done).
- **`_d3dGate`** serialises kernel dispatch with `renderer.Render`/`UpdateTexture`. `RunPerturb` holds it for the
  whole tiled render — a slow deep GPU frame blocks present for its duration (another reason weak-FP64 GPU is bad).
- Non-degenerate probe centres must have an **amplifying** orbit (|Z|≈2 range). The parabolic root (-0.75,0) is
  degenerate (orbit small, amp≈1, all interior). Canonical deep centre: `-1.9918151296901943… / -5.524…e-6` (4+
  limbs) — used across the deep-zoom probes.

---

## 6. START HERE next session — SA/BLA-on-GPU spike (#88)

> **SA spike CORRECTNESS: GREEN (2026-07-22).** In-shader Series Approximation
> landed and validated on the GT710. Kernel `MandelbrotKernelSource.BuildPerturbSA()`
> (entry `CSPerturbSA`) + `VulkanComputeKernel.RunPerturbSA` (8 coefficient SSBOs
> t2..t9, 80-byte SA UBO) + gate `--vulkanpturbsa` (`PerturbSaProbe`). In-shader
> `FindSkip` uses **squared magnitudes** (HLSL has no double `sqrt`); coefficients
> come from the production `Engine/Math/SeriesApproximation`. Results at zoom 1e6,
> tol 1e-3, refLen 3090:
> - **(1) GPU-SA vs CPU-SA = 0.141 %** (13/9216) — at the #82 GPU-vs-CPU dialect
>   floor (0.119 %). The in-shader FindSkip/EvalDelta + SA-seeded rebased loop
>   reproduces the CPU SA path. **This is the correctness proof.**
> - SA engaged: avg skip k = 32, max 3090, 100 % of pixels skipped ≥16.
> - **SA effect** (SA vs no-SA) = 8.76 % at tol 1e-3 — expected boundary chaos, NOT
>   a bug: a tolerance sweep (`FF_SA_TOL`) collapses it to the 0.011 % precision
>   floor at tol 1e-6/1e-9 (tighter tol → smaller skip → less truncation), proving
>   the divergence is genuine SA truncation, correctly controlled. GPU adds **0 %**
>   beyond the CPU SA path.
>
> **What is DONE:** the SA-spike correctness (step 1–3 below). **What remains:**
> (a) wire into `TryRunGpuPerturbation` behind a sub-toggle (step 4) + the D3D FXC
> compile of `BuildPerturbSA` (Vulkan/DXC proven; FXC unverified); (b) **perf
> sign-off on strong-FP64 HW** — GT710 cannot; (c) **BLA** (heavier, DD coeffs +
> table, deferred until SA lands — now it has).
>
> Gate: `dotnet run --project Rendering.Vulkan.Smoke/... -- --vulkanpturbsa`
>
> **SA WIRED IN (2026-10-07, GPU parity plan G4.5a).** Step 4 and (a) are done:
> `IGpuKernel.SupportsPerturbationSA` / `RunPerturbSA` on both backends (the D3D FXC
> compile works; Vulkan's SA dispatch gained the first-band too-slow abort), and
> `MandelbrotCalculator.TryRunGpuPerturbation` takes the SA kernel under the CPU's
> conditions (SafeMax ≥ 16, `DisableAcceleration` / `DisableSeriesApproximation`
> off; `FF_GPU_SA=0` opts out). `--d3dpturbcalc` runs every case plain and with SA
> (GT 710: 0–0.024% vs CPU; SA cuts GPU time 17→7 / 44→25 / 156→103 / 206→178 ms
> at 128²). `S88GpuPerturbSaTests` pins it on Vulkan. Remaining: (b) perf sign-off
> on strong-FP64 HW, (c) BLA.
>
> **BLA WIRED IN (2026-10-07, G4.5b).** (c) is done: the CPU `BlaTable` is flattened
> (`BlaTable.GpuCoefficients`) into the SA kernel's t10 and applied per pixel at the
> top of each rebased iteration (lookup on the reference index m; δ' = Aδ + B·dc,
> dz' = A·dz). `gBlaLevels` replaced `gPad0`. GT 710 at 128²: GPU SA+BLA beats the
> CPU on every `--d3dpturbcalc` view (3e47: 34 ms vs CPU 99, plain GPU 212). Only
> (b), the strong-FP64 perf sign-off, remains.


**Goal:** add iteration-skipping (SA first, BLA later) to the GPU perturbation kernel so it stops repeating work the
CPU elides. **Spike-first**, exactly like #82: prove bit-exact parity headless before a full build.

### Why SA before BLA
- **SA** (Series Approximation): dc-independent Taylor series near iter 0, one skip to a start iteration `k` +
  starting δ. Simplest to port — a few coefficient SSBOs + a per-pixel `FindSkip`/`EvalDelta`. CPU ref:
  `SeriesApproximation` (`Engine/…`), used via `sa.FindSkip(dcR,dcI,…)` / `sa.EvalDelta(k,…)` in
  `MandelbrotCalculator` (see the `dcRod`/`EvalDelta` sites).
- **BLA** (Bivariate Linear Approximation): hierarchical 2^k-step merge table, DD-precision `A_n` past ~1e15,
  in-shader table lookup — heavier, more divergence, needs DD coeffs. Defer until SA lands.

### The hard part (write it down before coding)
- **Divergence:** SA/BLA give *variable per-pixel skip counts*. On SIMT, lanes skipping different amounts diverge
  and erode the speedup. Options for the spike: (a) accept partial benefit (per-pixel skip, measure), or
  (b) uniform per-tile skip. Start with (a) for correctness; measure divergence later on strong HW.
- Correctness first: the skip must produce the **same escape iteration** as the non-skipped loop within the noise
  floor.

### Concrete first steps
1. **New probe `--vulkanpturbsa`** in `Rendering.Vulkan.Smoke/` (copy `PerturbCalcProbe`/`PerturbSpikeProbe`
   structure). Build a reference orbit, run the SA-skipping kernel, compare to the plain perturbation kernel AND to
   the CPU SA path. Gate = disagreement fraction vs noise floor (reuse the `--vulkanpturbprobe` philosophy).
2. **SA variant of the shared kernel** — add an `SA` section to a copy of `BuildPerturb()` (or a
   `BuildPerturbSA()`): upload SA coefficients as SSBO(s), per pixel do `FindSkip → k, δ_k` then run the existing
   rebased loop from `k`. Keep it FXC + DXC clean (no `[[vk::binding]]`; extend the shift-mapped bindings).
3. **Validate on GT710/lavapipe** — bit-exact parity is speed-independent, so weak HW is fine here.
4. Only after SA parity is green: wire into `TryRunGpuPerturbation` behind a new sub-toggle, and defer perf to
   strong-FP64 HW.

### What weak hardware can / cannot do for #88
- **CAN:** everything correctness — write the kernel, the probe, prove bit-exact parity on GT710/lavapipe.
- **CANNOT:** decide if SA/BLA-on-GPU is actually *faster* (perf sign-off) — needs strong-FP64 hardware; the #87
  fallback will disable the path on GT710/UHD 630 regardless.

### Read before starting
- `Docs/Deep-Zoom-Perturbation.md` (perturbation + rebasing + SA/BLA math — **do not derive from memory**).
- `Docs/Technical/Vulkan-Compute-DevelopmentPlan.md` §13–§15 (spike + full-build history).
- CPU SA/BLA in `Engine/Calculators/MandelbrotCalculator.cs`: `EnsureSeriesApproximation`, `EnsureBlaTable`,
  `SeriesApproximation`, `BlaTable`, and the `sa.FindSkip`/`EvalDelta` + `_blaTable` call sites.
- `MandelbrotKernelSource.BuildPerturb()` — the kernel you're extending.

---

## 7. One-line status for the next session

> V6 deep-GPU perturbation done + correctness-proven on both backends; no fast-FP64 HW here for perf sign-off
> (GT710 + UHD 630 both fall back to CPU by design). **SA-on-GPU spike (#88) correctness now GREEN too**
> (`--vulkanpturbsa`, GPU-vs-CPU-SA 0.141 % on GT710). Next: wire SA into `TryRunGpuPerturbation` + D3D FXC
> compile; perf sign-off still needs strong-FP64 HW; then BLA.
