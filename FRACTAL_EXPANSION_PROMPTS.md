# Fractal Expansion — Self-Contained Prompt Series

> **Status (2026-10-26, #1476): historical record. All nine prompts have shipped.**
>
> This was the May-2026 prompt series that took Fracturing Fog from one fractal to many.
> Each prompt below now opens with an **As built** note giving the real file paths and
> what changed from the plan. The original prompt text is kept, collapsed, for history.
> **Do not run the original prompts as written.** They target `MainForm` / WinForms
> (removed in #116), old paths and a Roslyn design that has since been retired.
>
> The live plans are elsewhere:
>
> - `Docs/Fractal-Expansion-Roadmap.md`: phases A–D (all 19 slices shipped) plus deferred
>   follow-ups.
> - Theoretical fractal R&D epic #850 (Lyapunov, transcendental Julia, Magnet,
>   Kleinian / Indra's Pearls, dual-orbit / Julibrot, parabolic implosion, …).
> - GitHub issues: the canonical task list (`AloneButUnsober/FracturingFog`).

Decisions in the original series, and how they turned out:

| Original decision | Outcome |
|---|---|
| Existing colour themes keep working on new types; interior-cycle themes gated to Mandelbrot | Held. Theme gating per `FractalType` / colour-map features. |
| User equations via **Roslyn scripting** (full C#), deferred | Shipped, then replaced by the Sandbox DSL / equation language (#937). Raw C# removed. |
| 3D fractals deferred | Shipped. Ten raymarched 3D families, CPU + GPU. |

---

## Prompt 0 — Foundation: kernel abstraction + enum + params

### As built

**Status: shipped.** Paths moved when the solution was split into projects.

- `FractalType` lives in `Abstractions/Models/Enums.cs` and has grown from the 13 members
  listed below to 56 (see *Current FractalType inventory*).
- `FractalParameters` is a class (not a record) in `Abstractions/Models/FractalParameters.cs`
  and now carries every family's tunables. `JuliaC`, `MultibrotExponent`, `PhoenixP`,
  `IFSMaps`, `UserEquationSource` exist as specified. `AffineMap` is a
  `readonly record struct` in the same file.
- `NewtonPolyCoeffs` is declared but **no calculator reads it**. The Newton family
  uses `NewtonExponent` (f(z) = z^d − 1).
- `IFractalKernel` is in `Engine/Interefaces/IFractalKernel.cs` and gained `InitState(...)`
  (per-kernel z0/derivative seeding). A sibling `ISimdFractalKernel` adds
  `InitStateSimd` / `StepSimd` for `Vector<double>` lanes.
- Kernels live in `Engine/Models/FractalKernels/`: Mandelbrot, Julia, BurningShip, Tricorn,
  Multibrot, Phoenix, plus later Glynn, MagnetOne, MagnetTwo, Spider.

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Extend the fractal engine to support multiple escape-time formulas without rewriting MandelbrotCalculator.cs.

1. In Models/Enums.cs, expand FractalType to:
   { Mandelbrot, Julia, BurningShip, Tricorn, Multibrot, Phoenix, Newton, Nova,
     BuddhaBrot, IFS, LSystem, StrangeAttractor, UserEquation }

2. Create Models/FractalParameters.cs — a record with optional per-fractal fields:
   - Complex JuliaC (default -0.7 + 0.27015i)
   - int MultibrotExponent (default 3)
   - Complex PhoenixP (default 0.56667)
   - Complex[]? NewtonPolyCoeffs
   - List<AffineMap>? IFSMaps
   - string? UserEquationSource

3. Create Interefaces/IFractalKernel.cs — struct-generic interface for JIT specialization:
   - double BailoutRadius2 { get; }
   - bool HasCardioidSkip { get; }
   - bool IsInTrivialInSet(double cx, double cy)
   - void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
   - The Step method is the inner-loop body. dr/di are dz/dc for distance/normal estimation.

4. Create Models/FractalKernels/ folder with one file per kernel:
   - MandelbrotKernel.cs  — z² + c, with cardioid+bulb skip
   - JuliaKernel.cs       — z² + c0 (c0 fixed, z0=pixel; readonly field on the struct)
   - BurningShipKernel.cs — (|Re|+i|Im|)² + c
   - TricornKernel.cs     — conj(z)² + c
   - MultibrotKernel.cs   — z^d + c (d as readonly field)
   - PhoenixKernel.cs     — z² + c + p·z_prev (keeps prev z; readonly p)

   Each is a struct implementing IFractalKernel. JIT specializes via Calculate<TKernel> where TKernel : struct, IFractalKernel.

5. Do NOT modify MandelbrotCalculator.cs in this phase. New kernels stand alone for now.
6. Build clean. No UI wiring yet.
```

</details>

---

## Prompt 1 — EscapeTimeCalculator + UI dispatch

### As built

**Status: shipped, UI path superseded.**

- `Engine/Calculators/EscapeTimeCalculator.cs` serves Julia, BurningShip, Tricorn,
  Multibrot, Phoenix, Magnet1/2, Glynn and Spider. `MandelbrotCalculator` stays the
  deep-zoom path for Mandelbrot.
- Dispatch is in `Engine/Rendering/FractalRenderHost.cs` (`FractalType` → calculator
  switch), not `MainForm`.
- **`MainForm` no longer exists.** WinForms was removed in #116. The fractal-type picker
  is in the Control Center *Explore* section
  (`UI.Avalonia/Views/ControlCenterSections/ExploreSectionView.axaml`). Per-family
  parameters are Control Center controls bound through `FloatingMenuViewModel`.
  Do not revive the toolbar combo or the FloatingMenu view.
- Deep zoom for these families: CalculatorGen-emitted variants (`GeneratedMandelbrotZ2..Z5`,
  `GeneratedTricorn`, `GeneratedBurningShip`) carry scalar / AVX2 / GPU / perturbation /
  BLA. Perturbation past ~1e13 for the hand-written Multibrot / Burning Ship / Tricorn
  paths is still open: #1253.

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Build a new calculator for the non-Mandelbrot escape-time fractals added in phase 0. Keep MandelbrotCalculator.cs untouched — it stays the optimized path for FractalType.Mandelbrot.

1. Create EscapeTimeCalculator.cs (sibling of MandelbrotCalculator.cs). Surface area matches MandelbrotCalculator: same public buffers (IterationBuffer, SmoothBuffer, DistanceBuffer, NormalXBuffer, NormalYBuffer, ColorBuffer, FinalZ*/FinalD* buffers), same Calculate(CancellationToken) entry point, same Resize, CenterX/Y/Zoom/MaxIterations/Quality/ColorMap properties.

2. Implement SP path only. Use Parallel.For + System.Numerics.Vector<double> SIMD ONLY for kernels whose Step is SIMD-friendly (Mandelbrot-shape, BurningShip, Tricorn). Multibrot, Phoenix, Julia (general) can fall back to scalar — keep code readable, optimize later.

3. Dispatch through a generic core: CalculateCore<TKernel, TMap>(TKernel kernel, TMap map, CancellationToken ct) where TKernel : struct, IFractalKernel, TMap : IColorMap.

4. Use the existing IColorMap dispatch pattern (pattern-match concrete color map type → call generic CalculateCore) so JIT inlines Map(). Steal the switch from MandelbrotCalculator.Calculate().

5. Cardioid/bulb skip ONLY when kernel.HasCardioidSkip is true (Mandelbrot kernel exposes true; new kernels expose false).

6. DD/QD/PT/SA/BLA NOT implemented here. EscapeTimeCalculator caps at SP zoom (~1e15). For new fractal types, MainForm should clamp Quality to Standard.

7. In MainForm.cs:
   - Add field: FractalType _currentFractalType = FractalType.Mandelbrot;
   - Add field: FractalParameters _fractalParams = new();
   - Add field: EscapeTimeCalculator? _escapeCalculator;
   - Wherever _calculator.Calculate() is called, route to the right engine based on _currentFractalType:
     • Mandelbrot → existing _calculator
     • Julia, BurningShip, Tricorn, Multibrot, Phoenix → _escapeCalculator
   - Wherever buffers are uploaded (UploadProcessedBuffer), accept either calculator's buffers via a small interface ICalculatorBuffers, OR a thin abstraction wrapping ColorBuffer / Width / Height. Pick the smaller diff.

8. Toolbar UI: add a fractal-type ComboBox to the right of the existing theme combo. Items: enum names with friendly labels ("Mandelbrot", "Julia", "Burning Ship", "Tricorn", "Multibrot", "Phoenix"). On change: set _currentFractalType, reset view to fractal-appropriate default, kick a re-render.

9. Per-fractal param panel: floating panel (model after FloatingMenu) shown only for fractals with params. Controls:
   - Julia: 2 numeric inputs for c.real / c.imag + draggable picker
   - Multibrot: integer slider 2-8 for exponent
   - Phoenix: 2 numeric inputs for p.real / p.imag
   Wire changes back into _fractalParams and re-render.

10. View defaults per type (centerX, centerY, zoom):
    - Mandelbrot: (-0.5, 0, 1)
    - Julia: (0, 0, 1)
    - BurningShip: (-0.5, -0.5, 1) — set is flipped
    - Tricorn: (0, 0, 1)
    - Multibrot: (0, 0, 1)
    - Phoenix: (0, 0, 1.5)

11. Build clean. Manually test: open app, switch through every fractal, confirm something sensible renders.
```

</details>

---

## Prompt 2 — Newton / Nova / Halley with basin coloring

### As built

**Status: shipped, different architecture.**

- `IBasinKernel`, `BasinCalculator` and `BasinColorMap` were **never built**.
- Newton and Nova share `Engine/Calculators/NewtonCalculator.cs`. Nova is selected by
  `FractalType.Nova` and adds the +c term (#1294, see `NovaJuliaC`).
- Halley and Secant each have their own calculator (`HalleyCalculator.cs`,
  `SecantCalculator.cs`). Both reuse `NewtonExponent` / `NewtonRelaxation`. Secant adds
  `SecantInitialOffset`.
- The arbitrary-polynomial entry (`NewtonPolyCoeffs`, "z^3 − 1" parser) was not wired.
  Root-finding on a user formula is reachable through the equation language instead
  (see Prompt 7).

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add root-finding fractals as a new family. They iterate z := z - f(z)/f'(z) and converge to one of f's roots — coloring is by basin (which root) blended with iteration count.

1. Add new IFractalKernel-like interface: IBasinKernel with:
   - Complex Step(Complex z)
   - int BasinOf(Complex z)  — nearest-root index
   - double ConvergenceEps  — typically 1e-6
   - int RootCount
   - Color RootColor(int idx)

2. Kernels in Models/FractalKernels/Basin/:
   - NewtonKernel.cs — f(z) = z^d - 1, d configurable
   - NovaKernel.cs   — Newton + Mandelbrot hybrid: z := z - R·f/f' + c
   - HalleyKernel.cs — z := z - 2·f·f' / (2·f'² - f·f'')

3. Generic polynomial input: FractalParameters.NewtonPolyCoeffs — array of complex coefficients (high→low order). Parse from a user-entered string ("z^3 - 1") via a small expression scanner in Phase 7, OR for now accept coefficients directly via a small grid UI.

4. New BasinCalculator.cs (separate from EscapeTimeCalculator — different inner loop and coloring path). Reuses IterationBuffer + SmoothBuffer + a new BasinBuffer (int[]).

5. Color map: implement BasinColorMap : IColorMap that picks root color from BasinBuffer (read via thread-local context — pass via constructor since IColorMap is stateless on inputs).

6. Add to FractalType enum dispatch in MainForm. UI: polynomial input row when Newton/Halley selected.

7. View default: (0, 0, 3).
```

</details>

---

## Prompt 3 — Buddhabrot / Nebulabrot

### As built

**Status: shipped and extended.**

- `Engine/Calculators/BuddhabrotCalculator.cs`. Metropolis–Hastings sampling is opt-in via
  `BuddhaMetropolis`; uniform sampling scales with window size and zoom (#1222 / #1224).
  GPU and CPU share one sampler and are chosen by measurement (#1218, #1220).
- Nebulabrot became its own `FractalType` rather than a mode. `AntiBuddhabrot`,
  `AntiNebulabrot` (`AntiBuddhabrotCalculator.cs`) and `DualBuddhabrot` (#1124,
  `DualBuddhabrotCalculator.cs`) followed.
- Interaction notes: #193, #194, #836–#838.

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add Buddhabrot. Reuses Mandelbrot iteration but accumulates orbit visit counts for ESCAPING points rather than per-pixel escape time.

1. Create BuddhabrotCalculator.cs:
   - Sample N random c values (Metropolis-Hastings preferred over uniform; uniform OK for v1).
   - For each c that escapes, record the orbit trajectory.
   - Project each visited z back into pixel space; increment per-pixel hit counter.
   - Three parallel hit buffers for different iteration-count bands → R, G, B channels (Nebulabrot effect).

2. Tone map: log(hit) → normalized → channel intensity.

3. Threading: Parallel.For over chunks of random samples. Per-thread local hit buffers, reduce at end (avoid contention).

4. Parameters in FractalParameters:
   - int BuddhaSampleCount (default 5M)
   - int[3] BuddhaIterBands (default {500, 5000, 50000})
   - bool UseMetropolisSampling

5. UI: progress bar (long-running render). Allow incremental refinement — render at 100k samples, then progressively accumulate more without restart.

6. View default: (-0.5, 0, 1).
```

</details>

---

## Prompt 4 — IFS engine (chaos game + Barnsley fern, Sierpinski, etc.)

### As built

**Status: shipped.**

- `Engine/Calculators/IFSCalculator.cs` + `Engine/Models/IFSPresets.cs`. Presets as
  specified: Sierpinski Triangle, Sierpinski Carpet, Barnsley Fern, Heighway Dragon,
  Koch Curve, Pythagoras Tree. Selected by `IFSPresetName`. Custom maps via `IFSMaps`.
- No `DataGridView` editor (WinForms is gone). The non-linear chaos-game successor is the
  `Flame` family (`FlameRenderer.cs`, `FlamePresets.cs`).

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add Iterated Function System rendering. Completely different pipeline from escape-time.

1. Models/AffineMap.cs — record (double a, b, c, d, e, f, weight) — applies x' = a·x + b·y + e, y' = c·x + d·y + f.

2. IFSCalculator.cs:
   - Chaos game: start at (0,0). Pick a map randomly weighted by weight field. Apply. Plot point (skip first ~20 iterations to settle on attractor). Repeat 1M-10M times.
   - Plot: accumulate hit density per pixel into uint[] hitBuffer; tone-map via log + IColorMap on density.

3. Built-in presets in Models/IFSPresets.cs:
   - SierpinskiTriangle: 3 maps, scale 1/2 to 3 corners
   - SierpinskiCarpet: 8 maps, scale 1/3
   - BarnsleyFern: 4 maps with classic coefficients (weights 0.01, 0.85, 0.07, 0.07)
   - KochCurve: 4 maps
   - HeighwayDragon: 2 maps
   - PythagorasTree: 2 maps

4. UI: preset combo. Optional editor — DataGridView of maps (a,b,c,d,e,f,weight). Re-render on change.

5. View handling: IFS attractors live in fixed regions — compute attractor bbox once, fit view to it. Pan/zoom still works on top.

6. Plug into FractalType.IFS dispatch in MainForm.
```

</details>

---

## Prompt 5 — L-System engine

### As built

**Status: shipped, preset list larger.**

- `Engine/Calculators/LSystemCalculator.cs` + `Engine/Models/LSystemPresets.cs`, selected by
  `LSystemPresetName`. Presets now include Hilbert, Gosper, Plant, Bush, Crystal, Dragon,
  Twindragon, Levy C, Koch Curve, Koch Snowflake, Quadratic Koch Island, Peano, Pentigree,
  Pythagoras Tree, Sierpinski Arrowhead and Sierpinski Carpet.

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add L-System fractals. String rewriting + turtle graphics.

1. Models/LSystem.cs — record (string Axiom, Dictionary<char,string> Rules, double Angle, int Iterations, double StepLength).

2. LSystemCalculator.cs:
   - Rewrite axiom N times by replacing each char per Rules.
   - Walk resulting string as turtle commands:
     F = forward + draw, f = forward no draw, + = turn left by Angle, - = turn right, [ = push state, ] = pop, |  = reverse
   - Draw line segments to ColorBuffer with Bresenham. Color per segment depth or via IColorMap density.

3. Built-in presets in Models/LSystemPresets.cs:
   - Hilbert curve: A → -BF+AFA+FB-, B → +AF-BFB-FA+, axiom A, angle 90
   - Gosper curve
   - Plant: F → F[+F]F[-F]F, angle 25
   - Sierpinski arrowhead
   - Dragon curve
   - Koch snowflake

4. UI: preset combo + iterations slider + angle slider.

5. View: auto-fit to bounding box of drawn polyline.

6. Plug into FractalType.LSystem dispatch.
```

</details>

---

## Prompt 6 — Strange attractors

### As built

**Status: shipped (preset-based, not an `IAttractorKernel` interface).**

- `Engine/Calculators/AttractorCalculator.cs`. Built-ins: Clifford, De Jong, Hopalong,
  Lorenz. Lorenz uses an RK4 step and projects (x, z) to 2D. There is no orbit camera or
  z-buffer.
- Selected by `AttractorPresetName`. Related histogram renderers that came later:
  `Logistic` (`LogisticCalculator.cs`) and `Lyapunov` (#851, `LyapunovCalculator.cs`).

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add strange attractor visualization (Lorenz, Clifford, De Jong, Hopalong).

1. IAttractorKernel:
   - void Step(ref double x, ref double y, ref double z)  — for 3D attractors
   - void Step2D(ref double x, ref double y)  — 2D attractors
   - bool Is3D

2. Kernels in Models/FractalKernels/Attractors/:
   - LorenzKernel: σ=10, ρ=28, β=8/3, RK4 integration, dt=0.005
   - CliffordKernel: x' = sin(a·y) + c·cos(a·x), y' = sin(b·x) + d·cos(b·y)
   - DeJongKernel: x' = sin(a·y) - cos(b·x), y' = sin(c·x) - cos(d·y)
   - HopalongKernel

3. AttractorCalculator.cs:
   - Iterate kernel N=5M times after warmup.
   - For 3D: project (orbit camera matrix). Z-buffer for depth fade.
   - Accumulate hit density to buffer; log tone-map; IColorMap on density.

4. UI: kernel parameter sliders (a, b, c, d for Clifford/De Jong; orbit camera for Lorenz).

5. View: auto-fit attractor bbox.

6. FractalType.StrangeAttractor dispatch.
```

</details>

---

## Prompt 7 — User-entered equation via Roslyn

### As built

**Status: shipped, then redesigned. Do not follow this prompt.**

- The raw-C# Roslyn compile path was **removed** (user-code surface reduction). User
  equations are translated to the Sandbox DSL and run by a safe interpreter
  (`Engine/Calculators/UserEquationCalculator.cs`, `SandboxCalculator.cs`). Legacy
  sources migrate via `UserEquationDslMigration.cs`.
- The shared equation language lives in the `Equations/` project (parser, formatter,
  completion, signatures). Spec: `Docs/Technical/Equation-Language.md` (epic #937).
  Editor formatting: epic #1360.
- 3D user formulas are the `UserBulb` family: typed expression-tree compile (#1110 /
  #1111) plus a GPU dispatch (`Engine/Calculators/Gpu/UserBulbGpuDispatch.cs`).
- `RoslynRefs.cs` still exists for the hot-load path. It is not a user-equation compiler.

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add user-defined equation rendering. Real-time edit + compile.

1. Add NuGet: Microsoft.CodeAnalysis.CSharp.Scripting.

2. UserEquationCompiler.cs:
   - Accept source like: `z = z * z + c` or full body `var z2 = z*z; return z2*z + c;`
   - Wrap in Roslyn script template:
     using System.Numerics;
     Complex Step(Complex z, Complex c, int n) { {USER_CODE} }
   - Compile to Func<Complex, Complex, int, Complex>.
   - Cache compiled delegate; recompile only on source change.
   - Display compile errors in textbox red-text.

3. Sandbox: ScriptOptions with restricted assembly references (System.Numerics only). No System.IO, no Reflection, no Network. Document the limitation.

4. UserEquationCalculator.cs: uses compiled delegate in per-pixel loop. Scalar only (no SIMD — delegate call overhead too high, but ~1MP renders still interactive).

5. UI: floating panel with multiline textbox, compile button (or auto-compile after 500ms debounce), error display, c constant inputs (for Julia-style mode).

6. Two modes:
   - Mandelbrot-mode: z0=0, c=pixel
   - Julia-mode: z0=pixel, c=fixed constant from UI

7. FractalType.UserEquation dispatch.
```

</details>

---

## Prompt 8 — 3D Mandelbulb (compute shader, deferred)

### As built

**Status: shipped. No longer deferred.**

- CPU: `Engine/Calculators/MandelbulbCalculator.cs`. GPU: ILGPU kernels in
  `Engine/Calculators/Gpu/` (`MandelbulbGpuCalculator.cs` and siblings), not a
  hand-written `Shaders/Mandelbulb.hlsl`.
- The 3D family grew to Mandelbox, KIFS, Menger / Sierpinski folds, Quaternion Julia /
  Mandelbrot, Bicomplex, Coquaternion, Kleinian, Julibrot and UserBulb. Shared camera,
  lighting, Relief 3D, AO, DoF and adaptive-AA infrastructure now sit on top. See the
  3D rendering roadmap (#389) and the GPU parity tracker (#1173).

<details>
<summary>Original prompt (May 2026, superseded)</summary>

```
Add 3D Mandelbulb / Mandelbox / Quaternion Julia. Distance-estimation raymarching on GPU.

1. New HLSL compute shader Shaders/Mandelbulb.hlsl:
   - Distance estimator using triplex (r, theta, phi) power-N formula.
   - Raymarch per pixel; trace until DE < threshold; shade by normal + AO.

2. New IFractalRenderer-compatible path: MandelbulbRenderer.cs wrapping Vortice compute pipeline. Dispatch compute, output texture.

3. UI: orbit camera (mouse drag rotates, wheel zooms), FOV slider, power-N slider (default 8), light direction.

4. Bypasses MandelbrotCalculator entirely — output texture goes straight to renderer.

5. View: spherical coords (theta, phi, distance from origin).

6. FractalType.Mandelbulb dispatch.

NOTE: this is the largest single phase. Consider as v2 milestone.
```

</details>

---

## Current FractalType inventory (56 members)

Grouped by engine. The enum's XML docs in `Abstractions/Models/Enums.cs` have the
per-family formula and tunables. The sources behind each family are listed in
`Docs/Resources-Bibliography.md`.

| Engine | Types |
|---|---|
| Deep-zoom escape-time | `Mandelbrot` (`MandelbrotCalculator`: perturbation / BLA / DD–OD precision) |
| Kernel escape-time (`EscapeTimeCalculator`) | `Julia`, `BurningShip`, `Tricorn`, `Multibrot`, `Phoenix`, `Magnet1`, `Magnet2`, `Glynn`, `Spider` |
| CalculatorGen-emitted | `GeneratedMandelbrotZ2`–`Z5`, `GeneratedTricorn`, `GeneratedBurningShip` |
| Other escape-time | `TearDrop`, `TranscendentalJulia`, `PrecisionField` |
| Root-finding basins | `Newton`, `Nova`, `Halley`, `Secant` |
| Orbit histograms | `BuddhaBrot`, `Nebulabrot`, `AntiBuddhabrot`, `AntiNebulabrot`, `DualBuddhabrot` |
| Dynamical-system histograms | `StrangeAttractor`, `Logistic`, `Lyapunov`, `ChaoticBilliard` |
| Geometric / IFS / grammar | `IFS`, `Flame`, `LSystem`, `Apollonian`, `IndrasPearls`, `RandomTile` |
| Stochastic / procedural | `Dla`, `Plasma`, `AcidWarp` |
| User formulas | `UserEquation`, `Sandbox` (2D DSL), `UserBulb` (3D) |
| 3D raymarched | `Mandelbulb`, `Mandelbox`, `Kifs`, `QuaternionJulia`, `QuaternionMandelbrot`, `BicomplexMandelbrot`, `Coquaternion`, `Kleinian`, `Julibrot` |
| Dual-orbit 2D | `JulibrotPair` |

---

## Cross-cutting notes (current)

- **UI:** new surfaces go in Control Center sections (`UI.Avalonia/Views/ControlCenterSections/`).
  `FloatingMenuView` is retired. `FloatingMenuViewModel` is the shared state hub.
- **Batch parity:** every render-affecting family or parameter must stay reproducible via
  `--batch` and the Control Center **Command** builder (#64). Per-family params ride
  `RegionFractalParams` (→ `--param Key=Value`). See `CLAUDE.md` → *Keep batch processing in
  step with every feature*.
- **Theme compatibility:** smooth-iteration + distance themes work on any escape-time
  family. Interior / orbit-trap themes need per-family validation. Histogram and
  procedural families push their scalar through the active `IColorMap`.
- **Screenshot / poster / video:** `Engine/Imaging/PosterRenderer.cs` handles every
  family. Video zoom is meaningful only for zoomable families. Fixed-attractor families
  (IFS, L-System, attractors, Plasma, Acid Warp) treat pan/zoom as fit-to-bounds or a no-op.
- **Citation contract:** any formula, preset or constant taken from outside goes into
  `Docs/Resources-Bibliography.md` in the same change, credited at point of use. Run
  `python Tools/CitationGuard/citation_guard.py --base origin/main` before committing.

## Writing a new prompt in this style

If you add a family, file a GitHub issue first and write the prompt against today's
layout:

1. `FractalType` member + XML doc → `Abstractions/Models/Enums.cs` (ASCII name only).
2. Tunables → `FractalParameters` + `RegionFractalParams` (batch / region round-trip).
3. Calculator → `Engine/Calculators/`, dispatched in `Engine/Rendering/FractalRenderHost.cs`
   and `PosterRenderer`.
4. Control Center controls + help text (`Abstractions/Help/HelpTextBundle.cs`).
5. Command-builder seed + parse round-trip test, and bibliography entry.

<details>
<summary>Original cross-cutting notes (May 2026, superseded)</summary>

- Color theme compatibility: most smooth-iter + distance themes work on any escape-time fractal as-is. Themes flagged `UsesInterior` or `UsesOrbitTrap` need per-fractal validation — gate via `IColorMap.Features` checks in MainForm theme combo population.
- Iteration lock: `_iterLocked` in MainForm should respect per-fractal sensible iteration ranges.
- Screenshot/poster pipeline: should work without changes — operates on ColorBuffer regardless of source calculator.
- Video zoom: only valid for fractals with meaningful zoom semantics (escape-time, basin). Disable for IFS/L-system/attractors which have fixed bounded attractors.

</details>
