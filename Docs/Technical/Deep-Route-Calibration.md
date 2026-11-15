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
- **Batch:** the toggle is not expressible in `--batch` yet. S4 (#1788) adds the flag and the Command builder seed. On fp64 kernels the output is identical either way; the fp32 kernels differ on about 0.35 % of pixels.

## Related

#1663 (the audit), #1339 (the store model), #1664 (a float-exp + LA GPU kernel, a future candidate), #1493 (folded into S3).
