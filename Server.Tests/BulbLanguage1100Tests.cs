// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1100 — the User Bulb 3D language adopts the 2D equation language's surface
// rules (Docs/Technical/UserBulb-Language-Alignment.md):
//   -x^y = -(x^y); if … then … else; statements typed in the editor; norm;
//   errors "at line L, col C. Did you mean …?" — with Sbx3Node unchanged.
// Saved text is protected by BulbLanguageMigration (version stamp on store
// entries and on region inline sources; backup before rewrite; imports too).
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Assets;
using FracturingFog.Calculators;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class BulbLanguage1100Tests
{
    private static string Tree(string src) =>
        SandboxBulbExpression.ToSExpression(SandboxBulbExpression.Parse(src, new[] { "t" }).Root);

    // ── Grammar (slots: $0 z, $1 c, $2 n, $3 t, then lets) ──

    [Theory]
    [InlineData("-z^2 + c",                       "(+ (neg (^ $0 2)) $1)")]
    [InlineData("(-z)^2 + c",                     "(+ (^ (neg $0) 2) $1)")]
    [InlineData("2*-z^2",                         "(* 2 (neg (^ $0 2)))")]
    [InlineData("z^-2",                           "(^ $0 (neg 2))")]
    [InlineData("z^2^3",                          "(^ $0 (^ 2 3))")]
    [InlineData("+z^2",                           "(^ $0 2)")]
    [InlineData("-z.x^2",                         "(neg (^ (.x $0) 2))")]
    [InlineData("if z.x > 0 then z else c",       "(? (> (.x $0) 0) $0 $1)")]
    [InlineData("var w = z*z; return w + c;",     "(let $4 (* $0 $0) (+ $4 $1))")]
    [InlineData("Vec3 w = z*z;\nw + c",           "(let $4 (* $0 $0) (+ $4 $1))")]
    [InlineData("if (n == 0) z = c;\nz*z + c",    "(let $4 (? (== $2 0) $1 $0) (+ (* $4 $4) $1))")]
    [InlineData("if (z.x > 2) return c;\nz*z",    "(? (> (.x $0) 2) $1 (* $0 $0))")]
    [InlineData("norm(z) * z + c",                "(+ (* (norm $0) $0) $1)")]
    [InlineData("z*z + c;",                       "(+ (* $0 $0) $1)")]
    public void ParsesToTheSpecifiedTree(string src, string expected) => Assert.Equal(expected, Tree(src));

    [Theory]
    [InlineData("-z^2",   "(^ (neg $0) 2)")]
    [InlineData("2*-z^2", "(* 2 (^ (neg $0) 2))")]
    [InlineData("z^-2",   "(^ $0 (neg 2))")]
    public void LegacyRules_ParseToTheVersion1Tree(string src, string expected)
        => Assert.Equal(expected, SandboxBulbExpression.ToSExpression(SandboxBulbExpression.ParseLegacy(src).Root));

    // ── Errors in the 2D format, span kept ──

    [Fact]
    public void UnknownFunction_IsPositioned_WithDidYouMean()
    {
        var ex = Assert.Throws<SbxParseException>(() => SandboxBulbExpression.Parse("z^2 + sni(c)"));
        Assert.Equal("Unknown function 'sni' at line 1, col 7. Did you mean 'sin'?", ex.Message);
        Assert.Equal(6, ex.Position);
        Assert.Equal(3, ex.Length);
    }

    [Fact]
    public void UnknownIdentifier_OnALaterLine_IsPositioned_WithDidYouMean()
    {
        var ex = Assert.Throws<SbxParseException>(() => SandboxBulbExpression.Parse("var w = z*z;\nw + cc"));
        Assert.Equal("Unknown identifier 'cc' at line 2, col 5. Did you mean 'c'?", ex.Message);
        Assert.Equal(17, ex.Position);
        Assert.Equal(2, ex.Length);
    }

    // ── norm ──

    [Fact]
    public void Norm_IsTheSquaredLength_OnEveryKind()
    {
        var env = new SbxVal3[8];
        var e = SandboxBulbExpression.Parse("vec(norm(z.x), norm(z), length(z)*length(z))");
        var r = e.EvalStep(new Vec3(1, 2, 3), new Vec3(0, 0, 0), 0, e.NewEnv());
        Assert.Equal(1.0, r.X);
        Assert.Equal(14.0, r.Y);
        Assert.Equal(14.0, r.Z, 12);
        var q = SandboxBulbExpression.Parse("qvec(norm(z), 0, 0, 0)");
        var rq = q.EvalStepQuat(new Quat(1, 2, 3, 4), new Quat(0, 0, 0, 0), 0, q.NewEnv());
        Assert.Equal(30.0, rq.X);
    }

    [Fact]
    public void Norm_Emits_AndMatchesTheInterpreter()
    {
        foreach (var (src, quat) in new[] { ("norm(z)*z + c", false), ("vec(norm(z.y), 1, 2) + c", false), ("norm(z)*z + c", true) })
        {
            var e = SandboxBulbExpression.Parse(src, new[] { "t" });
            var em = UserBulbSandboxEmitter.Emit(e.Root, new[] { "t" }, quat);
            Assert.True(em.Ok, em.Error);
            var errors = new List<string>();
            var type = BulbEmitterParityTests.Compile(new List<(int, string, bool)> { (0, em.Body!, quat) }, errors);
            Assert.True(type != null, string.Join("\n", errors));
            var m = type!.GetMethod("S0")!;
            if (quat)
            {
                var z = new Quat(0.2, 0.3, -0.4, 0.5); var c = new Quat(0.1, 0, 0.2, 0);
                var a = e.EvalStepQuat(z, c, 1, e.NewEnv(), new[] { 0.7 });
                var b = (Quat)m.Invoke(null, new object[] { z, c, 1, 0.7 })!;
                Assert.Equal(a, b);
            }
            else
            {
                var z = new Vec3(0.3, -0.4, 0.5); var c = new Vec3(0.1, 0, 0.2);
                var a = e.EvalStep(z, c, 1, e.NewEnv(), new[] { 0.7 });
                var b = (Vec3)m.Invoke(null, new object[] { z, c, 1, 0.7 })!;
                Assert.Equal((a.X, a.Y, a.Z), (b.X, b.Y, b.Z));
            }
        }
    }

    // ── Migration ──

    [Theory]
    [InlineData("-z^8 + c",                "(-z)^8 + c")]
    [InlineData("2*-z^8 + c",              "2*(-z)^8 + c")]
    [InlineData("- -z^2 + c",              "(- -z)^2 + c")]
    [InlineData("-z.x^2 + c.x",            "(-z.x)^2 + c.x")]
    [InlineData("-mbox^8 + c",             "(-mbox)^8 + c")]       // chain-step name: no scope needed
    [InlineData("let w = -z in -w^3 + c",  "let w = -z in (-w)^3 + c")]
    public void Migration_WrapsASignedBase(string v1, string v2)
    {
        var r = BulbLanguageMigration.UpgradeFromVersion1(v1);
        Assert.True(r.Changed, r.Note);
        Assert.Equal(v2, r.Source);
    }

    [Theory]
    [InlineData("z^8 + c")]
    [InlineData("-(z^2) + c")]
    [InlineData("z^-2 + c")]
    [InlineData("let w = -z in w*w + c")]
    [InlineData("Quat scaled = new Quat(z.W * n, z.X * n, z.Y * n, z.Z * n);")]   // C#-style: left as is
    public void Migration_LeavesOtherTextAlone(string src)
    {
        var r = BulbLanguageMigration.UpgradeFromVersion1(src);
        Assert.False(r.Changed);
        Assert.Equal(src, r.Source);
    }

    [Theory]
    [InlineData("-z^8 + c")]
    [InlineData("2*-z^3 - c")]
    [InlineData("- -z^2 + c*0.5")]
    [InlineData("vec(-z.x^2, -z.y^3, z.z) + c")]
    [InlineData("-qmul(z, z)^2 + c")]
    public void Migration_PreservesMeaning_OnAGrid(string v1)
    {
        var up = BulbLanguageMigration.UpgradeFromVersion1(v1).Source;
        var oldE = SandboxBulbExpression.ParseLegacy(v1);
        var newE = SandboxBulbExpression.Parse(up);
        bool quat = v1.Contains("qmul");
        foreach (var x in new[] { -0.7, 0.3, 1.2 })
            foreach (var y in new[] { -0.4, 0.6 })
            {
                if (quat)
                {
                    var z = new Quat(0.1, x, y, 0.2); var c = new Quat(0.3, y, x, -0.1);
                    Assert.Equal(oldE.EvalStepQuat(z, c, 1, oldE.NewEnv()), newE.EvalStepQuat(z, c, 1, newE.NewEnv()));
                }
                else
                {
                    var z = new Vec3(x, y, 0.2); var c = new Vec3(y, x, -0.1);
                    var a = oldE.EvalStep(z, c, 1, oldE.NewEnv()); var b = newE.EvalStep(z, c, 1, newE.NewEnv());
                    Assert.Equal((a.X, a.Y, a.Z), (b.X, b.Y, b.Z));
                }
            }
    }

    // ── Store ──

    private static string BulbsFile => AppDataPaths.Combine("userbulbs.json");

    private const string LegacyJson = """
        [
          { "Name": "B1100_Legacy", "Source": "-z^8 + c",
            "Chain": [ { "OutputName": "a", "Source": "-z^2 + c" }, { "OutputName": "b", "Source": "-a^3 + c" } ] },
          { "Name": "B1100_Current", "Source": "-z^8 + c", "LanguageVersion": 2 }
        ]
        """;

    [Fact]
    public void Store_UpgradesLegacyEntries_WithABackup_Once()
    {
        BulbLanguageMigration.Register();
        string dir = Path.GetDirectoryName(BulbsFile)!;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "userbulbs.json.*.bulb-language-v2*.bak")) File.Delete(f);
        File.WriteAllText(BulbsFile, LegacyJson);
        var store = UserBulbStore.Instance;
        try
        {
            store.Load();
            var legacy = store.GetByName("B1100_Legacy")!;
            Assert.Equal("(-z)^8 + c", legacy.Source);
            Assert.Equal("(-z)^2 + c", legacy.Chain![0].Source);
            Assert.Equal("(-a)^3 + c", legacy.Chain![1].Source);
            Assert.Equal(2, legacy.LanguageVersion);
            Assert.Equal("-z^8 + c", store.GetByName("B1100_Current")!.Source);   // already current
            Assert.Single(Directory.GetFiles(dir, "userbulbs.json.*.bulb-language-v2*.bak"));

            store.Load();                                                          // second load: no-op
            Assert.Equal("(-z)^8 + c", store.GetByName("B1100_Legacy")!.Source);
            Assert.Single(Directory.GetFiles(dir, "userbulbs.json.*.bulb-language-v2*.bak"));
            Assert.Contains("\"LanguageVersion\": 2", File.ReadAllText(BulbsFile));
        }
        finally { store.Remove("B1100_Legacy"); store.Remove("B1100_Current"); }
    }

    [Fact]
    public void AssetImport_UpgradesALegacyBulb()
    {
        BulbLanguageMigration.Register();
        var store = UserBulbStore.Instance;
        store.Load();
        try
        {
            new UserBulbAssetSource().ImportJson("""{ "Name": "B1100_Imported", "Source": "-z^4 + c" }""", overwrite: true);
            Assert.Equal("(-z)^4 + c", store.GetByName("B1100_Imported")!.Source);
        }
        finally { store.Remove("B1100_Imported"); }
    }

    // #1088's 2D stores had the same asset-import gap; fixed here.
    [Fact]
    public void AssetImport_UpgradesALegacyUserEquation()
    {
        var store = UserEquationStore.Instance;
        store.Load();
        try
        {
            new UserEquationAssetSource().ImportJson("""{ "Name": "UE1100_Imported", "Source": "-z^2 + c" }""", overwrite: true);
            Assert.Equal("(-z)^2 + c", store.GetByName("UE1100_Imported")!.Source);
        }
        finally { store.Remove("UE1100_Imported"); }
    }

    // ── Regions with an inline source ──

    [Fact]
    public void Region_InlineSource_FromBeforeTheChange_IsUpgradedOnRecall()
    {
        var legacy = new FractalRegion { FractalType = FractalType.UserBulb, UserBulbSource = "-z^8 + c" };
        Assert.Equal("(-z)^8 + c", legacy.EffectiveUserBulbSource());
        var p = new FractalParameters();
        legacy.ApplyHeadlessParams(p);
        Assert.Equal("(-z)^8 + c", p.UserBulbSource);

        var current = new FractalRegion { FractalType = FractalType.UserBulb, UserBulbSource = "-z^8 + c",
            UserBulbLanguageVersion = BulbLanguageMigration.CurrentLanguageVersion };
        Assert.Equal("-z^8 + c", current.EffectiveUserBulbSource());
    }

    // ── Exact quaternion-square DE detection (#115, unreachable since the raw-C# path went) ──

    [Theory]
    [InlineData("z*z + c", true)]
    [InlineData("c + qmul(z, z)", true)]
    [InlineData("qpow(z, 2) + c", true)]
    [InlineData("z^2 + c", true)]
    [InlineData("z*c + c", false)]
    [InlineData("qpow(z, 3) + c", false)]
    public void QuatSquare_IsDetected(string src, bool square)
    {
        var p = UserBulbAnalyticDE.DetectSandboxQuat(SandboxBulbExpression.Parse(src).Root);
        Assert.Equal(square ? AnalyticDEKind.Square : AnalyticDEKind.None, p.Kind);
    }
}
