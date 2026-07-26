// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog
{
    public enum QualityLevel { Fast, Normal, High, Ultra }

    /// <summary>
    /// Background composited behind translucent 2D pixels when the interior
    /// (in-set) region carries alpha &lt; 255 (issue #96). Only consulted by the
    /// 2D present path; 3D raymarchers use <c>LightingFxData.SkyMode</c> instead.
    /// </summary>
    public enum Interior2DBackgroundMode
    {
        /// <summary>Grey checkerboard — the F10.5 see-through editing aid.
        /// Default so the on-screen look is unchanged from the alpha-preview
        /// behaviour and translucency always reads as "see-through".</summary>
        Checkerboard,
        /// <summary>Flat fill from <c>Interior2DBgTop</c>.</summary>
        SolidColor,
        /// <summary>Vertical two-colour gradient, top = <c>Interior2DBgTop</c>,
        /// bottom = <c>Interior2DBgBottom</c>.</summary>
        Gradient,
        /// <summary>Image backdrop sampled from <c>Interior2DBgImagePath</c>,
        /// stretched to fill the viewport. Shows through both translucent
        /// interior pixels and translucent exterior colour stops.</summary>
        Image,
        /// <summary>No composite — keep straight alpha. The on-screen present is
        /// forced opaque, so this reads as opaque interior on screen and is only
        /// meaningful for PNG export (which preserves the authored alpha).</summary>
        Transparent,
    }

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
        /// <summary>Quaternion Mandelbrot (Norton 1982). Same Hamilton-product
        /// squaring map as <c>QuaternionJulia</c> (q = q² + c), but c varies
        /// per pixel — the 3D raymarch walks (x, y, z) through the 4D c-space
        /// with the 4th component pinned to <c>QMandelSliceW</c>. Orbit q
        /// starts at the origin (membership test). DE uses the Hubbard–Douady
        /// estimator with derivative dq/dc updated as
        /// <c>dq := 2·q·dq + 1</c>. Tunables: <c>QMandelSliceW</c>, plus
        /// shared iter / bailout / camera / light fields.</summary>
        QuaternionMandelbrot,
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
        /// <summary>Flame fractal (Apophysis-style). IFS chaos game with
        /// per-map non-linear "variation" (linear, sinusoidal, spherical,
        /// swirl, polar, heart, disc, julia), gamma-corrected log-density
        /// tone-map, and per-map colour index blended through the active
        /// gradient palette. Rendered by <c>FlameRenderer</c>. Tunables on
        /// <c>FractalParameters</c>: <c>FlamePresetName</c>,
        /// <c>FlameIterations</c>, <c>FlameGamma</c>, <c>FlameVibrancy</c>,
        /// plus an optional explicit <c>FlameMaps</c> list. Pan/zoom work
        /// via the standard IFS-style attractor-fit convention.</summary>
        Flame,
        /// <summary>Apollonian gasket — recursive circle packing built from the
        /// integral (−1, 2, 2, 3) seed quadruple. Each child quadruple is
        /// generated by Vieta jumping (Descartes Circle Theorem reflection) one
        /// non-enclosing circle through the other three; recursion stops when
        /// the next circle's radius drops below
        /// <c>ApollonianMinPixelRadius</c> device pixels or depth exceeds
        /// <c>ApollonianDepth</c>. Painted big-to-small so the natural nesting
        /// emerges from overdraw. Tunables on <c>FractalParameters</c>:
        /// <c>ApollonianDepth</c>, <c>ApollonianMinPixelRadius</c>,
        /// <c>ApollonianColorByDepth</c>. Pan/zoom supported — deeper detail
        /// auto-reveals as the device pixel pitch shrinks.</summary>
        Apollonian,
        /// <summary>Kleinian limit set — 3D fractal produced by iterated
        /// inversion in a Schottky-style group of reflection spheres. First
        /// cut ships a tetrahedral 4-sphere preset (radius √2 at the four
        /// even-parity ±1 corners). DE = signed nearest-sphere distance /
        /// accumulated inversion-scale product, raymarched by sphere tracing
        /// with finite-difference normals and Phong shading. Tunables on
        /// <c>FractalParameters</c>: <c>KleinianIterations</c>,
        /// <c>KleinianSphereScale</c> (loosens / tightens the tetrahedral
        /// packing), plus shared camera / light fields.</summary>
        Kleinian,
        /// <summary>Bicomplex (tessarine) Mandelbrot. Iteration t := t² + c
        /// with t, c in the commutative 4D algebra spanned by (1, i, j, k)
        /// under i² = j² = −1, k² = +1, ij = ji = k. Renderer raymarches a
        /// 3D slice — pixel (x, y, z) ↦ c = (x, y, z, <c>BicomplexSliceW</c>).
        /// Orbit t starts at the origin; derivative dt/dc tracked through
        /// chain rule for Hubbard–Douady DE. Visually overlaps the quaternion
        /// Mandelbrot on the (i, j = 0) slice but introduces zero-divisor
        /// seam slabs (commutativity + k² = +1) absent from Hamilton-algebra
        /// renderings. Tunables: <c>BicomplexSliceW</c>, plus shared iter /
        /// bailout / camera / light fields.</summary>
        BicomplexMandelbrot,
        /// <summary>Diffusion-Limited Aggregation (Witten–Sander 1981).
        /// Stochastic 2D fractal: a seed cell sits at the grid centre;
        /// particles spawn on a launch circle just outside the current
        /// aggregate, random-walk one cell per step, and stick the first time
        /// they land adjacent to the aggregate. The resulting Brownian-tree
        /// dendrite has fractal dimension ≈ 1.71. Tunables on
        /// <c>FractalParameters</c>: <c>DlaParticles</c>, <c>DlaSeed</c>.
        /// Pan/zoom unsupported — the simulation IS the image and pan/zoom
        /// would invalidate the cached grid.</summary>
        Dla,
    }

    public enum RenderProfile { Preview, Final }

    /// <summary>
    /// Bitmask describing which per-pixel data a fractal calculator surfaces to
    /// the active <see cref="FracturingFog.Interefaces.IColorMap"/> at render
    /// time. Used by the slideshow / video slideshow + UI to filter out themes
    /// whose required data the active fractal cannot supply (e.g. orbit-trap
    /// themes need <see cref="SuppliesOrbit"/>, interior themes need
    /// <see cref="SuppliesInterior"/>, Phong/PBR 3D themes need
    /// <see cref="SuppliesNormals"/>).
    /// </summary>
    [Flags]
    public enum FractalCapabilities
    {
        None               = 0,
        /// <summary>Calculator fills <c>nx, ny</c> surface-normal channels
        /// (calls the 5-param <c>Map(..., nx, ny)</c> overload).</summary>
        SuppliesNormals    = 1 << 0,
        /// <summary>Calculator supplies a valid exterior distance estimate.</summary>
        SuppliesDE         = 1 << 1,
        /// <summary>Calculator routes orbit-aware themes through a scalar path
        /// that calls <c>IOrbitAwareColorMap.Sample</c> once per iteration.</summary>
        SuppliesOrbit      = 1 << 2,
        /// <summary>Calculator runs cycle detection over in-set pixels and
        /// dispatches to <c>IInteriorAwareColorMap.MapInterior</c>.</summary>
        SuppliesInterior   = 1 << 3,
        /// <summary>Calculator passes <c>finalZr, finalZi</c> at escape (9-param overload).</summary>
        SuppliesFinalZ     = 1 << 4,
        /// <summary>Calculator passes <c>dzdcR, dzdcI</c> at escape (9-param overload).</summary>
        SuppliesDerivative = 1 << 5,
        /// <summary>Calculator produces output amenable to histogram equalisation
        /// (smooth-count distribution dense enough that the EQ slider helps).</summary>
        SuppliesHistogram  = 1 << 6,
    }

    /// <summary>
    /// Per-<see cref="FractalType"/> lookup of which calculator features are
    /// available to colour maps at render time. Single source of truth for
    /// theme / fractal compatibility filtering. Add new fractal types to the
    /// switch — the default arm conservatively returns <c>None</c>.
    /// </summary>
    public static class FractalCapabilityMap
    {
        public static FractalCapabilities For(FractalType ft) => ft switch
        {
            // Holomorphic 2D escape-time set — full pipeline.
            FractalType.Mandelbrot
                or FractalType.Julia
                or FractalType.Multibrot
                or FractalType.GeneratedMandelbrotZ2
                or FractalType.GeneratedMandelbrotZ3
                or FractalType.GeneratedMandelbrotZ4
                or FractalType.GeneratedMandelbrotZ5
                or FractalType.Phoenix
                or FractalType.Spider
                or FractalType.Magnet1
                or FractalType.Magnet2
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesDE
                 | FractalCapabilities.SuppliesOrbit
                 | FractalCapabilities.SuppliesInterior
                 | FractalCapabilities.SuppliesFinalZ
                 | FractalCapabilities.SuppliesDerivative
                 | FractalCapabilities.SuppliesHistogram,

            // Antiholomorphic / non-holomorphic 2D. conj(z)² (and |z| in Burning
            // Ship) is not complex-differentiable, so there is no exact analytic
            // dz/dc. The kernels still track an *approximate* derivative (see
            // TricornKernel: "track as if Mandelbrot"), and the escape-time /
            // generated / TearDrop calculators already fill DistanceBuffer from
            // it — so SuppliesDE exposes the DistanceField theme family with a
            // plausible (not metrically exact) estimate. SuppliesDerivative is
            // withheld: derivative-bailout themes assume an analytic dz/dc.
            FractalType.BurningShip
                or FractalType.Tricorn
                or FractalType.GeneratedBurningShip
                or FractalType.GeneratedTricorn
                or FractalType.Glynn
                or FractalType.TearDrop
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesDE
                 | FractalCapabilities.SuppliesOrbit
                 | FractalCapabilities.SuppliesFinalZ
                 | FractalCapabilities.SuppliesHistogram,

            // Newton-like root finders — basins + iteration shading.
            FractalType.Newton
                or FractalType.Nova
                or FractalType.Halley
                or FractalType.Secant
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesFinalZ
                 | FractalCapabilities.SuppliesHistogram,

            // 3D distance-estimation raymarchers — normals + DE only.
            FractalType.Mandelbulb
                or FractalType.Kleinian
                or FractalType.Mandelbox
                or FractalType.QuaternionJulia
                or FractalType.QuaternionMandelbrot
                or FractalType.Kifs
                or FractalType.BicomplexMandelbrot
                or FractalType.UserBulb
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesDE,

            // User-equation / Sandbox — orbit-aware via IOrbitAwareColorMap
            // dispatch in the per-pixel loop (P5). Interior (Brent cycle
            // detection) remains out of scope.
            FractalType.UserEquation
                or FractalType.Sandbox
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesOrbit
                 | FractalCapabilities.SuppliesHistogram,

            // Apollonian gasket — direct-color circle packing. Not escape-time,
            // but each disk is painted as a lit sphere-imposter that supplies a
            // per-pixel surface normal (nx, ny) to the 3D Phong/Relief themes
            // (ApollonianCalculator.PaintDisk). No orbit / DE / final-z data.
            FractalType.Apollonian
                => FractalCapabilities.SuppliesNormals
                 | FractalCapabilities.SuppliesHistogram,

            // Histogram / chaos-game families — no normals, no orbit data
            // surfaced to per-pixel themes (each calculator paints through the
            // 3-param Map overload only).
            FractalType.IFS
                or FractalType.LSystem
                or FractalType.StrangeAttractor
                or FractalType.BuddhaBrot
                or FractalType.Nebulabrot
                or FractalType.AntiBuddhabrot
                or FractalType.AntiNebulabrot
                or FractalType.Dla
                or FractalType.Flame
                or FractalType.Plasma
                or FractalType.Logistic
                => FractalCapabilities.SuppliesHistogram,

            _ => FractalCapabilities.None,
        };
    }

}
