using System;
using System.Collections.Generic;
using System.Numerics;

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
        /// Sandbox = restricted DSL (no BCL, shareable, Vec3-only).</summary>
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
        /// <summary>Optional chain of named-output steps. When non-empty, replaces
        /// UserBulbSource. Final z = last step's return value.</summary>
        public List<UserBulbChainStep> UserBulbChain { get; set; } = new();

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
                SandboxSource = SandboxSource,
                SandboxName = SandboxName,
                IFSPresetName = IFSPresetName,
                IFSIterations = IFSIterations,
                LSystemPresetName = LSystemPresetName,
                LSystemDepth = LSystemDepth,
                PlasmaRoughness = PlasmaRoughness,
                PlasmaSeed = PlasmaSeed,
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
                UserBulbChain = UserBulbChain.ConvertAll(s => s.Clone())
            };
        }
    }

    /// <summary>
    /// Affine map for IFS chaos game. x' = a·x + b·y + e, y' = c·x + d·y + f. Picked with weight.
    /// </summary>
    public readonly record struct AffineMap(double A, double B, double C, double D, double E, double F, double Weight);

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
