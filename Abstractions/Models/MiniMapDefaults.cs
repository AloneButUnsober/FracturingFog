// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MiniMapDefaults.cs
//
// Per-FractalType framing used by MiniMapPanel. Each entry says where the
// "interesting" area of that fractal sits in its parameter plane and what
// thumbnail zoom frames it inside the 220×180 overview bitmap.
//
// Zoom convention matches IFractalCalculator: scale = (3.5/maxDim) / Zoom.
// So at MapW=220, Zoom=1.5 → ~2.33-unit horizontal span.

// Namespace moved into FracturingFog.Models when promoted to the shared
// Abstractions assembly so both the Avalonia MiniMapControl and the legacy
// WinForms MiniMapPanel can pull defaults from one source of truth.
// Visibility changed from internal → public for the same reason.

namespace FracturingFog.Models;

public static class MiniMapDefaults
{
    public readonly record struct ViewBounds(double CenterX, double CenterY, double Zoom);

    /// <summary>
    /// 3D and unsupported types render a placeholder instead of a thumbnail.
    /// </summary>
    public static bool IsSupported(FractalType t) => t switch
    {
        FractalType.Mandelbulb => false,
        FractalType.Mandelbox  => false,
        FractalType.Kifs       => false,
        FractalType.QuaternionJulia => false,
        FractalType.QuaternionMandelbrot => false,
        FractalType.Kleinian   => false,
        FractalType.MirrorSpheres => false,
        FractalType.MathPlane3D => false,   // #1713
        FractalType.BicomplexMandelbrot => false,
        FractalType.Coquaternion => false,
        FractalType.Julibrot => false,
        FractalType.UserBulb   => false,
        _                      => true
    };

    /// <summary>
    /// Default centre + thumbnail zoom that frames the canonical view of each
    /// 2D fractal type inside the 220×180 mini-map. Tweaked to show the
    /// recognisable silhouette of the set, not a deep-zoom region.
    /// </summary>
    public static ViewBounds For(FractalType t) => t switch
    {
        // #29 — Zoom retuned so the FULL canonical fractal fits the 220×180
        // thumbnail with a ~15% margin (span = 3.5/Zoom). The old values were
        // zoomed in (e.g. Mandelbrot 1.5 → 2.33-unit span vs the 3.5-wide set)
        // so the antenna / tips were clipped inside the bitmap; no display-side
        // letterbox can recover content the render never drew.
        FractalType.Mandelbrot       => new(-0.5,  0.0, 0.85),
        FractalType.Julia            => new( 0.0,  0.0, 1.0),
        FractalType.BurningShip      => new(-0.5, -0.5, 1.0),
        FractalType.Tricorn          => new(-0.5,  0.0, 0.85),
        FractalType.Multibrot        => new( 0.0,  0.0, 1.0),
        FractalType.Phoenix          => new( 0.0,  0.0, 1.0),
        FractalType.Newton           => new( 0.0,  0.0, 0.9),
        FractalType.Nova             => new(-0.35, 0.0, 1.4),   // #1294
        FractalType.BuddhaBrot       => new(-0.5,  0.0, 0.85),
        FractalType.Nebulabrot       => new(-0.5,  0.0, 0.85),
        FractalType.AntiBuddhabrot   => new(-0.5,  0.0, 0.85),
        FractalType.AntiNebulabrot   => new(-0.5,  0.0, 0.85),
        FractalType.DualBuddhabrot   => new(-0.5,  0.0, 0.85),
        FractalType.IFS              => new( 0.0,  0.0, 1.0),
        FractalType.LSystem          => new( 0.0,  0.0, 1.0),
        FractalType.StrangeAttractor => new( 0.0,  0.0, 1.0),
        FractalType.TearDrop         => new( 0.0,  0.0, 0.16),
        FractalType.UserEquation     => new( 0.0,  0.0, 0.8),
        FractalType.Sandbox          => new( 0.0,  0.0, 0.8),
        FractalType.Magnet1          => new( 1.5,  0.0, 0.6),
        FractalType.Magnet2          => new( 1.5,  0.0, 0.5),
        FractalType.Glynn            => new(-0.2,  0.0, 0.7),
        FractalType.Logistic         => new( 3.5,  0.5, 2.0),
        FractalType.Lyapunov         => new( 2.9,  3.0, 1.7),
        FractalType.TranscendentalJulia => new( 0.0,  0.0, 0.5),
        FractalType.Halley           => new( 0.0,  0.0, 0.9),
        FractalType.Secant           => new( 0.0,  0.0, 0.9),
        FractalType.Spider           => new( 0.0,  0.0, 1.0),
        FractalType.Mandelbox        => new( 0.0,  0.0, 1.0),
        FractalType.Kifs             => new( 0.0,  0.0, 1.0),
        FractalType.QuaternionJulia  => new( 0.0,  0.0, 1.0),
        FractalType.QuaternionMandelbrot => new( 0.0,  0.0, 1.0),
        FractalType.Plasma           => new( 0.0,  0.0, 1.0),
        FractalType.AcidWarp         => new( 0.0,  0.0, 1.0),
        FractalType.Flame            => new( 0.0,  0.0, 1.0),
        FractalType.Apollonian       => new( 0.0,  0.0, 1.75),
        FractalType.ChaoticBilliard  => new( 0.0,  0.0, 0.875),
        FractalType.BilliardRays     => new( 0.0,  0.0, 0.525),
        FractalType.BlackHoleShadow  => new( 0.0,  0.0, 0.14),  // #1923 (#1927: camera-scale pitch)
        FractalType.HenonHeilesBasins => new( 0.0, -0.25, 0.85), // #1942 the well and its three saddles
        FractalType.BlackHoleRingOrbits => new(16.0,  0.0, 0.12), // #1947 r from ~1.5 to ~30 M (BlackHoleRingCalculator.Default*)
        FractalType.UserPotentialBasins => new( 0.0, -0.25, 0.85), // #1994 as Hénon–Heiles (its default V)
        FractalType.MixmasterBasins  => new( 1.5, -0.5, 3.5),   // #1946 u from 1 to 2, v from 0 to 1 (MixmasterCalculator.Default*)
        FractalType.SpinningBinaryBasins => new( 0.0,  0.0, 0.557), // #1945 θ1 from −π to π
        FractalType.MagnetisedBlackHole => new( 3.2, -1.5, 0.7), // #1944 Kick view: ρ0 to ~5.7, ℰ to ~2.9
        FractalType.ThreeBodyBasins  => new( 0.0,  0.0, 1.0),   // #1943 both primaries and L1-L5
        FractalType.SlitCascade      => new( 0.0,  0.0, 0.21875),
        FractalType.TalbotCarpet     => new( 0.0,  0.5, 1.6625),
        FractalType.MathPlane        => new( 0.0,  0.0, 0.4375),
        FractalType.PrecisionField   => new(-0.5,  0.0, 0.875),
        FractalType.JulibrotPair  => new(-0.5,  0.0, 0.875),
        FractalType.Kleinian         => new( 0.0,  0.0, 1.0),
        FractalType.MirrorSpheres    => new( 0.0,  0.0, 1.0),
        FractalType.MathPlane3D      => new( 0.0,  0.0, 1.0),   // #1713
        FractalType.BicomplexMandelbrot => new( 0.0,  0.0, 1.0),
        FractalType.Coquaternion => new( 0.0,  0.0, 1.0),
        FractalType.Julibrot => new( 0.0,  0.0, 1.0),
        FractalType.Dla              => new( 0.0,  0.0, 1.0),
        // Indra's Pearls (#892) — Maskit μ = 2i limit set sits above the real
        // axis (period-2 in x); frame it centred at (0, 1) with a wide zoom.
        FractalType.RandomTile       => new( 0.0,  0.0, 0.875),   // #1974 (was the fallback zoom 1)
        FractalType.IndrasPearls     => new( 0.0,  1.0, 0.525),
        _                            => new( 0.0,  0.0, 1.0)
    };

    /// <summary>
    /// Iteration budget for the thumbnail render. Chaos-game and density
    /// methods get much smaller counts than escape-time — at 220×180 they
    /// would otherwise dominate the panel's refresh cost.
    /// </summary>
    public static int IterationsFor(FractalType t) => t switch
    {
        FractalType.IFS              => 80_000,
        FractalType.StrangeAttractor => 80_000,
        FractalType.LSystem          => 4,
        FractalType.BuddhaBrot       => 20_000,
        FractalType.Nebulabrot       => 20_000,
        FractalType.AntiBuddhabrot   => 20_000,
        FractalType.AntiNebulabrot   => 20_000,
        FractalType.DualBuddhabrot   => 20_000,
        _                            => 256
    };
}
