// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// #1249 — the user-code equation on the batch command line: --equation NAME /
// --equation-source TEXT, emitted by the Command builder and parsed back, plus
// the User Equation / Sandbox render settings in the region snapshot.
public sealed class UserCodeSourceTests
{
    private const string Block = "var w = z*z;\nreturn w*z + c;";

    private static string[] Tokenize(string cmd)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < cmd.Length; i++)
        {
            char c = cmd[i];
            if (c == '\\' && inQuote && i + 1 < cmd.Length && cmd[i + 1] == '"') { sb.Append('"'); i++; continue; }
            if (c == '"') { inQuote = !inQuote; continue; }
            if (c == ' ' && !inQuote)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.ToArray();
    }

    private static BatchOptions ParseBuilt(BatchCommandSnapshot snap)
    {
        var argv = Tokenize(BatchCommandBuilder.Build(snap)).Select(a => a.StartsWith("<OUTPUT") ? "out.png" : a).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return opts;
    }

    [Theory]
    [InlineData("z*z + c")]
    [InlineData(Block)]
    [InlineData("a\\b\\\\c\nd\\n")]          // backslashes, including a literal "\n" pair
    [InlineData("line1\r\nline2\n\n\tend")]
    public void EncodeDecode_RoundTrips(string src)
    {
        string enc = UserCodeSource.Encode(src);
        Assert.DoesNotContain('\n', enc);
        Assert.DoesNotContain('\r', enc);
        Assert.Equal(src.Replace("\r\n", "\n"), UserCodeSource.Decode(enc));
    }

    [Fact]
    public void InlineSource_RoundTripsThroughTheCommand()
    {
        var snap = new BatchCommandSnapshot { Fractal = FractalType.UserEquation, EquationSource = Block };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains(BatchFlags.EquationSource, cmd);
        Assert.DoesNotContain('\n', cmd);
        var opts = ParseBuilt(snap);
        Assert.Equal(Block, opts.EquationSource);
        Assert.Null(opts.EquationName);
        Assert.Empty(BatchCommandBuilder.DetectGaps(snap).Where(g => g.Contains("equation")));
    }

    [Fact]
    public void SavedName_IsPreferredOverTheSource()
    {
        var snap = new BatchCommandSnapshot { Fractal = FractalType.Sandbox, EquationName = "My Sandbox", EquationSource = "z*z + c" };
        var opts = ParseBuilt(snap);
        Assert.Equal("My Sandbox", opts.EquationName);
        Assert.Null(opts.EquationSource);
    }

    [Fact]
    public void NoEquation_IsAGap_AndOtherTypesEmitNothing()
    {
        Assert.Contains(BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.UserBulb }),
                        g => g.Contains("no equation"));
        string cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { Fractal = FractalType.Multibrot, EquationSource = "z*z + c" });
        Assert.DoesNotContain("--equation", cmd);
    }

    [Fact]
    public void Parse_RejectsBothFlags()
    {
        Assert.False(BatchOptions.TryParse(new[] { "--equation", "a", "--equation-source", "z*z+c", "--out", "o.png" }, 0, out _, out var err));
        Assert.Contains("cannot be combined", err);
    }

    [Fact]
    public void ApplyInline_SetsTheFamilySourceAndClearsTheName()
    {
        var p = new FractalParameters { UserEquationName = "old", SandboxName = "old" };
        Assert.True(UserCodeSource.TryApplyInline(FractalType.UserEquation, Block, p, out _));
        Assert.Equal(Block, p.UserEquationSource);
        Assert.Null(p.UserEquationName);
        Assert.True(UserCodeSource.TryApplyInline(FractalType.UserBulb, "return z;", p, out _));
        Assert.Equal("return z;", p.UserBulbSource);
        Assert.False(UserCodeSource.TryApplyInline(FractalType.Mandelbrot, "z", p, out var err));
        Assert.Contains("--equation-source", err);
    }

    // The saved entry carries its render settings; the name is emitted only
    // when the entry reproduces the live source AND settings.
    [Fact]
    public void SavedEntry_AppliesItsSettings_AndMatchesOnlyWhenIdentical()
    {
        string name = "zz1249-" + Guid.NewGuid().ToString("N");
        var entry = new UserEquationEntry
        {
            Name = name, Source = "z^3 + c", EscapeRadius = 8, Seed = "1",
            BailoutCondition = "abs(z) < 1e-9", ColorInterior = true,
        };
        var store = UserEquationStore.Instance;
        lock (store.Equations) store.Equations.Add(entry);
        try
        {
            var p = new FractalParameters { EscapeRadius = 3, UserEquationSeed = "c" };
            Assert.True(UserCodeSource.TryApplySaved(FractalType.UserEquation, name, p, out var err), err);
            Assert.Equal("z^3 + c", p.UserEquationSource);
            Assert.Equal(name, p.UserEquationName);
            Assert.Equal(8, p.EscapeRadius);
            Assert.Equal("1", p.UserEquationSeed);
            Assert.Equal("abs(z) < 1e-9", p.UserEquationBailoutCondition);
            Assert.True(p.UserEquationColorInterior);

            Assert.Equal(name, UserCodeSource.SavedNameMatching(FractalType.UserEquation, p));
            p.UserEquationSeed = "-1";                                     // edited after loading
            Assert.Null(UserCodeSource.SavedNameMatching(FractalType.UserEquation, p));
            p.UserEquationSeed = "1";
            p.UserEquationSource = "z^3 + c + 0";                           // source edited
            Assert.Null(UserCodeSource.SavedNameMatching(FractalType.UserEquation, p));

            Assert.False(UserCodeSource.TryApplySaved(FractalType.UserEquation, name + "-missing", p, out err));
            Assert.Contains("No saved", err);
        }
        finally
        {
            lock (store.Equations) store.Equations.Remove(entry);
        }
    }

    // Inline source + --param reproduces non-default settings exactly.
    [Fact]
    public void Settings_RoundTripThroughParam()
    {
        var live = new FractalParameters
        {
            UserEquationSource = "z^3 + c", EscapeRadius = 6.5, UserEquationSeed = "0.5",
            UserEquationBailoutCondition = "abs(z - prev) < 1e-8", UserEquationBailoutReplacesModulus = true,
            UserEquationColorInterior = true,
        };
        var kv = RegionFractalParams.Snapshot(FractalType.UserEquation, live)!.ToKeyValues();
        var back = RegionFractalParams.FromKeyValues(kv, FractalType.UserEquation, out var err);
        Assert.True(back != null, err);
        var p = new FractalParameters();
        Assert.True(UserCodeSource.TryApplyInline(FractalType.UserEquation, live.UserEquationSource!, p, out _));
        back!.ApplyTo(p);
        Assert.Equal(live.EscapeRadius, p.EscapeRadius);
        Assert.Equal(live.UserEquationSeed, p.UserEquationSeed);
        Assert.Equal(live.UserEquationBailoutCondition, p.UserEquationBailoutCondition);
        Assert.True(p.UserEquationBailoutReplacesModulus);
        Assert.True(p.UserEquationColorInterior);

        // Defaults emit nothing.
        var none = RegionFractalParams.Snapshot(FractalType.UserEquation, new FractalParameters())?.ToKeyValues()
                   ?? Array.Empty<KeyValuePair<string, string>>();
        Assert.DoesNotContain(none, k => k.Key.StartsWith("UserEquation", StringComparison.Ordinal) && k.Key != "UserEquationRotationDegrees");

        // Sandbox shares the escape radius and bailout knobs.
        var sb = RegionFractalParams.Snapshot(FractalType.Sandbox, live)!.ToKeyValues();
        Assert.Contains(sb, k => k.Key == "UserEquationEscapeRadius");
        Assert.Contains(sb, k => k.Key == "UserEquationBailoutCondition");
    }
}
