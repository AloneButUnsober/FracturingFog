// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1576 — `--param KEY=@path` reads a text value from a file, so values with
// double quotes (Math Plane scenes) don't have to survive cmd / PowerShell 5.1
// quoting; and the Command builder's detector for such values.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ParamFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-paramfile-" + Guid.NewGuid().ToString("N"));

    public ParamFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Scene =
        "det on\r\nvector v 1.5 1 #009E73\r\nregion D \"x^2 + y^2 < 1\" #F0E442\r\ncurve c implicit \"x^2 + y^2 - 4\" nolabel\r\n";

    private static RegionFractalParams? Parse(params (string Key, string Value)[] kv)
        => RegionFractalParams.FromKeyValues(kv.Select(p => new System.Collections.Generic.KeyValuePair<string, string>(p.Key, p.Value)),
                                             FractalType.MathPlane, out _);

    private static uint[] Render(string scene)
    {
        var calc = new MathPlaneCalculator(320, 180) { Zoom = 0.5, FractalParameters = new FractalParameters { MathPlaneScene = scene } };
        calc.Calculate(CancellationToken.None);
        return calc.ColorBuffer;
    }

    [Fact]
    public void FileValue_EqualsTheSameSceneInline()
    {
        string path = Path.Combine(_dir, "scene.txt");
        File.WriteAllText(path, Scene, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));   // BOM is fine
        var fromFile = new FractalParameters();
        Parse(("MathPlaneScene", "@" + path))!.ApplyTo(fromFile);
        var inline = new FractalParameters();
        Parse(("MathPlaneScene", Scene))!.ApplyTo(inline);

        Assert.True(MathPlaneScene.TryParse(fromFile.MathPlaneScene, out var a, out var e), e?.ToString());
        Assert.True(MathPlaneScene.TryParse(inline.MathPlaneScene, out var b, out _));
        Assert.Equal(b, a);
        Assert.Equal(Render(inline.MathPlaneScene), Render(fromFile.MathPlaneScene));
        Assert.Equal(4, a.Objects.Count + (a.ShowDet ? 1 : 0));
    }

    [Fact]
    public void QuotedPath_AndRelativePath_Work()
    {
        string path = Path.Combine(_dir, "my scene.txt");
        File.WriteAllText(path, "point A 1 2");
        var p = new FractalParameters();
        Parse(("MathPlaneScene", "@\"" + path + "\""))!.ApplyTo(p);
        Assert.Equal("point A 1 2", p.MathPlaneScene);

        // Relative to the working directory (not changed here: tests run in parallel).
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        Assert.False(Path.IsPathRooted(relative));
        var q = new FractalParameters();
        Parse(("MathPlaneScene", "@" + relative))!.ApplyTo(q);
        Assert.Equal("point A 1 2", q.MathPlaneScene);
    }

    [Fact]
    public void MissingFile_IsAnErrorNamingThePath()
    {
        string path = Path.Combine(_dir, "nope.txt");
        Assert.Null(RegionFractalParams.FromKeyValues(
            new[] { new System.Collections.Generic.KeyValuePair<string, string>("MathPlaneScene", "@" + path) },
            FractalType.MathPlane, out var err));
        Assert.Contains("MathPlaneScene", err);
        Assert.Contains("nope.txt", err);

        Assert.Null(RegionFractalParams.FromKeyValues(
            new[] { new System.Collections.Generic.KeyValuePair<string, string>("MathPlaneScene", "@") },
            FractalType.MathPlane, out var err2));
        Assert.Contains("needs a file path", err2);
    }

    [Fact]
    public void DoubleAt_IsALiteralAt()
    {
        var p = new FractalParameters();
        Parse(("BilliardCustomDisks", "@@not-a-file"))!.ApplyTo(p);
        Assert.Equal("@not-a-file", p.BilliardCustomDisks);
    }

    [Fact]
    public void NumericKeys_DoNotReadFiles()
    {
        string path = Path.Combine(_dir, "num.txt");
        File.WriteAllText(path, "0.5");
        Assert.Null(RegionFractalParams.FromKeyValues(
            new[] { new System.Collections.Generic.KeyValuePair<string, string>("MathPlaneT", "@" + path) },
            FractalType.MathPlane, out var err));
        Assert.Contains("not a valid number", err);
    }

    [Fact]
    public void QuotedParamKeys_FindsValuesWithDoubleQuotes()
    {
        var args = new[] { "--fractal", "MathPlane", "--param", "MathPlaneScene=region R \"x < 1\"", "--param", "MathPlaneT=0.5",
                           "--param", "Other=\"q\"", "--out", "x \"y\".png" };
        Assert.Equal(new[] { "MathPlaneScene", "Other" }, ShellQuoting.QuotedParamKeys(args));
        Assert.Empty(ShellQuoting.QuotedParamKeys(new[] { "--param", "MathPlaneScene=point A 1 1" }));
    }

    [Fact]
    public void HelpMentionsTheFileForm()
        => Assert.Contains("KEY=@path", BatchFlagCatalog.FormatUsage());
}
