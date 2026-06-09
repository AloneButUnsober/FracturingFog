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
        FractalType.Mandelbrot       => new(-0.5,  0.0, 1.5),
        FractalType.Julia            => new( 0.0,  0.0, 1.0),
        FractalType.BurningShip      => new(-0.5, -0.5, 1.0),
        FractalType.Tricorn          => new(-0.5,  0.0, 1.5),
        FractalType.Multibrot        => new( 0.0,  0.0, 1.2),
        FractalType.Phoenix          => new( 0.0,  0.0, 1.2),
        FractalType.Newton           => new( 0.0,  0.0, 0.9),
        FractalType.Nova             => new( 1.0,  0.0, 0.8),
        FractalType.BuddhaBrot       => new(-0.5,  0.0, 1.5),
        FractalType.Nebulabrot       => new(-0.5,  0.0, 1.5),
        FractalType.AntiBuddhabrot   => new(-0.5,  0.0, 1.5),
        FractalType.AntiNebulabrot   => new(-0.5,  0.0, 1.5),
        FractalType.IFS              => new( 0.0,  0.0, 1.0),
        FractalType.LSystem          => new( 0.0,  0.0, 1.0),
        FractalType.StrangeAttractor => new( 0.0,  0.0, 1.0),
        FractalType.TearDrop         => new( 0.0,  0.0, 0.16),
        FractalType.UserEquation     => new( 0.0,  0.0, 0.8),
        FractalType.Sandbox          => new( 0.0,  0.0, 0.8),
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
        _                            => 256
    };
}
