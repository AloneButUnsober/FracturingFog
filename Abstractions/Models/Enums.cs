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
    }

    public enum RenderProfile { Preview, Final }

}
