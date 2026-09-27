// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #994 (CB2 of #64) — the Command panel's model. The oracle is always the real
// batch parser: what the composer emits must parse to what was chosen.
public sealed class CommandComposerTests
{
    private static BatchOptions ParseOk(IReadOnlyList<string> args)
    {
        Assert.True(BatchOptions.TryParse(args.ToArray(), 0, out var o, out var err), err);
        return o;
    }

    private static string Json(BatchOptions o) => JsonSerializer.Serialize(o);

    private static CommandComposer Coords()
    {
        var c = new CommandComposer();
        c.Batch(() =>
        {
            c.Set(BatchFlags.X, "-0.75");
            c.Set(BatchFlags.Y, "0.1");
            c.Set(BatchFlags.Zoom, "4");
            c.Set(BatchFlags.Out, "o.png");
        });
        return c;
    }

    [Fact]
    public void Fresh_IsAnImageRender_WithAPlaceholderOutput_AndNeedsASource()
    {
        var c = new CommandComposer();
        Assert.Equal(BatchMode.Image, c.Mode);
        Assert.True(c.HasPlaceholderOutput);
        Assert.Equal(new[] { BatchFlags.Out, CommandComposer.OutputPlaceholder }, c.Args());
        Assert.Contains("--region", c.Validate());
    }

    [Fact]
    public void Command_QuotesForTheShell_AndParsesToTheChoices()
    {
        var c = Coords();
        c.Set(BatchFlags.Theme, "Fire 3D (PBR)");
        Assert.Null(c.Validate());
        Assert.StartsWith("FracturingFog --batch ", c.Command());
        Assert.Contains("--theme \"Fire 3D (PBR)\"", c.Command());
        var o = ParseOk(c.Args());
        Assert.Equal(-0.75, o.CenterX);
        Assert.Equal("Fire 3D (PBR)", o.ThemeName);
        Assert.False(c.HasPlaceholderOutput);
    }

    [Fact]
    public void Aliases_AreStoredUnderTheCanonicalName()
    {
        var c = new CommandComposer();
        c.Set("-t", "Fire");
        Assert.True(c.IsSelected(BatchFlags.Theme));
        Assert.Equal("Fire", c.ValueOf("--theme"));
        c.Clear("-t");
        Assert.False(c.IsSelected(BatchFlags.Theme));
    }

    [Fact]
    public void ModeSpecificFlags_AreParked_NotLost()
    {
        var c = Coords();
        c.Set(BatchFlags.AovExr);
        Assert.Contains(BatchFlags.AovExr, c.Args());

        c.Mode = BatchMode.Video;
        Assert.Equal(new[] { BatchFlags.Mode, "video" }, c.Args().Take(2));
        Assert.DoesNotContain(BatchFlags.AovExr, c.Args());
        Assert.Contains(c.Parked, s => s.Name == BatchFlags.AovExr);
        Assert.Null(c.Validate());   // --aov-exr would be rejected in video mode, but it is parked

        c.Mode = BatchMode.Image;
        Assert.Contains(BatchFlags.AovExr, c.Args());
    }

    [Fact]
    public void ModesThatNeedAValue_EmitTheirSelectingFlag()
    {
        var c = Coords();
        c.Mode = BatchMode.Slideshow;
        Assert.Equal(new[] { BatchFlags.Mode, "slideshow" }, c.Args().Take(2));   // blank = active preset
        c.Set(BatchFlags.Slideshow, "Default");
        Assert.Equal(new[] { BatchFlags.Slideshow, "Default" }, c.Args().Take(2));
        Assert.Equal(BatchMode.Slideshow, ParseOk(c.Args()).Mode);

        c.Mode = BatchMode.Scene;
        Assert.Contains("--scene", c.Validate());   // name still missing
        c.Set(BatchFlags.Scene, "Intro");
        Assert.Equal(BatchMode.Scene, ParseOk(c.Args()).Mode);

        c.Mode = BatchMode.Regrade;
        c.Set(BatchFlags.RegradeExr, "in.exr");
        var o = ParseOk(c.Args());
        Assert.Equal(BatchMode.Regrade, o.Mode);
        Assert.Null(o.CenterX);   // the source flags do not apply to a regrade — parked
    }

    [Fact]
    public void Remote_EmitsOnlyRemoteFlags()
    {
        var c = Coords();
        c.Set(BatchFlags.Relief);
        c.Remote = true;
        c.Set(BatchFlags.Connection, "box");
        c.Set(BatchFlags.Render, "poster");
        var args = c.Args();
        Assert.Equal(BatchFlags.Remote, args[0]);
        Assert.DoesNotContain(BatchFlags.Relief, args);
        Assert.DoesNotContain(BatchFlags.X, args);
        var o = ParseOk(args);
        Assert.True(o.Remote);
        Assert.Equal("box", o.RemoteConnection);
    }

    [Fact]
    public void FractalSpecificFlags_ApplyOnlyToTheirFractal()
    {
        var c = Coords();
        c.Set(BatchFlags.MultibrotExp, "5");
        Assert.DoesNotContain(BatchFlags.MultibrotExp, c.Args());   // default fractal is Mandelbrot
        c.Set(BatchFlags.Fractal, "Multibrot");
        Assert.Contains(BatchFlags.MultibrotExp, c.Args());

        // A region hides the fractal, so family flags stay available.
        var r = new CommandComposer();
        r.Set(BatchFlags.Region, "Seahorse Valley");
        r.Set(BatchFlags.MultibrotExp, "5");
        Assert.Null(r.Fractal);
        Assert.Contains(BatchFlags.MultibrotExp, r.Args());
    }

    [Fact]
    public void State_ReportsImplied_Blocked_AndMissing()
    {
        var c = Coords();
        c.Set(BatchFlags.DofAperture, "0.2");
        var relief = c.StateOf(BatchFlagCatalog.Find(BatchFlags.Relief)!);
        Assert.Contains(BatchFlags.DofAperture, relief.ImpliedBy);

        // --dof-aperture conflicts with the ortho camera directly, and with
        // --relief-absolute through the raymarch it implies.
        Assert.Contains(BatchFlags.DofAperture, c.StateOf(BatchFlagCatalog.Find(BatchFlags.ReliefCameraOrtho)!).BlockedBy);
        Assert.Contains(BatchFlags.ReliefRaymarch, c.StateOf(BatchFlagCatalog.Find(BatchFlags.ReliefAbsolute)!).BlockedBy);

        c.Clear(BatchFlags.DofAperture);
        c.Set(BatchFlags.DofFocus, "3");
        Assert.Contains(BatchFlags.DofAperture, c.StateOf(BatchFlagCatalog.Find(BatchFlags.DofFocus)!).Missing);
    }

    [Fact]
    public void ChoiceImplications_FollowTheValue()
    {
        var c = Coords();
        var raymarch = BatchFlagCatalog.Find(BatchFlags.ReliefRaymarch)!;
        c.Set(BatchFlags.LightFlag(1, BatchFlags.LightFieldType), "directional");
        Assert.False(c.StateOf(raymarch).IsImplied);
        c.Set(BatchFlags.LightFlag(1, BatchFlags.LightFieldType), "spot");
        Assert.True(c.StateOf(raymarch).IsImplied);
        Assert.True(ParseOk(c.Args()).ReliefRaymarch);   // and the parser agrees
    }

    [Fact]
    public void Load_RoundTripsTheLiveBuilder()
    {
        var p = new FractalParameters { MultibrotExponent = 5 };
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Multibrot, CenterX = 0.25, CenterY = -0.5, Zoom = 12, Iterations = 900,
            ThemeName = "Fire", QualityName = "High", Brightness = 10,
            ReliefEnabled = true, ReliefRaymarch = true, ReliefHeight = 2.5, ReliefIsolate = true, ReliefIsolateColors = "#FF0000, #00FF00",
            FogDensity = 0.3, DomainWarpActive = true, DomainWarpStrength = 0.4, Parameters = p,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var c = new CommandComposer();
        c.Load(report.Args);
        Assert.Equal(Json(ParseOk(report.Args)), Json(ParseOk(c.Args())));
    }

    [Fact]
    public void Load_DerivesTheMode()
    {
        var c = new CommandComposer();
        c.Load(new[] { "--slideshow", "Default", "--seconds", "90", "--out", "s.mp4" });
        Assert.Equal(BatchMode.Slideshow, c.Mode);
        c.Load(new[] { "--remote", "--connection", "a", "--render", "b", "--out", "o.png" });
        Assert.True(c.Remote);
        c.Load(new[] { "--mode", "video", "--region", "R", "--out", "o" });
        Assert.Equal(BatchMode.Video, c.Mode);
        Assert.False(c.Remote);
        Assert.Throws<ArgumentException>(() => c.Load(new[] { "--no-such-flag" }));
    }

    [Fact]
    public void SeedLook_ReplacesTheLook_AndKeepsTheJob()
    {
        var c = Coords();
        c.Mode = BatchMode.Video;
        c.Batch(() =>
        {
            c.Set(BatchFlags.Seconds, "45");
            c.Set(BatchFlags.Width, "3840");
            c.Set(BatchFlags.Relief);
            c.Set(BatchFlags.Theme, "Old");
        });

        c.SeedLook(new[] { "--fractal", "Julia", "--x", "0", "--y", "0", "--zoom", "2", "--theme", "New",
                           "--width", "1920", "--height", "1080", "--out", "<OUTPUT.png>" });

        Assert.Equal(BatchMode.Video, c.Mode);
        Assert.Equal("45", c.ValueOf(BatchFlags.Seconds));
        Assert.Equal("o.png", c.ValueOf(BatchFlags.Out));
        Assert.Equal("3840", c.ValueOf(BatchFlags.Width));     // already set: kept
        Assert.Equal("1080", c.ValueOf(BatchFlags.Height));    // unset: taken from the seed
        Assert.Equal("New", c.ValueOf(BatchFlags.Theme));
        Assert.False(c.IsSelected(BatchFlags.Relief));         // look flag not in the seed: cleared
        Assert.Equal(FractalType.Julia, c.Fractal);
    }

    [Fact]
    public void Changed_IsCoalescedInsideBatch()
    {
        var c = new CommandComposer();
        int n = 0;
        c.Changed += () => n++;
        c.Batch(() => { c.Set(BatchFlags.X, "1"); c.Set(BatchFlags.Y, "2"); c.Mode = BatchMode.Video; });
        Assert.Equal(1, n);
        c.Set(BatchFlags.X, "1");   // no-op
        Assert.Equal(1, n);
        c.Reset();
        Assert.Equal(2, n);
        Assert.Equal(BatchMode.Image, c.Mode);
    }

    // ── #996 value-dependent and glass requirements ──────────────────────────

    private static CommandFlagState State(CommandComposer c, string flag) => c.StateOf(BatchFlagCatalog.Find(flag)!);

    [Fact]
    public void LightPositionFields_NeedAPositionalLightType()
    {
        var c = Coords();
        string type = BatchFlags.LightFlag(2, BatchFlags.LightFieldType);
        string pos = BatchFlags.LightFlag(2, BatchFlags.LightFieldPos);
        string cone = BatchFlags.LightFlag(2, BatchFlags.LightFieldCone);
        c.Set(pos, "0,1,2");
        c.Set(cone, "10,20");
        Assert.Contains(type + " point|spot", State(c, pos).Missing);   // absent type = directional (default)
        Assert.Contains(type + " spot", State(c, cone).Missing);

        c.Set(type, "point");
        Assert.Empty(State(c, pos).Missing);
        Assert.Contains(type + " spot", State(c, cone).Missing);

        c.Set(type, "SPOT");
        Assert.Empty(State(c, cone).Missing);
        Assert.Empty(State(c, BatchFlags.LightFlag(1, BatchFlags.LightFieldColor)).Missing);   // plain fields need nothing
    }

    [Fact]
    public void GlassOptics_NeedGlass_WhichTransmissionProvides()
    {
        var c = Coords();
        c.Set(BatchFlags.Ior, "1.33");
        Assert.Contains(BatchFlags.Glass, State(c, BatchFlags.Ior).Missing);
        c.Set(BatchFlags.Transmission, "0.8");   // implies --glass
        Assert.Empty(State(c, BatchFlags.Ior).Missing);
    }

    [Fact]
    public void Requirements_OnlyReportForSelectedFlags()
    {
        var c = Coords();
        Assert.Empty(State(c, BatchFlags.LightFlag(1, BatchFlags.LightFieldCone)).Missing);
        Assert.Empty(State(c, BatchFlags.Ior).Missing);
    }
}
