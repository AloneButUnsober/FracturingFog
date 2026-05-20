# Fractal Expansion — Self-Contained Prompt Series

Each prompt below is standalone. Drop into a fresh Claude Code session in this repo. Run in order.

Decisions baked in:
- Existing color themes must continue to work on new fractal types where mathematically meaningful (smooth iter + distance themes). Interior-cycle themes gated to Mandelbrot.
- User-equation entry uses **Roslyn scripting** (full C#). Deferred to later phase.
- 3D fractals (Mandelbulb, etc.) deferred.

---

## Prompt 0 — Foundation: kernel abstraction + enum + params

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

---

## Prompt 1 — EscapeTimeCalculator + UI dispatch

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

---

## Prompt 2 — Newton / Nova / Halley with basin coloring

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

---

## Prompt 3 — Buddhabrot / Nebulabrot

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

---

## Prompt 4 — IFS engine (chaos game + Barnsley fern, Sierpinski, etc.)

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

---

## Prompt 5 — L-System engine

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

---

## Prompt 6 — Strange attractors

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

---

## Prompt 7 — User-entered equation via Roslyn

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

---

## Prompt 8 — 3D Mandelbulb (compute shader, deferred)

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

---

## Cross-cutting notes for future phases

- Color theme compatibility: most smooth-iter + distance themes work on any escape-time fractal as-is. Themes flagged `UsesInterior` or `UsesOrbitTrap` need per-fractal validation — gate via `IColorMap.Features` checks in MainForm theme combo population.
- Iteration lock: `_iterLocked` in MainForm should respect per-fractal sensible iteration ranges.
- Screenshot/poster pipeline: should work without changes — operates on ColorBuffer regardless of source calculator.
- Video zoom: only valid for fractals with meaningful zoom semantics (escape-time, basin). Disable for IFS/L-system/attractors which have fixed bounded attractors.
