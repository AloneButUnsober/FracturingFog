// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1100 — frozen behaviour fingerprint of the User Bulb 3D language, taken
// with the parser BEFORE #1100 changed its surface rules (version 1:
// -x^y = (-x)^y). Every corpus source is evaluated on a fixed grid of
// (z, c, n, t) in Vec3 or Quat mode and the bit patterns are hashed.
//
// After #1100 each source is first upgraded (BulbLanguageMigration), then
// parsed with the current rules: the SAME fingerprint proves the new parser
// plus the migration evaluate every source exactly as the old parser did.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BulbLanguageGoldenTests
{
    private const ulong GoldenFingerprint = 11174027821771987222UL;   // frozen from the pre-#1100 parser

    // Hand-written cases covering the grammar, including the forms #1100
    // changes (a signed base before ^).
    private static readonly string[] Extra =
    {
        "z*z + c", "-z + c", "-(z^2) + c", "(-z)^2 + c", "-z^2 + c", "-z^3*2 + c",
        "2*-z^2 + c", "c - z^2", "z^-1 + c", "-z.x^2 + c", "vec(-z.x^2, z.y, z.z) + c",
        "let w = z*z in w + c", "let w = -z in w^2 + c",
        "z.x > 0 ? z^2 + c : -z^2 + c", "z.x > 0 && z.y < 1 ? z + c : c", "!(z.x > 0) ? z : c",
        "length(z) < 2 ? z^8 + c : z", "dot(z, z)*z + c", "cross(z, c) + c", "normalize(z) + c",
        "rot(z, vec(0, 0, 1), 0.3) + c", "triplex(z, 3) + c", "mod(z, 2.0) + c",
        "vec(smin(z.x, z.y, 0.2), floor(z.y), sign(z.z)) + c",
        "vec(min(z.x, z.y), max(z.y, z.z), clamp(z.x, -1, 1)) + c",
        "absx(z)^2 + absz(c)", "pow(z, 3) + c", "exp(z*0.1) + log(abs(z) + 1) + sqrt(abs(c))",
        "tan(z*0.2) + sinh(z*0.1) + tanh(c)", "/* note */ z^2 + c // trailing",
        "z^(2 + 0.5*sin(t)) + c", "z^2^1 + c", "+z^2 + c", "- -z^2 + c",
        // quaternion mode
        "qmul(z, z) + c", "qpow(z, 3) + c", "-qmul(z, z) + c", "-z^2 + c",
        "qexp(z*0.1) + qlog(c + 2) + qsqrt(c + 3)", "qinv(z + 2) + qconj(c)",
        "qsin(z*0.2) + qcos(c*0.1) + qtanh(z*0.1)", "qvec(z.x, z.y, z.z, -z.w^2) + c",
        "qasinh(z*0.1) + qatan(c*0.1) + qcot(c + 2)",
    };

    private static bool IsQuat(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"\bq[a-z]+\(|\.w\b");

    public static IEnumerable<string> Corpus()
    {
        foreach (var f in typeof(UserBulbStore).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Dsl"))
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
            yield return (string)f.GetRawConstantValue()!;
        foreach (var p in UserBulbChainPrimitives.All) yield return p.Source;
        foreach (var e in Extra) yield return e;
    }

    // #1100 — every source is upgraded from version 1 before it is parsed.
    private static string Upgrade(string src) => BulbLanguageMigration.UpgradeFromVersion1(src).Source;

    private static IEnumerable<List<UserBulbChainStep>> Chains()
    {
        yield return UserBulbChainPrimitives.MandelboxBulbHybrid();
        yield return UserBulbChainPrimitives.MengerBulbHybrid();
        yield return UserBulbChainPrimitives.KaleidoscopicIfsChain();
    }

    private static readonly double[] Ts = { 0.7 };

    [Fact]
    public void Corpus_EvaluatesExactlyAsTheFrozenOracle()
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixD(double d) => Mix((ulong)BitConverter.DoubleToInt64Bits(double.IsNaN(d) ? double.NaN : d));

        var zs = new[] { new Vec3(0.3, -0.2, 0.1), new Vec3(-0.7, 0.4, 0.25), new Vec3(1.1, 0.5, -0.6), new Vec3(0.05, 0.9, 0.3) };
        var cs = new[] { new Vec3(-0.5, 0.1, 0.2), new Vec3(0.3, -0.6, 0.45) };
        int parsed = 0, total = 0;
        var unparsed = new List<string>();
        foreach (string raw in Corpus())
        {
            total++;
            string src = Upgrade(raw);
            SandboxBulbExpression e;
            try { e = SandboxBulbExpression.Parse(src, new[] { "t" }); parsed++; Mix(1); }
            catch (FormatException ex) { Mix(2); unparsed.Add(raw + " → " + ex.Message); continue; }
            var env = e.NewEnv();
            bool quat = IsQuat(raw);
            foreach (var z in zs)
                foreach (var c in cs)
                    for (int n = 0; n < 2; n++)
                    {
                        try
                        {
                            if (quat)
                            {
                                var r = e.EvalStepQuat(new Quat(0.2, z.X, z.Y, z.Z), new Quat(-0.1, c.X, c.Y, c.Z), n, env, Ts);
                                MixD(r.W); MixD(r.X); MixD(r.Y); MixD(r.Z);
                            }
                            else
                            {
                                var r = e.EvalStep(z, c, n, env, Ts);
                                MixD(r.X); MixD(r.Y); MixD(r.Z);
                            }
                        }
                        catch (InvalidOperationException) { Mix(3); }
                    }
        }
        foreach (var steps in Chains())
        {
            var upgraded = steps.Select(s => new UserBulbChainStep { OutputName = s.OutputName, Source = Upgrade(s.Source) }).ToList();
            var chain = SandboxBulbChain.Parse(upgraded, new[] { "t" });
            var env = chain.NewEnv();
            foreach (var z in zs) foreach (var c in cs)
            {
                var r = chain.EvalStep(z, c, 1, env, Ts);
                MixD(r.X); MixD(r.Y); MixD(r.Z);
            }
        }
        Assert.True(parsed >= total - 1, $"only {parsed}/{total} corpus sources parse: " + string.Join(" | ", unparsed));
        Assert.True(GoldenFingerprint == 0UL || h == GoldenFingerprint, $"fingerprint {h}UL != frozen {GoldenFingerprint}UL");
        if (GoldenFingerprint == 0UL) Assert.Fail($"freeze GoldenFingerprint = {h}UL ({parsed}/{total} parsed)");
    }
}
