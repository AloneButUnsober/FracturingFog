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
| **3D families** (Mandelbulb, Mandelbox, KIFS, QuatJulia, QuatMandel, Kleinian, Bicomplex, Coquaternion) | Raymarch + in-kernel lighting: 3 lights (point/spot/area), shadows, AO, fog/volumetrics, reflections, PBR terms, SSS, triplanar, IBL, sky, caustics; froxel 3D via the #1070 hybrid | None left after Phases 1–3 (2026-10-06): the post stack (#1172), AOV views + depth (#323), HDRI (#1173-B), every fold / axis / colouring (#1173-C…F) and Coquaternion (#1173-G) render on the GPU. Open follow-ups: #1181 (device-resident G-buffers; thin-lens + tonemap still CPU) and #1197 (KIFS fold shapes, CPU and GPU together) |
| **UserBulb** | Every DE the CPU uses except two — Vec3 analytic / numerical (Julia too), quaternion numerical / exact (#1112) — with the family kernels' full shading + post stack (#1173-A) | Orbit-metric colour drivers (#1202); scalar KIFS and non-escaping DEs |
| **Relief 3D** | Near parity: DoF, positional/area lights, glass, froxel temporal/reprojection, HDRI | Lighting-component / HDR-beauty captures and AOV views (#389 S1/S2, #323) |
| **2D Mandelbrot** | SP iteration ≤ zoom 1e4 (D3D11/Vulkan); deep-zoom perturbation with SA + BLA skipping (#82, #88, both backends); orbit ColorGen themes on the GPU at any depth, filling the orbit-trap relief field (#1173-J, #607) | Strong-fp64 perf sign-off for SA / BLA ⚑ (#88); interior-colouring and `trap`-shape orbit themes stay CPU-only (no GPU orbit palette for them at any depth) |
| **2D other families** | Julia/Burning Ship/Tricorn iteration + GPU palette (#1173-H); Multibrot (d ≤ 12), Phoenix and the domain warp (#1173-I); Buddhabrot / Nebulabrot / Anti* uniform sampling, zoom ≤ 100 (#838) | Magnet / Glynn / Spider and the chaotic billiard (no kernel; billiard colour programs follow the billiard, #1173-K won't-do); Buddhabrot Metropolis sampling and Dual Buddhabrot (#838) |

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
| G2.3 | #1173-B | HDRI environment sampling in the 3D kernels (port relief's equirect path). It was never gated: HDRI scenes rendered on the GPU with the gradient sky. Delivered: `GpuHdriEnv` hands the 8 kernels relief's `ReliefHdriBuffer` layout and `GpuKernelUtils.SampleHdri` (twin of `ReliefHdriBuffer.Sample`) feeds miss pixels, IBL ambient and reflections; the Solid-sky env ambient is fixed too. `S1173BGpu3DHdriTests`: drift 0.03–0.86 vs 25–43 for the gradient frame (1.6–26 on the surface alone); bench gains `MandelboxHdri` | G0.1 | M | **Done** |
| G2.4 | #1173-A | UserBulb GPU shading: shadows, AO, specular, reusing `GpuKernelUtils`. Delivered: `UserBulbShadeKernel` — the family kernels' shading body around a user-DE hook, adapted to the CPU UserBulb trace (cull sphere, clip plane, forward-diff normals, StepDepth / Normal drivers, the CPU's cone-march tile hints so the step-count colour matches). The legacy path compiles it with the built-in triplex DE; the sandbox path splices the user DE into the same source text (embedded resource) — no drift possible. Both run on `GpuAcceleratorHost` (CPU-accelerator fallback kept), tiled, with G-buffers → the CPU post stack, AOV views and stereo depth. Also fixed: the GPU took the analytic DE when the CPU didn't (a different surface); it now mirrors the CPU's DE choice. `S1173AUserBulbGpuShadingTests`: drift 0.000–0.17 (vec / quat, lit, post, AOV, Normal driver). Follow-ups #1201 (cull-sphere miss ignores the sky, CPU+GPU), #1202 (orbit colour drivers) | G0.1 | M | **Done** |
| G2.5 | #1112 | UserBulb Vec3 numerical DE + Julia kernels, exact quaternion routing. Delivered: the sandbox kernel's Vec3 DE gains the numerical Jacobian with Julia (twin of `UserBulbDE`) and the quaternion DE the exact q²+c form (twin of `UserBulbQuatExactDE`, `UseAnalyticDE` = 2); the gate sends every CPU DE but the scalar KIFS / non-escaping ones to the GPU and names those two. `S1112UserBulbGpuDeTests`: drift 0.09–0.27, contrasts 14–17; dropping either branch fails it. Benchmark re-run (CPU accelerator, RDP): Mandelbox 8138 → 929 ms | G2.4 | L | **Done** |

### Phase 3 — 3D family coverage (independent; ordered by cost/benefit)

| Slice | Issue | Goal | Depends | Size |
|---|---|---|---|---|
| G3.1 | #1173-C | Bicomplex: slice-axis parameter in the kernel. Delivered: `BicomplexGpuParams.SliceAxis` mirrors the CPU packing, the K-only gate is gone, `S1173CBicomplexSliceAxisGpuTests` checks each axis against the CPU (drift 0.04–0.07; ignoring the axis gives 3.5–4.0), and the bench gains `BicomplexR`. Batch: the axis joins the region snapshot (`--param BicomplexSliceAxis=N`), and family-unique Kleinian / QuatMandel keys now apply without the camera keys. **Done** | G0.1 | S |
| G3.2 | #1173-D | KIFS: Octahedron/Dodecahedron/MandelboxRot folds (one kernel branching on fold kind). Delivered: `KifsFoldGpuCalculator` (the Menger kernel's shading body, a fold-dispatching DE; Menger and Sierpinski keep their own kernels) with ports of the shipped CPU DEs — still the 5.9.f1 approximations, parity only. Rotation coefficients come from the host, so there is no per-DE-call trig on fp64-poor cards. `S1173DKifsFoldGpuTests`: drift 0.000–0.19 vs 7–32 to every other fold; bench gains the three folds. **Done** | G0.1 | M |
| G3.3 | #1173-F | QuatMandel dual-orbit colouring in the kernel. Delivered: the kernel runs the twin of `DualOrbitSurfaceScalar` (log constants from the host) and feeds it to the colour-map LUT through `GpuKernelUtils.SurfaceAlbedoAt`; `GpuAlbedoLut.Bake` takes a minimum range so the LUT spans smooth 255. `S1173FQuatMandelDualOrbitGpuTests`: drift 0.07–0.13, seed tracked; bench gains `QMandelDual`. **Done** | G0.1 | M |
| G3.4 | #880 + #1173-E ⚑ | Kleinian: variable generator lists, rotation generators, word-length colouring, analytic DE. Delivered: the generators reach the kernel as a (cx, cy, cz, r) buffer (any count, per-sphere radius; the count is uniform per launch, so no divergence), plus the #877 rotation fold, #878 WordLength / LastGenerator colour (via `SurfaceAlbedoAt`) and #881 `KleinianDeFactor` ("analytic DE" shipped as under-relaxation; the analytic Jacobian is a no-op for this family). Every inversion group now renders on the GPU. `S1173EKleinianGpuTests`: drift 0.05–0.70 across 6 groups and the three features; a kernel limited to four generators, or ignoring any feature, fails. Bench gains `KleinianNecklace`. ⚑ performance sign-off on a modern GPU still open. **Done** | G0.1 | L |
| G3.5 | #1173-G | Coquaternion GPU kernel. Delivered: `CoquaternionGpuCalculator` (the shared per-fractal shading body with the split-algebra DE, a twin of `CoquaternionDE`); the calculator gets the standard GPU path (LUT albedo, HDRI, AOV, post stack, thin-lens). `S1173GCoquaternionGpuTests`: drift 0.02–0.08 at two slices, plain and with shadows / AO / reflections / fog; Hamilton signs or an ignored slice fail it. Also added to the albedo, HDRI and froxel-hybrid family tests and the GPU bench. **Done** | G0.1 | M |

#880 was deferred for lack of hardware. The GT 710 plus the ILGPU CPU-accelerator twin is probably
enough for correctness; the ⚑ is for performance sign-off only.

### Phase 4 — 2D GPU

| Slice | Issue | Goal | Depends | Size |
|---|---|---|---|---|
| G4.1 | #1173-L | Investigate why Vulkan SP is ~20x slower than D3D11 SP (889 vs 44 ms, 1080p, GT 710): readback, fp64 in the SP shader, dispatch shape. **Found: readback.** Dispatch was ~22 ms; reading the result buffers took ~780 ms because every buffer took the first `HOST_VISIBLE \| HOST_COHERENT` type — uncached write-combined memory on NVIDIA. Readback buffers now prefer a `HOST_CACHED` type (`VulkanHostMemory`, all three Vulkan kernels): 1080p SpShallow 864 → 36 ms (D3D11 45 ms), SpZoom1e4 146 ms (D3D11 154); perturbation ~9% behind D3D11 (fp64-bound). All Vulkan smoke gates pass on the GT 710; `S1173LVulkanReadbackMemoryTests` pins it. **Done** | — | S–M |
| G4.2 | #1173-H | GPU palette for Julia / Burning Ship / Tricorn (the deferred "phase 5"). **Finding:** the GPU palette already served them (`EscapeTimeCalculator.TryDispatchGpu`, T3.1 phase 4; the "phase 5" note was stale). What made their GPU frames differ from the CPU was the smooth iteration count: the SP shader used `log2(ln\|z\|)` where the CPU (and the perturbation shader) use `log2(log2\|z\|)` — every GPU pixel +0.529, Mandelbrot included — and `EscapeTimeCalculator` ran the GPU at a hardcoded bailout radius 2 (CPU 512). Fixed both (D3D11 + Vulkan share the HLSL; the Vulkan smoke CPU mirror + its golden digest follow). GT 710, GPU-palette theme, mean drift: Mandelbrot 5.00 → 0.42, Julia 6.33 → 1.43, Tricorn 6.44 → 0.35, Burning Ship 8.73 → 4.22 (fp32 boundary chaos). `S1173HGpuEscapeTimeColourTests`. Still CPU-coloured: the 291 of 312 themes with no HLSL palette (they iterate on the GPU and say so) | — | M | **Done** |
| G4.3 | #1173-J | GPU orbit path fills `TrapBuffer` → orbit-trap relief on the GPU. The F16 orbit kernel wrote colour only, so `MandelbrotCalculator.TrapBuffer` kept the last CPU frame's field (or nothing) whenever an orbit ColorGen theme coloured on the GPU: the display-res Trap / Blend relief and the batch / poster path read a stale or empty field, and the hi-res relief twin was forced to the CPU. The orbit kernel now writes the slot-1 trap minimum to a `gTrap` UAV (u4, Vulkan binding 204; 0 in the set and for a theme that does not read `trapMin`), read back into `TrapBuffer` by both backends (`IGpuKernel.Run(..., trapDst)`), and the twin keeps the GPU. GT 710, 320×240: \|GPU − CPU\| trap median 3e-8, p95 2.4e-7 (D3D11, `--d3dorbittrapprobe`); Vulkan `S1173JGpuOrbitTrapTests` (incl. the live host twin). Still CPU: the shape-selectable `trap` input (#611, no HLSL SDFs) and orbit themes past `MaxGpuZoom` (#607). **Done** | — | M |
| G4.4 | #1173-I | Multibrot (`pow`), Phoenix (previous-z carry), domain warp (per-pixel c). The shared SP kernel (D3D11 + Vulkan) gains `FractalKind` 4 = Multibrot (the CPU's closed forms for d = 3–5; other d by repeated complex multiplication, the same value as the CPU's polar form without fp32 `pow` / trig error) and 5 = Phoenix (previous z and dz/dc carried, dz/dc from 0, as `PhoenixKernel.StepWithPrevDeriv`). The domain warp is applied per pixel from three new cbuffer scalars (64 → 80 bytes; strength 0 leaves the un-warped c expression untouched, so the Vulkan `--colorprobe` golden digest is unchanged). **Found:** a high-power Multibrot escapes with \|z\| ~ 1e21, where fp32 `sqrt(zr*zr + zi*zi)` overflowed to +inf. Those pixels got smooth −inf and rendered black (5.8% of a d = 8 frame). `SafeMag` fixes it and is bit-identical whenever the plain form is finite. d > 12 stays on the CPU and says so (`Multibrot d > 12`). GT 710, 320×240, GPU palette: escape iteration agrees on 98.9–99.95% of pixels, median \|smooth\| ≤ 4e-7, mean colour drift 0.32–1.63 against 9.9–43 for a contrast frame (`--d3dfamilyprobe`; Vulkan `S1173IGpuEscapeFamiliesTests`). **Done** | — | M each |
| G4.5 | #88 ⚑ | SA, then BLA, on the GPU perturbation path (spike-first). **G4.5a SA — done:** the correctness-green spike kernel (`BuildPerturbSA`, Vulkan-only, reachable only from `--vulkanpturbsa`) is now an `IGpuKernel` member on both backends (`SupportsPerturbationSA` / `RunPerturbSA`; the D3D11 FXC compile, never verified before, works; the D3D band loop + readback are shared with `RunPerturb`, and the Vulkan SA dispatch gained the first-band too-slow abort it lacked). `TryRunGpuPerturbation` uses it under the CPU's own conditions (SA coefficients valid past 16 iterations, `DisableAcceleration` / `DisableSeriesApproximation` off; `FF_GPU_SA=0` opts out); the route reads "deep-zoom perturbation + series approximation". GT 710, `--d3dpturbcalc` 128²: GPU+SA vs CPU 0–0.024% iteration disagreement, and SA cuts the GPU time 17→7, 44→25, 156→103, 206→178 ms (the 1e15 view now beats the CPU's 13 ms). `S88GpuPerturbSaTests` (Vulkan): the kernel matches an independent CPU-SA oracle on the production `SeriesApproximation` (0.14%) and not the plain answer (SA moves 8.8% there); tolerance 0 equals `RunPerturb`; the calculator honours the toggles. **G4.5b BLA — done:** the CPU's `BlaTable` (built host-side by `EnsureBlaTable` for the image-corner \|dc\|) is flattened once per table (`BlaTable.GpuCoefficients`: A, B collapsed Hi+Lo as the CPU applies them, plus R²) into one extra SRV (t10) of the SA kernel; `gBlaLevels` (the former `gPad0`, so the cbuffer stays 80 bytes) turns it on. At the top of each iteration the kernel does `BlaTable.Lookup` on the reference index m (longest aligned L ≥ 2 whose R² holds \|δ\|², m + L < refLen, iter + L ≤ maxIter), then δ' = Aδ + B·dc and dz' = A·dz, exactly as the CPU SIMD loop. The lookup is per pixel (the CPU's is lane-uniform over 4 pixels, which only skips less). `safeMax` 0 gives BLA without SA, so every CPU toggle combination maps onto the GPU; `FF_GPU_BLA=0` opts out. GT 710, `--d3dpturbcalc` 128², GPU SA+BLA vs CPU 0–0.055% iteration disagreement and the GPU now **beats the CPU on all four views** even on this fp64-starved card: 85 ms vs 94 (1e14), 9 vs 15 (1e15), 10 vs 26 (4.5e46), 34 vs 99 (3e47; plain GPU 212). `S88GpuPerturbSaTests` adds an oracle on the production `BlaTable.Lookup` (BLA skips > 30% of iterations at 1e12), a corrupted-table check (the kernel reads the table, and only with levels > 1) and the SA × BLA toggle matrix. ⚑ perf sign-off on strong-fp64 hardware still open | G0.6 | L |
| G4.6 | #607 | GPU deep-zoom orbit accumulation. **Done:** the CPU reference the issue asked for already existed (`CalculateOrbitAwarePerturbation`, #609). New `MandelbrotKernelSource.BuildPerturbOrbit(mask)` is `BuildPerturb`'s rebased δ loop plus the F16 orbit accumulators, sampled on the reconstructed z = Z[m] + δ (pre-update, iter > 0, the CPU convention). It is one shader per orbit mask on both backends (`IGpuKernel.SupportsPerturbationOrbit` / `RunPerturbOrbit`). The cbuffer is `BuildPerturb`'s 64 bytes; two pad ints carry the float view centre. The kernel writes the mask'd means (bit order) to a fourth UAV. The calculator (`TryRunGpuOrbitPerturbation`) colours each pixel from them through the CPU path's own `FinalizeOrbitPixel`, so `MapWithOrbit` and the aux buffers are the CPU code. No SA/BLA: an orbit theme needs every iteration (as on the CPU). The scope is the shallow GPU orbit kernel's (a GPU orbit palette, exterior colouring). The toggles are `UseGpuPerturbation` plus `FF_GPU_DEEP_ORBIT=0`. Float sampling needed one change: the triangle-inequality term is rearranged without cancellation (M − m = 2 min(\|w\|, \|c\|), \|z\| − m from 2 Re(c̄w) + \|w\|², w = z_{n−1}² from the double z). The plain float form cancels near a minibrot and gave a median TIA error of 2e-3. **CPU bug found by the probe:** the #609 path's periodicity check compared the reconstructed z only. At 3e47, \|δ\| is below double resolution of Z[m], so z rounds to the reference and every pixel was flagged periodic: the frame rendered all in-set. It now requires z **and** δ to repeat (δ = 0, the reference pixel, is never flagged). An attracting cycle is still caught, and a 200 000-iteration minibrot-interior frame still exits early. GT 710, `--d3ddeeporbitprobe` 128²: 0% iteration disagreement, colour drift ≤ 0.04 levels per channel (direct double: 0.38–29), **GPU 4.6–6.4× faster than the CPU deep orbit path**: 211 vs 1312 ms (1e14), 30 vs 156 (1e15), 69 vs 272 (4.5e46), 290 vs 1593 (3e47). `S607GpuDeepOrbitTests` (Vulkan); `DeepZoomOrbitPerturbationTests` adds the 3e47 and interior regressions | CPU reference (#609) | L |
| G4.7 | #838 | Buddhabrot GPU spike (scatter/atomic histogram). **Done (uniform sampling):** `BuddhaKernelSource` (FXC + DXC; `IGpuKernel.SupportsBuddhabrot` / `RunBuddhaBatch`). One thread per sample draws c from a PCG stream keyed by (seed, batch, sample). Pass 1 classifies in float exactly as `IterateOrbit`; pass 2 replays the orbit and `InterlockedAdd`s into the three band histograms (nearest pixel, or HD stochastic bilinear + mirror). `BuddhaFamilyCalculator` adds the result into its usual `_hits*`, so composite, recolour and relief are unchanged. The host's 2D GPU compute switch drives it; `FF_GPU_BUDDHA=0` opts out. **Findings:** (1) the rare in-set c outside the cardioid/bulb held its warp for all 50 000 iterations. Brent cycle detection (exact float compare) now rejects it at once, and for the anti types it splats a found cycle once with its repeat counts. Both are exact, and Anti went from 3.6 s to 1.25 s. (2) Fixed-size dispatches spent most of their time on submit overhead (761 vs 178 ms); dispatches now adapt toward 150 ms each, under the watchdog. (3) **Bug found by the statistics:** the two passes iterated in separately compiled loops with differently fused multiply-adds. A c that pass 1 classified as escaping late could replay as an orbit trapped in an attracting cycle, so one pixel took 10% of the high band, a top/bottom imbalance. Both passes now share one `precise` `Step()`, and the kernel equals `BuddhaUniformSampler`, a strict-float C# replay, on both backends: equal band totals, ≤ 34 of 19 200 pixels off through approximate division. (4) **Metropolis stays on the CPU:** 4096 short GPU chains took 30 s against the CPU's 10 s at zoom 3 on the GT 710, and they mix differently from the CPU's 12 long chains. Zoom compensation turns Metropolis on above zoom 1.2, so zoomed Buddhabrot views stay CPU. GT 710, defaults (640×480, 500K samples): Buddhabrot 291 → 220 ms, HD 323 → 269 ms, AntiBuddhabrot 7.1 s → 1.27 s (D3D11 `--d3dbuddhaprobe`). The GPU realization differs from the CPU's for the same seed, and is deterministic. GPU-vs-CPU block-histogram distance 1.0–1.1× the CPU seed-vs-seed noise; contrast frame 9–20×. **Follow-up #1218 (smoke test):** toggling GPU compute changed the picture: two random draws, and the few long high-band orbits are the visible loops. The CPU's uniform sampler is now the kernel's own algorithm (`BuddhaUniformSampler`, float, counter-based stream, run in parallel), so GPU, CPU and `--batch` render the same image (≤ 33 of 307 200 px apart, approximate division), independent of the core count. With the cycle detection on the CPU too, that run is far faster than the old one and than the GT 710 (Buddhabrot 291 → 66 ms, GPU 195; AntiBuddhabrot 7.1 s → 125 ms, GPU 1214). GPU sampling is therefore opt-in (`FF_GPU_BUDDHA=1`). The same issue fixed the near-black default Buddhabrot: every orbit's z0 = 0 put ~437K hits on the origin pixel (next hottest ~1.8K), and the log normalisation divided the image by it. No sampler draws z0 now, and the new `BuddhaMinIter` (default 12) drops the fast escapers that washed Nebulabrot's |c| ≤ 2 disc red | — | L |
| G4.8 | #1173-K | Billiard colour programs: evaluate; likely a documented won't-do. **Decided: won't-do.** A billiard ColorGen program (#633) colours the per-pixel outcome (gate, bounce count, path length) of `ChaoticBilliardCalculator`'s trace, and that trace has no GPU kernel. A GPU palette on its own would upload the three outcome buffers (12 B/px) and read the colour back (4 B/px), about 33 MB a frame at 1080p, to save the program's CPU time. Measured on 12 cores (warm JIT, median of 25 frames), trace alone vs the same frame with a ColorGen program: 640×480 three-disk 6 vs 8–10 ms; 1080p three-disk 13 vs 50 ms; 1080p 64-disk field 61 vs 96 ms, 64-disk ring 70–81 vs 110–127 ms. The program costs ~35–45 ms at 1080p, interactive already, and on a GT 710-class card the transfers alone take a comparable time. In the heavy geometries the trace dominates, so the real gain would be a GPU billiard trace kernel: a new family kernel, like Magnet / Glynn / Spider, not a colour-parity gap. With GPU compute on, the host already reports "ChaoticBilliard has no GPU path" (#1173-M). `BilliardColorGenTests.Billiard_Programs_Advertise_No_Gpu_Palette` pins the decision. Revisit only together with a billiard GPU kernel; the HLSL emitter would then take the three inputs as it does the orbit accumulators. **Done (decision)** | — | S (decision) |

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
