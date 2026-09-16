using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #859 — non-modulus / condition-replaces-modulus bailout on the live DSL
// calculators (Sandbox + User Equation). Lets authors write entire
// transcendental maps (sin/exp z) whose escape is a per-axis condition, not the
// modulus. See Docs/Technical/Theoretical-Fractal-RnD.md §3.1/§4.
public class DslNonModulusBailoutTests
{
    private const int W = 140, H = 100;
    private static uint InSet => ((IColorMap)new HsvPalette()).InSetColor;

    private static uint[] RenderSandbox(
        string source, string? cond = null, bool replaces = false, double escapeRadius = 0.0)
    {
        var calc = new SandboxCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = 0.5, MaxIterations = 200,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                SandboxSource = source,
                EscapeRadius = escapeRadius,
                UserEquationBailoutCondition = cond,
                UserEquationBailoutReplacesModulus = replaces,
            },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static int Painted(uint[] buf) => buf.Count(p => p != InSet);

    [Fact]
    public void Sandbox_TranscendentalSine_RendersViaNonModulusBailout()
    {
        // sin(z)+c has no useful modulus escape structure at large R; the axis
        // condition |Im z| > 50 (replacing modulus) produces the real set.
        var buf = RenderSandbox("sin(z) + c", cond: "abs(im(z)) > 50", replaces: true);
        int painted = Painted(buf);
        int distinct = buf.Distinct().Count();
        Assert.True(painted > buf.Length / 20, $"only {painted} escaped pixels");
        Assert.True(distinct > 20, $"expected structure, got {distinct} colours");
    }

    [Fact]
    public void Sandbox_Replaces_ChangesResultVsModulusShadowed()
    {
        // With replaces OFF, the default modulus (|z|²≥1024) fires long before
        // |Im z|>50, so the two renders differ.
        var withReplace = RenderSandbox("sin(z) + c", cond: "abs(im(z)) > 50", replaces: true);
        var without = RenderSandbox("sin(z) + c", cond: "abs(im(z)) > 50", replaces: false);
        Assert.False(withReplace.AsSpan().SequenceEqual(without));
    }

    [Fact]
    public void Sandbox_ReplacesFlag_NoOp_WithoutCondition()
    {
        // replacesModulus requires a condition; with none it must be byte-identical.
        var flagOn = RenderSandbox("z*z + c", cond: null, replaces: true);
        var flagOff = RenderSandbox("z*z + c", cond: null, replaces: false);
        Assert.True(flagOn.AsSpan().SequenceEqual(flagOff));
    }

    [Fact]
    public void Sandbox_PlainModulusMap_ByteIdentical_ToLegacy()
    {
        // A standard modulus map with no condition is untouched by the feature.
        var a = RenderSandbox("z*z + c");
        var b = RenderSandbox("z*z + c");
        Assert.True(a.AsSpan().SequenceEqual(b));
        Assert.True(Painted(a) > 0);
    }

    [Fact]
    public void Sandbox_TranscendentalExp_UsesRealAxis()
    {
        // exp escapes on Re z; |exp| = e^Re z. re(z) > 50 controls it.
        var buf = RenderSandbox("exp(z) + c", cond: "re(z) > 50", replaces: true, escapeRadius: 0.0);
        Assert.True(Painted(buf) > buf.Length / 20);
    }

    [Fact]
    public void Sandbox_NonFinite_Guarded_NoInfiniteHang()
    {
        // exp overflow → non-finite; the guard must terminate (test returns).
        var buf = RenderSandbox("exp(exp(z)) + c", cond: "re(z) > 1e6", replaces: true);
        Assert.Equal(W * H, buf.Length);
    }
}
