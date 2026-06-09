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
    }

    public enum RenderProfile { Preview, Final }

}
