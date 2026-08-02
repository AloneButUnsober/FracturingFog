// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 2a — executable gap-audit for folding the built-in UserBulb
// presets onto the safe 3D DSL (SandboxBulbExpression).
//
// For each single-source built-in the historical C# body (Vec3.* / new Vec3 /
// Math.* / var+if) is paired with a hand-authored DSL equivalent, and the two
// are evaluated over a (z, c) grid. Native Vec3 evaluation is the intended
// math (what the Roslyn path computes), so matching it proves the DSL form is
// a faithful replacement — the strings validated here are what Phase 2b ships
// into UserBulbStore.
//
// Also pins the Phase 2a grammar addition: `//` and `/* */` comments (the
// presets carry explanatory `//` lines).
//
// Audit outcome: every preset math idiom has a DSL form. `var x = …;` maps to
// `let x = … in`, `if (cond) x = A;` to `x = (cond ? A : x)` via nested lets,
// `new Vec3(…)` to `vec(…)`, `Vec3.Fn`/`Math.Fn` to the lowercase DSL builtin.
// The only construct with no DSL representation was comments, now supported.

using System;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SandboxBulbDslAuditTests
{
    private const double Tol = 1e-9;

    private static readonly Vec3[] ZSamples =
    {
        new(0.5, 0.3, -0.2), new(1.1, -0.7, 0.4),
        new(-0.6, 0.8, 0.9), new(0.2, 0.2, 1.3),
    };
    private static readonly Vec3[] CSamples =
    {
        new(0.1, 0.0, 0.0), new(-0.4, 0.2, 0.1),
    };

    private static Vec3 EvalDsl(string dsl, Vec3 z, Vec3 c, double t = 0.0)
    {
        var expr = SandboxBulbExpression.Parse(dsl, new[] { "t" });
        var env = expr.NewEnv();
        return expr.EvalStep(z, c, 0, env, new[] { t });
    }

    private static void AssertClose(Vec3 want, Vec3 got, string label, Vec3 z, Vec3 c)
    {
        double err = Math.Max(Math.Max(Math.Abs(want.X - got.X), Math.Abs(want.Y - got.Y)), Math.Abs(want.Z - got.Z));
        double scale = Math.Max(1.0, want.Length);
        Assert.True(err <= Tol * scale,
            $"[{label}] z={z.X},{z.Y},{z.Z} c={c.X},{c.Y},{c.Z}: want ({want.X},{want.Y},{want.Z}) got ({got.X},{got.Y},{got.Z}) err {err}");
    }

    private void CheckOverGrid(string label, string dsl, Func<Vec3, Vec3, Vec3> reference)
    {
        foreach (var z in ZSamples)
        foreach (var c in CSamples)
            AssertClose(reference(z, c), EvalDsl(dsl, z, c), label, z, c);
    }

    [Fact]
    public void SquareTriplex()
        => CheckOverGrid("square",
            "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c",
            (z, c) => new Vec3(z.X * z.X - z.Y * z.Y - z.Z * z.Z, 2 * z.X * z.Y, 2 * z.X * z.Z) + c);

    [Fact]
    public void MandelbulbP8()
        => CheckOverGrid("bulb8", "z^8 + c", (z, c) => Vec3.Pow(z, 8) + c);

    [Fact]
    public void MandelbulbP4()
        => CheckOverGrid("bulb4", "z^4 + c", (z, c) => Vec3.Pow(z, 4) + c);

    [Fact]
    public void SinBulb()
        => CheckOverGrid("sin", "sin(z)*1.5 + c", (z, c) => Vec3.Sin(z) * 1.5 + c);

    [Fact]
    public void AbsBulbP8()
        => CheckOverGrid("absbulb8", "abs(z)^8 + c", (z, c) => Vec3.Pow(Vec3.Abs(z), 8) + c);

    [Fact]
    public void Mandelbox()
        => CheckOverGrid("mandelbox",
            "spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c",
            (z, c) => Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0) * 2.0 + c);

    [Fact]
    public void CoshSinBulb_HadamardSemantics()
        // The C# preset used Vec3*Vec3, which has no operator (never compiled
        // under Roslyn). The DSL defines vec*vec as Hadamard — the intended
        // per-component product — so migration also repairs this preset.
        => CheckOverGrid("coshsin", "sin(z)*cosh(z) + c", (z, c) =>
        {
            var s = Vec3.Sin(z);
            var h = Vec3.Cosh(z);
            return new Vec3(s.X * h.X, s.Y * h.Y, s.Z * h.Z) + c;
        });

    [Fact]
    public void FoldedAbsYBulb()
        => CheckOverGrid("foldedabsY", "absy(z)^8 + c", (z, c) => Vec3.Pow(Vec3.AbsY(z), 8) + c);

    [Fact]
    public void ReflectedTriplex()
        => CheckOverGrid("reflected",
            "let w = vec(abs(z.x), abs(z.y), z.z) in " +
            "vec(w.x*w.x - w.y*w.y - w.z*w.z, 2*w.x*w.y, 2*w.x*w.z) + c",
            (z, c) =>
            {
                var w = new Vec3(Math.Abs(z.X), Math.Abs(z.Y), z.Z);
                return new Vec3(w.X * w.X - w.Y * w.Y - w.Z * w.Z, 2 * w.X * w.Y, 2 * w.X * w.Z) + c;
            });

    [Fact]
    public void AnimatedBreathing_UsesT()
    {
        const string dsl = "z^(4 + 2*sin(t)) + c";
        foreach (double t in new[] { 0.0, 0.7, 2.1 })
        foreach (var z in ZSamples)
        foreach (var c in CSamples)
        {
            Vec3 want = Vec3.Pow(z, 4 + 2 * Math.Sin(t)) + c;
            Vec3 got = EvalDsl(dsl, z, c, t);
            AssertClose(want, got, $"animated t={t}", z, c);
        }
    }

    [Fact]
    public void MengerFold_IfChainAsNestedLetTernary_WithComments()
    {
        // if (cond) v = A;  →  v = (cond ? A : v). Comments are Phase 2a-new.
        const string dsl =
            "// Menger-sponge fold: |x|,|y|,|z| then sort descending, scale-3 from (1,1,1).\n" +
            "let v0 = abs(z) in\n" +
            "let v1 = (v0.x - v0.y < 0 ? vec(v0.y, v0.x, v0.z) : v0) in\n" +
            "let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in\n" +
            "let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in\n" +
            "vec(v3.x*3.0 - 2.0, v3.y*3.0 - 2.0, v3.z*3.0)";

        Vec3 Reference(Vec3 z)
        {
            var v = Vec3.Abs(z);
            if (v.X - v.Y < 0) v = new Vec3(v.Y, v.X, v.Z);
            if (v.X - v.Z < 0) v = new Vec3(v.Z, v.Y, v.X);
            if (v.Y - v.Z < 0) v = new Vec3(v.X, v.Z, v.Y);
            return new Vec3(v.X * 3.0 - 2.0, v.Y * 3.0 - 2.0, v.Z * 3.0);
        }

        foreach (var z in ZSamples)
            AssertClose(Reference(z), EvalDsl(dsl, z, Vec3.Zero), "menger", z, Vec3.Zero);
    }

    // ── comment grammar coverage ────────────────────────────────────────────

    [Fact]
    public void LineComment_IsSkipped()
    {
        var got = EvalDsl("// leading note\nz + c // trailing note", new Vec3(1, 2, 3), new Vec3(0.1, 0.1, 0.1));
        AssertClose(new Vec3(1.1, 2.1, 3.1), got, "linecomment", default, default);
    }

    [Fact]
    public void BlockComment_IsSkipped()
    {
        var got = EvalDsl("z /* inline\nblock */ + c", new Vec3(1, 2, 3), new Vec3(0.5, 0, 0));
        AssertClose(new Vec3(1.5, 2, 3), got, "blockcomment", default, default);
    }

    [Fact]
    public void LoneSlash_IsStillDivision()
    {
        // z / 2 must not be swallowed as a comment.
        var got = EvalDsl("z / 2", new Vec3(4, 8, 2), Vec3.Zero);
        AssertClose(new Vec3(2, 4, 1), got, "division", default, default);
    }
}
