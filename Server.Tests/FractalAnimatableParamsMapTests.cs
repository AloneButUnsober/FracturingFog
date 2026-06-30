using System;
using System.Numerics;
using System.Reflection;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Round-trip the FractalAnimatableParamsMap registry against the live
/// FractalParameters class via reflection. Catches drift between the
/// registry (hand-maintained) and the property surface (canonical source
/// of truth). Animation Roadmap Phase 1 deliverable.
/// </summary>
public sealed class FractalAnimatableParamsMapTests
{
    /// <summary>Every entry's ParamName must resolve to a public instance
    /// property on FractalParameters whose CLR type matches the declared
    /// Kind. Round-trip write/read must preserve the value (modulo int
    /// truncation for ScalarInt). Catches typos, renames, and Kind/type
    /// mismatches.</summary>
    [Fact]
    public void EveryEntry_ResolvesAndRoundTrips()
    {
        var fp = new FractalParameters();
        var t = typeof(FractalParameters);

        foreach (FractalType ft in Enum.GetValues<FractalType>())
        {
            var descriptors = FractalAnimatableParamsMap.For(ft);
            foreach (var d in descriptors)
            {
                var prop = t.GetProperty(
                    d.ParamName,
                    BindingFlags.Public | BindingFlags.Instance);

                Assert.True(prop != null,
                    $"FractalParameters has no public property '{d.ParamName}' " +
                    $"(referenced by FractalType.{ft})");

                Assert.True(prop!.CanRead && prop.CanWrite,
                    $"Property '{d.ParamName}' must be read/write");

                switch (d.Kind)
                {
                    case AnimatableParamKind.ScalarDouble:
                        Assert.True(prop.PropertyType == typeof(double),
                            $"'{d.ParamName}' Kind=ScalarDouble but CLR type is {prop.PropertyType.Name}");
                        prop.SetValue(fp, 0.5);
                        Assert.Equal(0.5, (double)prop.GetValue(fp)!);
                        break;

                    case AnimatableParamKind.ScalarInt:
                        Assert.True(prop.PropertyType == typeof(int),
                            $"'{d.ParamName}' Kind=ScalarInt but CLR type is {prop.PropertyType.Name}");
                        var clamped = Math.Max(
                            (int)Math.Round(d.Min),
                            Math.Min((int)Math.Round(d.Max), 4));
                        prop.SetValue(fp, clamped);
                        Assert.Equal(clamped, (int)prop.GetValue(fp)!);
                        break;

                    case AnimatableParamKind.Complex:
                        Assert.True(prop.PropertyType == typeof(Complex),
                            $"'{d.ParamName}' Kind=Complex but CLR type is {prop.PropertyType.Name}");
                        var c = new Complex(0.3, 0.4);
                        prop.SetValue(fp, c);
                        Assert.Equal(c, (Complex)prop.GetValue(fp)!);
                        break;

                    default:
                        Assert.Fail($"Unhandled Kind {d.Kind}");
                        break;
                }
            }
        }
    }

    /// <summary>Min &lt;= Max for every entry. Procedural motion requires it.</summary>
    [Fact]
    public void EveryEntry_HasValidRange()
    {
        foreach (FractalType ft in Enum.GetValues<FractalType>())
        {
            foreach (var d in FractalAnimatableParamsMap.For(ft))
            {
                Assert.True(d.Min <= d.Max,
                    $"FractalType.{ft} param '{d.ParamName}': Min ({d.Min}) > Max ({d.Max})");
            }
        }
    }

    /// <summary>Every fractal type must produce a non-null descriptor list
    /// (the empty-array default is fine — null is not).</summary>
    [Fact]
    public void For_ReturnsNonNull_ForEveryType()
    {
        foreach (FractalType ft in Enum.GetValues<FractalType>())
        {
            Assert.NotNull(FractalAnimatableParamsMap.For(ft));
        }
    }

    /// <summary>Spot-check: Julia must list JuliaC. Smoke test that the
    /// fundamental case isn't broken.</summary>
    [Fact]
    public void Julia_IncludesJuliaC()
    {
        var descriptors = FractalAnimatableParamsMap.For(FractalType.Julia);
        Assert.Contains(descriptors,
            d => d.ParamName == "JuliaC" && d.Kind == AnimatableParamKind.Complex);
    }
}
