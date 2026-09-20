// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

        // #253 — domain warp is the only animatable scalar on these.
        FractalType.BurningShip or FractalType.Tricorn
            or FractalType.Magnet1 or FractalType.Magnet2
            => _domainWarpOnlyList,

        // Newton / Halley / Secant share the polynomial-exponent + relaxation
        // pair. Secant also exposes an initial-offset complex.
        FractalType.Newton or FractalType.Nova or FractalType.Halley
            => _newtonList,

        FractalType.Secant
            => _secantList,

        FractalType.Logistic
            => _logisticList,

        FractalType.Lyapunov
            => _lyapunovList,

        FractalType.TranscendentalJulia
            => _transcendentalList,

        // ── Procedural / chaos-game ───────────────────────────────────────
        FractalType.IFS
            => _ifsList,

        FractalType.LSystem
            => _lsystemList,

        FractalType.StrangeAttractor
            => _attractorList,

        FractalType.Plasma
            => _plasmaList,

        FractalType.AcidWarp
            => _acidWarpList,

        FractalType.Flame
            => _flameList,

        FractalType.Apollonian
            => _apollonianList,

        FractalType.Dla
            => _dlaList,

        FractalType.RandomTile
            => _randomTileList,

        FractalType.ChaoticBilliard
            => _billiardList,

        FractalType.PrecisionField
            => _precisionFieldList,

        FractalType.DualOrbitEscape
            => _dualOrbitList,

        FractalType.IndrasPearls
            => _indrasList,

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

        FractalType.Coquaternion
            => _coquaternionList,

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

    // #253 — cross-fractal domain warp. Shared across the escape-time family;
    // animate for a breathing swirl. Needs the Domain-warp toggle on and a
    // shallow zoom (inactive above EscapeTimeCalculator.MaxWarpZoom).
    private static readonly AnimatableParamDescriptor _domainWarp =
        new("DomainWarpStrength", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0,
            Notes: "#253 domain warp — animate for a breathing swirl. Needs the "
                 + "Domain-warp toggle on; shallow zoom only.");

    private static readonly AnimatableParamDescriptor[] _juliaList =
    {
        new("JuliaC", AnimatableParamKind.Complex, Min: 0.05, Max: 1.5,
            Notes: "Classic orbit-around-the-Mandelbrot-boundary sweep."),
        // #920 — iteration ramp for the faithful parabolic implosion: rises as c
        // nears a parabolic root so the near-parabolic crawl stays resolved.
        new("EscapeIterationScale", AnimatableParamKind.ScalarDouble, Min: 1.0, Max: 6.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Multiplies the iteration cap (≥1). Ramp it up as c approaches a parabolic root."),
        _domainWarp,
    };

    private static readonly AnimatableParamDescriptor[] _multibrotList =
    {
        new("MultibrotExponent", AnimatableParamKind.ScalarInt, Min: 2, Max: 8),
        _domainWarp,
    };

    private static readonly AnimatableParamDescriptor[] _phoenixList =
    {
        new("PhoenixP", AnimatableParamKind.Complex, Min: 0.0, Max: 1.5),
        _domainWarp,
    };

    private static readonly AnimatableParamDescriptor[] _glynnList =
    {
        new("GlynnC", AnimatableParamKind.Complex, Min: 0.05, Max: 1.0),
        _domainWarp,
    };

    private static readonly AnimatableParamDescriptor[] _spiderList =
    {
        new("SpiderCDecay", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0),
        _domainWarp,
    };

    // BurningShip / Tricorn / Magnet 1&2 have no other animatable scalar; the
    // domain warp (#253) is their sole animatable knob.
    private static readonly AnimatableParamDescriptor[] _domainWarpOnlyList =
    {
        _domainWarp,
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

    private static readonly AnimatableParamDescriptor[] _transcendentalList =
    {
        new("TranscendentalLambdaRe", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Notes: "Real part of λ — morphs the exploding Julia set."),
        new("TranscendentalLambdaIm", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Notes: "Imaginary part of λ — rotates the hairs / bouquet."),
    };

    private static readonly AnimatableParamDescriptor[] _lyapunovList =
    {
        new("LyapunovWarmup", AnimatableParamKind.ScalarInt, Min: 0, Max: 5000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Re-runs the full per-pixel forced orbit each tick — animate slowly."),
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

    private static readonly AnimatableParamDescriptor[] _acidWarpList =
    {
        new("AcidWarpFrequency", AnimatableParamKind.ScalarDouble, Min: 0.1, Max: 8.0),
        new("AcidWarpCenterX", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
        new("AcidWarpCenterY", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0),
        new("AcidWarpPattern", AnimatableParamKind.ScalarInt, Min: 0, Max: 19,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Discrete pattern pick — hard-cuts between fields. For a smooth "
                 + "transition enable Morph and animate AcidWarpFlow instead."),
        new("AcidWarpFlow", AnimatableParamKind.ScalarDouble,
            Min: 0, Max: FracturingFog.Models.FractalParameters.AcidWarpPatternCount,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Continuous pattern position (needs Morph on). Animate 0 → "
                 + "pattern-count for an endless morph through every pattern, "
                 + "wrapping seamlessly. Blends two fields per pixel — expensive."),
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

    // Chaotic billiard (#627). Both continuous geometry knobs morph the basin
    // structure smoothly — animate the gap opening/closing for a scatter reveal.
    private static readonly AnimatableParamDescriptor[] _billiardList =
    {
        new("BilliardSeparation", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 2.5,
            Notes: "Disk spacing — widening opens escape gaps, simplifying basins."),
        new("BilliardDiskRadius", AnimatableParamKind.ScalarDouble, Min: 0.1, Max: 0.9,
            Notes: "Disk size — larger disks narrow the gaps toward deeper chaos."),
    };

    // PrecisionField (#632, Renderer C2). The two tiers form a precision
    // ladder Float(0) → Double(1) → DoubleDouble(2) → QuadDouble(3). Sweeping
    // the low tier upward while the high tier stays pinned at QuadDouble is the
    // convergence animation: each step, the low-tier outcome agrees with the
    // reference over more of the frame, so the divergence field dims toward
    // black — the per-pixel *rate* at which a pixel settles is the image. Both
    // are Expensive: every tick re-iterates the fractal at BOTH tiers (QD is
    // the heaviest arithmetic in the codebase), so animate slowly. Enum kind →
    // Min/Max are ladder indices, driven with a Linear ramp 0 → 3.
    private static readonly AnimatableParamDescriptor[] _precisionFieldList =
    {
        new("PrecisionLowTier", AnimatableParamKind.Enum, Min: 0, Max: 3,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Convergence sweep — ramp Float(0)→QuadDouble(3) with the "
                 + "reference tier pinned at QuadDouble; the field dims as the "
                 + "low tier catches up. Re-iterates both tiers each tick."),
        new("PrecisionHighTier", AnimatableParamKind.Enum, Min: 0, Max: 3,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Reference (ceiling) tier — usually pinned; animate only to "
                 + "sweep the reference itself. Re-iterates both tiers each tick."),
    };

    private static readonly AnimatableParamDescriptor[] _dlaList =
    {
        new("DlaParticles", AnimatableParamKind.ScalarInt, Min: 1000, Max: 50_000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Each tick re-runs the random-walk simulation. Animate slowly."),
    };

    // RandomTile (Bourke). Two axes animate cleanly; the rest reshuffle the
    // packing every tick (a deliberate boil, not a smooth tween) — labelled so
    // the editor / cost-ceiling can treat them accordingly (cf. DLA).
    private static readonly AnimatableParamDescriptor[] _randomTileList =
    {
        // Placement is a deterministic prefix, so growing the count only *adds*
        // tiles — existing ones stay put. A genuine fill-in reveal.
        new("RandomTileCount", AnimatableParamKind.ScalarInt, Min: 100, Max: 20_000,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Monotonic fill-in reveal — existing tiles stay put; re-renders each tick."),
        // Shading only — placement is cached (#338), so this is a paint-only
        // re-render and morphs smoothly.
        new("RandomTileRelief", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 2.0,
            Cost: AnimatableParamCost.Cheap,
            Notes: "Smooth — shading only; placement cached, paint-only re-render."),
        // The regenerating axes: any change reshuffles the whole packing.
        new("RandomTileSizeExponent", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 3.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Regenerates the packing each tick (boil) — deliberate effect, not a tween."),
        new("RandomTileGap", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 1.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Regenerates the packing each tick (boil) — deliberate effect, not a tween."),
        new("RandomTileMinPixelRadius", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 8.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Regenerates the packing each tick (boil) — deliberate effect, not a tween."),
    };

    // Dual-orbit escape-geometry (#864/#866). Sweeping the decoupled c-seed
    // animates the texture on a stationary body; the quaternion s_z is the
    // "decoupling / phase dial" (flat control → rich scattering). Every tick
    // re-iterates the dual field, so Expensive.
    private static readonly AnimatableParamDescriptor[] _dualOrbitList =
    {
        new("DualOrbitCSeedX", AnimatableParamKind.ScalarDouble, Min: -1.5, Max: 1.5,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Sweeps the c-seed real — texture-morph on a fixed silhouette (§3.6)."),
        new("DualOrbitCSeedY", AnimatableParamKind.ScalarDouble, Min: -1.5, Max: 1.5,
            Cost: AnimatableParamCost.Expensive),
        new("DualOrbitCSeedZ", AnimatableParamKind.ScalarDouble, Min: -1.5, Max: 1.5,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Quaternion c-seed 3rd component."),
        new("DualOrbitSZ", AnimatableParamKind.ScalarDouble, Min: -1.5, Max: 1.5,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Quaternion s_z decoupling dial — a literal 'turning-on' from flat control to rich scattering (§3.6)."),
    };

    // Indra's Pearls (#895) — the marquee "group degenerating into a limit curve"
    // animation. Sweeping the group parameter toward the Maskit-slice boundary
    // walks through cusp groups. Every tick rebuilds the two-generator group and
    // re-enumerates the limit set, so each is Expensive.
    private static readonly AnimatableParamDescriptor[] _indrasList =
    {
        new("IndrasMaskitMuRe", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Maskit μ real part — a horizontal sweep along the slice crosses successive cusp groups (the marquee)."),
        new("IndrasMaskitMuIm", AnimatableParamKind.ScalarDouble, Min: 0.5, Max: 3.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Maskit μ imag part — approach 2i (the parabolic 'apple' cusp) to watch the group degenerate onto a limit curve."),
        new("IndrasGrandmaTaRe", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Expensive,
            Notes: "Grandma trace ta real — morphs the quasi-Fuchsian group."),
        new("IndrasGrandmaTaIm", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Expensive),
        new("IndrasGrandmaTbRe", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Expensive),
        new("IndrasGrandmaTbIm", AnimatableParamKind.ScalarDouble, Min: -3.0, Max: 3.0,
            Cost: AnimatableParamCost.Expensive),
        new("IndrasRileyCRe", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Cost: AnimatableParamCost.Expensive),
        new("IndrasRileyCIm", AnimatableParamKind.ScalarDouble, Min: -2.0, Max: 2.0,
            Cost: AnimatableParamCost.Expensive),
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

    private static readonly AnimatableParamDescriptor[] _coquaternionList =
    {
        new("CoquaternionSliceW", AnimatableParamKind.ScalarDouble, Min: -1.0, Max: 1.0,
            Cost: AnimatableParamCost.Moderate,
            Notes: "Slices through the 4D coquaternion (split-quaternion) set."),
    };

    private static readonly AnimatableParamDescriptor[] _userEquationList =
    {
        new("UserEquationRotationDegrees", AnimatableParamKind.ScalarDouble, Min: 0.0, Max: 360.0,
            Notes: "Cycles through a full rotation. Wrap is at 360."),
    };
}
