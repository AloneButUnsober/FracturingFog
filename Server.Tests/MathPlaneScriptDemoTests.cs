// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1701 (scripted scenes SS7) — the scripted demos. Each one parses with a script,
// renders at 0, D/2 and D with no scene error, changes over its run, holds its end,
// starts as the scene written without its steps, and is named in the guide and the
// in-app help. (The parameterised MathPlanePresetTests also cover every demo:
// --param and Command-builder round trips.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptDemoTests
{
    private const int W = 320, H = 180;

    private static IEnumerable<MathPlanePreset> Scripted => MathPlanePresets.All.Where(d => d.Name.StartsWith("Script:"));

    public static IEnumerable<object[]> ScriptDemos() => Scripted.Select(d => new object[] { d.Name });

    private static MathPlanePreset Demo(string name) => MathPlanePresets.All.Single(d => d.Name == name);

    private static (uint[] Pixels, MathPlaneSceneError? Error) Render(MathPlanePreset d, double s, string? sceneOverride = null)
    {
        var p = new FractalParameters();
        d.Apply(p);
        if (sceneOverride != null) p.MathPlaneScene = sceneOverride;
        p.MathPlaneScriptTime = s;
        var calc = new MathPlaneCalculator(W, H)
        {
            CenterX = d.ViewWorldX, CenterY = -d.ViewWorldY, Zoom = d.Zoom, MaxIterations = 128, FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), calc.LastSceneError);
    }

    [Fact]
    public void ThereAreScriptedDemos()
    {
        Assert.True(Scripted.Count() >= 4);
        // Only "Script:" demos carry steps, so the plain demos stay single-state.
        foreach (var d in MathPlanePresets.All)
        {
            Assert.True(MathPlaneScene.TryParse(d.Scene, out var s, out var e), $"{d.Name}: {e}");
            Assert.Equal(d.Name.StartsWith("Script:"), s.Steps.Count > 0);
        }
    }

    [Theory]
    [MemberData(nameof(ScriptDemos))]
    public void AScriptedDemo_PlaysFromItsSceneToItsEnd(string name)
    {
        var d = Demo(name);
        Assert.True(MathPlaneScene.TryParse(d.Scene, out var scene, out _));
        double dur = MathPlaneScript.Duration(scene);
        Assert.InRange(dur, 3, 60);

        var start = Render(d, 0);
        var mid = Render(d, dur / 2);
        var end = Render(d, dur);
        Assert.Null(start.Error); Assert.Null(mid.Error); Assert.Null(end.Error);
        Assert.NotEqual(start.Pixels, end.Pixels);          // something happens
        Assert.NotEqual(start.Pixels, mid.Pixels);
        Assert.Equal(end.Pixels, Render(d, dur + 5).Pixels); // the end holds

        // At 0 s it is the scene written without its steps.
        string noSteps = string.Join("; ", d.Scene.Split(';').Select(x => x.Trim())
            .Where(x => !x.StartsWith("step ") && !x.StartsWith("with ")));
        Assert.Equal(Render(d, 0, noSteps).Pixels, start.Pixels);
    }

    [Theory]
    [MemberData(nameof(ScriptDemos))]
    public void AScriptedDemo_EndsWithoutACaption(string name)
    {
        // Each demo fades its last caption out, so its final frame is a clean picture.
        var d = Demo(name);
        Assert.True(MathPlaneScene.TryParse(d.Scene, out var scene, out _));
        var state = MathPlaneScript.Evaluate(scene, new FractalParameters(), MathPlaneScript.Duration(scene));
        Assert.True(state.Reveal == null || state.Reveal.CaptionAlpha == 0 || state.Reveal.Caption == null);
    }

    [Fact]
    public void TheGuideAndHelp_ExplainScripts()
    {
        string guide = File.ReadAllText(Path.Combine(RepoRoot(), "Docs", "User", "MathPlane-Guide.md"));
        foreach (var d in Scripted) Assert.Contains(d.Name, guide);
        foreach (var action in new[] { "wait", "show", "hide", "move", "draw", "orbit", "cobweb", "phase", "caption", "camera", "matrix" })
            Assert.Contains("`" + action, guide);
        Assert.Contains("--video-motion script", guide);
        // The guide's example script parses and has steps.
        string norm = guide.Replace("\r\n", "\n");
        int at = norm.IndexOf("**Example:**\n\n```\n", StringComparison.Ordinal);
        Assert.True(at >= 0, "the Scripts section has an example");
        int from = at + "**Example:**\n\n```\n".Length;
        string example = norm[from..norm.IndexOf("\n```", from, StringComparison.Ordinal)];
        Assert.True(MathPlaneScene.TryParse(example, out var ex, out var exErr), exErr?.ToString());
        Assert.True(ex.Steps.Count >= 3);
        Assert.Contains("step", FracturingFog.Help.HelpTextBundle.MathPlaneText);
        Assert.Contains("Script:", FracturingFog.Help.HelpTextBundle.MathPlaneText);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
