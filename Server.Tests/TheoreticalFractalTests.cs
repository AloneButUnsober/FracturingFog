using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// Frontier fractal R&D (epic #850): Lyapunov (#851) calculator + Magnet
// convergence colouring (#852). See Docs/Technical/Theoretical-Fractal-RnD.md.
public class TheoreticalFractalTests
{
    private const int W = 160, H = 120;

    // ── Lyapunov (#851) ──────────────────────────────────────────────────────

    private static LyapunovCalculator RenderLyapunov(
        string seq = "AB", int warmup = 100, int maxIt = 300,
        double cx = 3.5, double cy = 3.5, double zoom = 1.0)
    {
        var calc = new LyapunovCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIt,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                LyapunovSequence = seq,
                LyapunovWarmup = warmup,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    [Fact]
    public void Lyapunov_SameParams_AreDeterministic()
    {
        var a = RenderLyapunov();
        var b = RenderLyapunov();
        Assert.True(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }

    [Fact]
    public void Lyapunov_ProducesStructure_NotFlat()
    {
        var calc = RenderLyapunov();
        int distinct = calc.ColorBuffer.Distinct().Count();
        // A structured parameter-plane image has many shades, not one flat fill.
        Assert.True(distinct > 20, $"expected structured image, got {distinct} colours");
    }

    [Fact]
    public void Lyapunov_DifferentSequence_DiffersFromAB()
    {
        var ab = RenderLyapunov(seq: "AB");
        var bbbaaa = RenderLyapunov(seq: "BBBAAA");
        Assert.False(ab.ColorBuffer.AsSpan().SequenceEqual(bbbaaa.ColorBuffer));
    }

    [Fact]
    public void Lyapunov_OutOfRangeView_IsAllInSet()
    {
        // (a, b) far outside the valid (0, 4] logistic-rate window → every pixel
        // is invalid and painted InSetColor.
        var calc = RenderLyapunov(cx: 100.0, cy: 100.0, zoom: 1.0);
        uint inSet = calc.ColorMap.InSetColor;
        Assert.All(calc.ColorBuffer, p => Assert.Equal(inSet, p));
    }

    [Fact]
    public void Lyapunov_SmoothBuffer_FeedsRelief()
    {
        var calc = RenderLyapunov();
        // SmoothBuffer must carry the mapped λ field (IHeightFieldSource) — non-zero
        // somewhere so Relief-3D / SmoothBuffer themes have a height signal.
        Assert.Contains(calc.SmoothBuffer, v => v > 0f);
    }

    // ── Magnet convergence colouring (#852) ──────────────────────────────────

    private static uint[] RenderMagnet(bool convergence, FractalType type = FractalType.Magnet1)
    {
        var calc = new EscapeTimeCalculator(W, H)
        {
            FractalType = type,
            CenterX = 0.0, CenterY = 0.0, Zoom = 0.35, MaxIterations = 256,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                MagnetConvergence = convergence,
                MagnetConvergenceEpsilon = 1e-4,
            },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static int Painted(uint[] buf, uint inSet) => buf.Count(p => p != inSet);

    [Fact]
    public void Magnet1_Convergence_ShadesTheFixedPointBasin()
    {
        uint inSet = ((IColorMap)new HsvPalette()).InSetColor;
        int withConv = Painted(RenderMagnet(convergence: true), inSet);
        int without = Painted(RenderMagnet(convergence: false), inSet);
        // Convergence colouring paints the z=1 basin that plain escape-time left
        // flat InSetColor, so strictly more pixels carry colour.
        Assert.True(withConv > without,
            $"convergence-on painted {withConv} vs escape-time {without}");
    }

    [Fact]
    public void Magnet2_Convergence_ShadesTheFixedPointBasin()
    {
        uint inSet = ((IColorMap)new HsvPalette()).InSetColor;
        int withConv = Painted(RenderMagnet(convergence: true, FractalType.Magnet2), inSet);
        int without = Painted(RenderMagnet(convergence: false, FractalType.Magnet2), inSet);
        Assert.True(withConv > without,
            $"convergence-on painted {withConv} vs escape-time {without}");
    }

    [Fact]
    public void Magnet_ConvergenceOff_IsLegacyEscapeTime()
    {
        // With convergence disabled the path must match the pre-#852 dispatch
        // (DispatchByColorMap) — asserted indirectly: it renders without throwing
        // and leaves at least some in-set (unconverged, non-escaped) pixels.
        uint inSet = ((IColorMap)new HsvPalette()).InSetColor;
        var buf = RenderMagnet(convergence: false);
        Assert.Contains(buf, p => p == inSet);
    }

    // ── Coquaternion (split-quaternion) Mandelbrot (#853) ────────────────────

    private static uint[] RenderCoquaternion(double sliceW = 0.0)
    {
        var calc = new CoquaternionMandelbrotCalculator(W, H)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { CoquaternionSliceW = sliceW },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void Coquaternion_Deterministic()
    {
        Assert.True(RenderCoquaternion().AsSpan().SequenceEqual(RenderCoquaternion()));
    }

    [Fact]
    public void Coquaternion_RendersSolidBody_NotEmpty()
    {
        uint sky = ((IColorMap)new HsvPalette()).InSetColor;
        var buf = RenderCoquaternion();
        int surface = buf.Count(p => p != sky);
        // The raymarcher must hit the set body for a healthy fraction of pixels.
        Assert.True(surface > buf.Length / 20, $"only {surface}/{buf.Length} surface pixels");
    }

    [Fact]
    public void Coquaternion_SliceW_ChangesTheSet()
    {
        Assert.False(RenderCoquaternion(0.0).AsSpan().SequenceEqual(RenderCoquaternion(0.6)));
    }

    [Fact]
    public void Coquaternion_DiffersFromBicomplex_SameView()
    {
        // Swapped product table → a genuinely different set, not a rename.
        var bike = new BicomplexMandelbrotCalculator(W, H)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { BicomplexSliceW = 0.0 },
        };
        bike.Calculate(default);
        Assert.False(RenderCoquaternion(0.0).AsSpan().SequenceEqual(bike.ColorBuffer));
    }

    // ── Transcendental Julia (#854) ──────────────────────────────────────────

    private static TranscendentalJuliaCalculator RenderTranscendental(
        TranscendentalMap map = TranscendentalMap.Sine,
        double lr = 1.0, double li = 0.0, double zoom = 0.5, int maxIt = 200)
    {
        var calc = new TranscendentalJuliaCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = zoom, MaxIterations = maxIt,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                TranscendentalMap = map,
                TranscendentalLambdaRe = lr,
                TranscendentalLambdaIm = li,
                TranscendentalBailout = 50.0,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    [Fact]
    public void Transcendental_Deterministic()
    {
        Assert.True(RenderTranscendental().ColorBuffer.AsSpan()
            .SequenceEqual(RenderTranscendental().ColorBuffer));
    }

    [Fact]
    public void Transcendental_ProducesStructure()
    {
        int distinct = RenderTranscendental().ColorBuffer.Distinct().Count();
        Assert.True(distinct > 20, $"expected structured image, got {distinct} colours");
    }

    [Fact]
    public void Transcendental_SineVsExp_Differ()
    {
        var sine = RenderTranscendental(TranscendentalMap.Sine);
        var exp = RenderTranscendental(TranscendentalMap.Exp, lr: 0.3);
        Assert.False(sine.ColorBuffer.AsSpan().SequenceEqual(exp.ColorBuffer));
    }

    [Fact]
    public void Transcendental_LambdaChangesTheSet()
    {
        var a = RenderTranscendental(lr: 1.0);
        var b = RenderTranscendental(lr: 1.0, li: 0.4);
        Assert.False(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }

    [Fact]
    public void Transcendental_SmoothBuffer_FeedsRelief()
    {
        Assert.Contains(RenderTranscendental().SmoothBuffer, v => v > 0f);
    }

    [Fact]
    public void Transcendental_NoNaNOrInf_InFloatBuffers()
    {
        // Non-modulus bailout + double-exponential growth must never leak a
        // NaN/Inf into the smooth / final-z channels (guarded escape).
        var calc = RenderTranscendental(TranscendentalMap.Exp, lr: 0.5);
        Assert.All(calc.SmoothBuffer, v => Assert.True(float.IsFinite(v)));
        Assert.All(calc.FinalZrBuffer, v => Assert.True(float.IsFinite(v)));
        Assert.All(calc.DistanceBuffer, v => Assert.True(float.IsFinite(v)));
    }
}
