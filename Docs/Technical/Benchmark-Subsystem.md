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
| `--bench`                  | **BenchmarkDotNet**   | `MandelbrotCalculator` (the shipping calc) | BDN summary table + `BenchmarkDotNet.Artifacts/` |
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

Plus `[MemoryDiagnoser]` (per-frame allocation columns) and
`[Orderer(FastestToSlowest)]` (summary sorted by mean).

### Console attach (Windows WinExe quirk)

`FracturingFogCLD` is an `OutputType=WinExe` — it has **no console** by default, so
`Console.WriteLine` would silently no-op and you'd see nothing. `BenchEntry.Run` fixes this on
Windows via `AttachConsole(ATTACH_PARENT_PROCESS)` (attach to the launching terminal) falling
back to `AllocConsole` (pop a fresh console window), then rebinds `stdout`/`stderr` to the
attached handle. On Linux/macOS the streams are already wired to the launching terminal, so the
attach is gated behind `OperatingSystem.IsWindows()`. At the end it `FreeConsole`s and, if input
is not redirected, waits on a keypress so a pop-up console does not vanish before you read it.

### Argument pass-through

`BenchEntry.Run` forwards any args **after** `--bench` straight to BenchmarkDotNet's
`BenchmarkSwitcher`. So the whole BDN CLI is available — `--filter`, `--list`, `--job`, etc. With
no extra args it calls `BenchmarkRunner.Run<MandelbrotBench>()` directly (the switcher with empty
args just prints help and runs nothing, hence the explicit direct-run branch).

> [!NOTE]
> **Narrowing the run.** BenchmarkDotNet's `--filter` glob matches the fully-qualified benchmark
> **name** (`*MandelbrotBench*`), *not* `[Params]` values — there is only one `[Benchmark]` method
> here, so `--filter` is all-or-nothing and cannot select a single regime/theme/width. To run a
> subset, temporarily edit the relevant `[Params]` array in `MandelbrotBench.cs` (e.g. drop
> `Width` to `[Params(640)]`, or `Regime` to the two HP values) and rebuild. `--list flat` /
> `--list tree` enumerate the cases without running.

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
sorted fastest→slowest:

```text
| Method    | Width | Regime      | Theme      | Accel | Mean      | Error    | StdDev   | Gen0   | Allocated |
|---------- |------ |------------ |----------- |------ |----------:|---------:|---------:|-------:|----------:|
| Calculate | 640   | ShallowSP   | Hsv        | True  |  12.34 ms | 0.21 ms  | 0.19 ms  |      - |   1.2 KB  |
| Calculate | 1920  | DeepHPInPT  | PhongStone | False | 842.10 ms | 9.88 ms  | 8.71 ms  |   3.00 |  48.9 KB  |
```

Column meanings:

- **Mean** — average time for one `Calculate()`. This is the headline number.
- **Error** — half of the 99.9% confidence interval; treat differences smaller than this as noise.
- **StdDev** — spread across the 5 iterations; a large StdDev relative to Mean means the machine
  was noisy (background load, thermal throttling) — rerun on a quiet box.
- **Gen0 / Gen1 / Gen2** — GC collections per 1000 ops (from `MemoryDiagnoser`).
- **Allocated** — managed bytes allocated **per frame**. This is the regression tripwire: a hot
  path that starts churning buffers on resize shows up here even if Mean barely moves.

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
