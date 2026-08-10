// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 / #211 — the C#->DSL translator for saved 3D bulbs
// (UserBulbSourcePreprocessor) and the on-startup migration
// (UserBulbDslMigration). These prove:
//   1. a historical raw-C# Vec3/Quat bulb body translates to DSL that PARSES and
//      evaluates to the SAME step math as the hand-written DSL equivalent
//      (parity — the raw-C# render path is gone, so equivalence is proven
//      against the known-good shipped DSL rather than a live Roslyn compile),
//   2. statement blocks (var / if-assign / return) desugar correctly,
//   3. quaternion bodies translate to the q* builtins,
//   4. an arbitrary imperative body with no DSL form is left untranslated (null),
//   5. the startup migration upgrades translatable saved bulbs to DSL + pins the
//      Sandbox compiler, backs the file up first, and leaves both an
//      untranslatable bulb and an already-pinned entry alone.
//
// AppDataPaths is redirected to a throwaway temp dir for the whole test process
// (TestDataRootIsolation), so the UserBulbStore singleton here never touches real
// user data.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using FracturingFog;
using FracturingFog.Abstractions;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class UserBulbCSharpToDslTranslatorTests
{
    private static readonly string[] Extras = { "t" };

    // (z, c, n, t) sample grid the parity checks evaluate over.
    private static readonly (Vec3 Z, Vec3 C, int N, double T)[] Samples =
    {
        (new Vec3(0.5, 0.3, -0.2), new Vec3(0.1, 0.0, 0.0), 1, 0.4),
        (new Vec3(-0.7, 0.9, 0.4), new Vec3(-0.2, 0.3, 0.1), 3, 1.1),
        (new Vec3(0.2, -0.1, 0.8), new Vec3(0.05, -0.05, 0.2), 5, 2.7),
    };

    private static Vec3 EvalVec(string dsl, Vec3 z, Vec3 c, int n, double t)
    {
        var expr = SandboxBulbExpression.Parse(dsl, Extras);
        return expr.EvalStep(z, c, n, expr.NewEnv(), new[] { t });
    }

    private static void AssertParityVec(string csharp, string knownDsl, string label)
    {
        string? dsl = UserBulbSourcePreprocessor.Preprocess(csharp);
        Assert.False(string.IsNullOrWhiteSpace(dsl), $"[{label}] expected translation, got null");
        Assert.DoesNotContain("Vec3.", dsl!);
        Assert.DoesNotContain("new ", dsl!);
        foreach (var (z, c, n, t) in Samples)
        {
            var got = EvalVec(dsl!, z, c, n, t);
            var want = EvalVec(knownDsl, z, c, n, t);
            Assert.True(
                Math.Abs(got.X - want.X) < 1e-9 &&
                Math.Abs(got.Y - want.Y) < 1e-9 &&
                Math.Abs(got.Z - want.Z) < 1e-9,
                $"[{label}] {z} -> got ({got.X},{got.Y},{got.Z}) want ({want.X},{want.Y},{want.Z})\nDSL: {dsl}");
        }
    }

    // ── 1. single-expression Vec3 bodies ────────────────────────────────────

    [Fact]
    public void SquareTriplex_TranslatesAndMatchesDsl()
    {
        const string cs =
            "// Square-triplex Mandelbulb-lite.\n" +
            "return new Vec3(\n" +
            "    z.X*z.X - z.Y*z.Y - z.Z*z.Z,\n" +
            "    2*z.X*z.Y,\n" +
            "    2*z.X*z.Z) + c;";
        AssertParityVec(cs,
            "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c",
            "SquareTriplex");
    }

    [Fact]
    public void AnimatedPow_TranslatesPowToOperatorAndMatches()
    {
        AssertParityVec(
            "return Vec3.Pow(z, 4 + 2*Math.Sin(t)) + c;",
            "z^(4 + 2*sin(t)) + c",
            "AnimatedPow");
    }

    [Fact]
    public void HybridMandelboxSinglePass_TranslatesNestedCallsAndMatches()
    {
        AssertParityVec(
            "return Vec3.Pow(Vec3.SphereFold(Vec3.BoxFold(z, 1.0), 0.5, 1.0) * 2.0 + c, 8.0) + c;",
            "(spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c)^8.0 + c",
            "HybridMandelbox");
    }

    // ── 2. statement blocks (var / if-assign / return) ──────────────────────

    [Fact]
    public void MengerBlock_DesugarsToLetTernaryAndMatchesPrimitive()
    {
        const string cs =
            "var v = Vec3.Abs(z);\n" +
            "if (v.X - v.Y < 0) v = new Vec3(v.Y, v.X, v.Z);\n" +
            "if (v.X - v.Z < 0) v = new Vec3(v.Z, v.Y, v.X);\n" +
            "if (v.Y - v.Z < 0) v = new Vec3(v.X, v.Z, v.Y);\n" +
            "return new Vec3(v.X * 3.0 - 2.0, v.Y * 3.0 - 2.0, v.Z * 3.0);";
        // The shipped Menger primitive is the known-good DSL for the same fold.
        AssertParityVec(cs, UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdMenger)!.Source, "Menger");
    }

    [Fact]
    public void SierpinskiBlock_DesugarsAndMatchesPrimitive()
    {
        const string cs =
            "var v = z;\n" +
            "if (v.X + v.Y < 0) v = new Vec3(-v.Y, -v.X,  v.Z);\n" +
            "if (v.X + v.Z < 0) v = new Vec3(-v.Z,  v.Y, -v.X);\n" +
            "if (v.Y + v.Z < 0) v = new Vec3( v.X, -v.Z, -v.Y);\n" +
            "return new Vec3(v.X * 2.0 - 1.0, v.Y * 2.0 - 1.0, v.Z * 2.0 - 1.0);";
        AssertParityVec(cs, UserBulbChainPrimitives.GetById(UserBulbChainPrimitives.IdSierpinski)!.Source, "Sierpinski");
    }

    [Fact]
    public void KaleidoscopicSinglePass_TranslatesAndEvaluatesFinite()
    {
        const string cs =
            "var v = z;\n" +
            "if (v.X + v.Y < 0) v = new Vec3(-v.Y, -v.X,  v.Z);\n" +
            "if (v.X + v.Z < 0) v = new Vec3(-v.Z,  v.Y, -v.X);\n" +
            "if (v.Y + v.Z < 0) v = new Vec3( v.X, -v.Z, -v.Y);\n" +
            "v = Vec3.Rot(v, new Vec3(0, 1, 0), 0.5);\n" +
            "return v * 2.0 - new Vec3(1, 1, 1);";
        string? dsl = UserBulbSourcePreprocessor.Preprocess(cs);
        Assert.False(string.IsNullOrWhiteSpace(dsl));
        foreach (var (z, c, n, t) in Samples)
        {
            var v = EvalVec(dsl!, z, c, n, t);
            Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z));
        }
    }

    // ── 3. quaternion body ──────────────────────────────────────────────────

    [Fact]
    public void QuatDrift_TranslatesToQBuiltinsAndParses()
    {
        const string cs = "return Quat.Sin(z * n) + Quat.Exp(c + z) + z;";
        string? dsl = UserBulbSourcePreprocessor.Preprocess(cs);
        Assert.False(string.IsNullOrWhiteSpace(dsl));
        Assert.Contains("qsin(", dsl!);
        Assert.Contains("qexp(", dsl!);
        Assert.DoesNotContain("Quat.", dsl!);

        // Parses, and evaluates finite in Quat mode.
        var expr = SandboxBulbExpression.Parse(dsl!, Extras);
        var q = expr.EvalStepQuat(new Quat(0.3, 0.1, -0.2, 0.4), new Quat(0.05, 0.0, 0.1, 0.0), 2,
                                  expr.NewEnv(), new[] { 0.5 });
        Assert.True(double.IsFinite(q.W) && double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z));
    }

    // ── 4. untranslatable body stays null ───────────────────────────────────

    [Fact]
    public void HandRolledImperativeQuat_HasNoDslForm_ReturnsNull()
    {
        const string cs =
            "Quat scaled = new Quat(z.W * n, z.X * n, z.Y * n, z.Z * n);\n" +
            "double a = scaled.W;\n" +
            "Vec3 v = scaled.ToVec3();\n" +
            "double r = v.Length;\n" +
            "Vec3 vNorm = v / r;\n" +
            "Quat zSin = new Quat();\n" +
            "if (r < Math.Exp(-8)) { zSin = new Quat(0.0,0.0,0.0,Math.Sin(a)); }\n" +
            "else { zSin = Quat.FromVec3(Math.Cos(a) * Math.Sinh(r) * vNorm, Math.Sin(a) * Math.Cosh(r)); }\n" +
            "return zSin + z;";
        Assert.Null(UserBulbSourcePreprocessor.Preprocess(cs));
    }

    // "No DSL form" is the migration-level contract: the preprocessor is purely
    // syntactic, so a body it can't structurally handle returns null, while one it
    // rewrites into text the grammar still rejects (an unmapped member / callee)
    // is caught by the parse validation. Both leave the saved bulb editable.
    private static bool TranslatesToValidDsl(string cs)
    {
        string? dsl = UserBulbSourcePreprocessor.Preprocess(cs);
        if (string.IsNullOrWhiteSpace(dsl)) return false;
        try { SandboxBulbExpression.Parse(dsl!, Extras); return true; }
        catch { return false; }
    }

    [Theory]
    [InlineData("for (int i = 0; i < 3; i++) z = z * z; return z;")]  // structural: null
    [InlineData("return z.ToVec3();")]                                 // unmapped member: parse-rejected
    [InlineData("return SomeUnknown.Thing(z) + c;")]                   // unknown callee: parse-rejected
    public void UntranslatableForms_HaveNoValidDsl(string cs)
        => Assert.False(TranslatesToValidDsl(cs));

    // ── 5. startup migration over a stored file ─────────────────────────────

    [Fact]
    public void Migration_UpgradesTranslatableSavedBulbs_LeavesRestEditable()
    {
        var store = UserBulbStore.Instance;
        string file = AppDataPaths.Combine("userbulbs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        const string rawSquare =
            "return new Vec3(z.X*z.X - z.Y*z.Y - z.Z*z.Z, 2*z.X*z.Y, 2*z.X*z.Z) + c;";
        const string rawMengerStep =
            "var v = Vec3.Abs(z);\n" +
            "if (v.X - v.Y < 0) v = new Vec3(v.Y, v.X, v.Z);\n" +
            "if (v.X - v.Z < 0) v = new Vec3(v.Z, v.Y, v.X);\n" +
            "if (v.Y - v.Z < 0) v = new Vec3(v.X, v.Z, v.Y);\n" +
            "return new Vec3(v.X * 3.0 - 2.0, v.Y * 3.0 - 2.0, v.Z * 3.0);";
        const string rawHandRolled =
            "Vec3 v = z;\n" +
            "double r = v.Length;\n" +          // .Length property — no DSL form
            "return v / r + c;";

        var seed = new[]
        {
            new UserBulbEntry { Name = "ZZ My Square (raw)", Source = rawSquare },
            new UserBulbEntry
            {
                Name = "ZZ My Hybrid (raw chain)",
                Source = "return Vec3.Pow(z, 8.0) + c;",
                Chain = new List<UserBulbChainStep>
                {
                    new() { OutputName = "menger", Source = rawMengerStep },
                    new() { OutputName = "bulb",   Source = "return Vec3.Pow(menger * 0.3, 8.0) + c;" },
                },
            },
            new UserBulbEntry { Name = "ZZ My HandRolled (raw)", Source = rawHandRolled },
            new UserBulbEntry
            {
                Name = "ZZ My Prepinned",
                Source = "z^8 + c",
                Settings = new UserBulbSnapshot { Compiler = UserBulbCompilerKind.Sandbox },
            },
        };
        File.WriteAllText(file, JsonSerializer.Serialize(seed, new JsonSerializerOptions { WriteIndented = true }));

        store.Load();                                  // seeds built-ins + built-in DSL migration
        int changed = UserBulbDslMigration.Run(store); // the new C#->DSL migration under test

        // Square: upgraded + pinned + parses.
        var sq = store.GetByName("ZZ My Square (raw)");
        Assert.NotNull(sq);
        Assert.Equal(UserBulbCompilerKind.Sandbox, sq!.Settings?.Compiler);
        Assert.DoesNotContain("Vec3", sq.Source);
        SandboxBulbExpression.Parse(sq.Source, Extras);   // throws if invalid

        // Hybrid chain: every step upgraded + the chain parses as a whole.
        var hy = store.GetByName("ZZ My Hybrid (raw chain)");
        Assert.NotNull(hy);
        Assert.Equal(UserBulbCompilerKind.Sandbox, hy!.Settings?.Compiler);
        Assert.NotNull(hy.Chain);
        Assert.All(hy.Chain!, s => Assert.DoesNotContain("Vec3", s.Source));
        SandboxBulbChain.Parse(hy.Chain!, Extras);        // throws if invalid

        // HandRolled: no DSL form — untouched, not pinned.
        var hr = store.GetByName("ZZ My HandRolled (raw)");
        Assert.NotNull(hr);
        Assert.Equal(rawHandRolled, hr!.Source);
        Assert.True(hr.Settings?.Compiler is null or not UserBulbCompilerKind.Sandbox);

        // Prepinned: already Sandbox — skipped, source unchanged.
        var pp = store.GetByName("ZZ My Prepinned");
        Assert.NotNull(pp);
        Assert.Equal("z^8 + c", pp!.Source);

        // Changed count covers exactly the two translatable user entries.
        Assert.True(changed >= 2, $"expected >= 2 migrations, got {changed}");

        // A backup snapshot was taken before the destructive rewrite.
        string dir = Path.GetDirectoryName(file)!;
        Assert.NotEmpty(Directory.GetFiles(dir, "userbulbs.json.*.userbulbdsl.bak"));

        // Idempotent: a second run migrates nothing new.
        Assert.Equal(0, UserBulbDslMigration.Run(store));
    }
}
