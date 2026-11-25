# Probes

Headless measurement and parity programs for the Windows WinExe (`FracturingFogCLD.csproj`).
Each one is a command-line flag dispatched near the top of `Program.cs`, prints its results to the
console and exits. They measure (frame times, speed-ups, pixel differences) or run a hardware
check that `Server.Tests` cannot run: the test project is `net10.0` and cannot load the D3D11
backend.

Run one from a Release build, for example:

```bash
./bin/Release/net10.0-windows/FracturingFog.exe --solidregionprobe 1920 1080
```

**New probes go in this folder** (#1468), and get a row in the table below. They keep the
`FracturingFog` namespace and an `internal static class` with a `Run` method returning the exit
code. Probes shared with other hosts live in `Engine/Diagnostics/` instead.

| File | Flag | Issue | What it checks |
|---|---|---|---|
| `AdaptiveAaProbe.cs` | `--adaptiveaaprobe [threshold tolerance]` | #1344, #1382 | Adaptive anti-aliasing against full supersampling (CPU), Mandelbrot views, the ten 3D raymarchers and the 2D alt calculators (`FF_AA_CASES=2d` runs those alone): samples computed, wall times, error |
| `BigFloatProbe.cs` | `--bigfloatprobe` | #1830 | Reference-orbit iteration cost in BigFloat (epic #1428) at the precisions for 1e100..1e10000, against OD; allocation per iteration (`FF_BIGFLOAT_ITERS`) |
| `D3DBuddhaProbe.cs` | `--d3dbuddhaprobe` | #838 | D3D11 half of the GPU Buddhabrot parity check |
| `DeepCostProbe.cs` | `--deepcostprobe [W H]` | #1613 | CPU deep-zoom cost on bench-kit minibrot scenes: frame time, rebased pixels, iterations, default vs acceleration off and the pixels that differ (`FF_DEEPCOST_DUMP` / `_SCENE` / `_EPS` / `_REPS`); `FF_DEEPCOST_LA_AB=1` adds the LA evaluator's frame (#1626); `FF_DEEPCOST_LA_SIMD_AB=1` times it scalar against 4-lane, alternating (#1628); #1629: `FF_DEEPCOST_REGIONS=<regions.json>` (default against LA on every deep Mandelbrot region), `FF_DEEPCOST_KFR=<dir>` (DeepLa Off against Auto on every `.kfr`), `FF_DEEPCOST_FFVIEWS=1` (adds FF's own deep views and the period-3 nucleus); #1614: `FF_DEEPCOST_LA_ASCEND_AB=1` (LA as Imagina's stages, with the climb back, and with the climb plus 3 stage bits, alternating); #1427: `FF_DEEPCOST_AA_REUSE_AB=1` (a 16-sample MSAA pass reusing the reference orbit against rebuilding it per sample, and a cancelled frame's stop time) |
| `NovaEarlyOutProbe.cs` | `--novaearlyoutprobe [W H]` | #1979, #1980, #1964 | Nova's early-outs (pauldelbrot's ideas): frame times off / Brent periodicity / opt-in chaos early-out / both, on minibrot, filament, Julia and deep (engine) views, median of `FF_NOVA_REPS`, at the caps in `FF_NOVA_CAPS`; pixels stopped, iterations saved, pixels / basins / final z that differ from off; `FF_NOVA_OUT` writes off / both / difference PNGs, `FF_NOVA_VIEWS` filters, `FF_NOVA_SCAN=1` maps a plane. Builds on a tree without the early-outs (sets them by name), which is how the "before" column was taken |
| `QdFloorSweepProbe.cs` | `--qdfloorsweep` | Wave 2.14, #1864 | Coordinate-separation floor, no rendering: bit-distinct per-pixel X coordinates (128 wide) in QD, OD, the past-OD path's exact centre in BigFloat at `PrecisionForZoom` (AP) and floatexp deltas, 1e40..1e1000, on a sparse (3E47) and a dense (Misiurewicz, 340 digits) centre; writes `qdfloorsweep.out` |
| `LaProbe.cs` | `--laprobe [W H]` | #1624 | Period-synchronised LA (stages + AT, epic #1623) proof of concept on the bench-kit minibrots: the stages, steps per pixel, agreement with FF's deep path and with exact iteration (`FF_LAPROBE_SAMPLES` / `_BITS` / `_SCENE` / `_CLIMB`); since #1626 also the engine's LA evaluator ("eng", `FF_LA_PERIOD` / `FF_LA_CLIMB` / `FF_LA_TAIL_BLA`) against the same exact samples |
| `D3DDeepOrbitProbe.cs` | `--d3ddeeporbitprobe` | #607 | D3D11 half of the deep-zoom orbit parity check |
| `D3DDeviceLossProbe.cs` | `--d3ddevicelossprobe` | #1045 | The D3D11 presenter recovers from a device loss |
| `D3DEscapeFamilyProbe.cs` | `--d3dfamilyprobe` | #1173 | D3D11 half of the Multibrot / Phoenix / domain-warp parity check |
| `D3DOrbitTrapProbe.cs` | `--d3dorbittrapprobe` | #1173 | D3D11 half of the orbit-trap parity check |
| `D3DPerturbProbe.cs` | `--d3dpturbcalc` | #82 | D3D11 deep-zoom perturbation parity (twin of `--vulkanpturbcalc`) |
| `DistanceFieldProbe.cs` | `--distancefieldprobe [W H]` | #1336, #1390 | 3D raymarch with and without the baked distance-field cache (KIFS, User Bulb, Mandelbulb, Mandelbox; the moving frames' level; `FF_DF_CASES` filters) |
| `DynamicResolutionProbe.cs` | `--dynresprobe` | #1333 | Drag responsiveness through a real host, dynamic preview resolution off and on |
| `GpuPrecisionProbe.cs` | `--gpuprecisionprobe` | #1611 | D3D11 fp32 shallow kernel vs the CPU: the picture's shift in pixels (from the smooth field) by pixel spacing in fp32 ulps, four views at 1080p and 4K; sets `MandelbrotCalculator.Fp32MinPixelUlps` |
| `GpuBandProbe.cs` | `--gpubandprobe` | #1334 | Deep perturbation frames in TDR row bands (D3D11 and Vulkan) |
| `VulkanReliefTileProbe.cs` | `--vkrelieftileprobe` | #1479 | Vulkan relief raymarch in tiles on a fog-heavy 1080p frame: tile count, wall / GPU time per packet, learned tiles == 64-row bands |
| `GpuShallowBandProbe.cs` | `--gpubandprobe shallow` | #1481 | Shallow escape-time frames (base, colour, orbit colour) in TDR row bands (D3D11 and Vulkan): banded == one band, the 1080p GT 710 TDR frame, banding cost |
| `DeepRouteCalProbe.cs` | `--deeproutecalprobe` | #1786 | The deep-zoom route calibration pass (#1665) on this machine, CPU only, D3D11 and Vulkan (`FF_DEEPCAL_BACKEND`): per scene the route times and iteration agreement with CPU LA, and the choice. Measures only; `FF_DEEPCAL_STORE=1` writes the store, `FF_DEEPCAL_BUDGET_S` the budget, `FF_DEEPCAL_SIZE=WxH` the frame size (#1789) |
| `GpuLaProbe.cs` | `--gpulaprobe [W H]` | #1630 | The LA stage kernel on the GPU (D3D11 and Vulkan) against the GPU SA / BLA kernel and the CPU BLA / LA paths, on deep views in 1e30..MaxGpuPerturbZoom: frame times and iteration agreement with the CPU LA frame (`FF_GPULA_REPS`). #1661: `FF_GPULA_SKEW=1` compiles the D3D11 kernel with the rounding difference that hung the GT 710 forced in (with `FF_GPULA_WARP=1`: no TDR if it hangs); #1664: float-exp (column 4) and float-exp LA (column 5) kernels, `FF_GPULA_DEEP=1` adds the bench scenes past 1e50; #1880: `FF_GPULA_EXACT=1` adds an exact view past 1e100 (the 340-digit centre near M(4,1) at 1e150 and 1e285), CPU against the float-exp LA kernel, with the LA table it ran (double or float-exp); #1882: also 1e320 and 1e600, past double's pixel scale ("fx px"); #1887: `FF_GPULA_MINIBROT=1` adds the antenna minibrots of periods 66 / 125 / 250 (~1e78 / 1e149 / 1e299), where the float-exp LA kernel returned the cap for every pixel before #1887 |
| `PgoDependenceProbe.cs` | `--pgodependenceprobe [W H]` | #2038 | Whether the CPU deep path needs .NET dynamic PGO: fresh child processes under default / `DOTNET_TieredPGO=0` / fully optimised (`DOTNET_TieredCompilation=0` + `DOTNET_ReadyToRun=0`), cold and warm frame of a DeepCostProbe scene (`FF_PGODEP_SCENE`, default `path 1e33`; `FF_PGODEP_REPS`); flags PGO off / default above 1.10 |
| `ApCostProbe.cs` | `--apcostprobe [W H]` | #2015 | Past 1e100 on a directory of .kfr scenes (`FF_APCOST_KFR`, `FF_APCOST_REPS`): frame time split into the reference orbit (length, µs per iteration, period found), BLA / SA tables and pixels, and threads kept busy; CPU, 1 sample per pixel |
| `BigFloatOpsProbe.cs` | `--bigfloatopsprobe` | #2033 | Cost of each BigFloat operation of one AP reference-orbit step (Mul, square, Add, ScaleB, ToOD, ToFloatExp) and of the whole step, serial and on `BigFloatStepWorkers`, at ~1.1k–33k bits |
| `Fp32ParityProbe.cs` | `--fp32parityprobe [W H]` | #2017 | The GPU deep routes (double, fp32, fp32 on the float-exp entry) against the CPU route on a directory of .kfr scenes (`FF_FP32_KFR`, e.g. the fractadyne bench-kit's): bit-identical colour, same iteration count, same in / out, iteration off by > 1 / > 10, smooth drift where the count matches. D3D11 (`FF_FP32_WARP=1`: software device) |
| `GpuDeferredReadbackProbe.cs` | `--gpudeferprobe` | #1332 | D3D11 deferred readback |
| `GpuDeepPeriodProbe.cs` | `--gpudeepperiodprobe` | #1805 | GPU deep kernels' periodicity check (plain, SA + BLA, float-exp, scaled fp32; D3D11 and Vulkan): off == on == diagnostic on the period-3 minibrot, catches, interior speed-up and exterior cost |
| `GpuGateProbe.cs` | `--gpugateprobe` | #1318 | D3D11 deep perturbation frame releasing the shared D3D gate between bands: a concurrent present's wait (median / worst) held vs lookahead 0 / 1 / 3, frame time, byte-identical frames |
| `GpuGroupShapeProbe.cs` | `--gpugroupshapeprobe` | #1339, #1377 | Tuned compute thread-group shapes (D3D11 and Vulkan): the 2D kernels, the relief raymarch and the froxel passes |
| `GpuPeriodicityProbe.cs` | `--gpuperiodicityprobe` | #1342 | D3D11 half of the GPU periodicity check |
| `GpuPixelMaskProbe.cs` | `--gpupixelmaskprobe [W H]` | #1394 | D3D11 half of the GPU pixel-mask check: masked dispatches (shallow base / colour, deep double / SA / fp32, both schedules) equal the full dispatch on marked pixels and leave unmarked ones untouched; masked vs full time |
| `GpuTimestampProbe.cs` | `--gputimestampprobe` | #1329 | D3D11 GPU timestamp queries |
| `GpuWavefrontProbe.cs` | `--gpuwavefrontprobe` | #1340, #1386, #1384 | D3D11 half of the wavefront schedule check; `deep` adds the seeded first frame (¼ estimate, schedule, time); `orbit` the orbit colour kernel, `deepkernels` the deep orbit and fp32 kernels |
| `BlackHoleGpuProbe.cs` | `--bhgpuprobe [W H] [eyebrow]` | #1972 | Every black hole trace backend (ILGPU, D3D11 hardware + WARP, Vulkan; fp64 and fp32) against the CPU trace: time per frame, interior / edge pixels whose fate differs; fails when a double kernel differs in the interior |
| `CosmicWebVolumeProbe.cs` | `--cwvolprobe [W H] [grids...]` | #2024 | Cosmic Web volume raymarch (D3D11 hardware + WARP, Vulkan) against the CPU reference on the Zeldovich preset at 128^3 / 256^3 grids: CIC deposit time and exact mass check, first / second frame times, pixels more than 2 units off; fails on a mass error or more than 0.5% of pixels off |
| `OidnProbe.cs` | `--oidnprobe [W H]`, `--oidnprobe live [W H]` | #1338, #1392 | Intel Open Image Denoise against the À-Trous denoiser and a full-sample reference; live: time to screen with the background denoise, cancel latency |
| `SolidRegionProbe.cs` | `--solidregionprobe [W H]` | #1343, #1394 | Solid-region skipping off / Auto / forced: times, fills, wrong fills; Mandelbrot and Julia / Multibrot views (`FF_SOLID_CASES=mandelbrot` / `escape` for one half); `FF_SOLID_GPU=1` runs GPU views on D3D11 (shallow and deep), `FF_SOLID_VIEW` filters by name; `FF_SOLID_CASES=pan`: #1330 pan frames (CalculatePanned alone), #1394 part 4 |
| `TemporalLightingProbe.cs` | `--temporallightingprobe [W H]` | #1337 | Temporal AO / shadow reuse on a moving camera |
| `VulkanPipelineCacheProbe.cs` | `--vkpipelinecacheprobe` | #1341 | The persisted `VkPipelineCache` across fresh Vulkan devices |
