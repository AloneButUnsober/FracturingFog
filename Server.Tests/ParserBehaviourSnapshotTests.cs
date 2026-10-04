// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1101 — behaviour snapshot of BOTH equation parsers (2D SandboxExpression and
// User Bulb 3D SandboxBulbExpression), frozen before the shared front end
// replaced their hand-written parsers. For every input it records the parse
// tree (S-expression), or the exception type, message and span; the legacy
// parses also record their migration edits. The refactor must reproduce the
// file exactly; any intended difference is a reviewed edit of the baseline.
//
// Baseline: Server.Tests/TestData/ParserBehaviour1101.txt (read from the source
// tree). If it is missing the test writes it and fails, so it is never
// silently regenerated.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ParserBehaviourSnapshotTests
{
    // Shared probes: valid forms, every syntax error path, and the corners
    // where the two languages differ (members, constants, keywords).
    private static readonly string[] Probes =
    {
        "", "   ", "z +", "(z", "z)", "z ? c", "z ? c :", "let", "let = 3 in z", "let w = z", "let w = z in",
        "let z = 1 in z", "let pi = 1 in pi", "let then = 1 in z", "if z then c", "if re(z) > 0 then z",
        "if z.x > 0 then z", "if (z > 0) w = 1; z", "if (z > 0)", "if (z > 0) ;", "if (n > 0) z = c; z*z",
        "if (n > 0) return c; z*z", "var = 3; z", "var w = ; z", "return", "return z; z", "z;;", "z;",
        "1e", "1.2.3", "..5", ".5 + z", "1e3*z", "2.5e-2 + z", "z @ c", "sin(", "sin(z", "sin(z,", "sin(z,)",
        "sni(z)", "zz", "Sin(z)", "SIN(z)", "PI", "Pi", "E", "I", "pi", "e", "i", "z.x", "z.", "z.q", "z.Real",
        "z . x", "(z).y", "c.x.y", "z.w", "Complex.Pow(z,2)", "return z*z + c;", "z^", "^z", "z^^2", "-", "--z",
        "!z", "!", "z == ", "z = c", "z =", "a = 1; b = a; b + z", "x = 1; z", "then = 1; z", "e = 2; e*z",
        "i = 3; i", "pi = 3; pi", "w = 1;", "/* unterminated", "// only comment", "z /* c */ + c",
        "z // c\n+ c", "z\n+\nsni(c)", "\n\nfoo", "min(z)", "min(z,c,z)", "clamp(z,1)", "vec(1,2)",
        "qvec(1,2,3)", "dot(z)", "norm()", "norm(z,z)", "norm(z)", "length(z)", "abs(z) > 2 ? z : c",
        "if abs(z) > 2 then z else c", "if (abs(z) > 2) return z; z", "if abs(z) > 2 && abs(c) < 1 then z else c",
        "if !(abs(z) > 2) then z else c", "-z^2", "2*-z^2", "- -z^2", "+z^2", "z^-z^2", "(-z)^2", "-z.x^2",
        "z^2^3", "2^-1", "-2^2", "a^b", "z^-2", "z*z + c", "z^8 + c", "triplex(z, 8) + c", "abs(z)^8 + c",
        "let v = abs(z) in v.x - v.y < 0 ? vec(v.y, v.x, v.z) : v", "qmul(z, z) + c", "-qmul(z,z)^2 + c",
        "z > c && c < z || !(z == c) ? z : c", "z ? z : c", "if if z then c else z then z else c",
        "let w = (let v = z in v*v) in w + c", "var w = z; var w = w*w; w + c", "Vec3 v = z; v + c",
        "Quat q = z; q", "double k = 2; z*k", "Complex w = z; w", "int k = 2; z^k", "decimal d = 1; z",
        "long d = 1; z", "float f = 1; z*f", "t*z", "k*z + t", "prev + iter + z", "z*z + c + 0.5*prev",
    };

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("|", "\\p");

    private static string Edits(List<(int Pos, int Len, string Text)> edits)
        => edits.Count == 0 ? "" : " edits[" + string.Join(";", edits.Select(e => $"{e.Pos},{e.Len},{e.Text}")) + "]";

    private static string Try(Func<string> f)
    {
        try { return f(); }
        catch (SbxParseException ex) { return $"ERR SbxParseException @{ex.Position}+{ex.Length} step{ex.StepIndex}: {ex.Message}"; }
        catch (Exception ex) { return $"ERR {ex.GetType().Name}: {ex.Message}"; }
    }

    private static IEnumerable<string> Inputs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Probes.Concat(EquationLanguageGoldenTests.AllSources()).Concat(BulbLanguageGoldenTests.Corpus()))
            if (s != null && seen.Add(s)) yield return s;
    }

    private static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var src in Inputs())
        {
            string k = Esc(src);
            sb.Append("2D  P |").Append(k).Append("| ").AppendLine(Try(() => EquationLanguage.ToSExpression(SandboxExpression.Parse(src))));
            sb.Append("2D  L |").Append(k).Append("| ").AppendLine(Try(() =>
            {
                var ed = new List<(int, int, string)>();
                var e = SandboxExpression.ParseLegacy(src, ed);
                return EquationLanguage.ToSExpression(e) + Edits(ed);
            }));
            sb.Append("3D  P |").Append(k).Append("| ").AppendLine(Try(() =>
                SandboxBulbExpression.ToSExpression(SandboxBulbExpression.Parse(src, new[] { "t", "k" }).Root)));
            sb.Append("3D  L |").Append(k).Append("| ").AppendLine(Try(() =>
            {
                var ed = new List<(int, int, string)>();
                var e = SandboxBulbExpression.ParseLegacy(src, ed);
                return SandboxBulbExpression.ToSExpression(e.Root) + Edits(ed);
            }));
            sb.Append("3D  N |").Append(k).Append("| ").AppendLine(Try(() =>
                SandboxBulbExpression.ToSExpression(SandboxBulbExpression.ParseLenient(src).Root)));
        }

        // Chains: shared scope across steps, step outputs, errors in a later step.
        var chains = new[]
        {
            new[] { ("a", "z^8 + c"), ("b", "a^4 + c") },
            new[] { ("a", "z^8 + c"), ("b", "aa^4 + c") },
            new[] { ("a", "let w = z in w*w"), ("b", "w + a") },
            new[] { ("z", "z*z") },
            new[] { ("a", "var v = boxfold(z, 1.0); v * 2.0 + c"), ("b", "if a.x > 0 then a else -a") },
            new[] { ("a", "z*z"), ("a", "a + c") },
        };
        foreach (var ch in chains)
        {
            var steps = ch.Select(s => new UserBulbChainStep { OutputName = s.Item1, Source = s.Item2 }).ToList();
            string k = Esc(string.Join(" ## ", ch.Select(s => s.Item1 + ":" + s.Item2)));
            sb.Append("3D  C |").Append(k).Append("| ").AppendLine(Try(() =>
            {
                var chain = SandboxBulbChain.Parse(steps, new[] { "t" });
                return string.Join(" ; ", chain.StepRoots.Select(SandboxBulbExpression.ToSExpression))
                       + " out[" + string.Join(",", chain.StepOutputSlots) + "] env" + chain.EnvSize;
            }));
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string BaselinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Server.Tests", "TestData", "ParserBehaviour1101.txt");
    }

    [Fact]
    public void BothParsers_BehaveExactlyAsTheFrozenBaseline()
    {
        string now = Dump();
        string path = BaselinePath();
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, now, new UTF8Encoding(false));
            Assert.Fail($"Baseline written to {path}; review and commit it, then re-run.");
        }
        string then = File.ReadAllText(path).Replace("\r\n", "\n");
        if (then == now) return;

        var a = then.Split('\n'); var b = now.Split('\n');
        var diffs = new List<string>();
        for (int i = 0; i < Math.Max(a.Length, b.Length) && diffs.Count < 20; i++)
        {
            string x = i < a.Length ? a[i] : "<missing>", y = i < b.Length ? b[i] : "<missing>";
            if (x != y) diffs.Add($"line {i + 1}\n  was: {x}\n  now: {y}");
        }
        Assert.Fail("Parser behaviour changed:\n" + string.Join("\n", diffs));
    }
}
