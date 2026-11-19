# Deep-zoom route calibration (#1665)

FF picks its deep-zoom route from fixed thresholds and opt-in flags. The route covers:
- CPU BLA rows or CPU LA;
- GPU perturbation on or off;
- GPU LA;
- the LA SIMD tail;
- the fp64 or float-exp GPU kernel.

All of those settings were measured on one machine, an i5-13420H with a GeForce GT 710. #1663 lists them. This plan replaces them with choices measured once per device and stored. It follows the model of the thread-group shape autotune (#1339): measure, key by device, reuse.

## Slices

The issues are the canonical task list.

| slice | issue | scope |
|---|---|---|
| S1 | #1785 | the per-device store; every route switch reads it; the winner selection (`DeepRouteChooser`) |
| S2 | #1786 | the calibration pass: scenes, timing, iteration agreement, first-use trigger |
| S3 | #1787 | perf HUD reason; Control Center view / reset / re-run; folds in #1493 |
| S4 | #1788 | `--batch` flag + Command builder |
| S5 | #1789 | validation on this machine and another one; #1663 close-out |

## The store (S1)

`Engine/Calculators/DeepRoute/DeepRouteStore.cs` writes `deep-route-calibration.json` under the app data root.

- **CPU rows** (`DeepRouteCpuResult`):
  - `LaMinZoom`: the Auto LA crossover. +∞ means LA never won.
  - `LaSimd`.
  - Keyed by `CpuKey` = CPU brand string (CPUID) | architecture | thread count | SIMD set.
- **GPU rows** (`DeepRouteGpuResult`):
  - `GpuDeep`, `GpuLa`, and `Precision` (Double or Fp32).
  - Keyed by the CPU key plus `IGpuKernel.DeviceKey`. That is the #1339 key: backend, adapter, ids and driver. The CPU is part of the key because "is the GPU faster" depends on both.
- **Invalidation:**
  - A new CPU, GPU or driver is a new key, so the old row stops matching.
  - Bumping `SchemaVersion` ignores the whole file. Bump it whenever the calibration procedure changes.
  - `Reset()` forgets everything.

## How the switches read it (S1)

`MandelbrotCalculator.DeepRoute.cs` applies, in order:

1. **The env switch, either way.**
   - `FF_GPU_PERTURB`, `FF_GPU_LA`, `FF_LA_SIMD`.
   - `FF_LA` and `FF_GPU_PERTURB_PRECISION` already force non-Auto modes.
   - `=0` beats a stored "on".
2. **An explicit setting on.** `UseGpuPerturbation`, `UseGpuLa` or `LaSimd` = true, set by the host, a probe or a test.
3. **The stored row** for this device.
4. **Today's default.**

`FF_DEEP_ROUTE_CALIBRATION=0` (`UseDeepRouteCalibration = false`) ignores the store. The session latches still win over everything: GPU deep too slow, or device lost.

| #1663 switch | reads |
|---|---|
| `LaAutoMinZoom` (Auto LA mode) | `EffectiveLaAutoMinZoom` |
| GPU deep perturbation | `GpuPerturbationEnabled(kernel)`. All gates use it: frame gate, orbit path, solid regions, route reason, render host, #1640's `CpuOnlyView` (any GPU row for this CPU) |
| `UseGpuLa` | `GpuLaEnabled(kernel)` |
| `LaSimd` | `LaSimdWanted` |
| `GpuDeepPrecision.Auto` | `AutoPicksFloatExp(kernel)`. Never picks fp64 on a device without it, and never picks float-exp on a kernel without it. |

**Vulkan default-on (S2, #1786).** A Vulkan session turns GPU deep on by default. The host marks that with `UseGpuPerturbationIsDefault`, so a stored "GPU slower" result turns it off. `FF_GPU_PERTURB=1`, or an explicit opt-in, still keeps it on.

## Choosing (S1, used by S2)

`DeepRouteChooser.Choose(runs)` takes timed runs and returns the store rows. Each run is a route, a scene, its zoom, its time and its iteration disagreement against CPU LA.

- **Eligibility.**
  - The CPU evaluators (BLA rows, LA, LA SIMD) are the shipped references, so they are always eligible.
  - A GPU route is excluded everywhere if, on any scene, it disagrees by more than max(2 %, 1.5 × the CPU evaluators' own disagreement on that scene). 2 % is the S1630 bound. The CPU evaluators' disagreement is the noise floor: measured on p448, CPU BLA differs from CPU LA on 0.2–3.8 % of pixels.
  - A fast wrong route never wins.
- **Minimum gain.** A measured choice replaces today's default only when it is at least 5 % faster: LA SIMD on, GPU deep on, GPU LA on, float-exp over fp64. Noise must not flip a default.
- **LA SIMD.** Wins when it is faster in total than the scalar LA tail, on the scenes where both ran.
- **LA crossover.** The shallowest scene from which the chosen LA tail beats BLA on that scene *and on every deeper one*.
- **GPU precision.** Float-exp against fp64, totalled.
- **GPU LA.** Against the chosen plain kernel.
- **GPU deep.** Per scene, the GPU's best eligible route against the CPU's best, totalled over the scenes where both ran.

A field stays null, so the default applies, where the runs can't decide.

## The pass (S2, #1786)

`DeepRouteCalibrator.Run(runner)` runs the scenes, shallow to deep.
- **Scenes:** the bench-kit p448 deep minibrot at 1e15, 1e22, 1e27, 1e31 and 3e33, which straddle the 1e30 crossover, then FF's built-in 3E47.
- **Order on each scene:**
  1. CPU LA runs first; it is the iteration reference.
  2. Every candidate runs.
  3. A second pass runs in reverse order, with CPU LA again.
- **Timing:** each route keeps the best of its two passes. A route already 3× slower than the scene's best skips the second pass.
- **Fallback:** a run that fell back (GPU too slow, route not taken) is not eligible.
- **Stopping early:** the budget (default 30 s) and the cancellation token stop the pass early. The finished scenes then decide what they can.

**The real runner.** `CalculatorDeepRouteRunner` renders 480×270 frames with a fresh calculator per run. Each calculator is forced onto its route by `MandelbrotCalculator.CalibrationRoute`. That setting is per calculator: the live frames' switches are never touched. Every route warms up once, untimed. A GPU run measured too slow during calibration does not latch GPU deep off for the session.

**When it runs** (`DeepRouteCalibration`):
- **Live shell:** once per process, in the background, on the first deep frame of a device with no stored result. It runs on its own below-normal thread with its own GPU kernel.
- **`--batch`:** never automatically; S4 adds the flag.
- **On demand:** `RunNow`.
- **Measuring only:** `--deeproutecalprobe` measures without storing.

**Kernel choice fix.** Once the float-exp kernel is chosen (Fp32 mode, a measured Fp32, or the calibration route), it now runs instead of the fp64 SA / BLA kernel. Before, the SA / BLA kernel took precedence on fp64 devices, so Fp32 never ran there with SA or BLA on.

## The UI (S3, #1787; folds in #1493)

- **Control Center → Advanced → GPU / Performance:**
  - **GPU deep zoom (perturbation)**, next to GPU compute. It is the user's override: above the env switch, the Vulkan default and the store. Ticking it re-arms the session latch (too slow / device lost). It is disabled, with the reason, when the device can't do deep zoom. Per session, like GPU compute.
  - The stored calibration for this CPU and GPU, plus **Re-run calibration** (background) and **Reset**.
- **Perf HUD:** on deep frames, a `deep` line shows each choice and where it came from (measured / default / env / set / toggle / session off / Vulkan default). Example: `deep   LA ≥1e27 measured · SIMD on measured · GPU off default`.
- **CPU route reasons:** a deep frame left on the CPU now says how to turn GPU deep zoom on (the toggle, `FF_GPU_PERTURB=1`), or that it was measured slower. The too-slow latch names `FF_GPU_PERTURB_BUDGET_MS` and the toggle.
- **Batch:** S4 (#1788) carries the toggle as `--gpu-deep` (see below).

## Batch (S4, #1788)

- **`--gpu-deep auto|on|off`:** the Control Center toggle. `on` and `off` beat `FF_GPU_PERTURB` and the measured route. `auto` (the default) keeps them. `on` with `--gpu off` is an error, exit 2.
- **`--deep-route-calibration auto|off|force`:**
  - `auto` (the default) uses the stored calibration and never runs a pass, so a batch's renders don't change under it;
  - `off` uses the fixed defaults;
  - `force` measures first (on a GPU device of its own; CPU only with `--gpu off`) and stores the result.
- **Command builder:** emits `--gpu-deep on|off` when the user set the toggle, with GPU compute on. With GPU compute off the live view has no kernel, so its deep frames are on the CPU too. The S3 fidelity gap is gone.
- **stderr:** the batch prints `deep route: …`, the stored rows, at setup. When the GPU opens it prints `deep zoom on the GPU / CPU (…)`, with the source: FF_GPU_PERTURB, --gpu-deep, measured on this device, or default.
- **Logic:** lives in `DeepRouteBatchSetup` (Engine, tested by `S1788DeepRouteBatchTests`). `Batch/BatchDeepRouteSetup.cs` only hands it the batch's own-device kernel factory and stderr.

## Validation (S5, #1789)

### On this machine (i5-13420H, 12 threads, AVX2 + GeForce GT 710)

`--deeproutecalprobe` was run at the calibration's own size and at full-size frames (`FF_DEEPCAL_SIZE`).

| frames | LA crossover | LA SIMD | GPU deep / GPU LA |
|---|---|---|---|
| 480×270 (the pass's size), CPU / D3D11 / Vulkan | 1e27 | on | off / off |
| 960×540, CPU | 1e27 | on | — |
| 1920×1080, CPU | 1e27 | on (5–15 %) | — |

So the pass picks the same CPU route at full size as at its own size. It keeps GPU deep off, as today's default does. It differs from two fixed defaults:
- **LA crossover:** 1e27 instead of 1e30. From 1e27 the LA rows (4-lane tail) beat BLA by 5–15 % at every size.
- **LA SIMD:** on instead of off.

**Why LA SIMD came out on although #1628 measured no gain.** The scalar LA tail's speed depends on the JIT's dynamic-PGO profile. The 4-lane tail's does not.

| p448 3e33, 960×540 | scalar LA | 4-lane LA |
|---|---|---|
| fresh process, this scene only | 170 ms | 176 ms |
| after the shallower scenes in the same process | 220 ms | 175 ms |
| `DOTNET_TieredPGO=0` | 260–300 ms | ~175 ms |

#1628's single-scene measurements are the first row. A session that zooms in, and the calibration pass, are the second.

**Three biases fixed in S5.**
- **The reference frame.** It was CPU LA's first timing. The first frame after a scene switch runs up to ~25 % slow, so CPU LA always took the slow slot. It is untimed now, and CPU LA alternates like every other route.
- **A cold process.** A fresh process ran the first frames of each code path up to 6× slow (CPU BLA at p448 1e22: 176 ms, then 27). So the first pass in a process chose differently (LA from 3e33, SIMD off) from the next ones. An untimed warm pass over every scene and route now runs first. After it, the first pass in a fresh process agrees with the others (CPU / D3D11 / Vulkan: 1e27, SIMD on, GPU off), with CPU times matching to the millisecond across passes.
- **Garbage.** The runner collects the previous frames' garbage before each timed frame.

Three timed passes instead of two (a loser still stops after one). More passes did not move the JIT-state effect above; the warm pass did.

The GPU SA / BLA kernel's iteration agreement past 1e27 was #1791. Its BLA table kept ε = 1e-6 in the QD band, where the CPU rows use 1e-10 (#1613), so it was off about 10× as often and the calibration excluded it. It now uses the same ε as the CPU rows and agrees as well as they do (S1791GpuBlaEpsilonTests).

### On another machine

The pass is per device, so the real test is a machine with a different CPU or GPU. On a Windows build, run:

```
FracturingFog.com --deeproutecalprobe
FracturingFog.com --deeproutecalprobe     (with FF_DEEPCAL_SIZE=1920x1080 FF_DEEPCAL_BACKEND=cpu)
```

Linux works too, with `FF_DEEPCAL_BACKEND=cpu` or `vulkan`. The probe only measures; it writes nothing unless `FF_DEEPCAL_STORE=1`. Paste the output on #1789. These are the choices it might change:
- **Modern NVIDIA / AMD GPU (fp64 at 1/32–1/64):** GPU deep on, with float-exp over fp64 (#1663's open question).
- **AVX-512 or larger-cache CPU:** a different LA crossover.
- **Hybrid vs non-hybrid CPU:** whether LA SIMD still wins.

In the live app, Control Center → Advanced → GPU / Performance shows the stored result after the background pass.

## Related

#1663 (the audit), #1339 (the store model), #1664 (a float-exp + LA GPU kernel, a future candidate), #1493 (folded into S3).
