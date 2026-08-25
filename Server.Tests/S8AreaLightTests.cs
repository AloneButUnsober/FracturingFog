// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S8 — area lights (3D-Rendering-Roadmap.md, #389 / #404). A light
// with a finite angular size (DirectionalLight.AreaAngularRadius > 0) softens the
// shadow penumbra: the IQ soft-shadow hardness is capped at cot(radius). These
// lock the contract — punctual (radius 0) is byte-identical (k unchanged), larger
// radius is monotonically softer, the HasAreaLight flag drives the GPU force-CPU
// gate + the SceneSignature repaint key, and the field survives a preset + batch
// round-trip.

using System;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8AreaLightTests
{
    // A sphere occluder DE: distance to the surface of a sphere. Positive outside,
    // so the IQ soft shadow reads its near-miss as a penumbra.
    private readonly struct SphereDe : IDistanceEstimator
    {
        private readonly double _cx, _cy, _cz, _r;
        public SphereDe(double cx, double cy, double cz, double r) { _cx = cx; _cy = cy; _cz = cz; _r = r; }
        public double Evaluate(double x, double y, double z)
        {
            double dx = x - _cx, dy = y - _cy, dz = z - _cz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) - _r;
        }
    }

    // ── EffectiveShadowK: the penumbra-hardness cap ──────────────────────────

    // Punctual (radius 0) returns the global k unchanged — the byte-identical
    // legacy contract. Exact equality, not approximate.
    [Fact]
    public void EffectiveShadowK_Punctual_Is_Exact_NoOp()
    {
        Assert.Equal(8.0, ShadingPipeline.EffectiveShadowK(8.0, 0.0));
        Assert.Equal(8.0, ShadingPipeline.EffectiveShadowK(8.0, -1.0));   // negative treated as 0
        Assert.Equal(0.0, ShadingPipeline.EffectiveShadowK(0.0, 0.0));    // k=0 (hard) stays hard
    }

    // A larger emitter → smaller effective k → softer shadow. Monotonically
    // decreasing, and always capped by (never exceeding) the global knob.
    [Fact]
    public void EffectiveShadowK_Larger_Radius_Is_Softer()
    {
        const double k = 64.0;   // a sharp global knob so the area cap dominates
        double k1 = ShadingPipeline.EffectiveShadowK(k, 1.0);
        double k5 = ShadingPipeline.EffectiveShadowK(k, 5.0);
        double k20 = ShadingPipeline.EffectiveShadowK(k, 20.0);
        Assert.True(k1 > k5 && k5 > k20, $"expected k1 {k1} > k5 {k5} > k20 {k20}");
        Assert.True(k1 <= k, "area cap never exceeds the global knob");
        // cot(5°) ≈ 11.43.
        Assert.Equal(1.0 / Math.Tan(5.0 * Math.PI / 180.0), k5, 6);
    }

    // A tiny emitter under a soft global knob keeps the global knob (the physical
    // cap is looser than the artist's choice).
    [Fact]
    public void EffectiveShadowK_Small_Radius_Keeps_Global()
    {
        // cot(0.1°) ≈ 573, far above k=8, so min picks the global 8.
        Assert.Equal(8.0, ShadingPipeline.EffectiveShadowK(8.0, 0.1), 9);
    }

    // A hemisphere-sized emitter is fully soft (k → 0).
    [Fact]
    public void EffectiveShadowK_Hemisphere_Is_Fully_Soft()
    {
        Assert.Equal(0.0, ShadingPipeline.EffectiveShadowK(50.0, 90.0));
        Assert.Equal(0.0, ShadingPipeline.EffectiveShadowK(50.0, 120.0));
    }

    // ── SoftShadow: an area light darkens/widens the penumbra ────────────────

    // A ray grazing an occluder toward the light: the softer (area) k lowers the
    // visibility in the penumbra band vs the sharp (punctual) k.
    [Fact]
    public void SoftShadow_Area_Widens_Penumbra()
    {
        // Surface at origin, light straight up. Occluder offset laterally so the
        // +Y ray passes just outside the sphere — a penumbra grazing, not a hit.
        var occ = new SphereDe(0.28, 2.0, 0.0, 0.25);
        double visSharp = ShadingPipeline.SoftShadow(occ, 0, 0, 0, 0, 1, 0,
            tMin: 0.01, tMax: 12.0, k: 64.0, maxSteps: 64);
        double visArea = ShadingPipeline.SoftShadow(occ, 0, 0, 0, 0, 1, 0,
            tMin: 0.01, tMax: 12.0, k: ShadingPipeline.EffectiveShadowK(64.0, 12.0), maxSteps: 64);
        Assert.True(visArea < visSharp,
            $"area penumbra {visArea} should be darker than sharp {visSharp}");
        Assert.InRange(visArea, 0.0, 1.0);
    }

    // ── HasAreaLight predicate ──────────────────────────────────────────────

    [Fact]
    public void HasAreaLight_Reflects_Active_Area_Emitter()
    {
        var fx = LightingFxData.CreateDefault();
        Assert.False(fx.HasAreaLight);   // all-punctual default

        fx.Light1.AreaAngularRadius = 6.0;
        Assert.True(fx.HasAreaLight);

        // An area radius on a zero-intensity light does not force the CPU path.
        var fx2 = LightingFxData.CreateDefault();
        fx2.Light2.AreaAngularRadius = 6.0;   // Light2 default intensity is 0
        Assert.False(fx2.HasAreaLight);
        fx2.Light2.Intensity = 0.5;
        Assert.True(fx2.HasAreaLight);
    }

    // ── SceneSignature: an area edit invalidates the render cache ────────────

    [Fact]
    public void SceneSignature_Changes_With_Area_Radius()
    {
        var a = LightingFxData.CreateDefault();
        var b = LightingFxData.CreateDefault();
        Assert.Equal(a.SceneSignature(), b.SceneSignature());   // identical baseline
        b.Light1.AreaAngularRadius = 4.0;
        Assert.NotEqual(a.SceneSignature(), b.SceneSignature());
    }

    // ── Preset round-trip ───────────────────────────────────────────────────

    [Fact]
    public void Preset_RoundTrips_Area_Radius()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light1.AreaAngularRadius = 7.5;
        fx.Light3.AreaAngularRadius = 12.25;

        var fx2 = LightingFxPresetData.FromFx(in fx).ToFx();

        Assert.Equal(7.5, fx2.Light1.AreaAngularRadius, 6);
        Assert.Equal(0.0, fx2.Light2.AreaAngularRadius, 6);
        Assert.Equal(12.25, fx2.Light3.AreaAngularRadius, 6);
    }

    // ── Batch parse + validation ────────────────────────────────────────────

    [Fact]
    public void Batch_Area_Flag_Parses_And_Forces_Relief()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--light1-area", "8.5", "--out", "out.png",
        };
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(8.5, opts.Lights[0].AreaAngularRadius!.Value, 6);
        // Any --lightN-* implies the relief raymarch path (area shadows are 3D).
        Assert.True(opts.Relief);
        Assert.True(opts.ReliefRaymarch);
    }

    [Fact]
    public void Batch_Area_OutOfRange_Rejected()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--light2-area", "120", "--out", "out.png",
        };
        Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
        Assert.Contains("light2-area", err);
    }

    // ── Builder emit round-trip ─────────────────────────────────────────────

    [Fact]
    public void Builder_Emits_Area_For_Positional_Light_RoundTrip()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light1.Type = LightType.Point;
        fx.Light1.Intensity = 1.5;
        fx.Light1.PosX = 0.3; fx.Light1.PosY = 1.1; fx.Light1.PosZ = -0.4;
        fx.Light1.AreaAngularRadius = 9.0;

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            Parameters = new FractalParameters { Lighting = fx },
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--light1-area", cmd);

        var argv = Tokenize(cmd);
        for (int i = 0; i < argv.Length; i++)
            if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(9.0, opts.Lights[0].AreaAngularRadius!.Value, 6);
    }

    // A punctual scene emits no --lightN-area (terse round-trip).
    [Fact]
    public void Builder_Omits_Area_When_Punctual()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light1.Type = LightType.Point;
        fx.Light1.Intensity = 1.5;
        fx.Light1.PosY = 2.0;
        // AreaAngularRadius left 0 (punctual).

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1,
            Parameters = new FractalParameters { Lighting = fx },
        };
        Assert.DoesNotContain("--light1-area", BatchCommandBuilder.Build(snap));
    }

    private static string[] Tokenize(string cmd)
    {
        var list = new System.Collections.Generic.List<string>();
        int i = 0;
        while (i < cmd.Length)
        {
            while (i < cmd.Length && char.IsWhiteSpace(cmd[i])) i++;
            if (i >= cmd.Length) break;
            if (cmd[i] == '"')
            {
                i++;
                int start = i;
                while (i < cmd.Length && cmd[i] != '"') i++;
                list.Add(cmd.Substring(start, i - start));
                i++;
            }
            else
            {
                int start = i;
                while (i < cmd.Length && !char.IsWhiteSpace(cmd[i])) i++;
                list.Add(cmd.Substring(start, i - start));
            }
        }
        return list.ToArray();
    }
}
