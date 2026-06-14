using System;

namespace FracturingFog
{
    public enum QualityLevel { Fast, Normal, High, Ultra }

    public enum FractalType
    {
        Mandelbrot,
        Julia,
        BurningShip,
        Tricorn,
        Multibrot,
        Phoenix,
        Newton,
        Nova,
        BuddhaBrot,
        /// <summary>3-band escape-orbit replay (low/mid/high iteration windows
        /// → R/G/B channels). Same Monte Carlo sampler as Buddhabrot but emits
        /// the classic Nebulabrot composite directly.</summary>
        Nebulabrot,
        /// <summary>Buddhabrot's complement — replays orbits of points that
        /// stay bounded (do NOT escape within MaxIter). Single-channel
        /// histogram driven through the active ColorMap.</summary>
        AntiBuddhabrot,
        /// <summary>3-band variant of AntiBuddhabrot. Bands split by orbit
        /// length within MaxIter; channel selection mirrors Nebulabrot.</summary>
        AntiNebulabrot,
        IFS,
        LSystem,
        StrangeAttractor,
        UserEquation,
        Mandelbulb,
        Sandbox,
        UserBulb,
        TearDrop,
        /// <summary>CalculatorGen-emitted z² + c (drop-in replacement for the
        /// hand-tuned MandelbrotCalculator demo). Carries the generator's full
        /// pipeline: scalar + AVX2 + ILGPU GPU + perturbation + BLA. Exposed
        /// so the toolbar can switch between the legacy implementation and
        /// the generated one for direct A/B comparisons.</summary>
        GeneratedMandelbrotZ2,
        /// <summary>CalculatorGen-emitted z³ + c. Same generator pipeline as
        /// GeneratedMandelbrotZ2 — different exponent. The orbit and set
        /// shape differ visually from Mandelbrot.</summary>
        GeneratedMandelbrotZ3,
        /// <summary>CalculatorGen-emitted z⁴ + c.</summary>
        GeneratedMandelbrotZ4,
        /// <summary>CalculatorGen-emitted z⁵ + c.</summary>
        GeneratedMandelbrotZ5,
        /// <summary>CalculatorGen-emitted conj(z)² + c (Tricorn). Anti-
        /// holomorphic — distance estimate disabled, smooth-count only.</summary>
        GeneratedTricorn,
        /// <summary>CalculatorGen-emitted (|Re(z)| + i|Im(z)|)² + c
        /// (BurningShip). Non-holomorphic — DE disabled.</summary>
        GeneratedBurningShip,
        /// <summary>Magnet 1 (Pickover). Rational map
        /// z = ((z² + c − 1) / (2z + c − 2))² with a pole-clamped
        /// denominator. Bailout 10² because the orbit grows more
        /// slowly than the polynomial families.</summary>
        Magnet1,
        /// <summary>Magnet 2 (Pickover). Rational map
        /// z = ((z³ + 3(c−1)z + (c−1)(c−2)) /
        ///       (3z² + 3(c−2)z + c² − 3c + 3))²
        /// with pole-clamped denominator. Bailout 10².</summary>
        Magnet2,
        /// <summary>Glynn fractal (Earl Glynn, 1990s). Julia set of
        /// z → z^1.5 + c at the canonical c ≈ −0.2. Fractional power
        /// evaluated via polar form; non-holomorphic at the origin
        /// branch cut.</summary>
        Glynn,
        /// <summary>Logistic bifurcation diagram. x_{n+1} = r·x_n·(1−x_n)
        /// rendered as a per-column density histogram over (r, x). Not
        /// escape-time — handled by a dedicated <c>LogisticCalculator</c>
        /// alongside the Buddhabrot histogram path.</summary>
        Logistic,
        /// <summary>Halley basins for f(z) = z^d − 1. Cubic-convergence
        /// root-finding (z := z − 2 f f' / (2 f'² − f f'')). Reuses
        /// <c>NewtonExponent</c> + <c>NewtonRelaxation</c>; basin
        /// colouring is identical to Newton.</summary>
        Halley,
        /// <summary>Secant basins for f(z) = z^d − 1. Two-point
        /// recurrence (z_{n+1} = z_n − f(z_n)·(z_n − z_{n−1}) /
        /// (f(z_n) − f(z_{n−1}))) — derivative-free root-finder. Per-
        /// pixel state carries z and z_{n−1}; initial offset is
        /// tunable via <c>SecantInitialOffset</c>.</summary>
        Secant,
        /// <summary>Spider fractal. Two-state recurrence
        /// z = z² + c, c = decay·c + z. c mutates per iteration —
        /// routed through a dedicated <c>CalculateSpider</c> path.
        /// Decay tunable via <c>SpiderCDecay</c>; 0.5 is classic
        /// Spider, 1.0 degenerates to Mandelbrot.</summary>
        Spider,
        /// <summary>Mandelbox (Tom Lowe, 2010). 3D box-fold +
        /// sphere-fold + scale iteration rendered via distance-
        /// estimation raymarching. Tunable via
        /// <c>MandelboxScale</c> (default 2.0; ≈−1.5, 2.0, 3.0 are
        /// classics), <c>MandelboxFixedRadius</c>,
        /// <c>MandelboxMinRadius</c>, plus dedicated camera /
        /// light fields. Distance estimate tracks a scalar dz
        /// magnitude through folds.</summary>
        Mandelbox,
        /// <summary>Kaleidoscopic IFS (KIFS) — repeated reflective fold +
        /// scale-from-pivot. Two built-in fold tables: Menger sponge
        /// (sort-3 + scale-3) and Sierpinski tetrahedron (3 vertex
        /// reflections + scale-2). DE: (|z|−κ) / scale^n. Tunables on
        /// <c>FractalParameters</c>: <c>KifsFold</c>, <c>KifsIterations</c>,
        /// <c>KifsScale</c>, <c>KifsOffsetX/Y/Z</c>, plus shared camera /
        /// light fields. Rendered via distance-estimation raymarching
        /// alongside the Mandelbulb and Mandelbox paths.</summary>
        Kifs,
        /// <summary>Quaternion Julia (Hart 1989). Iteration
        /// q = q² + c with q, c ∈ ℍ (Hamilton quaternions). Renderer
        /// raymarches a 3D slice through the 4D set — pixel coordinate
        /// (x,y,z) becomes q = (x,y,z, <c>QJuliaSliceW</c>). DE uses the
        /// Hubbard–Douady estimator <c>0.5·|q|·ln|q| / |dq|</c> with the
        /// derivative tracked as a quaternion dq through the Hamilton
        /// product. Tunables: <c>QJuliaCX/Y/Z/W</c> (constant c),
        /// <c>QJuliaSliceW</c>, plus shared iter / bailout / camera /
        /// light fields.</summary>
        QuaternionJulia,
        /// <summary>Plasma (diamond-square midpoint displacement).
        /// Procedural 2D noise field with fractional-Brownian statistics —
        /// not strictly a fractal but visually fractal-like. Rendered by
        /// <c>PlasmaCalculator</c> in a single pass: generate the (2ⁿ+1)²
        /// height grid, normalise, sample through the active
        /// <c>IColorMap</c>. Tunables on <c>FractalParameters</c>:
        /// <c>PlasmaRoughness</c> (0 = smooth gradient, 1 = full
        /// amplitude / very rough), <c>PlasmaSeed</c>. Pan/zoom is a
        /// no-op — the generated field IS the image.</summary>
        Plasma,
    }

    public enum RenderProfile { Preview, Final }

}
