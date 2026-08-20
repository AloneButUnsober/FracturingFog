# Fracturing Fog — Benchmarks Guide

This guide is for anyone who wants to **measure how fast Fracturing Fog renders on their own
machine** — to compare hardware, to check whether a setting helps, or to hand a developer a
before/after number when reporting a performance problem.

You do **not** need to be a programmer to run these. You do need a terminal (PowerShell on
Windows) and the ability to run one command. If you are a contributor and want the internals —
the code, the parameter matrix, how to add a case — read the
[Benchmark Subsystem](../Technical/Benchmark-Subsystem.md) technical reference instead.

> [!WARNING]
> **The benchmarks run on Windows only.** They are built into the Windows program
> (`FracturingFogCLD.csproj`), which is a Windows-only build and will not even compile on Linux or
> macOS — trying it fails with an error like `NETSDK1073 … Microsoft.WindowsDesktop.App…`. That is
> expected, not a bug: it means you pointed a Linux build at the Windows program. The other
> Fracturing Fog command-line features (`--server`, `--batch`, and friends) *do* work cross-platform
> via a different program, but the benchmark commands have not been wired into it yet. If you are on
> Linux/macOS and need benchmarks, that porting task is described for developers in the
> [Benchmark Subsystem](../Technical/Benchmark-Subsystem.md#wiring-the-harness-into-fracturingfogapp-linuxmacos)
> reference.

> [!NOTE]
> A *benchmark* here means: the app renders the same fractal views over and over, times how long
> each render takes, and prints a table. Nothing is saved to your gallery and no window opens —
> it is a pure measurement run.

---

## What can I measure?

There are three benchmarks, from most thorough to quickest:

| Command          | What it measures                                             | How long |
|------------------|-------------------------------------------------------------|----------|
| `--bench`        | The real Mandelbrot engine across 32 combinations of resolution, zoom depth, colour theme, and acceleration. **The proper one.** | Minutes  |
| `--gentestbench` | A quick four-step speed ladder of the built-in test fractal. | Seconds  |
| `--benchmark`    | The same quick ladder, but for **your own equation**.        | Seconds  |

Most people want `--bench`. The other two are handy quick checks.

---

## Before you start: make it a fair test

Benchmark numbers are only trustworthy if the machine is calm and the app is built for speed.

1. **Build in Release mode.** A "Debug" build turns off the speed optimisations and will report
   numbers several times slower than reality. Always add `-c Release` (shown in every command
   below).
2. **Close heavy background apps.** Browsers with many tabs, video calls, game launchers, and
   antivirus scans all steal CPU time and inflate the numbers.
3. **Plug in a laptop and set it to full performance.** On battery, Windows throttles the CPU and
   your results will wander.
4. **Let it warm up, then re-run.** The first run of the day includes one-time startup costs. If a
   number looks odd, run it again — the benchmark's own **StdDev** column tells you how noisy the
   machine was (see "Reading the results").

---

## Running the full benchmark (`--bench`)

Open PowerShell in the folder where you have the source, and run:

```bash
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench
```

A console appears, prints progress for a few minutes, and ends with a results table. On Windows the
console may pop up as its own window; it waits for a key press at the end so you can read the table
before it closes.

That default run measures **32 combinations** — every mix of:

- **Resolution:** small (640×360) and full HD (1920×1080).
- **Zoom depth:** shallow, medium, and two kinds of very-deep zoom.
- **Colour theme:** a plain rainbow (`Hsv`) and a lit 3-D stone look (`PhongStone`).
- **Acceleration:** on and off (the clever deep-zoom shortcuts, so you can see how much they help).

### See the list without running

```bash
dotnet run -c Release --project FracturingFogCLD.csproj -- --bench --list flat
```

This prints every combination it *would* measure, without measuring anything — handy to confirm
what a full run covers.

> [!NOTE]
> The full sweep takes a while because it runs all 32 combinations. There is no command-line
> switch to run just some of them (e.g. only the deep zooms) — picking a subset means editing the
> source, which is a developer task covered in the
> [Benchmark Subsystem](../Technical/Benchmark-Subsystem.md) reference. For a quick partial check,
> use `--gentestbench` below instead.

---

## The quick benchmarks

### Built-in test fractal (`--gentestbench`)

```bash
dotnet run -c Release --project FracturingFogCLD.csproj -- --gentestbench
```

Renders the built-in test Mandelbrot at four zoom levels and prints a short `ms/frame` table in
seconds. Also written to a file called `gentestbench.out` next to the program.

### Your own equation (`--benchmark`)

Time an equation you write, across five zoom levels:

```bash
dotnet run -c Release --project FracturingFogCLD.csproj -- --benchmark --equation "z^2 + c" --name classic
```

| Option        | Short | Default     | What it does |
|---------------|-------|-------------|--------------|
| `--equation`  | `-e`  | *(required)*| The formula to test, in the same language as the in-app equation editor. |
| `--name`      | `-n`  | `UserBench` | A label for the printout. |
| `--width`     |       | `640`       | Picture width in pixels. |
| `--height`    |       | `480`       | Picture height in pixels. |
| `--frames`    |       | `3`         | How many times to render each step (more = steadier average). |

The result is printed and also saved to `benchmark.out` next to the program. (For which equations
are valid, see the [User Equation & DSL Guide](CalcGen-UserGuide.md).)

---

## Reading the results

### The full benchmark table (`--bench`)

You get a table with one row per combination, sorted fastest at the top:

```text
| Method    | Width | Regime      | Theme      | Accel | Mean      | Error   | StdDev  | Allocated |
|---------- |------ |------------ |----------- |------ |----------:|--------:|--------:|----------:|
| Calculate | 640   | ShallowSP   | Hsv        | True  |  12.34 ms | 0.21 ms | 0.19 ms |   1.2 KB  |
| Calculate | 1920  | DeepHPInPT  | PhongStone | False | 842.10 ms | 9.88 ms | 8.71 ms |  48.9 KB  |
```

The columns that matter to you:

- **Mean** — the headline: average time to render one frame. Lower is faster.
- **Error** — the wobble in that average. If two numbers differ by *less* than the Error, treat
  them as **the same speed** — the difference is just noise.
- **StdDev** — how much the individual runs jumped around. If this is large compared to Mean, your
  machine was busy with something else — close background apps and run again.
- **Allocated** — how much memory each frame used. Usually you can ignore it; developers watch it
  for leaks.

The other columns just say *which* combination the row is: **Width** (resolution), **Regime**
(zoom depth), **Theme** (colour style), **Accel** (deep-zoom shortcuts on/off).

Useful comparisons to eyeball:

- **Do the deep-zoom shortcuts help?** Find two rows identical except `Accel True` vs `False` on a
  `DeepHP` regime. The `True` row should be noticeably faster. (On shallow zooms they are the
  same — the shortcuts only apply deep.)
- **How much does the fancy theme cost?** Compare an `Hsv` row against the matching `PhongStone`
  row. The difference is the price of the 3-D lighting.
- **How does my machine scale?** Compare `640` against `1920` — full HD is nine times the pixels,
  so expect roughly nine times the Mean.

### The quick tables (`--gentestbench` / `--benchmark`)

```text
CalcGen benchmark — classic (equation: z^2 + c)
  default      zoom=1          iter=  256 →    18 ms/frame
  shallow      zoom=20         iter=  256 →    21 ms/frame
  mid-1e3      zoom=1e+03      iter= 1024 →    74 ms/frame
  deep-1e6     zoom=1e+06      iter= 2048 →  156 ms/frame
  deep-1e9     zoom=1e+09      iter= 4096 →  402 ms/frame
```

Each line is one zoom step and how many milliseconds one frame took there (deeper zoom + more
iterations = slower, as expected). These numbers are **rounded to whole milliseconds** and have no
error bars, so use them only to compare two runs of the *same* step — not against the precise
`--bench` numbers. If a line shows `0 ms`, that step was faster than a millisecond; raise
`--frames` or the resolution to see it.

---

## Sharing results with a developer

If you are reporting a "this got slow" problem, the most useful thing you can attach is:

1. The **full `--bench` table** (or the `BenchmarkDotNet.Artifacts` folder it leaves behind — it
   contains the same data as CSV/HTML).
2. Your **CPU model and RAM**, and whether it is a laptop on battery or plugged in.
3. Whether you ran in **Release** (you should have).

That gives them a reproducible baseline instead of "it feels laggy."

---

## See also

- [Benchmark Subsystem](../Technical/Benchmark-Subsystem.md) — the developer reference: code,
  parameter matrix, how to add cases.
- [User Equation & DSL Guide](CalcGen-UserGuide.md) — writing the equations `--benchmark` accepts.
- [Keyboard Shortcuts](Keyboard-Shortcuts.md) — everything else you can drive from the keyboard.
