// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Numerics;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Models
{
    /// <summary>
    /// Per-fractal parameters carried by MainForm and passed to the appropriate
    /// calculator. Only the fields relevant to the active FractalType are read.
    /// </summary>
    public sealed class FractalParameters
    {
        public Complex JuliaC { get; set; } = new Complex(-0.7, 0.27015);

        public int MultibrotExponent { get; set; } = 3;

        public Complex PhoenixP { get; set; } = new Complex(0.56667, 0.0);

        /// <summary>Constant c for the Glynn fractal (Julia set of
        /// z → z^1.5 + c). Default −0.2 produces the canonical
        /// dendrite. Real part dominates the dendrite tilt; small
        /// imaginary tweaks deform it asymmetrically.</summary>
        public Complex GlynnC { get; set; } = new Complex(-0.2, 0.0);

        /// <summary>Logistic bifurcation burn-in iterations — discarded
        /// before density accumulation starts so transients don't
        /// pollute the attractor histogram. Plot count = MaxIterations
        /// − LogisticBurnIn (clamped &gt;= 1).</summary>
        public int LogisticBurnIn { get; set; } = 1000;

        /// <summary>Logistic seed x₀ ∈ (0, 1). Default 0.5 — any
        /// non-fixed-point seed lands on the same attractor after
        /// burn-in, but extreme values (near 0 or 1) lengthen the
        /// transient.</summary>
        public double LogisticSeed { get; set; } = 0.5;

        public Complex[]? NewtonPolyCoeffs { get; set; }

        public List<AffineMap>? IFSMaps { get; set; }

        public string? UserEquationSource { get; set; }

        /// <summary>
        /// Name of the saved <see cref="UserEquationEntry"/> the current source
        /// came from. Null when the user has typed a custom equation that doesn't
        /// match any saved entry. Region save/recall uses this to round-trip
        /// the equation by reference (name) rather than copying source into JSON.
        /// </summary>
        public string? UserEquationName { get; set; }

        /// <summary>
        /// View rotation applied to the UserEquation parameter plane, in degrees
        /// (CCW). Rotates the (dx, dy) pixel offset before adding to center so the
        /// rendered fractal appears tilted. 0 = unrotated.
        /// </summary>
        public double UserEquationRotationDegrees { get; set; } = 0.0;

        /// <summary>
        /// Bare CalcGen DSL source bound to the User Equation editor's "DSL" tab.
        /// Independent of <see cref="UserEquationSource"/> (the C#-style tab) so
        /// switching tabs does not destroy the other tab's content.
        /// Routed straight to CalculatorGen without C#→DSL preprocessing.
        /// </summary>
        public string? UserEquationDslSource { get; set; }

        /// <summary>
        /// Last-active tab index in the User Equation editor. 0 = User Equation,
        /// 1 = DSL. Persisted so the modal reopens to the tab the user was last
        /// editing. Compile / Generate buttons route by this value.
        /// </summary>
        public int UserEquationActiveTab { get; set; } = 0;

        /// <summary>
        /// When true, the UserEquation calculator skips its parallel-perturbation
        /// Jacobian trajectory (2× delegate calls per iteration) and emits zero
        /// surface normals at escape. 3D Phong themes degrade to flat lighting,
        /// but throughput roughly doubles for expensive equations. Default false
        /// preserves the full-fidelity Hubbard-Douady gradient path.
        /// </summary>
        public bool UserEquationSkipJacobian { get; set; } = false;

        /// <summary>
        /// Source for the Sandbox fractal — a restricted expression DSL parsed by
        /// <see cref="SandboxExpression"/>. Safe to evaluate in untrusted contexts:
        /// no BCL access, no IO, no reflection.
        /// </summary>
        public string? SandboxSource { get; set; }

        /// <summary>
        /// Name of the saved <see cref="SandboxEquationEntry"/> the current source
        /// came from. Round-tripped by region save/recall the same way
        /// <see cref="UserEquationName"/> is.
        /// </summary>
        public string? SandboxName { get; set; }

        public string IFSPresetName { get; set; } = "Sierpinski Triangle";
        public int IFSIterations { get; set; } = 2_000_000;

        public string LSystemPresetName { get; set; } = "Hilbert";
        public int LSystemDepth { get; set; } = 5;

        /// <summary>Plasma diamond-square roughness coefficient. Per
        /// subdivision the displacement amplitude is multiplied by
        /// 2^(−roughness). roughness=0 collapses to a smooth bilinear
        /// gradient; roughness=1 preserves full amplitude at every level
        /// (very rocky terrain). Default 0.55 matches the visually
        /// "cloudy" Apophysis-style plasma palette.</summary>
        public double PlasmaRoughness { get; set; } = 0.55;

        /// <summary>PRNG seed for the Plasma calculator. Same (W, H, seed,
        /// roughness) deterministically produces the same field.</summary>
        public int PlasmaSeed { get; set; } = 12345;

        /// <summary>Optional explicit Flame-fractal map list. When null the
        /// renderer falls back to <see cref="FlamePresetName"/> from
        /// <c>FlamePresets.All</c>.</summary>
        public List<FlameMap>? FlameMaps { get; set; }

        /// <summary>Built-in Flame preset name. Default Sierpinski-Variation
        /// is the simplest visually-distinct flame (Sierpinski IFS with
        /// sinusoidal variation per leg).</summary>
        public string FlamePresetName { get; set; } = "Sierpinski Variation";

        /// <summary>Chaos-game sample count per render. Flames need an order
        /// of magnitude more samples than plain IFS because variations
        /// spread density and gamma tone-map needs a fat histogram.</summary>
        public int FlameIterations { get; set; } = 8_000_000;

        /// <summary>Gamma applied to the log-density before palette lookup.
        /// Apophysis default 2.2. Lower = punchier highlights, higher = more
        /// dynamic range in dim regions.</summary>
        public double FlameGamma { get; set; } = 2.2;

        /// <summary>Vibrancy ∈ [0, 1] (Apophysis term). Blends the gamma-
        /// corrected colour back toward the linear colour: 1 = full
        /// gamma-on-colour (saturated highlights), 0 = pure linear
        /// (preserves filament tint). Default 0.8.</summary>
        public double FlameVibrancy { get; set; } = 0.8;

        public string AttractorPresetName { get; set; } = "Clifford";
        public int AttractorIterations { get; set; } = 2_000_000;
        public double AttractorA { get; set; } = -1.4;
        public double AttractorB { get; set; } = 1.6;
        public double AttractorC { get; set; } = 1.0;
        public double AttractorD { get; set; } = 0.7;

        public int NewtonExponent { get; set; } = 3;
        public double NewtonRelaxation { get; set; } = 1.0;

        /// <summary>Initial prev-z offset for the Secant basin renderer.
        /// Secant's two-point recurrence is undefined when prev = z, so
        /// the first prev is set to (pixel + offset). Default (0.5, 0)
        /// gives a stable starting chord across the unit-roots
        /// configuration; the imaginary component biases the early
        /// chord direction without changing the asymptotic basins.</summary>
        public Complex SecantInitialOffset { get; set; } = new Complex(0.5, 0.0);

        /// <summary>Spider c-decay coefficient. Each iteration
        /// updates c := decay · c + z. Default 0.5 = canonical
        /// Spider. decay = 1.0 cancels mutation (degenerates to
        /// Mandelbrot); decay = 0 reseeds c to z each step
        /// (heavy chaos). Clamped to [0, 1] in the calculator.</summary>
        public double SpiderCDecay { get; set; } = 0.5;

        public int BuddhaSamples { get; set; } = 500_000;
        public int BuddhaIterLow { get; set; } = 500;
        public int BuddhaIterMid { get; set; } = 5_000;
        public int BuddhaIterHigh { get; set; } = 50_000;

        /// <summary>Output blend for the Buddhabrot family. NebulabrotBands
        /// keeps the classic three-band R/G/B composite; ColorMap log-norms
        /// a single-channel hit histogram and feeds it through the active
        /// IColorMap. Defaults differ per FractalType (single-channel calcs
        /// pick ColorMap, 3-band calcs pick NebulabrotBands).</summary>
        public BuddhaColorMode BuddhaColorMode { get; set; } = BuddhaColorMode.NebulabrotBands;

        /// <summary>Render quality for the Buddhabrot family. Standard matches
        /// the classic per-pixel splat. HighDefinition enables stochastic
        /// bilinear splatting (4-tap subpixel jitter), real-axis mirror sample
        /// duplication (free 2× effective sample count), joint-channel
        /// normalisation, and low-hit noise-floor rejection. HD trades ~15-20%
        /// extra CPU per sample for substantially smoother filaments and a
        /// cleaner background (no speckle lift).</summary>
        public BuddhaQualityMode BuddhaQualityMode { get; set; } = BuddhaQualityMode.Standard;

        /// <summary>Use Metropolis-Hastings importance sampling. When the
        /// view is zoomed in, the vast majority of uniform random samples
        /// produce orbits that don't enter the viewport — MH concentrates
        /// samples on c values whose orbits hit pixels the user can see.
        /// Big quality gain when zoomed, small loss when fully zoomed out.</summary>
        public bool BuddhaMetropolis { get; set; } = false;

        /// <summary>Progressive accumulation: split the sample budget into
        /// chunks and composite to the output buffer between chunks. The
        /// user can cancel mid-render and still get a usable image. Cost is
        /// a few extra composites per render (cheap relative to sampling).</summary>
        public bool BuddhaProgressive { get; set; } = false;

        // Mandelbox (Tom Lowe, 2010). Box-fold + sphere-fold + scale DE.
        /// <summary>Mandelbox scale parameter. Per iter:
        /// z = scale · sphereFold(boxFold(z)) + c. Default 2.0;
        /// −1.5 (Juliabox-like inversion), 2.0 (canonical), 3.0
        /// (open-pore variants) are classic values.</summary>
        public double MandelboxScale { get; set; } = 2.0;
        /// <summary>Fixed radius for the sphere-fold band. Points with
        /// |z| in [minRadius, fixedRadius] scale by fixedR²/|z|².
        /// Default 1.0.</summary>
        public double MandelboxFixedRadius { get; set; } = 1.0;
        /// <summary>Inner radius for the sphere-fold. Points with
        /// |z| &lt; minRadius scale uniformly by fixedR²/minR² (a
        /// constant per-iteration zoom). Default 0.5.</summary>
        public double MandelboxMinRadius { get; set; } = 0.5;
        /// <summary>DE inner iteration count. Higher = sharper folds,
        /// more time per step. Default 12.</summary>
        public int MandelboxIterations { get; set; } = 12;
        /// <summary>Bailout for the DE inner loop. |z|² above this exits
        /// early so deep escape doesn't waste cycles. Default 1024.</summary>
        public double MandelboxBailout { get; set; } = 1024.0;
        public double MandelboxCameraDistance { get; set; } = 12.0;
        public double MandelboxCameraTheta { get; set; } = Math.PI * 0.25;
        public double MandelboxCameraPhi { get; set; } = Math.PI * 0.35;
        public double MandelboxLightTheta { get; set; } = Math.PI * 0.25;
        public double MandelboxLightPhi { get; set; } = Math.PI * 0.45;
        public int MandelboxMaxSteps { get; set; } = 128;
        public double MandelboxEpsilon { get; set; } = 0.0015;

        // KIFS (Kaleidoscopic IFS). Repeated reflective fold + scale-from-pivot
        // DE. KifsFold selects which fold table runs per iter:
        //   Menger     — Knighty's sort-3 fold, scale 3 from (1,1,1).
        //   Sierpinski — 3 vertex reflections, scale 2 from (1,1,1).
        public KifsFoldKind KifsFold { get; set; } = KifsFoldKind.Menger;
        /// <summary>DE inner iter count. Higher = sharper detail, slower.
        /// Default 14 — Menger sponge reads as recognisable at this depth.</summary>
        public int KifsIterations { get; set; } = 14;
        /// <summary>Per-iter linear scale applied after fold. Defaults are
        /// fold-specific; the calculator picks 3.0 for Menger and 2.0 for
        /// Sierpinski when this is left at sentinel 0.</summary>
        public double KifsScale { get; set; } = 0.0;
        public double KifsOffsetX { get; set; } = 1.0;
        public double KifsOffsetY { get; set; } = 1.0;
        public double KifsOffsetZ { get; set; } = 1.0;
        /// <summary>Bailout for DE inner loop. |z|² above this exits early.</summary>
        public double KifsBailout { get; set; } = 1024.0;
        public double KifsCameraDistance { get; set; } = 4.0;
        public double KifsCameraTheta { get; set; } = Math.PI * 0.25;
        public double KifsCameraPhi { get; set; } = Math.PI * 0.35;
        public double KifsLightTheta { get; set; } = Math.PI * 0.25;
        public double KifsLightPhi { get; set; } = Math.PI * 0.45;
        public int KifsMaxSteps { get; set; } = 160;
        public double KifsEpsilon { get; set; } = 0.0012;

        // Quaternion Julia (Hart 1989). q_{n+1} = q_n² + c with q ∈ ℍ.
        // Renderer raymarches a 3D slice of the 4D set — pixel maps to
        // (x,y,z) ∈ ℝ³, the 4th component is pinned to QJuliaSliceW.
        /// <summary>Constant quaternion c, x component.</summary>
        public double QJuliaCX { get; set; } = -0.2;
        /// <summary>Constant quaternion c, y component.</summary>
        public double QJuliaCY { get; set; } = 0.4;
        /// <summary>Constant quaternion c, z component.</summary>
        public double QJuliaCZ { get; set; } = -0.4;
        /// <summary>Constant quaternion c, w component.</summary>
        public double QJuliaCW { get; set; } = -0.4;
        /// <summary>W component of the 3D slice plane through ℍ. Pixel
        /// (x,y,z) becomes q = (x,y,z, QJuliaSliceW). Sliding this slider
        /// reveals different 3D cross-sections of the same 4D set.</summary>
        public double QJuliaSliceW { get; set; } = 0.0;
        /// <summary>DE inner iteration count. Higher = sharper detail
        /// but more cycles per ray sample. Default 11 — quaternion Julia
        /// detail saturates around iter 10–14.</summary>
        public int QJuliaIterations { get; set; } = 11;
        /// <summary>|q|² escape threshold. 16 = canonical Hart bailout.</summary>
        public double QJuliaBailout { get; set; } = 16.0;
        public double QJuliaCameraDistance { get; set; } = 4.0;
        public double QJuliaCameraTheta { get; set; } = Math.PI * 0.25;
        public double QJuliaCameraPhi { get; set; } = Math.PI * 0.35;
        public double QJuliaLightTheta { get; set; } = Math.PI * 0.25;
        public double QJuliaLightPhi { get; set; } = Math.PI * 0.45;
        public int QJuliaMaxSteps { get; set; } = 160;
        public double QJuliaEpsilon { get; set; } = 0.0012;

        // Quaternion Mandelbrot (Norton 1982 / Holroyd). Same q := q² + c
        // squaring map as QuatJulia but c varies per pixel — c = (x, y, z,
        // QMandelSliceW) with the 3D raymarcher walking (x, y, z) through the
        // 4D c-space. q starts at the origin (membership test). DE uses the
        // Hubbard–Douady estimator with derivative dq/dc updated as
        // dq := 2·q·dq + 1 each iter.
        /// <summary>W component of the 4D slice plane through ℍ in c-space.
        /// Pixel (x, y, z) becomes c = (x, y, z, QMandelSliceW). Sliding this
        /// reveals different 3D cross-sections of the same 4D set.</summary>
        public double QMandelSliceW { get; set; } = 0.0;
        /// <summary>Reserved — alternate slice plane Z constant when a future
        /// slice-axis selector lets pixel.z route to c.W instead. Currently
        /// unused (raymarched z always feeds c.Z).</summary>
        public double QMandelSliceZ { get; set; } = 0.0;
        /// <summary>DE inner iteration count. Default 11 — quaternion
        /// Mandelbrot detail saturates around iter 10–14.</summary>
        public int QMandelIterations { get; set; } = 11;
        /// <summary>|q|² escape threshold. 16 = canonical Hart bailout.</summary>
        public double QMandelBailout { get; set; } = 16.0;
        public double QMandelCameraDistance { get; set; } = 4.0;
        public double QMandelCameraTheta { get; set; } = Math.PI * 0.25;
        public double QMandelCameraPhi { get; set; } = Math.PI * 0.35;
        public double QMandelLightTheta { get; set; } = Math.PI * 0.25;
        public double QMandelLightPhi { get; set; } = Math.PI * 0.45;
        public int QMandelMaxSteps { get; set; } = 160;
        public double QMandelEpsilon { get; set; } = 0.0012;

        // Apollonian gasket (Descartes Circle Theorem recursive packing).
        /// <summary>Maximum recursion depth for the Vieta-jump tree. The
        /// inside-R sub-gaskets sit several levels deeper than the cusp circles
        /// around the (−1, 2, 2, 3) seed, so the default is generous enough to
        /// let those branches finish without the pixel-radius cutoff firing
        /// before the sub-gasket starts. Default 24.</summary>
        public int ApollonianDepth { get; set; } = 24;
        /// <summary>Stop recursing when the next generated circle would draw
        /// at fewer than this many device pixels of radius. Higher = lighter
        /// renders, faster; lower = more detail at the cost of pile-up at
        /// kissing-point cusps. Default 0.75.</summary>
        public double ApollonianMinPixelRadius { get; set; } = 0.75;
        /// <summary>When true, each circle is coloured by its recursion depth
        /// modulo palette size. When false, colour is driven by log(radius)
        /// — a smoother gradient that emphasises scale instead of generation.
        /// Default true.</summary>
        public bool ApollonianColorByDepth { get; set; } = true;

        // Diffusion-Limited Aggregation (Witten–Sander 1981).
        /// <summary>Number of random-walk particles launched into the
        /// aggregate. Aggregate cell count = DlaParticles + 1 (initial
        /// seed). Higher = denser tree, longer render. Default 8000 fills
        /// a 512² canvas with a recognisable dendrite in well under a
        /// second.</summary>
        public int DlaParticles { get; set; } = 8000;
        /// <summary>PRNG seed for the Witten–Sander walk. Identical (W, H,
        /// seed, particles) reproduce the same tree. Default 12345.</summary>
        public int DlaSeed { get; set; } = 12345;

        // Kleinian limit set (3D, sphere-inversion Schottky group).
        /// <summary>Inversion-iteration cap for the Kleinian DE. Higher =
        /// sharper limit-set boundary, slower per ray sample. Default 16
        /// covers the visible boundary; deep cusps need 24+.</summary>
        public int KleinianIterations { get; set; } = 16;
        /// <summary>Tetrahedral sphere arrangement scale. Centres sit at
        /// (±s, ±s, ±s) with even parity and radius √2·s. At s = 1 the four
        /// spheres are mutually tangent at the edge midpoints and the
        /// fundamental domain shrinks to a single point at the origin;
        /// smaller scales separate the spheres and open the domain.</summary>
        public double KleinianSphereScale { get; set; } = 1.0;
        /// <summary>Sphere-trace step cap. Default 160.</summary>
        public int KleinianMaxSteps { get; set; } = 160;
        /// <summary>Hit threshold for the sphere-trace DE. Default 0.0012.</summary>
        public double KleinianEpsilon { get; set; } = 0.0012;
        public double KleinianCameraDistance { get; set; } = 4.0;
        public double KleinianCameraTheta { get; set; } = Math.PI * 0.25;
        public double KleinianCameraPhi { get; set; } = Math.PI * 0.35;
        public double KleinianLightTheta { get; set; } = Math.PI * 0.25;
        public double KleinianLightPhi { get; set; } = Math.PI * 0.45;

        // Bicomplex Mandelbrot (tessarine algebra, commutative; i² = j² = −1,
        // k² = +1, ij = ji = k). Raymarched 3D slice; pixel (x, y, z) routes
        // to (c.1, c.i, c.j) with c.k pinned to BicomplexSliceW.
        /// <summary>k-component slice constant for the bicomplex Mandelbrot.
        /// Pixel (x, y, z) becomes c = (x, y, z, sliceW). 0 collapses the slice
        /// to a 3D extrusion of the standard 2D Mandelbrot; non-zero values
        /// expose the zero-divisor seam slabs unique to the tessarine algebra.</summary>
        public double BicomplexSliceW { get; set; } = 0.0;
        /// <summary>Wave 5.14 — which 4D axis takes the slice constant. Default
        /// K (legacy behaviour: pixel walks (1, i, j), constant rides on k).</summary>
        public BicomplexSliceAxis BicomplexSliceAxis { get; set; } = BicomplexSliceAxis.K;
        /// <summary>DE inner iteration count. Default 11.</summary>
        public int BicomplexIterations { get; set; } = 11;
        /// <summary>|t|² escape threshold. 16 = canonical Hart bailout.</summary>
        public double BicomplexBailout { get; set; } = 16.0;
        public double BicomplexCameraDistance { get; set; } = 4.0;
        public double BicomplexCameraTheta { get; set; } = Math.PI * 0.25;
        public double BicomplexCameraPhi { get; set; } = Math.PI * 0.35;
        public double BicomplexLightTheta { get; set; } = Math.PI * 0.25;
        public double BicomplexLightPhi { get; set; } = Math.PI * 0.45;
        public int BicomplexMaxSteps { get; set; } = 160;
        public double BicomplexEpsilon { get; set; } = 0.0012;

        // Mandelbulb camera + DE settings.
        public double BulbPower { get; set; } = 8.0;
        public int BulbIterations { get; set; } = 8;
        public double BulbCameraDistance { get; set; } = 3.0;
        public double BulbCameraTheta { get; set; } = Math.PI * 0.25;  // azimuth (around Y)
        public double BulbCameraPhi { get; set; } = Math.PI * 0.35;    // elevation
        public double BulbLightTheta { get; set; } = Math.PI * 0.25;
        public double BulbLightPhi { get; set; } = Math.PI * 0.45;
        public int BulbMaxSteps { get; set; } = 96;
        public double BulbEpsilon { get; set; } = 0.0015;

        // ── UserBulb: user-supplied 3D iteration step rendered Mandelbulb-style.
        // Source is C# expression body of: Vec3 Step(Vec3 z, Vec3 c, int n).
        public string? UserBulbSource { get; set; }
        public string? UserBulbName { get; set; }
        public int UserBulbIterations { get; set; } = 8;
        public double UserBulbBailout { get; set; } = 16.0;      // |z| escape threshold
        public double UserBulbCameraDistance { get; set; } = 3.0;
        public double UserBulbCameraTheta { get; set; } = Math.PI * 0.25;
        public double UserBulbCameraPhi { get; set; } = Math.PI * 0.35;
        public double UserBulbLightTheta { get; set; } = Math.PI * 0.25;
        public double UserBulbLightPhi { get; set; } = Math.PI * 0.45;
        public int UserBulbMaxSteps { get; set; } = 96;
        public double UserBulbEpsilon { get; set; } = 0.0015;
        /// <summary>Finite-diff perturbation magnitude for numerical Jacobian DE.</summary>
        public double UserBulbJacobianH { get; set; } = 1e-4;
        /// <summary>Radius of bounding sphere around fractal target. Rays that miss this sphere
        /// skip raymarching entirely. Set large enough to enclose any feature; 2.5 covers all
        /// standard bulbs/Mandelboxes.</summary>
        public double UserBulbCullRadius { get; set; } = 2.5;
        /// <summary>Per-iteration linear scale factor for the scalar KIFS/Mandelbox
        /// distance estimator. When &gt; 0 the DE uses a running-derivative
        /// (dr *= |scale| each iteration, DE = |z|/dr) instead of the numerical
        /// Jacobian — correct for fold+rotation IFS maps whose folds have
        /// discontinuities the finite-difference Jacobian cannot handle. 0 =
        /// disabled (use the selected DE mode). The user declares this because it
        /// cannot be inferred from arbitrary chain source.</summary>
        public double UserBulbKifsScale { get; set; }
        /// <summary>DE mode: Auto picks analytic when source matches a known power map and
        /// a probe validates within tolerance; Analytic forces analytic (mis-detect = wrong
        /// surface); Numerical forces numerical Jacobian.</summary>
        public UserBulbDEModeKind UserBulbDEMode { get; set; } = UserBulbDEModeKind.Auto;
        /// <summary>Enable identity-blit cache when scene+camera unchanged between renders.</summary>
        public bool UserBulbTemporalReuse { get; set; } = true;
        /// <summary>Render backend. GPU mode requires source pass UserBulbIlgpuTranslator
        /// validation; otherwise falls back to CPU.</summary>
        public UserBulbBackendKind UserBulbBackend { get; set; } = UserBulbBackendKind.CPU;
        /// <summary>Algebra mode: Vec3 (3D triplex) or Quat (4D Hamilton). Affects step signature.</summary>
        public UserBulbAxisModeKind UserBulbAxisMode { get; set; } = UserBulbAxisModeKind.Vec3;
        /// <summary>Step-function compiler. Roslyn = full C# body (default).
        /// Sandbox = restricted DSL (no BCL, shareable; Vec3 + Quat, CPU + GPU).</summary>
        public UserBulbCompilerKind UserBulbCompiler { get; set; } = UserBulbCompilerKind.Roslyn;
        /// <summary>W component of 4D slice plane (Quat mode only). c.W = this value.</summary>
        public double UserBulbQuatSliceW { get; set; } = 0.0;
        /// <summary>Named scalar params exposed in compiled step source. Live-tweakable.</summary>
        public List<UserBulbParam> UserBulbParams { get; set; } = new();
        /// <summary>Animation time global. Exposed inside user step as 'double t'.</summary>
        public double UserBulbTime { get; set; } = 0.0;
        /// <summary>Julia mode: hold c constant at UserBulbJuliaC; perturb initial z for Jacobian.</summary>
        public bool UserBulbJuliaMode { get; set; } = false;
        public double UserBulbJuliaCX { get; set; } = -0.2;
        public double UserBulbJuliaCY { get; set; } = 0.4;
        public double UserBulbJuliaCZ { get; set; } = 0.0;
        public double UserBulbJuliaCW { get; set; } = 0.0;
        /// <summary>Scalar driver feeding ColorMap.Map. Defaults to StepDepth (existing behavior).</summary>
        public BulbColorDriver UserBulbColorDriver { get; set; } = BulbColorDriver.StepDepth;
        public double UserBulbOrbitTrapX { get; set; } = 0.0;
        public double UserBulbOrbitTrapY { get; set; } = 0.0;
        public double UserBulbOrbitTrapZ { get; set; } = 0.0;
        public int UserBulbIterComponentAxis { get; set; } = 0; // 0=X, 1=Y, 2=Z
        // ── 3-light shading ───
        public double UserBulbLight1Intensity { get; set; } = 1.0;
        public uint UserBulbLight1Color { get; set; } = 0xFFFFFFFFu;
        public double UserBulbLight2Theta { get; set; } = Math.PI * 1.25;
        public double UserBulbLight2Phi { get; set; } = Math.PI * 0.55;
        public double UserBulbLight2Intensity { get; set; } = 0.0;
        public uint UserBulbLight2Color { get; set; } = 0xFFB0C8FFu;
        public double UserBulbLight3Theta { get; set; } = Math.PI * 0.75;
        public double UserBulbLight3Phi { get; set; } = Math.PI * 0.30;
        public double UserBulbLight3Intensity { get; set; } = 0.0;
        public uint UserBulbLight3Color { get; set; } = 0xFFFFC890u;
        public double UserBulbShadowSoft { get; set; } = 0.0;
        public int UserBulbAOSamples { get; set; } = 0;
        public double UserBulbAOStrength { get; set; } = 0.4;
        public double UserBulbFogDensity { get; set; } = 0.0;
        public uint UserBulbBgTopColor { get; set; } = 0xFF202040u;
        public uint UserBulbBgBottomColor { get; set; } = 0xFF101020u;
        // ── Camera / view ───
        public double UserBulbFovDegrees { get; set; } = 60.0;
        public double UserBulbDoFAperture { get; set; } = 0.0; // 0 = off
        public double UserBulbDoFFocusDist { get; set; } = 3.0;
        public int UserBulbDoFSamples { get; set; } = 8;
        public bool UserBulbClipPlaneEnabled { get; set; } = false;
        public double UserBulbClipPlaneNX { get; set; } = 0.0;
        public double UserBulbClipPlaneNY { get; set; } = 1.0;
        public double UserBulbClipPlaneNZ { get; set; } = 0.0;
        public double UserBulbClipPlaneD { get; set; } = 0.0;
        public int UserBulbSuperSample { get; set; } = 1; // 1, 2, 4

        /// <summary>P2 — per-raymarcher low-res interactive preview scale factor.
        /// 0.5 = render at half-res, upscale nearest. 1.0 = legacy full-res (no
        /// preview path). Range clamped at [0.25, 1.0] by callers. Each
        /// raymarcher checks its own <c>LowResPreview</c> flag before honouring
        /// this knob — flag off = bit-identical legacy regardless of value.</summary>
        public double LowResPreviewScale { get; set; } = 0.5;

        /// <summary>Optional chain of named-output steps. When non-empty, replaces
        /// UserBulbSource. Final z = last step's return value.</summary>
        public List<UserBulbChainStep> UserBulbChain { get; set; } = new();

        /// <summary>
        /// Shared lighting + post-FX parameters consumed by every 3D raymarcher.
        /// Replaces per-fractal duplicates (Bulb*Light*, UserBulb*Light*, etc.)
        /// going forward. Defaults reproduce the pre-Phase-1 single-light look
        /// so renders are pixel-identical until a calculator opts in.
        /// </summary>
        public LightingFxData Lighting { get; set; } = LightingFxData.CreateDefault();

        // ── Interior alpha (2D) — issue #96 ──────────────────────────────────
        // Global opacity of the in-set (interior) region for 2D escape-time
        // fractals. Applies to the whole in-set region regardless of theme; a
        // per-theme interior alpha (color theme editor) is a planned follow-up.

        /// <summary>Global opacity of the in-set (interior) region, 0..255.
        /// 255 = opaque (legacy, pixel-identical). Below 255 the interior turns
        /// translucent and composites over <see cref="Interior2DBackground"/>.
        /// Scales any alpha a theme already authored (interior-aware maps emit
        /// opaque today, so this sets it). Currently honoured on the canonical
        /// Mandelbrot path only.</summary>
        public int InteriorAlpha { get; set; } = 255;

        /// <summary>Background composited behind translucent 2D pixels.
        /// Checkerboard (default) preserves the F10.5 see-through look; Solid /
        /// Gradient paint a colour backdrop; Transparent keeps straight alpha
        /// for export.</summary>
        public Interior2DBackgroundMode Interior2DBackground { get; set; }
            = Interior2DBackgroundMode.Checkerboard;

        /// <summary>Top colour of the Gradient background and the fill of the
        /// Solid background (packed 0xAARRGGBB).</summary>
        public uint Interior2DBgTop { get; set; } = 0xFF202040u;

        /// <summary>Bottom (horizon) colour of the Gradient background
        /// (packed 0xAARRGGBB).</summary>
        public uint Interior2DBgBottom { get; set; } = 0xFF101020u;

        /// <summary>Path to the image used when
        /// <see cref="Interior2DBackground"/> is <c>Image</c>. Stretched to fill
        /// the viewport; shows through translucent interior AND colour-stop
        /// pixels. Null/empty falls back to a flat fill.</summary>
        public string? Interior2DBgImagePath { get; set; }

        public FractalParameters Clone()
        {
            return new FractalParameters
            {
                JuliaC = JuliaC,
                MultibrotExponent = MultibrotExponent,
                PhoenixP = PhoenixP,
                GlynnC = GlynnC,
                LogisticBurnIn = LogisticBurnIn,
                LogisticSeed = LogisticSeed,
                NewtonPolyCoeffs = NewtonPolyCoeffs is null ? null : (Complex[])NewtonPolyCoeffs.Clone(),
                IFSMaps = IFSMaps is null ? null : new List<AffineMap>(IFSMaps),
                UserEquationSource = UserEquationSource,
                UserEquationName = UserEquationName,
                UserEquationRotationDegrees = UserEquationRotationDegrees,
                UserEquationDslSource = UserEquationDslSource,
                UserEquationActiveTab = UserEquationActiveTab,
                UserEquationSkipJacobian = UserEquationSkipJacobian,
                SandboxSource = SandboxSource,
                SandboxName = SandboxName,
                IFSPresetName = IFSPresetName,
                IFSIterations = IFSIterations,
                LSystemPresetName = LSystemPresetName,
                LSystemDepth = LSystemDepth,
                PlasmaRoughness = PlasmaRoughness,
                PlasmaSeed = PlasmaSeed,
                FlameMaps = FlameMaps is null ? null : new List<FlameMap>(FlameMaps),
                FlamePresetName = FlamePresetName,
                FlameIterations = FlameIterations,
                FlameGamma = FlameGamma,
                FlameVibrancy = FlameVibrancy,
                AttractorPresetName = AttractorPresetName,
                AttractorIterations = AttractorIterations,
                AttractorA = AttractorA, AttractorB = AttractorB,
                AttractorC = AttractorC, AttractorD = AttractorD,
                NewtonExponent = NewtonExponent,
                NewtonRelaxation = NewtonRelaxation,
                SecantInitialOffset = SecantInitialOffset,
                SpiderCDecay = SpiderCDecay,
                BuddhaSamples = BuddhaSamples,
                BuddhaIterLow = BuddhaIterLow,
                BuddhaIterMid = BuddhaIterMid,
                BuddhaIterHigh = BuddhaIterHigh,
                BuddhaColorMode = BuddhaColorMode,
                BuddhaQualityMode = BuddhaQualityMode,
                BuddhaMetropolis = BuddhaMetropolis,
                BuddhaProgressive = BuddhaProgressive,
                MandelboxScale = MandelboxScale,
                MandelboxFixedRadius = MandelboxFixedRadius,
                MandelboxMinRadius = MandelboxMinRadius,
                MandelboxIterations = MandelboxIterations,
                MandelboxBailout = MandelboxBailout,
                MandelboxCameraDistance = MandelboxCameraDistance,
                MandelboxCameraTheta = MandelboxCameraTheta,
                MandelboxCameraPhi = MandelboxCameraPhi,
                MandelboxLightTheta = MandelboxLightTheta,
                MandelboxLightPhi = MandelboxLightPhi,
                MandelboxMaxSteps = MandelboxMaxSteps,
                MandelboxEpsilon = MandelboxEpsilon,
                KifsFold = KifsFold,
                KifsIterations = KifsIterations,
                KifsScale = KifsScale,
                KifsOffsetX = KifsOffsetX,
                KifsOffsetY = KifsOffsetY,
                KifsOffsetZ = KifsOffsetZ,
                KifsBailout = KifsBailout,
                KifsCameraDistance = KifsCameraDistance,
                KifsCameraTheta = KifsCameraTheta,
                KifsCameraPhi = KifsCameraPhi,
                KifsLightTheta = KifsLightTheta,
                KifsLightPhi = KifsLightPhi,
                KifsMaxSteps = KifsMaxSteps,
                KifsEpsilon = KifsEpsilon,
                QJuliaCX = QJuliaCX,
                QJuliaCY = QJuliaCY,
                QJuliaCZ = QJuliaCZ,
                QJuliaCW = QJuliaCW,
                QJuliaSliceW = QJuliaSliceW,
                QJuliaIterations = QJuliaIterations,
                QJuliaBailout = QJuliaBailout,
                QJuliaCameraDistance = QJuliaCameraDistance,
                QJuliaCameraTheta = QJuliaCameraTheta,
                QJuliaCameraPhi = QJuliaCameraPhi,
                QJuliaLightTheta = QJuliaLightTheta,
                QJuliaLightPhi = QJuliaLightPhi,
                QJuliaMaxSteps = QJuliaMaxSteps,
                QJuliaEpsilon = QJuliaEpsilon,
                QMandelSliceW = QMandelSliceW,
                QMandelSliceZ = QMandelSliceZ,
                QMandelIterations = QMandelIterations,
                QMandelBailout = QMandelBailout,
                QMandelCameraDistance = QMandelCameraDistance,
                QMandelCameraTheta = QMandelCameraTheta,
                QMandelCameraPhi = QMandelCameraPhi,
                QMandelLightTheta = QMandelLightTheta,
                QMandelLightPhi = QMandelLightPhi,
                QMandelMaxSteps = QMandelMaxSteps,
                QMandelEpsilon = QMandelEpsilon,
                ApollonianDepth = ApollonianDepth,
                ApollonianMinPixelRadius = ApollonianMinPixelRadius,
                ApollonianColorByDepth = ApollonianColorByDepth,
                DlaParticles = DlaParticles,
                DlaSeed = DlaSeed,
                KleinianIterations = KleinianIterations,
                KleinianSphereScale = KleinianSphereScale,
                KleinianMaxSteps = KleinianMaxSteps,
                KleinianEpsilon = KleinianEpsilon,
                KleinianCameraDistance = KleinianCameraDistance,
                KleinianCameraTheta = KleinianCameraTheta,
                KleinianCameraPhi = KleinianCameraPhi,
                KleinianLightTheta = KleinianLightTheta,
                KleinianLightPhi = KleinianLightPhi,
                BicomplexSliceW = BicomplexSliceW,
                BicomplexSliceAxis = BicomplexSliceAxis,
                BicomplexIterations = BicomplexIterations,
                BicomplexBailout = BicomplexBailout,
                BicomplexCameraDistance = BicomplexCameraDistance,
                BicomplexCameraTheta = BicomplexCameraTheta,
                BicomplexCameraPhi = BicomplexCameraPhi,
                BicomplexLightTheta = BicomplexLightTheta,
                BicomplexLightPhi = BicomplexLightPhi,
                BicomplexMaxSteps = BicomplexMaxSteps,
                BicomplexEpsilon = BicomplexEpsilon,
                BulbPower = BulbPower,
                BulbIterations = BulbIterations,
                BulbCameraDistance = BulbCameraDistance,
                BulbCameraTheta = BulbCameraTheta,
                BulbCameraPhi = BulbCameraPhi,
                BulbLightTheta = BulbLightTheta,
                BulbLightPhi = BulbLightPhi,
                BulbMaxSteps = BulbMaxSteps,
                BulbEpsilon = BulbEpsilon,
                UserBulbSource = UserBulbSource,
                UserBulbName = UserBulbName,
                UserBulbIterations = UserBulbIterations,
                UserBulbBailout = UserBulbBailout,
                UserBulbCameraDistance = UserBulbCameraDistance,
                UserBulbCameraTheta = UserBulbCameraTheta,
                UserBulbCameraPhi = UserBulbCameraPhi,
                UserBulbLightTheta = UserBulbLightTheta,
                UserBulbLightPhi = UserBulbLightPhi,
                UserBulbMaxSteps = UserBulbMaxSteps,
                UserBulbEpsilon = UserBulbEpsilon,
                UserBulbJacobianH = UserBulbJacobianH,
                UserBulbCullRadius = UserBulbCullRadius,
                UserBulbKifsScale = UserBulbKifsScale,
                UserBulbDEMode = UserBulbDEMode,
                UserBulbTemporalReuse = UserBulbTemporalReuse,
                UserBulbBackend = UserBulbBackend,
                UserBulbAxisMode = UserBulbAxisMode,
                UserBulbCompiler = UserBulbCompiler,
                UserBulbQuatSliceW = UserBulbQuatSliceW,
                UserBulbParams = UserBulbParams.ConvertAll(p => p.Clone()),
                UserBulbTime = UserBulbTime,
                UserBulbJuliaMode = UserBulbJuliaMode,
                UserBulbJuliaCX = UserBulbJuliaCX,
                UserBulbJuliaCY = UserBulbJuliaCY,
                UserBulbJuliaCZ = UserBulbJuliaCZ,
                UserBulbJuliaCW = UserBulbJuliaCW,
                UserBulbColorDriver = UserBulbColorDriver,
                UserBulbOrbitTrapX = UserBulbOrbitTrapX,
                UserBulbOrbitTrapY = UserBulbOrbitTrapY,
                UserBulbOrbitTrapZ = UserBulbOrbitTrapZ,
                UserBulbIterComponentAxis = UserBulbIterComponentAxis,
                UserBulbLight1Intensity = UserBulbLight1Intensity,
                UserBulbLight1Color = UserBulbLight1Color,
                UserBulbLight2Theta = UserBulbLight2Theta,
                UserBulbLight2Phi = UserBulbLight2Phi,
                UserBulbLight2Intensity = UserBulbLight2Intensity,
                UserBulbLight2Color = UserBulbLight2Color,
                UserBulbLight3Theta = UserBulbLight3Theta,
                UserBulbLight3Phi = UserBulbLight3Phi,
                UserBulbLight3Intensity = UserBulbLight3Intensity,
                UserBulbLight3Color = UserBulbLight3Color,
                UserBulbShadowSoft = UserBulbShadowSoft,
                UserBulbAOSamples = UserBulbAOSamples,
                UserBulbAOStrength = UserBulbAOStrength,
                UserBulbFogDensity = UserBulbFogDensity,
                UserBulbBgTopColor = UserBulbBgTopColor,
                UserBulbBgBottomColor = UserBulbBgBottomColor,
                UserBulbFovDegrees = UserBulbFovDegrees,
                UserBulbDoFAperture = UserBulbDoFAperture,
                UserBulbDoFFocusDist = UserBulbDoFFocusDist,
                UserBulbDoFSamples = UserBulbDoFSamples,
                UserBulbClipPlaneEnabled = UserBulbClipPlaneEnabled,
                UserBulbClipPlaneNX = UserBulbClipPlaneNX,
                UserBulbClipPlaneNY = UserBulbClipPlaneNY,
                UserBulbClipPlaneNZ = UserBulbClipPlaneNZ,
                UserBulbClipPlaneD = UserBulbClipPlaneD,
                UserBulbSuperSample = UserBulbSuperSample,
                LowResPreviewScale = LowResPreviewScale,
                UserBulbChain = UserBulbChain.ConvertAll(s => s.Clone()),
                Lighting = Lighting, // struct value-copy; EnvironmentName is string (immutable)
                InteriorAlpha = InteriorAlpha,
                Interior2DBackground = Interior2DBackground,
                Interior2DBgTop = Interior2DBgTop,
                Interior2DBgBottom = Interior2DBgBottom,
                Interior2DBgImagePath = Interior2DBgImagePath
            };
        }
    }

    /// <summary>
    /// Affine map for IFS chaos game. x' = a·x + b·y + e, y' = c·x + d·y + f. Picked with weight.
    /// </summary>
    public readonly record struct AffineMap(double A, double B, double C, double D, double E, double F, double Weight);

    /// <summary>Apophysis-style variation. Each map runs its affine pre-
    /// transform, then a single non-linear "variation" warps the output.
    /// Slice 1 ships <see cref="Linear"/> (identity); slices 2–3 add the
    /// other stock variations and their tone-map / palette glue.</summary>
    public enum FlameVariation
    {
        /// <summary>v0 — identity. f(x,y) = (x, y).</summary>
        Linear = 0,
        /// <summary>v1 — sinusoidal. f = (sin x, sin y).</summary>
        Sinusoidal = 1,
        /// <summary>v2 — spherical. f = (x, y) / (x² + y²).</summary>
        Spherical = 2,
        /// <summary>v3 — swirl. r² = x²+y²; f = (x sin r² − y cos r², x cos r² + y sin r²).</summary>
        Swirl = 3,
        /// <summary>v5 — polar. θ = atan2(x,y); r = √(x²+y²); f = (θ/π, r − 1).</summary>
        Polar = 5,
        /// <summary>v6 — handkerchief unused; this slot is heart. f = r·(sin(θ·r), −cos(θ·r)).</summary>
        Heart = 6,
        /// <summary>v8 — disc. f = (θ/π · sin(π r), θ/π · cos(π r)).</summary>
        Disc = 8,
        /// <summary>v13 — julia. r = √(x²+y²); θ = atan2(x,y); φ = θ/2 + nπ;
        /// f = √r · (cos φ, sin φ).</summary>
        Julia = 13,

        // Wave 5.11 — next 10 Apophysis stock variations.

        /// <summary>v4 — horseshoe. f = (1/r) · (x² − y², 2 x y).</summary>
        Horseshoe = 4,
        /// <summary>v9 — spiral. f = (cos θ + sin r, sin θ − cos r) / r.</summary>
        Spiral = 9,
        /// <summary>v10 — hyperbolic. f = (sin θ / r, r · cos θ).</summary>
        Hyperbolic = 10,
        /// <summary>v11 — diamond. f = (sin θ · cos r, cos θ · sin r).</summary>
        Diamond = 11,
        /// <summary>v12 — ex. p = sin³(θ+r); q = cos³(θ−r);
        /// f = r · (p + q, p − q).</summary>
        Ex = 12,
        /// <summary>v14 — bent. Quadrant-dependent piecewise scale.</summary>
        Bent = 14,
        /// <summary>v16 — fisheye. f = (2 / (r+1)) · (y, x).</summary>
        Fisheye = 16,
        /// <summary>v18 — exponential. f = e^(x−1) · (cos(π y), sin(π y)).</summary>
        Exponential = 18,
        /// <summary>v19 — power. f = r^sin(θ) · (cos θ, sin θ).</summary>
        Power = 19,
        /// <summary>v20 — cosine. f = (cos(π x) · cosh y, −sin(π x) · sinh y).</summary>
        Cosine = 20,
    }

    /// <summary>
    /// Flame fractal map. Pre-affine + up to two blended non-linear
    /// variations + post-affine + per-map colour index. Apophysis-equivalent.
    ///
    /// Pre-affine:  p  = A·v + t
    /// Variations:  q  = Σ amount_i · V_i(p)
    /// Post-affine: q' = P·q + tp
    ///
    /// The post-affine (Pa..Pf) defaults to the identity (1, 0, 0, 1, 0, 0)
    /// so single-variation legacy presets behave as before.
    /// </summary>
    public readonly record struct FlameMap(
        double A, double B, double C, double D, double E, double F,
        double Weight,
        FlameVariation Variation,
        double VariationAmount,
        double ColorIndex,
        FlameVariation Variation2 = FlameVariation.Linear,
        double VariationAmount2 = 0.0,
        double Pa = 1.0, double Pb = 0.0, double Pc = 0.0, double Pd = 1.0,
        double Pe = 0.0, double Pf = 0.0);

    /// <summary>KIFS fold table choice. Each value selects a different
    /// reflective-fold + scale combination inside KifsCalculator's DE.</summary>
    public enum KifsFoldKind
    {
        /// <summary>Menger sponge fold — Knighty's sort-3 + scale-3
        /// from (1,1,1). Produces the classic cube-with-holes shape.</summary>
        Menger,
        /// <summary>Sierpinski tetrahedron fold — 3 vertex reflections +
        /// scale-2 from (1,1,1). Produces the tetra gasket.</summary>
        Sierpinski,
        /// <summary>Octahedron fold — Menger's sort-3 without the
        /// corner-mirror Z-fold. Yields the octahedral gasket dual
        /// of the Sierpinski tetra. Scale 2 default.</summary>
        Octahedron,
        /// <summary>Dodecahedron fold — three φ-based plane mirrors
        /// (Knighty). Produces icosahedral / pentagonal symmetry.</summary>
        Dodecahedron,
        /// <summary>Mandelbox-style box-fold at ±1 + per-iter Y-axis
        /// rotation (~7.5°) + scale. Produces a Mandelbox-flavoured
        /// twisted-cube limit set inside the fixed-dr KIFS scheme.</summary>
        MandelboxRot,
    }

    /// <summary>
    /// Bicomplex Mandelbrot 4D slice-axis selector. The pixel maps (x, y, z)
    /// onto three of the four algebra basis vectors (1, i, j, k); the fourth
    /// takes <see cref="FractalParameters.BicomplexSliceW"/>. Bicomplex
    /// algebra is commutative, so the resulting iteration math has a clean
    /// dependence on which axis routes the constant.
    /// </summary>
    public enum BicomplexSliceAxis
    {
        /// <summary>k-axis (default — visually similar to quat Mandelbrot
        /// on the (i,j) slice, with zero-divisor seam slabs when sliceW != 0).</summary>
        K = 0,
        /// <summary>j-axis (slice constant rides on the imaginary-j slot,
        /// pixel walks (1, i, k) — exposes the k²=+1 split direction).</summary>
        J = 1,
        /// <summary>i-axis (slice constant rides on i, pixel walks (1, j, k)).</summary>
        I = 2,
        /// <summary>Real axis (constant rides on the scalar slot — exposes the
        /// 3D (i, j, k) imaginary-only slice).</summary>
        R = 3,
    }

    public enum UserBulbDEModeKind
    {
        Auto,
        Analytic,
        Numerical,
    }

    public enum UserBulbBackendKind
    {
        CPU,
        GPU,
    }

    public enum UserBulbAxisModeKind
    {
        Vec3,
        Quat,
    }

    /// <summary>Step-function compiler. Roslyn = full C# expression body with
    /// BCL access (legacy default, GPU-translatable). Sandbox = restricted
    /// DSL parsed by SandboxBulbExpression — no BCL, safe to share, but
    /// CPU-only and slightly slower per Step call.</summary>
    public enum UserBulbCompilerKind
    {
        Roslyn,
        Sandbox,
    }

    /// <summary>Output blend for the Buddhabrot family.</summary>
    public enum BuddhaColorMode
    {
        /// <summary>Classic three-band R/G/B composite. Three iter windows
        /// (Low/Mid/High) → three hit buffers → log-normalised channels.</summary>
        NebulabrotBands,
        /// <summary>Single hit buffer, log-normalised, driven through the
        /// active <see cref="IColorMap"/>. High iter cap = MaxIterations.</summary>
        ColorMap,
    }

    /// <summary>Render quality for the Buddhabrot family.</summary>
    public enum BuddhaQualityMode
    {
        /// <summary>Classic nearest-pixel splat, per-channel log normalisation.
        /// Fastest; matches reference Buddhabrot output.</summary>
        Standard,
        /// <summary>Stochastic bilinear splat (subpixel anti-aliasing),
        /// real-axis mirror sampling (free 2× effective samples), joint
        /// channel normalisation, low-hit noise-floor reject. Slower but
        /// markedly smoother filaments and clean background.</summary>
        HighDefinition,
    }

    public enum BulbColorDriver
    {
        StepDepth,
        OrbitTrap,
        EscapeAngle,
        FinalMagnitude,
        IterComponent,
        Normal,
    }
}
