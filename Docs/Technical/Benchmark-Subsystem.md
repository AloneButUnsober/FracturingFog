# Fracturing Fog — Benchmark Subsystem

This is the contributor / developer reference for Fracturing Fog's performance-measurement
tooling. For the end-user "how do I run a benchmark on my machine" walkthrough, see the
[Benchmarks Guide](../User/Benchmarks-Guide.md). For where the numbers feed back into the
optimisation roadmap, see the [Performance Development Plan](Performance-DevelopmentPlan.md).

The subsystem exists to answer one question repeatably: *did a change to the maths pipeline
make a frame faster or slower, and did it change allocations?* Every entry point is headless,
runs off a CLI flag on the main WinExe, and centres on a **fixed viewpoint ladder** so results
are comparable across commits.

> [!WARNING]
> **Windows-only today.** All three benchmark flags live on `FracturingFogCLD.csproj`, which is a
> `net10.0-windows` WinExe (it ProjectReferences the Windows-only D3D / Win / Audio.Win backends).
> That project **cannot build on Linux/macOS** — attempting it fails at restore with
> `NETSDK1073 … Microsoft.WindowsDesktop.App…` because the Windows Desktop targeting pack is
> absent (this is *not* a WinForms regression; WinForms was removed in #116 and no project sets
> `UseWindowsForms=true`). The cross-platform CLI leg, `FracturingFog.App` (`net10.0`), dispatches
> `--server`, `--batch`, `--ilgpu-probe`, the cluster flags, etc., but **does not** dispatch
> `--bench`, `--gentestbench`, or `--benchmark` — the `Benchmarks/` sources compile only into the
> WinExe. `BenchEntry.Run` is *written* to be portable (its Win32 console-attach is gated behind
> `OperatingSystem.IsWindows()`), but the App-side wiring never landed. **Run benchmarks on
> Windows.** Porting them is tracked under "Wiring the harness into FracturingFog.App" below.

---

## Three entry points, two engines

There are three CLI-flagged benchmark drivers. They split across two measurement engines:

| Flag                       | Engine                | Target under test                          | Output |
|----------------------------|-----------------------|--------------------------------------------|--------|
| `--bench`                  | **BenchmarkDotNet**   | `MandelbrotCalculator` (the shipping calc); with `--filter`, the 10 ILGPU 3D kernels (`*GpuCalculatorBench*`), the D3D11/Vulkan Mandelbrot kernels (`*MandelbrotGpuKernelBench*`) or the QD reference-orbit build (`*ReferenceOrbitBench*`, #1166) | BDN summary table + `BenchmarkDotNet.Artifacts/` |
| `--gentestbench`           | hand-rolled Stopwatch | `Generated.MandelbrotZ2Calculator` (CalcGen output) | console + `gentestbench.out` |
| `--benchmark --equation …` | hand-rolled Stopwatch | an **arbitrary** hot-compiled DSL equation | console + `benchmark.out` |

- Use `--bench` when you want **statistically rigorous** numbers (warmup detection, outlier
  removal, per-op allocation accounting) on the production calculator. This is the one that
  gates real perf regressions.
- Use `--gentestbench` / `--benchmark` for **quick relative** measurements while iterating on
  the CalculatorGen template or a user equation — a coarse ms/frame ladder, no statistics, but
  fast to run and trivial to diff.

All three are dispatched at the very top of [`Program.cs`](../../Program.cs) `Main`, before the
Avalonia shell boots, so they never pay UI startup cost.

---

## `--bench` — the BenchmarkDotNet harness

Source: [`Benchmarks/MandelbrotBench.cs`](../../Benchmarks/MandelbrotBench.cs). Package:
`BenchmarkDotNet` 0.15.8 (see `FracturingFogCLD.csproj`).

### The coverage matrix

The single `[Benchmark] Calculate()` method is swept across four parameter axes. BenchmarkDotNet
runs the Cartesian product:

| Axis (`[Params]`)   | Values                                                     | Cases |
|---------------------|------------------------------------------------------------|-------|
| `Width`             | `640` (→ 360h, fast) · `1920` (→ 1080h, representative)     | 2 |
| `Regime`            | `ShallowSP` · `MediumSP` · `DeepHP` · `DeepHPInPT`         | 4 |
| `Theme`             | `Hsv` · `PhongStone`                                        | 2 |
| `Accel`             | `true` · `false`                                            | 2 |

That is **2 × 4 × 2 × 2 = 32 benchmark cases** in a default run. Each case runs the full
`Calculate()` — iteration + auxiliary-buffer fill + colouring — so every stage is timed end to end.

> [!NOTE]
> `ThemeChoice.StripeAverage` exists in the enum (it exercises a different, orbit-aware scalar
> colour path) but is intentionally **left out of `[Params]`** to keep the default matrix at 32.
> Add it back to the `Theme` `[Params]` line if you are specifically profiling stripe-average.

### The precision regimes

The regimes are hand-chosen to drive each precision code path in `MandelbrotCalculator`, centred
on detail-rich coordinates (not empty in-set or fast-escape halo) so iteration counts approximate
real usage:

| Regime        | Centre / Zoom                                   | MaxIter | Path exercised |
|---------------|--------------------------------------------------|---------|----------------|
| `ShallowSP`   | `(-0.5, 0)` @ `1`                                | 512     | scalar SIMD **double** (`ComputeRowSP`) |
| `MediumSP`    | seahorse `(-0.7436…, 0.1318…)` @ `1e8`          | 2048    | still single-precision, higher iter cost |
| `DeepHP`      | same seahorse @ `1e15`                           | 4096    | DD perturbation, AVX2 4-lane (`ComputeRowHP`) |
| `DeepHPInPT`  | same seahorse @ `1e15`                           | 2048    | **fully inside** the AVX2 perturbation loop |

`DeepHPInPT` ("in perturbation") is the subtle one. At that centre the reference orbit escapes
near iteration ~3088. Capping `MaxIterations` at 2048 (below that) guarantees **every pixel
resolves inside the AVX2 perturbation loop** instead of some falling through to the scalar
double-double glitch fallback. This is the workload where **SA (series approximation)** and
**BLA (bilinear approximation)** acceleration are actually visible in wall-time — which is why
the `Accel` axis matters most here.

### The `Accel` axis

`Accel` maps to `MandelbrotCalculator.DisableAcceleration` (inverted). When `false`, the harness
sets `DisableAcceleration = true`, which nulls out the BLA table and the SA polynomial on the HP
paths (see `MandelbrotCalculator.cs` — the `bla = DisableAcceleration ? null : _blaTable` and
`sa = (DisableAcceleration || DisableSeriesApproximation) ? null : _sa` guards). This gives a
clean **"raw perturbation loop" baseline** to diff the accelerated path against.

> [!WARNING]
> `Accel` is **HP-only meaningful**. On the SP regimes (`ShallowSP`, `MediumSP`) SA/BLA never
> engage, so the `true`/`false` pair renders identical work and doubles the SP run time for no
> new signal. There is no CLI way to skip those cases (see "Narrowing the run" below) — to focus
> purely on deep-zoom acceleration, temporarily trim the `Regime` `[Params]` to the HP values.

### Toolchain: why in-process

The `Config` uses `InProcessEmitToolchain` rather than BenchmarkDotNet's default external-build
toolchain. This is a **workaround, not a preference**: BDN's default `CsProjGenerator` looks for a
`.csproj` whose filename matches the `AssemblyName` (`FracturingFog`), but the real project file is
`FracturingFogCLD.csproj`. The name mismatch breaks the external build, so we emit and run
in-process. The trade-off is losing per-benchmark process isolation — acceptable here because
`Calculate()` owns all of its own state and does not leak across cases.

The job is configured deliberately light for a heavy per-op workload:

```csharp
Job.Default
   .WithToolchain(InProcessEmitToolchain.Instance)
   .WithWarmupCount(2)       // 2 warmup iterations
   .WithIterationCount(5)    // 5 measured iterations
   .WithInvocationCount(1)   // 1 Calculate() per iteration (a frame is already ~ms–s)
   .WithUnrollFactor(1);
```

Plus `[MemoryDiagnoser]` (per-frame allocation columns), the custom `Footprint` column
(resident memory, see below) and `[Orderer(FastestToSlowest)]` (summary sorted by mean).

### Memory: what is and isn't measured

- **`MemoryDiagnoser` sees all managed allocations.** BDN 0.15.8 reads
  `GC.GetTotalAllocatedBytes(precise: true)`, which covers the small-object, large-object and
  pinned-object heaps on every thread, including the `Parallel` workers.
- **The calculator's buffers are managed.** `MandelbrotCalculator` allocates its 17 per-pixel
  buffers with `GC.AllocateUninitializedArray<T>(n, pinned: true)`, so they live on the pinned
  object heap. That's about 68 B per pixel: ~15 MB at 640x360 and ~135 MB at 1920x1080.
- **FF allocates no native heap memory on this path.** The codebase has no `NativeMemory.*` or
  `Marshal.AllocHGlobal` call sites, and every GPU toggle is off under `--bench`. That leaves no
  unmanaged memory to track (#1048).
- **Footprint (`Benchmarks/CaseMetrics.cs`).** Per-op `Allocated` can't show the buffers above,
  because they're allocated once in `[GlobalSetup]`. The Footprint column fills that gap.
  - **How it measures:** `Setup()` settles the heap with a forced full GC, constructs the
    calculator, configures it and runs one warm frame, then settles and measures again. Footprint is
    the difference.
  - **What it covers:** the per-pixel buffers plus lazily built deep-zoom state (reference orbit,
    SA/BLA tables). Measured on an i5-13420H: 14.9 MB (SP), 15.6 MB (`DeepHPInPT`) and 16.1 MB
    (`DeepHP`) at 640x360. The 134.5 MB SP figure at 1920x1080 matches the 68 B/px arithmetic
    exactly.
  - **`Accel` doesn't change it.** The tables are built either way; `DisableAcceleration` only
    skips *using* them. The deep-zoom extra scales with `MaxIterations` (reference-orbit length),
    not with `Accel`.
  - **Toolchain dependency:** the value reaches the summary through a static registry keyed by the
    case's parameters. That works only because the harness runs in-process. Under an out-of-process
    toolchain the column prints `-`.
- **GPU memory is covered by the GPU bench.** See "GPU calculator bench" below (#1162).

### Console attach (Windows WinExe quirk)

`FracturingFogCLD` is an `OutputType=WinExe` — it has **no console** by default, so
`Console.WriteLine` would silently no-op and you'd see nothing. `BenchEntry.Run` fixes this on
Windows via `AttachConsole(ATTACH_PARENT_PROCESS)` (attach to the launching terminal) falling
back to `AllocConsole` (pop a fresh console window), then rebinds `stdout`/`stderr` to the
attached handle. On Linux/macOS the streams are already wired to the launching terminal, so the
attach is gated behind `OperatingSystem.IsWindows()`. At the end it `FreeConsole`s. It waits on a
keypress **only if it had to allocate a fresh console window**, so a pop-up console doesn't vanish
before you read it. When attached to a terminal it returns straight to the prompt. (Before #1162 the
pause keyed off `Console.IsInputRedirected`. After `AttachConsole` that check reflects the console,
not the caller's redirected stdin, so scripted runs blocked forever on `ReadKey` and kept the exe
locked.)

### Argument pass-through

`BenchEntry.Run` forwards any args **after** `--bench` straight to BenchmarkDotNet's
`BenchmarkSwitcher`. So the whole BDN CLI is available — `--filter`, `--list`, `--job`, etc. With
no extra args it calls `BenchmarkRunner.Run<MandelbrotBench>()` directly (the switcher with empty
args just prints help and runs nothing, hence the explicit direct-run branch).

> [!NOTE]
> **Narrowing the run.** BenchmarkDotNet's `--filter` glob matches the fully-qualified benchmark
> **name** (`Namespace.Class.Method`), *not* `[Params]` values. So it selects a **class**:
> `*MandelbrotBench*` is the CPU matrix, `*GpuCalculatorBench*` the 3D GPU bench,
> `*MandelbrotGpuKernelBench*` the D3D11/Vulkan bench, `*ReferenceOrbitBench*` the QD reference-orbit
> build (#1166), `*` all four. Within a
> class it is all-or-nothing, because each class has a single `[Benchmark]` method. To run a
> subset of cases, temporarily edit the relevant `[Params]` array (e.g. drop `Width` to
> `[Params(640)]`) and rebuild. `--list flat` / `--list tree` enumerate the cases without running.

### GPU calculator bench (`GpuCalculatorBench`, #1162)

Source: [`Benchmarks/GpuCalculatorBench.cs`](../../Benchmarks/GpuCalculatorBench.cs). It's opt-in;
the default `--bench` run stays CPU-only:

```powershell
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --filter "*GpuCalculatorBench*"
```

It times the **production** 3D calculators with `Lighting.UseGpuRender = true`, so the frame is
what the app renders: real parameter construction, kernel launch, `Synchronize`, device-to-host
copy. Matrix: 17 cases per width (`Mandelbulb`, `Mandelbox`, KIFS `Menger` / `Sierpinski` /
`Octahedron` / `Dodecahedron` / `MandelboxRot` (the last three CPU-only before #1173-D), `QJulia`,
`Coquaternion` (no GPU path before #1173-G), `UserBulb` (sandbox `z^8 + c`, analytic DE, on the shared-shading kernel since #1173-A), `QMandel`, `QMandelDual` (dual-orbit colour, CPU-only before #1173-F), `Kleinian`,
`KleinianNecklace` (9-sphere necklace + rotation + last-generator colour, CPU-only before #880), `Bicomplex`; `BicomplexR`, the R slice axis at sliceW 0.4, which
rendered on the CPU before #1173-C; and `MandelboxHdri`, Mandelbox under a synthetic 512x256
HDRI with IBL ambient and reflections, which used the gradient sky before #1173-B)
x {640x360, 1920x1080} = 34 cases. Same in-process job as
`MandelbrotBench`.

**It refuses to time a CPU fallback.** In the app, a missing device or a failed kernel load falls
through to the CPU ShadingPipeline silently. The bench guards against measuring that:

- Every GPU frame allocates its buffers through `GpuMemoryStats.Allocate1D`
  (`Engine/Calculators/Gpu/GpuMemoryStats.cs`), a thin counting wrapper used by every per-frame
  allocation site in the 10 ILGPU calculators. A CPU frame allocates nothing there.
- `Setup()` renders a JIT frame and then a steady-state frame. If the steady-state frame added no
  device bytes, it throws, and BDN reports the case as failed (`NA`) instead of a number.
- The exception names the inner GPU calculator's state: `=null (Render never called)` means a
  calculator-side gate kept the frame on the CPU, and `LastError='…'` means the kernel failed on
  this device.
- `No GPU accelerator available (GPU accelerator init failed: out of memory)` with plenty of free
  VRAM usually means a **Remote Desktop session**: a GeForce card under WDDM can't create a CUDA
  context there, and the driver reports it as out of memory (seen on the GT 710, driver 456.71).
  Run GPU benches and the S749 CUDA tests from the console session.
  (Confirmed 2026-10-07: the same machine at the console creates the CUDA context and runs
  all S749 cases; D3D11 and Vulkan reach the card either way.)

That guard is what exposed #1164: on CUDA, all 8 kernels failed to JIT because the context lacked
`ILGPU.Algorithms`. Since #1164 they JIT, and every kernel context comes from
`GpuAcceleratorHost.CreateContext()`, which calls `EnableAlgorithms()`.

**Mandelbulb runs last.** A CUDA launch failure poisons the whole process, and
`GpuAcceleratorHost` then latches GPU 3D off for the session (`ReportRenderFault`). Every case
after a fault would report `NA` with "GPU device faulted … disabled for this session". Mandelbulb
was the family that faulted (#1169), so the `GpuFamily` enum declares it last to keep the other
cases measurable. The order has to live in the enum because BenchmarkDotNet runs param values in
**value order** and ignores the `[Params]` order. `Family` is also declared before `Width`, making
it the outer axis, so both Mandelbulb cases run after every other case.

**Long frames are tiled.** A single long raymarch launch on a slow GPU outlasts the Windows GPU
watchdog (TDR). The driver resets the device, which is the same sticky fault and the same latch.
The kernels therefore go out through `GpuTiledDispatch`
(`Engine/Calculators/Gpu/GpuTiledDispatch.cs`). Every frame over 64 pixels starts with a
~64-pixel probe launch, and later launches are sized to about 0.25 s each (#1170, #1169). The
timed frame includes every launch and its `Synchronize`, so the number is still one whole frame.

**Too-slow kernels give up.** #1169 turned out to be the watchdog too. Mandelbulb's DE calls
software fp64 `acos` / `atan2` / `pow` / `sin` / `cos` (ILGPU.Algorithms) on every iteration,
which costs ~5 ms per pixel on a GT 710 (1/24-rate fp64). Even a 96x72 frame in one launch
outlasted the watchdog. If three launches in a row each measure more than 1 ms per pixel, `GpuTiledDispatch` returns
`TooSlow`. One stalled launch behind another GPU client can't trip it. The GPU calculator then
latches "GPU too slow for … on this device", per family and per device for the rest of the
process, and the frame renders on the CPU. The bench reports `NA` with that message instead of poisoning the device for the cases
after it.

Extra columns (`Benchmarks/CaseMetrics.cs`):

- **DeviceAlloc/op**: ILGPU device bytes allocated per frame, measured on the steady-state frame.
  On every frame the kernels allocate an output buffer, a palette LUT, and a depth buffer (one
  element unless froxel compositing is on). So this is per-frame device churn, not resident VRAM.
- **Device**: the accelerator that ran the case (`AcceleratorType` + name). GPU numbers mean
  nothing without it.

**Device selection** is the app's: `GpuAcceleratorHost.TryAcquire` picks an fp64-capable non-CPU
device. If none exists, Setup throws `No GPU accelerator available …`. Intel Xe iGPUs, for example,
expose no OpenCL fp64.

### Mandelbrot GPU kernel bench (`MandelbrotGpuKernelBench`, #1162)

Source: [`Benchmarks/MandelbrotGpuKernelBench.cs`](../../Benchmarks/MandelbrotGpuKernelBench.cs).
It's opt-in:

```powershell
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --filter "*MandelbrotGpuKernelBench*"
```

It times `MandelbrotCalculator` with a GPU `IGpuKernel` attached. Matrix (16 cases):

| Axis      | Values |
|-----------|--------|
| `Backend` | `D3D11`: `Rendering.D3D` `MandelbrotGpuKernel` on the default hardware adapter. `Vulkan`: `VulkanComputeKernel.TryCreateWithOwnContext()`. |
| `Path`    | `SpShallow` (`UseGpuCompute`, zoom 1, 512 iter) · `SpZoom1e4` (`UseGpuCompute`, seahorse at `MaxGpuZoom` = 1e4, 2048 iter) · `PerturbInPT` (`UseGpuPerturbation`, seahorse at 1e15, 2048 iter) · `PerturbDeep` (same, 4096 iter) |
| `Width`   | 640x360 · 1920x1080 |

The coordinates match `MandelbrotBench`, so a GPU row can be read against its CPU twin:
`PerturbInPT` vs `DeepHPInPT`, and `PerturbDeep` vs `DeepHP`.

- **Fallback guard.** After the warm frame, Setup requires
  `MandelbrotCalculator.LastFrameUsedGpuCompute` (new, #1162) or the existing
  `LastFrameUsedGpuPerturbation` to be set; otherwise it throws. The perturbation paths also
  require `IGpuKernel.SupportsPerturbation` (fp64 shader ops) and say so when it's missing.
- **`UseGpuPerturbation` is process-wide static.** Setup sets it per case and Cleanup restores it.
- **`Resident` column (#1166), not per-op churn.** These kernels keep persistent, frame-sized
  device buffers rather than allocating per frame, so there's nothing per op to count. `Resident`
  is what they hold after the warm frame:
  - **D3D11:** DXGI's per-process usage (`IDXGIAdapter3.QueryVideoMemoryInfo` `CurrentUsage`).
    It is the delta from just before the kernel is created to after the warm frame, with the
    context flushed. It is split into the local segment (VRAM) and the non-local one (shared
    system memory, e.g. the readback staging), and includes the driver's own overhead.
  - **Vulkan:** the kernel's own count of the buffer memory it allocated
    (`VulkanComputeKernel.ResidentBytes`, every buffer goes through `AllocBuffer` / `FreeBuffer`),
    plus the part on a device-local heap.

  The `Device` column records the adapter.
- **`NA` on perturbation rows can be by design.** `TryRunGpuPerturbation` estimates the frame
  (first band's time x band count). If that exceeds its 3 s budget, it throws
  `GPU-PERTURB-TOO-SLOW` and **switches `UseGpuPerturbation` off for the session**, so the app
  renders that view on the CPU too. The bench detects the flipped static and says so in the failure
  message. Set `FF_GPU_PERTURB_DEBUG=1` to get every gate input and failure in
  `%TEMP%\ff_gpu_perturb_86.log`.

Excerpt from a real run (GeForce GT 710, a low-end Kepler card with 1/24-rate fp64; 2026-10-06):

```text
| Backend | Path        | Width | Mean         | Device                                       |
|-------- |------------ |------ |-------------:|--------------------------------------------- |
| D3D11   | SpShallow   | 640   |     6.800 ms | D3D11 NVIDIA GeForce GT 710                  |
| D3D11   | SpShallow   | 1920  |    43.947 ms | D3D11 NVIDIA GeForce GT 710                  |
| Vulkan  | SpShallow   | 1920  |   889.062 ms | Vulkan compute (DiscreteGpu: GeForce GT 710) |  ← before #1173-L
| D3D11   | PerturbInPT | 640   | 1,494.751 ms | D3D11 NVIDIA GeForce GT 710                  |
| D3D11   | PerturbInPT | 1920  |           NA | -   (too-slow guard: ~200 ms x 108 bands > 3 s) |
```

How to read that run:

- **The card is slower than the CPU here.** The i5-13420H CPU does `ShallowSP` 1080p in ~23 ms and
  `DeepHPInPT` 640 (Accel on) in ~28 ms, against 44 ms and 1.49 s on the GPU. That's expected
  for a card with crippled fp64. Run this on the hardware you care about.
- **Vulkan SP was ~20x slower than D3D11 SP on the same card** (889 vs 44 ms at 1080p). That was
  the readback, not the shader: the result buffers lived in uncached write-combined host memory
  (~780 ms to read; dispatch ~22 ms). Fixed in #1173-L by reading back from host-cached memory.
  Re-run (GT 710, 2026-10-07, over Remote Desktop — D3D11 and Vulkan still reach the card there):

  | Backend | Path        | 640        | 1920      |
  |-------- |------------ |-----------:|----------:|
  | D3D11   | SpShallow   |   7.6 ms   |  45.1 ms  |
  | Vulkan  | SpShallow   |   5.2 ms   |  35.9 ms  |
  | D3D11   | SpZoom1e4   |  20.6 ms   | 153.6 ms  |
  | Vulkan  | SpZoom1e4   |  18.6 ms   | 145.5 ms  |
  | D3D11   | PerturbInPT | 1,491 ms   | NA (too-slow guard) |
  | Vulkan  | PerturbInPT | 1,627 ms   | NA (too-slow guard) |
  | D3D11   | PerturbDeep | 2,467 ms   | NA |
  | Vulkan  | PerturbDeep | 2,706 ms   | NA |

  Vulkan is now a drop-in for D3D11 on this card (a little faster on SP, ~9% slower on the
  fp64-bound perturbation).

Resident memory (#1166), GT 710, 2026-10-08:

| Backend | Path        | 640x360                      | 1920x1080                      |
|-------- |------------ |----------------------------- |------------------------------- |
| D3D11   | SpShallow   | 6.5 MB VRAM + 6.2 MB shared  | 55.6 MB VRAM + 55.5 MB shared  |
| D3D11   | PerturbInPT | 5.9 MB VRAM + 7.2 MB shared  | 48.0 MB VRAM + 56.8 MB shared  |
| Vulkan  | SpShallow   | 7.0 MB (0.0 MB device-local) | 63.3 MB (0.0 MB device-local)  |
| Vulkan  | PerturbInPT | 7.3 MB (0.0 MB device-local) | 63.6 MB (0.0 MB device-local)  |

- **D3D11:** ~28 B/px on the card plus ~28 B/px of shared staging. That is about one copy of the
  frame's per-pixel outputs on each side (iteration, smooth, final z/dz, colour, trap: 32 B/px
  requested).
- **Vulkan:** 32 B/px, all of it in **host** memory. Its memory-type choice
  (`VulkanHostMemory.FindType`) picks host-visible types for every buffer, the CPU-written ones
  too, so the shader reads and writes system RAM over PCIe on this card. #1173-L made the readback
  fast; nothing here says the kernel is slower for it, but a device-local layout with a copy
  before readback is the conventional design.
- The perturbation rows add the reference orbit (16 B per iteration) and the SA / BLA tables.

### Reference-orbit bench (`ReferenceOrbitBench`, #1166)

Source: [`Benchmarks/ReferenceOrbitBench.cs`](../../Benchmarks/ReferenceOrbitBench.cs).

```powershell
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --filter "*ReferenceOrbitBench*"
```

It times one uncached QD reference-orbit build:
- `Builder`: `Cpu` is `ComputeReferenceOrbitQD`; `Gpu` is `MandelbrotRefOrbitGpu`, ILGPU, on with
  `UseGpuReferenceOrbit`.
- `Length`: 10K, 100K or 1M iterations.

Design points:
- **Each op misses the orbit cache.** The orbit is cached by centre, so each op flips the centre's
  lowest QD limb (1e-60 / 2e-60) before calling `MandelbrotCalculator.BuildReferenceOrbit()`. That
  is the path a deep-zoom frame takes, without rendering.
- **The centre is c = -1.9 at zoom 1e30 (QD tier).** Real c in [-2, 0.25] never escapes, so
  `Length` is the real orbit length, and -1.9 is chaotic, so the orbit never settles.
- **Two centres that would distort the CPU numbers:**
  - A centre whose low QD limbs are exactly zero costs the CPU ~3 ms extra over the first ~10K
    iterations (450 against a steady ~130 ns per iteration).
  - An interior c (a converging orbit) is slower still early on (~1.2 µs per iteration).

  Neither looks like a real deep-zoom centre, which carries all its limbs.
- **Fallback guard.** `Gpu` requires `MandelbrotCalculator.LastFrameBuiltGpuReferenceOrbit` (new)
  after the warm build. It also refuses ILGPU's CPU accelerator, which is the kernel's last-resort
  device. The `Device` column records what built the orbit.
- **Iteration time ~250 ms, not a fixed single invocation.** A lone 5 ms op after BenchmarkDotNet's
  pause between iterations started on a cold core clock, and read 4.6 ms for a 1.3 ms build. The
  15 s GPU case still runs once per iteration, under a 30-minute in-process timeout.

GeForce GT 710 (CUDA) vs i5-13420H, 2026-10-08:

| Builder | 10K      | 100K     | 1M       | Device                |
|-------- |---------:|---------:|---------:|---------------------- |
| Cpu     | 1.5 ms   | 12.6 ms  | 127 ms   | CPU QD                |
| Gpu     | 159 ms   | 1,574 ms | 15,727 ms | Cuda — GeForce GT 710 |

- **Linear, ~126 ns vs ~15.7 µs per iteration.** The GPU is ~125x slower on this card: one
  sequential thread of QD on 1/24-rate fp64. That is why `UseGpuReferenceOrbit` is off by default.
  Run it on the hardware you care about.
- **#1164 does not apply.** CUDA builds and runs the QD kernel on the GT 710.
- **Found by the bench: the watchdog.** The kernel ran the whole orbit in one launch. At 1M
  iterations the OS killed it after 3.5 s ("unspecified launch failure", a driver reset). The orbit
  now runs in launches sized from the measured time toward 100 ms, each resuming from the last slot
  written. `S1166ReferenceOrbitTests` pins it:
  - any launch size, one iteration included, gives the single-launch orbit limb for limb, and
    matches the CPU QD orbit to round-off early on;
  - a 2000-iteration orbit at 250 per launch takes 8 launches.
- **GPU vs CPU limbs differ in the last bit.** The GPU's split TwoProduct and the CPU's FMA differ
  in the last bit of the lowest limb, as `--gpurefprobe` already reported; chaos amplifies that
  along the orbit.

---

## `--gentestbench` — CalcGen fixed calculator

Source: inline in [`Program.cs`](../../Program.cs) (search `--gentestbench`). Times the
**generated** `MandelbrotZ2Calculator` (CalculatorGen output, with `UsePerturbation`, `UseBla`,
`UseSa` all on) at a 640×480 resolution across a four-rung ladder:

| Label      | Centre           | Zoom  | Iter |
|------------|------------------|-------|------|
| `default`  | `(-0.5, 0)`      | `1`   | 256  |
| `shallow`  | `(-0.75, 0.1)`   | `20`  | 256  |
| `mid-1e3`  | `(-0.745, 0.113)`| `1e3` | 1024 |
| `deep-1e6` | `(-0.745, 0.113)`| `1e6` | 2048 |

One warm-up call, then 3 timed frames; reports `ElapsedMilliseconds / frames` as
`ms/frame`. Writes the same table to `gentestbench.out` next to the exe. Purpose: a fast, no-stats
sanity check on perf changes to the **CalcGen template**.

## `--benchmark` — arbitrary equation ladder

Source: `BenchmarkEquation(string[])` in [`Program.cs`](../../Program.cs). Same shape as
`--gentestbench` but the equation is **user-supplied and hot-compiled** at run time, so any
Phase-D perf change (SA orders, BLA hierarchy, cached SA tables) can be measured against an
unchanging equation baseline.

```text
--benchmark --equation "<expr>" [--name N] [--width W] [--height H] [--frames F]
```

| Flag            | Alias | Default     | Meaning |
|-----------------|-------|-------------|---------|
| `--equation`    | `-e`  | *(required)*| The DSL expression to compile and time. |
| `--name`        | `-n`  | `UserBench` | Label used in the output header + emitted calculator name. |
| `--width`       |       | `640`       | Render width. |
| `--height`      |       | `480`       | Render height. |
| `--frames`      |       | `3`         | Timed frames per rung (after one warm-up). |

Its ladder adds a fifth, deeper rung over `--gentestbench`:

| Label      | Zoom  | Iter |
|------------|-------|------|
| `default`  | `1`   | 256  |
| `shallow`  | `20`  | 256  |
| `mid-1e3`  | `1e3` | 1024 |
| `deep-1e6` | `1e6` | 2048 |
| `deep-1e9` | `1e9` | 4096 |

### The force-load gotcha

Before compiling, `BenchmarkEquation` **force-loads** a fixed set of assemblies (`ILGPU`,
`Parallel`, `Avx2`, `Avx512F`, `HsvPalette`, `IFractalCalculator`) by touching each
`typeof(T).Assembly.Location`. This is load-bearing: `CalculatorGenHotLoad` harvests its Roslyn
reference set from `AppDomain.GetAssemblies()`, and assemblies the loader has not touched yet do
not appear there. The interactive UserEquation dialog avoids this because the UI path has already
JIT-touched those assemblies; the headless `--benchmark` path has not, so it must pull them in
manually or the compile fails with missing references. If you add a new dependency the generated
calculators need, add its `typeof` to that `forceLoad` array.

On compile failure the driver prints `hot.Error` and returns exit code 1.

---

## How to read the output

### BenchmarkDotNet summary (`--bench`)

BDN prints an environment block (host CPU, .NET runtime, GC mode) then a table, one row per case,
sorted fastest→slowest. Excerpt from a real run (i5-13420H, .NET 10.0.11, 2026-10-06):

```text
| Method    | Width | Regime     | Theme      | Accel | Mean         | Error       | StdDev      | Footprint | Allocated |
|---------- |------ |----------- |----------- |------ |-------------:|------------:|------------:|----------:|----------:|
| Calculate | 640   | ShallowSP  | Hsv        | False |     4.149 ms |   0.4760 ms |   0.0737 ms |   14.9 MB |    6.8 KB |
| Calculate | 1920  | ShallowSP  | Hsv        | False |    23.299 ms |   2.1100 ms |   0.5480 ms |  134.5 MB |   9.76 KB |
| Calculate | 1920  | DeepHPInPT | Hsv        | True  |   253.409 ms |  16.8311 ms |   4.3710 ms |  135.2 MB |   13.8 KB |
| Calculate | 1920  | DeepHPInPT | Hsv        | False | 2,989.251 ms |  69.9645 ms |  18.1695 ms |  135.2 MB |  13.52 KB |
| Calculate | 1920  | DeepHP     | Hsv        | True  | 3,982.006 ms | 114.3847 ms |  29.7053 ms |  135.6 MB |   14.3 KB |
| Calculate | 1920  | DeepHP     | Hsv        | False | 6,828.540 ms | 929.8048 ms | 241.4674 ms |  135.6 MB |  19.13 KB |
```

On this run, SA+BLA cut the fully-in-perturbation 1080p frame from ~2.99 s to ~0.25 s (~12x). On
`DeepHP` the gain is ~1.7x, because pixels that spill to the scalar DD fallback get no
acceleration. No `Gen0` column appears because no case triggered a collection; BDN hides all-zero
GC columns.

Column meanings:

- **Mean** — average time for one `Calculate()`. This is the headline number.
- **Error** — half of the 99.9% confidence interval; treat differences smaller than this as noise.
- **StdDev** — spread across the 5 iterations; a large StdDev relative to Mean means the machine
  was noisy (background load, thermal throttling) — rerun on a quiet box.
- **Gen0 / Gen1 / Gen2** — GC collections per 1000 ops (from `MemoryDiagnoser`).
- **Allocated** — managed bytes allocated **per frame**. This is the regression tripwire: a hot
  path that starts churning buffers on resize shows up here even if Mean barely moves.
- **Footprint** — managed memory the calculator **holds** after construction plus one warm frame.
  It is resident size, not per-frame churn. If it grows, a buffer or table was added or enlarged;
  that costs memory on every open view even when Mean is unchanged.

Interpreting the matrix:

- Compare **`Accel=True` vs `Accel=False`** within the same `Regime`/`Width`/`Theme` to read the
  SA+BLA speed-up. Expect a meaningful gap on `DeepHP`/`DeepHPInPT`, ~none on the SP regimes.
- Compare **`DeepHP` vs `DeepHPInPT`** to see the cost of the scalar DD glitch fallback (DeepHP
  lets some pixels spill out of the AVX2 loop; DeepHPInPT does not).
- Compare **`Hsv` vs `PhongStone`** to isolate colouring cost — same iteration work, different
  shader; the delta is pure theme dispatch + lighting.
- Compare **`640` vs `1920`** for scaling; it should be roughly pixel-count-linear (×9) once
  fixed per-frame overhead is amortised — sub-linear means overhead dominates at 640.

BDN also writes machine-readable artifacts under `BenchmarkDotNet.Artifacts/results/` (CSV, JSON,
Markdown, and an HTML report) beside the working directory — commit or diff those to track perf
over time.

### Stopwatch ladders (`--gentestbench`, `--benchmark`)

```text
CalcGen benchmark — UserBench (equation: z^2 + c)
  default      zoom=1          iter=  256 →    18 ms/frame
  shallow      zoom=20         iter=  256 →    21 ms/frame
  mid-1e3      zoom=1e+03      iter= 1024 →    74 ms/frame
  deep-1e6     zoom=1e+06      iter= 2048 →  156 ms/frame
  deep-1e9     zoom=1e+09      iter= 4096 →  402 ms/frame
```

This is **integer** `ms/frame` (`ElapsedMilliseconds / frames`) — coarse, no error bars, no
allocation data. Read it only for **relative** movement between two builds of the *same* rung; do
not compare absolute numbers against a `--bench` Mean (different resolution, no statistical
rigour, integer truncation). Sub-millisecond rungs floor to `0 ms` — bump `--frames` or resolution
if you need to resolve them. Output is duplicated to `gentestbench.out` / `benchmark.out` beside
the exe.

---

## Running them

See the [Benchmarks Guide](../User/Benchmarks-Guide.md) for the copy-paste commands and machine
hygiene. **Run these on Windows** — the flags live on the Windows-only WinExe (see the platform
warning at the top). In short, from the repo root:

```powershell
# Full 32-case statistical sweep (minutes; do it on a Release build)
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench

# List every case without running (then trim [Params] to run a subset)
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --list flat

# GPU 3D kernels (16 cases; needs an fp64 GPU, see the GPU calculator bench section)
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --filter "*GpuCalculatorBench*"

# Mandelbrot on the D3D11 / Vulkan kernels (16 cases)
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --filter "*MandelbrotGpuKernelBench*"

# CalcGen template quick-check
dotnet run -c Release --project FracturingFogCLD.csproj -- --gentestbench

# Arbitrary equation ladder
dotnet run -c Release --project FracturingFogCLD.csproj -- --benchmark --equation "z^2 + c" --name classic
```

> [!WARNING]
> Always benchmark a **Release** build (`-c Release`). A Debug build disables JIT optimisations and
> SIMD intrinsics inlining, so its numbers are meaningless for perf work.

---

## Extending the subsystem

- **New precision path / calculator flag?** Add a `PrecisionRegime` enum value + a `case` in
  `Setup()` with the coordinates that force that path, and document which code path it targets in
  the comment block (mirror `DeepHPInPT`'s note about `MaxIter < refLen`).
- **New colour path worth timing?** Add a `ThemeChoice` and construct it in `Setup()`; add it to
  the `Theme` `[Params]` only if you want it in the *default* matrix (mind the case-count blow-up).
- **New CalcGen dependency?** If a generated calculator needs an assembly at runtime, add its
  `typeof` to the `forceLoad` array in `BenchmarkEquation` or the headless hot-compile will fail to
  resolve references.
- **Keep the ladders in sync.** `--gentestbench` and `--benchmark` share a coordinate ladder by
  convention; if you change one rung's centre/zoom, change both so their outputs stay comparable.

---

## Wiring the harness into FracturingFog.App (Linux/macOS)

The harness is Windows-only today only because of *packaging*, not portability of the code — the
maths pipeline under test (`MandelbrotCalculator`, the generated calculators, SA/BLA) already
builds and runs on the `net10.0` leg. To make `--bench` work on Linux/macOS:

1. **Compile the benchmark sources into the portable leg.** `Benchmarks/MandelbrotBench.cs` is
   currently picked up only by `FracturingFogCLD.csproj`. Add it (and a `BenchmarkDotNet`
   `PackageReference`) to `FracturingFog.App.csproj`, or factor the harness into a small shared
   project both exes reference. Keep the Win32 `AttachConsole`/`AllocConsole` P/Invokes behind the
   existing `OperatingSystem.IsWindows()` gate — on Linux/macOS stdout is already wired to the
   launching terminal, so `BenchEntry` needs no console attach there.
2. **Dispatch the flags in `FracturingFog.App/Program.cs`.** Add the `--bench` / `--gentestbench`
   / `--benchmark` branches (mirroring `Program.cs` in the WinExe) ahead of the Avalonia boot,
   next to the existing `--server` / `--batch` / `--ilgpu-probe` dispatch.
3. **Invoke with the framework pinned.** `FracturingFog.App` multi-targets
   `net10.0;net10.0-windows`, so `dotnet run` must be told which TFM to use on a non-Windows host:

   ```bash
   dotnet run -c Release --project FracturingFog.App/FracturingFog.App.csproj -f net10.0 -- --bench
   ```

Caveats to verify when porting: the `DeepHP*` regimes depend on **AVX2** (and the SP paths may use
**AVX-512**) — on an ARM host (Apple Silicon, aarch64 Linux) those intrinsics are unavailable and
the calculator falls back to its scalar path, so absolute numbers are not comparable to an x86 run.
The `InProcessEmitToolchain` and `MemoryDiagnoser` are cross-platform and need no change.

---

## See also

- [Benchmarks Guide](../User/Benchmarks-Guide.md) — the user-facing how-to.
- [Performance Development Plan](Performance-DevelopmentPlan.md) — where the numbers drive the roadmap.
- [Deep-Zoom Perturbation](../Deep-Zoom-Perturbation.md) — the SA/BLA/perturbation maths the HP
  regimes exercise.
- [CalculatorGen Architecture](CalculatorGen-Architecture.md) — what `--gentestbench` /
  `--benchmark` actually compile.
