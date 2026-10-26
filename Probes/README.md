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
| `AdaptiveAaProbe.cs` | `--adaptiveaaprobe [threshold tolerance]` | #1344, #1382 | Adaptive anti-aliasing against full supersampling (CPU), Mandelbrot views and the ten 3D raymarchers: samples computed, wall times, error |
| `D3DBuddhaProbe.cs` | `--d3dbuddhaprobe` | #838 | D3D11 half of the GPU Buddhabrot parity check |
| `D3DDeepOrbitProbe.cs` | `--d3ddeeporbitprobe` | #607 | D3D11 half of the deep-zoom orbit parity check |
| `D3DDeviceLossProbe.cs` | `--d3ddevicelossprobe` | #1045 | The D3D11 presenter recovers from a device loss |
| `D3DEscapeFamilyProbe.cs` | `--d3dfamilyprobe` | #1173 | D3D11 half of the Multibrot / Phoenix / domain-warp parity check |
| `D3DOrbitTrapProbe.cs` | `--d3dorbittrapprobe` | #1173 | D3D11 half of the orbit-trap parity check |
| `D3DPerturbProbe.cs` | `--d3dpturbcalc` | #82 | D3D11 deep-zoom perturbation parity (twin of `--vulkanpturbcalc`) |
| `DistanceFieldProbe.cs` | `--distancefieldprobe [W H]` | #1336, #1390 | 3D raymarch with and without the baked distance-field cache (KIFS, User Bulb, Mandelbulb, Mandelbox; the moving frames' level; `FF_DF_CASES` filters) |
| `DynamicResolutionProbe.cs` | `--dynresprobe` | #1333 | Drag responsiveness through a real host, dynamic preview resolution off and on |
| `GpuBandProbe.cs` | `--gpubandprobe` | #1334 | Deep perturbation frames in TDR row bands (D3D11 and Vulkan) |
| `GpuDeferredReadbackProbe.cs` | `--gpudeferprobe` | #1332 | D3D11 deferred readback |
| `GpuGroupShapeProbe.cs` | `--gpugroupshapeprobe` | #1339, #1377 | Tuned compute thread-group shapes (D3D11 and Vulkan): the 2D kernels, the relief raymarch and the froxel passes |
| `GpuPeriodicityProbe.cs` | `--gpuperiodicityprobe` | #1342 | D3D11 half of the GPU periodicity check |
| `GpuTimestampProbe.cs` | `--gputimestampprobe` | #1329 | D3D11 GPU timestamp queries |
| `GpuWavefrontProbe.cs` | `--gpuwavefrontprobe` | #1340, #1386, #1384 | D3D11 half of the wavefront schedule check; `deep` adds the seeded first frame (¼ estimate, schedule, time); `orbit` the orbit colour kernel, `deepkernels` the deep orbit and fp32 kernels |
| `OidnProbe.cs` | `--oidnprobe [W H]`, `--oidnprobe live [W H]` | #1338, #1392 | Intel Open Image Denoise against the À-Trous denoiser and a full-sample reference; live: time to screen with the background denoise, cancel latency |
| `SolidRegionProbe.cs` | `--solidregionprobe [W H]` | #1343, #1394 | Solid-region skipping off / Auto / forced: times, fills, wrong fills; Mandelbrot and Julia / Multibrot views (`FF_SOLID_CASES=mandelbrot` / `escape` for one half) |
| `TemporalLightingProbe.cs` | `--temporallightingprobe [W H]` | #1337 | Temporal AO / shadow reuse on a moving camera |
| `VulkanPipelineCacheProbe.cs` | `--vkpipelinecacheprobe` | #1341 | The persisted `VkPipelineCache` across fresh Vulkan devices |
