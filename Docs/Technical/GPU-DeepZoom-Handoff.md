<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<!-- SPDX-FileCopyrightText: 2026 Bradley Brown -->

# GPU Deep-Zoom — Session Handoff

**Branch:** `feature/vulkan-compute` · **Tip at handoff:** `468859c` ·
**Next task:** SA/BLA-on-GPU spike ([#88](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/88)) — jump to
[§6](#6-start-here-next-session--sabla-on-gpu-spike-88).

---

## 1. TL;DR — where things stand

- **V6 GPU deep-zoom perturbation ([#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82)) is
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
dotnet run --project Rendering.Vulkan.Smoke/FracturingFog.Rendering.Vulkan.Smoke.csproj -- --vulkanpturbprobe  # kernel-vs-CPU at noise floor
dotnet run --project Rendering.Vulkan.Smoke/FracturingFog.Rendering.Vulkan.Smoke.csproj -- --vulkanpturbdc    # deep-dc precision sweep 1e6→1e50
```

### Env vars
| Var | Effect |
|-----|--------|
| `FF_GPU_PERTURB=1` | D3D/Windows: opt in to deep GPU perturbation (deep-only). |
| `FF_GPU_PERTURB_BUDGET_MS` | Perf-fallback threshold, ms (default 3000). Raise to force GPU to stay on for measuring. |
| `FF_PERTURB_BANDROWS` | Force band height (test hook — stress multi-band; e.g. `8`). |
| `FF_GPU_PERTURB_DEBUG=1` | Write `#86` present/gate trace to `%TEMP%/ff_gpu_perturb_86.log` (WinExe has no console). |

---

## 4. Open issues

| # | Title | State |
|---|-------|-------|
| [#82](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/82) | V6 GPU perturbation | Core done both backends. Remaining: `MaxGpuPerturbZoom` lift decision + close-out — **both need a strong-FP64 perf sign-off**. |
| [#88](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/88) | **SA/BLA on GPU** | **Deferred — the next task (this handoff).** |
| [#85](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/85) | Resize-during-calc buffer realloc race | Mitigated (cancel before realloc) + not reproducing; proper **full calc-thread drain** still open, low priority. |
| [#84](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/84) | LightingFX HUD gone post-Vulkan | Open UI regression, unrelated to compute — quick win, weak HW fine. |
| [#44](https://github.com/AloneButUnsober/MandelbrotExplorer/issues/44) | V5 macOS via MoltenVK | Stretch — needs a **Mac**, not FP64. |

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
