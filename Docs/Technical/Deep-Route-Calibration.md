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

**Known gap, for S2.** A Vulkan session turns GPU deep on by default. A stored "GPU slower" result does not turn it off yet, because the store can only turn GPU deep *on* where the default is off. S2 handles this case.

## Choosing (S1, used by S2)

`DeepRouteChooser.Choose(runs)` takes timed runs and returns the store rows. Each run is a route, a scene, its zoom, its time and its iteration disagreement against CPU LA.

- **Eligibility.** A route that disagrees on more than 2 % of pixels on any scene is not eligible anywhere. 2 % is the S1630 GPU-vs-CPU LA bound. A fast wrong route never wins.
- **LA SIMD.** Wins when it is faster in total than the scalar LA tail, on the scenes where both ran.
- **LA crossover.** The shallowest scene from which the chosen LA tail beats BLA on that scene *and on every deeper one*.
- **GPU precision.** Float-exp against fp64, totalled.
- **GPU LA.** Against the chosen plain kernel.
- **GPU deep.** Per scene, the GPU's best eligible route against the CPU's best, totalled over the scenes where both ran.

A field stays null, so the default applies, where the runs can't decide.

## Related

#1663 (the audit), #1339 (the store model), #1664 (a float-exp + LA GPU kernel, a future candidate), #1493 (folded into S3).
