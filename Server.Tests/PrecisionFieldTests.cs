using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Interefaces;
using FracturingFog.Imaging;

namespace FracturingFog.Server.Tests;

// #628 — precision-sensitivity field. Locks in the core invariants: same-tier
// pairs agree everywhere (dark), a mismatched pair diverges, the divergence
// grows as the low tier becomes inadequate for the zoom, renders deterministically,
// and the poster path honours tier changes.
public class PrecisionFieldTests
{
    private static PrecisionFieldCalculator Make(
        double zoom, PrecisionTier lo, PrecisionTier hi,
        double cx = -0.743643887, double cy = 0.131825904, int maxIter = 400, int size = 80)
        => new(size, size)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter,
            FractalParameters = new FractalParameters { PrecisionLowTier = lo, PrecisionHighTier = hi },
        };

    private static double MeanHeight(float[] h)
    {
        double s = 0; foreach (float v in h) s += v; return s / h.Length;
    }

    [Fact]
    public void Same_Tier_Pair_Is_Everywhere_Zero()
    {
        // Double vs Double must agree at every pixel -> flat black divergence.
        var c = Make(50000.0, PrecisionTier.Double, PrecisionTier.Double);
        c.Calculate(default);
        Assert.All(c.SmoothBuffer, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Mismatched_Tier_Pair_Diverges_At_Deep_Zoom()
    {
        var c = Make(50000.0, PrecisionTier.Float, PrecisionTier.Double);
        c.Calculate(default);
        Assert.True(MeanHeight(c.SmoothBuffer) > 0.0, "float vs double showed no divergence at deep zoom");
    }

    [Fact]
    public void Weaker_Low_Tier_Diverges_More()
    {
        // At a deep zoom, Float is inadequate (big divergence vs Double); Double
        // itself still holds vs DoubleDouble (tiny divergence). So the float pair
        // must be markedly more fragile than the double pair on the same view.
        var floatPair = Make(50000.0, PrecisionTier.Float, PrecisionTier.Double);
        var doublePair = Make(50000.0, PrecisionTier.Double, PrecisionTier.DoubleDouble);
        floatPair.Calculate(default);
        doublePair.Calculate(default);
        Assert.True(MeanHeight(floatPair.SmoothBuffer) > MeanHeight(doublePair.SmoothBuffer),
            "float/double should be more fragile than double/DD at this zoom");
    }

    [Fact]
    public void Is_Deterministic()
    {
        var a = Make(50000.0, PrecisionTier.Float, PrecisionTier.Double); a.Calculate(default);
        var b = Make(50000.0, PrecisionTier.Float, PrecisionTier.Double); b.Calculate(default);
        Assert.Equal(a.SmoothBuffer, b.SmoothBuffer);
    }

    [Fact]
    public void PosterPath_Honors_Tier_Change()
    {
        PosterRequest Req(PrecisionTier lo, PrecisionTier hi) => new()
        {
            FractalType = FractalType.PrecisionField,
            CenterX = -0.743643887, CenterY = 0.131825904, Zoom = 50000.0,
            MaxIterations = 400, Width = 80, Height = 80,
            ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters { PrecisionLowTier = lo, PrecisionHighTier = hi },
            Path = "unused.png", Format = ImageFileFormat.Png,
        };
        var a = PosterRenderer.RenderToPixels(Req(PrecisionTier.Double, PrecisionTier.Double), default, out _, out _);
        var b = PosterRenderer.RenderToPixels(Req(PrecisionTier.Float, PrecisionTier.Double), default, out _, out _);
        Assert.NotEqual(a, b);   // same-tier (flat) must differ from a diverging pair
    }

    [Fact]
    public void Region_RoundTrip_Preserves_Tiers()
    {
        var src = new FractalParameters
        {
            PrecisionLowTier = PrecisionTier.Double,
            PrecisionHighTier = PrecisionTier.QuadDouble,
            PrecisionDiffMetric = PrecisionDiffMetric.AngleOnly,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.PrecisionField, src);
        Assert.NotNull(snap);
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(PrecisionTier.Double, dst.PrecisionLowTier);
        Assert.Equal(PrecisionTier.QuadDouble, dst.PrecisionHighTier);
        Assert.Equal(PrecisionDiffMetric.AngleOnly, dst.PrecisionDiffMetric);
    }
}
