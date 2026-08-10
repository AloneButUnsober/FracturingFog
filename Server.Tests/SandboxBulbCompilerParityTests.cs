// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #283 — the Expression-tree compiler (SandboxBulbCompiler) must be
// bit-identical to the AST interpreter across every DSL construct. Both paths
// share SbxFuncEval + the SbxVal3 static ops, so any divergence is a wiring bug
// in the emitter. Comparison is on raw IEEE-754 bits (NaN-exact), not a
// tolerance — the two paths do the same arithmetic in the same order.
public class SandboxBulbCompilerParityTests
{
    private static readonly (double X, double Y, double Z)[] Inputs =
    {
        (0.0, 0.0, 0.0), (0.3, -0.1, 0.2), (-0.7, 0.45, -0.15),
        (1.2, 0.8, -0.6), (-1.1, -0.9, 0.35), (2.0, -1.5, 0.05),
    };

    // Vec3-mode expressions exercising every operator/function family.
    public static TheoryData<string> Vec3Exprs()
    {
        var d = new TheoryData<string>();
        foreach (var s in new[]
        {
            "z + c",
            "z*z + c",
            "z^8 + c",
            "vec(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) + c",
            "triplex(z, 4) + c",
            "abs(z)^8 + c",
            "sin(z)*1.5 + c",
            "sin(z)*cosh(z) + c",
            "absy(z)^8 + c",
            "spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c",
            "rot(z, vec(0,1,0), 0.3)*3.0 - vec(2,2,0)",
            "mod(z, 2.0) + c",
            "vec(length(z), dot(z, c), 0) + normalize(z)",
            "cross(z, c) + c",
            "vec(min(z.x, z.y), max(z.y, z.z), clamp(z.x, -1, 1)) + c",
            "vec(floor(z.x*3), sign(z.y), absx(z).x) + c",
            "let w = vec(abs(z.x), abs(z.y), z.z) in vec(w.x*w.x - w.y*w.y - w.z*w.z, 2*w.x*w.y, 2*w.x*w.z) + c",
            "(z.x > 0 ? z*2 : z*0.5) + c",
            "((z.x > 0 && z.y < 1) || z.z == 0 ? vec(1,0,0) : z) + c",
            "(!(z.x > 0) ? z : -z) + c",
            "smin(length(z), length(c), 0.5)*z + c",
            "z^(4 + 2*sin(z.x)) + c",
        }) d.Add(s);
        return d;
    }

    [Theory]
    [MemberData(nameof(Vec3Exprs))]
    public void Compiled_matches_interpreter_vec3(string src)
    {
        var interp = SandboxBulbExpression.Parse(src);
        var comp = SandboxBulbExpression.Parse(src);
        Assert.True(comp.TryCompile(), "expression failed to compile");
        Assert.True(comp.IsCompiled);
        Assert.False(interp.IsCompiled);

        var envI = interp.NewEnv();
        var envC = comp.NewEnv();
        var c = new Vec3(0.35, -0.2, 0.15);
        foreach (var (x, y, zc) in Inputs)
            for (int n = 0; n < 3; n++)
            {
                var z = new Vec3(x, y, zc);
                Vec3 ri = interp.EvalStep(z, c, n, envI);
                Vec3 rc = comp.EvalStep(z, c, n, envC);
                AssertBitEqual(ri, rc, $"{src} @ z=({x},{y},{zc}) n={n}");
            }
    }

    [Fact]
    public void Compiled_matches_interpreter_with_params()
    {
        // The Amoser dr body: named params + reserved dr/de scalar slots.
        const string body = UserBulbStore.DslAmoserDeBody;
        var names = new[] { "StretchScale", "StretchMax", "drScale", "drOffset", "t", "dr", "de" };
        var interp = SandboxBulbExpression.Parse(body, names);
        var comp = SandboxBulbExpression.Parse(body, names);
        Assert.True(comp.TryCompile());

        var envI = interp.NewEnv();
        var envC = comp.NewEnv();
        var c = new Vec3(0.1, 0.2, 0.3);
        double[] ex = { 0.81, 1.04, 1.0, 1.0, 0.0, 3.5, 2.0 };
        foreach (var (x, y, zc) in Inputs)
        {
            var z = new Vec3(x, y, zc);
            double ri = interp.EvalStep(z, c, 0, envI, ex).X;
            double rc = comp.EvalStep(z, c, 0, envC, ex).X;
            Assert.Equal(BitConverter.DoubleToInt64Bits(ri), BitConverter.DoubleToInt64Bits(rc));
        }
    }

    [Theory]
    [InlineData("qmul(z, z) + c")]
    [InlineData("qpow(z, 3) + c")]
    [InlineData("qsin(z) + c")]
    [InlineData("qexp(z) + qconj(z)")]
    [InlineData("abs(z) + c")]
    public void Compiled_matches_interpreter_quat(string src)
    {
        var interp = SandboxBulbExpression.Parse(src, new[] { "t" });
        var comp = SandboxBulbExpression.Parse(src, new[] { "t" });
        Assert.True(comp.TryCompile());

        var envI = interp.NewEnv();
        var envC = comp.NewEnv();
        var c = new Quat(0.2, -0.1, 0.3, 0.15);
        double[] ex = { 0.0 };
        foreach (var (x, y, zc) in Inputs)
        {
            var z = new Quat(x, y, zc, 0.5);
            Quat ri = interp.EvalStepQuat(z, c, 0, envI, ex);
            Quat rc = comp.EvalStepQuat(z, c, 0, envC, ex);
            AssertBitEqual(ri, rc, $"{src} @ z=({x},{y},{zc})");
        }
    }

    private static void AssertBitEqual(Vec3 a, Vec3 b, string ctx)
    {
        Bit(a.X, b.X, ctx + " .x");
        Bit(a.Y, b.Y, ctx + " .y");
        Bit(a.Z, b.Z, ctx + " .z");
    }

    private static void AssertBitEqual(Quat a, Quat b, string ctx)
    {
        Bit(a.W, b.W, ctx + " .w");
        Bit(a.X, b.X, ctx + " .x");
        Bit(a.Y, b.Y, ctx + " .y");
        Bit(a.Z, b.Z, ctx + " .z");
    }

    private static void Bit(double a, double b, string ctx) =>
        Assert.True(
            BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"bit mismatch [{ctx}]: interp={a:R} compiled={b:R}");
}
