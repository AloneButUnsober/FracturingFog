// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 follow-up (#718) — the AOV-EXR relight ROUND-TRIP. Three pieces
// under test:
//   1. AovExrExporter emits a bare albedo.R/.G/.B layer (straight-encoded) when an
//      albedo buffer is supplied — the base colour the compositor multiplies.
//   2. OpenExrReader.ParseLayers reads arbitrary named float channels back (not just
//      R/G/B), so the saved lighting passes survive the round-trip.
//   3. ExrRelight recombines the read-back albedo + diffuse/specular/AO through the
//      SAME LightCompositor the live path uses — so an offline relight of a saved
//      EXR matches a direct composite of the same inputs.
// Values are chosen half-exact (0, .25, .5, .75, 1) so the half-precision EXR
// store round-trips bit-for-bit and the offline relight is byte-identical to the
// direct operator.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1AovExrRelightRoundTripTests
{
    private static string TempExr() =>
        Path.Combine(Path.GetTempPath(), $"ff-relight-{Guid.NewGuid():N}.exr");
    private static string TempPng() =>
        Path.Combine(Path.GetTempPath(), $"ff-relight-{Guid.NewGuid():N}.png");

    // Half-exact ramp so float→half→float is lossless.
    private static readonly float[] HalfExact = { 0f, 0.25f, 0.5f, 0.75f, 1f };

    private static (uint[] albedo, ShadingPipeline.ShadeComponents[] comps) BuildScene(int w, int h)
    {
        int n = w * h;
        var albedo = new uint[n];
        var comps = new ShadingPipeline.ShadeComponents[n];
        for (int i = 0; i < n; i++)
        {
            // Albedo bytes recover exactly from half (steps of 1/255 > half error here).
            byte ar = (byte)((i * 7) & 0xFF), ag = (byte)((i * 13) & 0xFF), ab = (byte)((i * 3) & 0xFF);
            albedo[i] = 0xFF000000u | ((uint)ar << 16) | ((uint)ag << 8) | ab;

            float d = HalfExact[i % HalfExact.Length];
            float s = HalfExact[(i + 1) % HalfExact.Length];
            float ao = HalfExact[(i + 2) % HalfExact.Length];
            comps[i] = new ShadingPipeline.ShadeComponents(d, d * 0.5f, d * 0.25f, s, s, s, ao, 0f);
        }
        return (albedo, comps);
    }

    // ── Part 2 (export albedo) ─────────────────────────────────────────
    [Fact]
    public void BuildChannels_Emits_Straight_Albedo_Layer()
    {
        // Albedo present → albedo.R/.G/.B, NOT sRGB-linearized (0xC0 → 0.7529, raw).
        var ch = AovExrExporter.BuildChannels(1, 1, new uint[] { 0xFF000000u },
            new Dictionary<AovView, uint[]>(), albedo: new uint[] { 0xFFC08040u });
        var names = ch.Select(c => c.Name).ToArray();
        Assert.Contains("albedo.R", names);
        Assert.Contains("albedo.G", names);
        Assert.Contains("albedo.B", names);
        Assert.Equal(0xC0 / 255f, ch.First(c => c.Name == "albedo.R").Data[0], 4);
        Assert.Equal(0x80 / 255f, ch.First(c => c.Name == "albedo.G").Data[0], 4);
        Assert.Equal(0x40 / 255f, ch.First(c => c.Name == "albedo.B").Data[0], 4);
    }

    [Fact]
    public void No_Albedo_Buffer_Emits_No_Albedo_Layer()
    {
        var ch = AovExrExporter.BuildChannels(1, 1, new uint[] { 0xFF000000u },
            new Dictionary<AovView, uint[]>());
        Assert.DoesNotContain(ch, c => c.Name.StartsWith("albedo"));
    }

    // ── Part 1 (multi-layer float reader) ──────────────────────────────
    [Fact]
    public void ParseLayers_Reads_Arbitrary_Named_Channels()
    {
        int w = 5, h = 4, n = w * h;
        var (albedo, comps) = BuildScene(w, h);
        string path = TempExr();
        try
        {
            AovExrExporter.Write(path, w, h, Fill(n, 0xFF202020u),
                new Dictionary<AovView, uint[]>(), null, null, comps,
                ExrCompression.None, albedo);

            using var fs = File.OpenRead(path);
            var img = OpenExrReader.ParseLayers(fs);
            Assert.NotNull(img);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
            // Custom AOV channels survived (RGB-only Parse would have dropped them).
            foreach (var name in new[] { "albedo.R", "diffuse.R", "specular.R", "AO.V" })
                Assert.NotNull(img.Plane(name));
            // Albedo recovers exactly; diffuse is half-exact.
            var aR = img.Plane("albedo.R")!;
            var dR = img.Plane("diffuse.R")!;
            for (int i = 0; i < n; i++)
            {
                // Albedo recovers to the exact byte (the contract ExrRelight relies on).
                Assert.Equal((albedo[i] >> 16) & 0xFF, (uint)Math.Clamp(aR[i] * 255f + 0.5f, 0, 255));
                Assert.Equal(comps[i].DiffR, dR[i], 5);   // diffuse is half-exact
            }
        }
        finally { File.Delete(path); }
    }

    // ── Part 3 (relight-from round-trip) ───────────────────────────────
    [Fact]
    public void ExrRelight_RoundTrip_Matches_Direct_Composite()
    {
        int w = 6, h = 5, n = w * h;
        var (albedo, comps) = BuildScene(w, h);
        string path = TempExr();
        try
        {
            AovExrExporter.Write(path, w, h, Fill(n, 0xFF000000u),
                new Dictionary<AovView, uint[]>(), null, null, comps,
                ExrCompression.None, albedo);

            var lp = new LightCompositeParams { DiffuseGain = 1.4, SpecularGain = 0.7, AoStrength = 1.0, Ambient = 0.1 };
            var offline = ExrRelight.Composite(path, lp, out int rw, out int rh);
            Assert.NotNull(offline);
            Assert.Equal(w, rw);
            Assert.Equal(h, rh);

            // The direct operator on the SAME half-exact inputs (albedo A forced opaque,
            // shadow unused) is what the offline path must reproduce.
            var direct = LightCompositor.Composite(albedo, comps, w, h, lp);
            Assert.Equal(direct, offline);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExrRelight_Rejects_BeautyOnly_Exr()
    {
        // A plain beauty EXR (no albedo/diffuse layers) is not relightable.
        int w = 3, h = 3;
        string path = TempExr();
        try
        {
            AovExrExporter.Write(path, w, h, Fill(w * h, 0xFF7F7F7Fu),
                new Dictionary<AovView, uint[]>());
            Assert.Null(ExrRelight.Composite(path, new LightCompositeParams(), out _, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExrRelight_RenderToFile_Writes_Png()
    {
        int w = 4, h = 4, n = w * h;
        var (albedo, comps) = BuildScene(w, h);
        string exr = TempExr(), png = TempPng();
        try
        {
            AovExrExporter.Write(exr, w, h, Fill(n, 0xFF000000u),
                new Dictionary<AovView, uint[]>(), null, null, comps, ExrCompression.None, albedo);
            Assert.True(ExrRelight.RenderToFile(exr, png, new LightCompositeParams()));
            Assert.True(File.Exists(png) && new FileInfo(png).Length > 0);
        }
        finally { File.Delete(exr); File.Delete(png); }
    }

    // ── Batch flag wiring ──────────────────────────────────────────────
    [Fact]
    public void Batch_RelightFrom_Selects_Relight_Mode()
    {
        var args = new[] { "--batch", "--relight-from", "in.exr", "--out", "out.png", "--relight-diffuse", "2" };
        Assert.True(BatchOptions.TryParse(args, 1, out var opts, out string? err), err);
        Assert.Equal(BatchMode.Relight, opts.Mode);
        Assert.Equal("in.exr", opts.RelightFromInput);
        Assert.Equal(2.0, opts.RelightDiffuse);
    }

    [Fact]
    public void Batch_RelightFrom_Requires_Out()
    {
        var args = new[] { "--batch", "--relight-from", "in.exr" };
        Assert.False(BatchOptions.TryParse(args, 1, out _, out string? err));
        Assert.Contains("--out", err);
    }

    [Fact]
    public void Batch_RelightFrom_Rejects_OutOfRange_Gain()
    {
        var args = new[] { "--batch", "--relight-from", "in.exr", "--out", "o.png", "--relight-diffuse", "99" };
        Assert.False(BatchOptions.TryParse(args, 1, out _, out string? err));
        Assert.Contains("--relight-diffuse", err);
    }

    private static uint[] Fill(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }
}
