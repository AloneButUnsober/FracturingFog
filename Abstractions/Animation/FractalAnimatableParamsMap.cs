using System;
using System.Collections.Generic;
using FracturingFog;

namespace FracturingFog.Abstractions.Animation;

/// <summary>
/// Per-<see cref="FractalType"/> registry of which fields on
/// <see cref="FracturingFog.Models.FractalParameters"/> can be animated.
/// Peer of <see cref="FractalCapabilityMap"/> — same shape, different
/// question. CapabilityMap answers "what does this fractal produce?";
/// this map answers "what does this fractal consume that's safe to
/// animate?".
/// <para>
/// Source of truth is hand-maintained against the per-type visibility
/// blocks in <c>UI.Avalonia/Views/FractalParamsView.axaml</c>. Default
/// values + Min/Max derive from the property setters' clamp ranges in
/// <c>FractalParameters.cs</c> and the Animation Roadmap appendix.
/// Default arm returns an empty list — fractal types with no animatable
/// params (e.g. <see cref="FractalType.IFS"/> with only preset selection)
/// stay out of the registry intentionally.
/// </para>
/// </summary>
public static class FractalAnimatableParamsMap
{
    public static IReadOnlyList<AnimatableParamDescriptor> For(FractalType ft) => ft switch
    {
        // ── 2D escape-time — Julia c orbit, related complex params ────────
        FractalType.Julia
            => _juliaList,

        FractalType.Multibrot
            => _multibrotList,

        FractalType.Phoenix
            => _phoenixList,

        FractalType.Glynn
            => _glynnList,

        FractalType.Spider
            => _spiderList,

        // Newton / Halley / Secant share the polynomial-exponent + relaxation
        // pair. Secant also exposes an initial-offset complex.
        FractalType.Newton or FractalType.Nova or FractalType.Halley
            => _newtonList,

        FractalType.Secant
            => _secantList,

        FractalType.Logistic
            => _logisticList,

        // ── Procedural / chaos-game ───────────────────────────────────────
        FractalType.IFS
            => _ifsList,

        FractalType.LSystem
            => _lsystemList,

        FractalType.StrangeAttractor
            => _attractorList,

        FractalType.Plasma
            => _plasmaList,

        FractalType.Flame
            => _flameList,

        FractalType.Apollonian
            => _apollonianList,

        FractalType.Dla
            => _dlaList,

        // ── 3D raymarched ─────────────────────────────────────────────────
        FractalType.Mandelbulb
            => _mandelbulbList,

        FractalType.UserBulb
            => _userBulbList,

        FractalType.Mandelbox
            => _mandelboxList,

        FractalType.Kifs
            => _kifsList,

        FractalType.QuaternionJulia
            => _quaternionJuliaList,

        FractalType.QuaternionMandelbrot
            => _quaternionMandelbrotList,

        FractalType.Kleinian
            => _kleinianList,

        FractalType.BicomplexMandelbrot
            => _bicomplexList,

        // ── User-defined 2D ───────────────────────────────────────────────
        FractalType.UserEquation
            => _userEquationList,

        // Types with no animatable scalars in MVP (Mandelbrot itself,
        // TearDrop, Buddhabrot family, generated polynomial families,
        // Sandbox, Magnet 1/2, the burning-ship / tricorn variants). They
        // *can* host an animated Lighting block once Phase 2 lands, but
        // that's a cross-type concern — handled separately.
        _ => Array.Empty<AnimatableParamDescriptor>(),
    };

    // ── Static lists — built once, returned by reference ──────────────────
    // Names match public property names on FractalParameters.cs verbatim.
    // Reflection in the round-trip test enforces that.

    private static readonly AnimatableParamDescriptor[] _juliaList =
    {
        new("JuliaC", AnimatableParamKind.Complex, Min: 0.05, Max: 1.5,
            Notes: "Classic orbit-around-the-Mandelbrot-boundary sweep."),
    };

    private static readonly AnimatableParamDescriptor[] _multibrotList =
    {
        new("MultibrotExponent", AnimatableParamKind.ScalarInt, Min: 2, Max: 8),
    };

    private static readonly AnimatableParamDescriptor[] _phoenixList =
    {
        new("PhoenixP", AnimatableParamKind.Complex, Min: 0.0, Max: 1.5),
    };

    private static readonly AnimatableParamDescriptor[] _glynnList =
    {
        new("GlynnC", AnimatableParamKind.Complex, Min: 0.05, Max: 1.0),
    };

    private static readonly AnimatableParamDescriptor[] _spiderList =
    {
        new("SpiderCDecay", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0),
    };

    private static readonly AnimatableParamDescriptor[] _newtonList =
    {
        new("NewtonExponent", AnimatableParamKind.ScalarInt, Min: 2, Max: 8),
        new("NewtonRelaxation", AnimatableParamKind.ScalarDouble, Min: 0.1, Max: 2.0),
    };

    private static readonly AnimatableParamDescriptor[] _secantList =
    {
        new("NewtonExponent", AnimatableParamKind.ScalarInt, Min: 2, Max: 8),
        new("NewtonRelaxation", AnimatableParamKind.ScalarDouble, Min: 0.1, Max: 2.0),
        new("SecantInitialOffset", AnimatableParamKind.Complex, Min: 0.05, Max: 1.5),
    };

    private static readonly AnimatableParamDescriptor[] _logisticList =
    {
        new("LogisticBurnIn", AnimatableParamKind.ScalarInt, Min: 0, Max: 5000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Re-runs full burn-in accumulator each tick — animate slowly."),
        new("LogisticSeed", AnimatableParamKind.ScalarDouble, Min: 0.001, Max: 0.999,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Re-runs full burn-in accumulator each tick."),
    };

    private static readonly AnimatableParamDescriptor[] _ifsList =
    {
        new("IFSIterations", AnimatableParamKind.ScalarInt, Min: 100_000, Max: 20_000_000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Resamples the entire IFS each tick — clamp animation rate."),
    };

    private static readonly AnimatableParamDescriptor[] _lsystemList =
    {
        new("LSystemDepth", AnimatableParamKind.ScalarInt, Min: 0, Max: 12,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Exponential growth in element count — sweep slowly."),
    };

    private static readonly AnimatableParamDescriptor[] _attractorList =
    {
        new("AttractorA", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
        new("AttractorB", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
        new("AttractorC", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
        new("AttractorD", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
    };

    private static readonly AnimatableParamDescriptor[] _plasmaList =
    {
        new("PlasmaRoughness", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0),
        new("PlasmaSeed", AnimatableParamKind.ScalarInt, Min: 0, Max: 1_000_000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Seed change regenerates the noise field — flashes at high rates."),
    };

    private static readonly AnimatableParamDescriptor[] _flameList =
    {
        new("FlameGamma", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 4.0),
        new("FlameVibrancy", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0),
    };

    private static readonly AnimatableParamDescriptor[] _apollonianList =
    {
        new("ApollonianDepth", AnimatableParamKind.ScalarInt, Min: 2, Max: 36,
            Notes: "Recursive — high values cost cubic-ish."),
        new("ApollonianMinPixelRadius", AnimatableParamKind.ScalarDouble, Min: 0.25, Max: 4.0),
    };

    private static readonly AnimatableParamDescriptor[] _dlaList =
    {
        new("DlaParticles", AnimatableParamKind.ScalarInt, Min: 1000, Max: 50_000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Each tick re-runs the random-walk simulation. Animate slowly."),
    };

    private static readonly AnimatableParamDescriptor[] _mandelbulbList =
    {
        new("BulbPower", AnimatableParamKind.ScalarDouble, Min: 2.0, Max: 12.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Classic 2→8 sweep produces the canonical bulb morph."),
        new("BulbIterations", AnimatableParamKind.ScalarInt, Min: 4, Max: 16,
            Cost: AnimatableParamCost.Moderate),
    };

    private static readonly AnimatableParamDescriptor[] _userBulbList =
    {
        new("BulbPower", AnimatableParamKind.ScalarDouble, Min: 2.0, Max: 12.0,
            Cost: AnimatableParamCost.Moderate),
        new("UserBulbIterations", AnimatableParamKind.ScalarInt, Min: 4, Max: 16,
            Cost: AnimatableParamCost.Moderate),
        new("UserBulbTime", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1000.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Monotonic time uniform — set linear motion, never bounded."),
    };

    private static readonly AnimatableParamDescriptor[] _mandelboxList =
    {
        new("MandelboxScale", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Avoid scale ≈ ±1 — degenerate; raise bailout or skip."),
        new("MandelboxFixedRadius", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 2.0,
            Cost: AnimatableParamCost.Moderate),
        new("MandelboxMinRadius", AnimatableParamKind.ScalarDouble, Min: 0.1, Max: 1.0,
            Cost: AnimatableParamCost.Moderate),
        new("MandelboxIterations", AnimatableParamKind.ScalarInt, Min: 4, Max: 32,
            Cost: AnimatableParamCost.Moderate),
    };

    private static readonly AnimatableParamDescriptor[] _kifsList =
    {
        new("KifsScale", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 4.0,
            Cost: AnimatableParamCost.Moderate),
        new("KifsOffsetX", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Cost: AnimatableParamCost.Moderate),
        new("KifsOffsetY", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Cost: AnimatableParamCost.Moderate),
        new("KifsOffsetZ", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Cost: AnimatableParamCost.Moderate),
        new("KifsIterations", AnimatableParamKind.ScalarInt, Min: 4, Max: 32,
            Cost: AnimatableParamCost.Moderate),
    };

    private static readonly AnimatableParamDescriptor[] _quaternionJuliaList =
    {
        new("QJuliaCX", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate),
        new("QJuliaCY", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate),
        new("QJuliaCZ", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate),
        new("QJuliaCW", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate),
        new("QJuliaSliceW", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Slices through the 4D set — the iconic quaternion morph."),
    };

    private static readonly AnimatableParamDescriptor[] _quaternionMandelbrotList =
    {
        new("QMandelSliceW", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Slices through the 4D set."),
    };

    private static readonly AnimatableParamDescriptor[] _kleinianList =
    {
        new("KleinianSphereScale", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 1.5,
            Cost: AnimatableParamCost.Moderate),
        new("KleinianIterations", AnimatableParamKind.ScalarInt, Min: 4, Max: 32,
            Cost: AnimatableParamCost.Moderate),
    };

    private static readonly AnimatableParamDescriptor[] _bicomplexList =
    {
        new("BicomplexSliceW", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Slices through the 4D bicomplex set."),
    };

    private static readonly AnimatableParamDescriptor[] _userEquationList =
    {
        new("UserEquationRotationDegrees", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 360.0,
            Notes: "Cycles through a full rotation. Wrap is at 360."),
    };
}
