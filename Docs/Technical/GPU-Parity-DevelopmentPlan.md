# GPU Parity — Development Plan

> Companion pages: [Technical Index](_Index.md) · [Benchmark Subsystem](Benchmark-Subsystem.md) · [Performance Development Plan](Performance-DevelopmentPlan.md) · [Vulkan Compute Plan](Vulkan-Compute-DevelopmentPlan.md) · [3D Rendering Roadmap #389](https://github.com/AloneButUnsober/FracturingFog/issues/389)

> [!IMPORTANT]
> **Snapshot 2026-10-06.** This plan orders all known GPU work: the open GPU issues plus the
> previously untracked gaps now listed in the parent tracker
> [#1173](https://github.com/AloneButUnsober/FracturingFog/issues/1173). **The issues are the
> canonical task list.** This page explains the order and dependencies. When a slice is picked up,
> file (or reuse) its issue and link it from its row here and on #1173.

**Goal:** every render that *can* run on the GPU does, produces the same image as the CPU within a
documented tolerance, and never falls back silently.

**Non-goals:** a new graphics API, a shader node graph, or changing the default render path (GPU
stays opt-in via **Use GPU render** / the kernel factories). CPU remains the authoritative oracle.

---

## 1. Where things stand

The audit traced every GPU path to the point where it hands work to the CPU (code references are on
each #1173 row).

| Area | On the GPU today | Still CPU-only |
|---|---|---|
| **3D families** (Mandelbulb, Mandelbox, KIFS, QuatJulia, QuatMandel, Kleinian, Bicomplex) | Raymarch + in-kernel lighting: 3 lights (point/spot/area), shadows, AO, fog/volumetrics, reflections, PBR terms, SSS, triplanar, IBL, sky, caustics; froxel 3D via the #1070 hybrid | Screen-space post stack: SSAO, tonemap+bloom, HDR DoF, edge ink (#1172); AOV views + depth output (#323); HDRI env (#1173-B); some folds/axes/colourings per family (#1173-C…F); Coquaternion entirely (#1173-G) |
| **UserBulb** | Analytic vec + sandbox quaternion DE; ambient+diffuse shade | Shadows/AO/reflections (#1173-A); Vec3 numerical DE, Julia (#1112); scalar KIFS |
| **Relief 3D** | Near parity: DoF, positional/area lights, glass, froxel temporal/reprojection, HDRI | Lighting-component / HDR-beauty captures and AOV views (#389 S1/S2, #323) |
| **2D Mandelbrot** | SP iteration ≤ zoom 1e4 (D3D11/Vulkan); deep-zoom perturbation (#82, both backends) | SA/BLA skipping (#88); orbit accumulation at depth (#607) |
| **2D other families** | Julia/Burning Ship/Tricorn iteration | Their palette (#1173-H); Multibrot, Phoenix, domain warp (#1173-I); orbit trap buffer (#1173-J); billiard colour programs (#1173-K) |

**What changed on 2026-10-06.** Before #1164 / PR #1171, **no ILGPU 3D kernel loaded on CUDA**: every
NVIDIA frame silently ran on the CPU. With `ILGPU.Algorithms` enabled, 7 of 8 families render on CUDA.
That exposed real device faults (#1169 Mandelbulb; #1170 watchdog on long launches), now contained by
a session latch (`GpuAcceleratorHost.ReportRenderFault`). **The GPU path is newly live on NVIDIA**,
which is why Phase 0 is stability and visibility.

### Hardware available

Only a **GeForce GT 710** (Kepler SM_35, fp64 at 1/24 rate; CUDA, Vulkan, D3D11) and an **Intel UHD**
iGPU (no OpenCL fp64, so unusable for the fp64 kernels). That's enough to prove correctness on all three
backends. It is **not** enough for performance conclusions, and it may produce faults a modern card
wouldn't (#1169, #1170 may be Kepler-specific). Slices marked **⚑ needs modern GPU** should get a final
check on a Turing-or-newer NVIDIA card, ideally plus an AMD card for OpenCL/Vulkan, before closing. On
the GT 710, the CPU (i5-13420H) beat the GPU on every Mandelbrot case measured with both. 3D families
were measured on the GPU only (Sierpinski 518 ms, Menger 668 ms, Mandelbox 2.82 s at 640x360). So
"faster" claims must come from better hardware.

---

## 2. Ground rules for every slice

1. **No silent fallback.** If a slice adds GPU coverage, add the case to the GPU benches
   (`GpuCalculatorBench` / `MandelbrotGpuKernelBench`). They refuse to time a CPU fallback. Surface the
   fallback reason to the user (#1173-M).
2. **Twin and tolerance.** The CPU path is the oracle. For CI (no GPU), add a JIT + render test on the
   ILGPU CPU accelerator (S749 / S742 style). For hardware, add an on-device parity gate. Keep or
   tighten the `S742Gpu3DDriftBoundTests` ceiling.
3. **Byte-identical default.** With the new option off, output must not change.
4. **Batch parity** (CLAUDE.md): any render-affecting change must stay reproducible via `--batch` and
   the Command builder.
5. **Contexts:** every ILGPU context that runs kernels comes from `GpuAcceleratorHost.CreateContext()`
   (Algorithms enabled, #1164).
6. **CUDA faults are sticky per process.** New kernels report through `ReportRenderFault`. Tests that
   need a CUDA device skip, not fail, when an earlier test poisoned it.

---

## 3. Phases and slices

Sizes are rough estimates (S ≈ a day, M ≈ a few days, L ≈ a week or more), not commitments.

### Phase 0 — Stabilise and make the live GPU path visible

| Slice | Issue | Goal | Depends | Size | Done when |
|---|---|---|---|---|---|
| G0.1 | #1164 (PR #1171) | 3D kernels JIT on CUDA; fault latch | — | S | **Done** (PR #1171 merged) |
| G0.2 | #1170 | Tile the 3D dispatch into watchdog-sized launches (`GpuTiledDispatch`: comb launches sized from a probe, per-launch sync, cancellable between launches), as #1044 did for relief | G0.1 | M | **Done** (PR #1175 merged) |
| G0.3 | #1173-M | Show the GPU-fallback reason for every family (HUD/status). Fold in #1112's "explain the fallback" bullet. Built as `GpuRoute` (`Abstractions/Render/GpuRoute.cs`): each GPU-capable calculator reports it (`IGpuRouteSource`), and it is shown in the status bar tag, the perf HUD `gpu` line and on `--batch` stderr | G0.1 | S–M | **Done** (PR #1176 merged) |
| G0.4 | #1169 ⚑ | Find and fix the Mandelbulb CUDA launch fault. **Root cause: the watchdog, not codegen.** Software fp64 trig/pow makes Mandelbulb ~5 ms/px on a GT 710, so even a 96x72 single launch outlasts TDR. Fix: a ~64-px probe launch with every frame above that tiled, plus a `TooSlow` guard (3 consecutive launches >1 ms/px → that family renders on the CPU on this device for the session, with the reason shown) | G0.1 | M | Mandelbulb renders on CUDA, or is proven too slow there and gated off cleanly. Done on the GT 710; ⚑ a modern card should render it on the GPU |
| G0.5 | #1045 | D3D11 presenter survives a device loss (recreate device + re-attach kernels) | — | M | A forced TDR doesn't crash the app |
| G0.6 | #82 | Close out V6: live D3D parity smoke. The bench already ran D3D11 perturbation on the GT 710; add the image-parity check. Delivered as the headless gate `--d3dpturbcalc` (D3DPerturbProbe.cs): 1e14 → 3e47, 0/16384 exact on the GT 710 | — | S | **Done** |
| G0.7 | #310 | On-device relief volumetric CPU-vs-GPU parity test. Delivered: `S310ReliefVolumetricParityTests` (GPU twin plugged in as the kernel vs the CPU trace, on the in-scatter and shaft terms) found and fixed a GPU-only bug (the #455 edge dissolve blended into an UNFOGGED floor); `--reliefgpuraymarch [hw]` gains a god-ray scene and a real-GPU mode | — | S–M | **Done** |

### Phase 1 — Kernel output contract (the foundation)

| Slice | Issue | Goal | Depends | Size | Done when |
|---|---|---|---|---|---|
| G1.1 | #323 | The 8 ILGPU 3D kernels encode every `DebugAov` view in-kernel (`GpuKernelUtils.EncodeSurfaceAov`, twin of `ShadingPipeline.EncodeAov`) and publish their depth buffer for stereo / autostereogram output (`ScreenSpacePost.GpuWantsDepth`). This lifts the AOV-view and depth-output force-CPU gates (#1009 path); only depth + thin-lens stays CPU. The normal G-buffer emit moves to G2.1, where its consumer (SSAO / edge ink) lands | G0.2 | L | `DebugAov` views and depth output render on the GPU; default output byte-identical |
| G1.2 | #1172 (prereq) | Optional float HDR (pre-clamp) beauty emit, same contract as G1.1. Delivered with normal + depth G-buffers: the kernels write the CPU Shade contract (unit normal / depth / pre-clamp HDR; 0 / +Inf / NaN on a miss) | G1.1 | M | **Done** (#1172 PR) |

G1.1 depends on G0.2 because tiling changes the dispatch shape: build the emit contract band-aware
once rather than twice.

### Phase 2 — 3D shading parity

| Slice | Issue | Goal | Depends | Size | Done when |
|---|---|---|---|---|---|
| G2.1 | #1172 | Run SSAO, tonemap+bloom, HDR DoF and edge ink on GPU frames via the existing `GpuPostKernels` (CPU passes on downloaded buffers as fallback). Includes the normal G-buffer emit from the kernels (SSAO / edge ink input). Delivered: `ScreenSpacePost.ApplyPost3D` runs the CPU tail's exact stack on the kernel G-buffers, through `GpuPostKernels` when `UseGpuPost` is on. Left (#1181): device-resident G-buffers (perf) and thin-lens HDR averaging (thin-lens + tonemap stays CPU until then) | G1.1, G1.2 | M–L | **Done** except the device-resident perf follow-up |
| G2.2 | #1172 | Align the GPU albedo with `ShadingPipeline.Shade`; tighten the S742 ceiling; add post-FX-on drift cases. Delivered: `GpuAlbedoLut` bakes the colour map into a LUT over the CPU's smooth axis (9x9 normal grid for normal-dependent themes) and `GpuKernelUtils.SurfaceAlbedo` samples it with the same smooth arithmetic. GPU vs CPU mean drift fell from 2.7-79 to 0.03-1.3 across the 7 families; S742 ceiling 40 → 3 | G2.1 (or parallel) | M | **Done** |
| G2.3 | #1173-B | HDRI environment sampling in the 3D kernels (port relief's equirect path) | G0.1 | M | HDRI scenes stay on the GPU |
| G2.4 | #1173-A | UserBulb GPU shading: shadows, AO, specular, reusing `GpuKernelUtils` | G0.1 | M | UserBulb GPU frames match the family kernels' lighting |
| G2.5 | #1112 | UserBulb Vec3 numerical DE + Julia kernels, exact quaternion routing | G2.4 (shared shade code; not strict) | L | Those modes stay on the GPU |

### Phase 3 — 3D family coverage (independent; ordered by cost/benefit)

| Slice | Issue | Goal | Depends | Size |
|---|---|---|---|---|
| G3.1 | #1173-C | Bicomplex: slice-axis parameter in the kernel | G0.1 | S |
| G3.2 | #1173-D | KIFS: Octahedron/Dodecahedron/MandelboxRot folds (one kernel branching on fold kind) | G0.1 | M |
| G3.3 | #1173-F | QuatMandel dual-orbit colouring in the kernel | G0.1 | M |
| G3.4 | #880 + #1173-E ⚑ | Kleinian: variable generator lists, rotation generators, word-length colouring, analytic DE | G0.1 | L |
| G3.5 | #1173-G | Coquaternion GPU kernel | G0.1 | M |

#880 was deferred for lack of hardware. The GT 710 plus the ILGPU CPU-accelerator twin is probably
enough for correctness; the ⚑ is for performance sign-off only.

### Phase 4 — 2D GPU

| Slice | Issue | Goal | Depends | Size |
|---|---|---|---|---|
| G4.1 | #1173-L | Investigate why Vulkan SP is ~20x slower than D3D11 SP (889 vs 44 ms, 1080p, GT 710): readback, fp64 in the SP shader, dispatch shape | — | S–M |
| G4.2 | #1173-H | GPU palette for Julia / Burning Ship / Tricorn (the deferred "phase 5") | — | M |
| G4.3 | #1173-J | GPU orbit path fills `TrapBuffer` → orbit-trap relief on the GPU | — | M |
| G4.4 | #1173-I | Multibrot (`pow`), Phoenix (previous-z carry), domain warp (per-pixel c) | — | M each |
| G4.5 | #88 ⚑ | SA, then BLA, on the GPU perturbation path (spike-first) | G0.6 | L |
| G4.6 | #607 ⚑ | GPU deep-zoom orbit accumulation. Speculative; needs the CPU perturbation-orbit reference first (per the issue) | CPU reference | L |
| G4.7 | #838 | Buddhabrot GPU spike (scatter/atomic histogram) | — | L |
| G4.8 | #1173-K | Billiard colour programs: evaluate; likely a documented won't-do | — | S (decision) |

**G4.5 value check.** On the GT 710, GPU perturbation was much slower than the CPU (1.49 s vs 28 ms at
640x360) because of weak fp64. SA/BLA only pays off on strong-fp64 hardware, so the spike gate must
include a performance check on real hardware, not just parity.

### Phase 5 — Platform and measurement (run alongside, as needed)

| Issue | Goal | When |
|---|---|---|
| #1166 | Reference-orbit build bench + resident VRAM for the D3D11/Vulkan kernels | Before G4.5/G4.6 decisions |
| #432 | Benchmark improvements (GPU compute bullet) | Ongoing |
| #44 | Vulkan on macOS via MoltenVK (stretch) | After G4.1 |
| #623 (B5) | GPU parity for the out-of-bounds backdrop compositor | Rides the compositor work |

---

## 4. Recommended order

**Critical path:** G0.1 → G0.2 → G0.3 → G0.4 → G1.1 → G1.2 → G2.1 → G2.2.

**Quick wins to slot in alongside the critical path:** G0.6 (#82 close-out), G0.7 (#310), G3.1
(Bicomplex axis), G2.3 (HDRI).

**Then:** G2.4 → G2.5, the rest of Phase 3, then Phase 4 (G4.1 first if Linux GPU matters, since
Vulkan is the Linux backend).

Why this order:

1. **Stability before scope.** PR #1171 made the GPU path live on NVIDIA for the first time. Faults
   (#1169, #1170) and invisible fallbacks (#1173-M) have to be contained and visible before adding more
   GPU coverage, or every new slice inherits them.
2. **One foundation, three payoffs.** The kernel output contract (G1.1/G1.2) unblocks #323 (AOVs),
   the depth-output gate (#1009) and #1172 (post stack). Doing #1172 first would mean building a
   one-off G-buffer path and then redoing it.
3. **Biggest visible gap next.** #1172 is the difference users will notice: their FX disappear on
   GPU frames.
4. **Family coverage is cheap and independent**, so it can fill gaps between larger slices.
5. **2D deep-zoom GPU work is last** because it's the most speculative and the most
   hardware-dependent (#88, #607), and its value isn't proven on available hardware.

---

## 5. Tracking

- **Parent tracker:** [#1173](https://github.com/AloneButUnsober/FracturingFog/issues/1173) holds
  the previously untracked gaps (A–M). File a slice issue when a row is picked up.
- **Open GPU issues in this plan:** #1164 (PR #1171), #1169, #1170, #1172, #1045, #310, #82, #323,
  #1112, #880, #88, #607, #838, #1166, #432, #44, #623. Overlaps with the 3D roadmap are noted against
  [#389](https://github.com/AloneButUnsober/FracturingFog/issues/389) (S1/S2/S3/S4 AOV, tonemap and
  DoF work).
- Update this page's tables when a slice closes (tick it on #1173 too). Re-snapshot the date at the top.
