// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S5 tail (3D-Rendering-Roadmap.md, #389 / #406): the refractive
// glass batch flags + their command-builder emit. The DielectricOps refraction
// + LightingFxData Transmission/Ior/Absorption* fields already shipped and are
// wired into ShadingPipeline; these tests lock the USER-FACING batch wiring —
// the flags parse (implying relief + raymarch, since transmission is a raymarch
// shade feature), range-check, apply onto fp.Lighting, and round-trip through the
// CLI builder. Opaque (no glass flag) stays omitted = byte-identical.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class GlassBatchWiringTests
    {
        private static string[] Tokenize(string cmd)
        {
            var list = new System.Collections.Generic.List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuote = false;
            foreach (char c in cmd)
            {
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == ' ' && !inQuote) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list.ToArray();
        }

        private static string[] BaseArgs(params string[] extra)
        {
            var head = new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" };
            var all = new string[head.Length + extra.Length];
            head.CopyTo(all, 0);
            extra.CopyTo(all, head.Length);
            return all;
        }

        [Fact]
        public void Transmission_Parses_And_Implies_Relief_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--transmission", "0.8"), 2, out var opts, out var err), err);
            Assert.Equal(0.8, opts.Transmission!.Value, 6);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Bare_Glass_Turns_On_Default_Transmission()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--glass"), 2, out var opts, out var err), err);
            Assert.Equal(0.9, opts.Transmission!.Value, 6);   // shorthand default
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Explicit_Transmission_Wins_Over_Bare_Glass()
        {
            // --glass sets 0.9 via ??=, then --transmission overrides.
            Assert.True(BatchOptions.TryParse(BaseArgs("--glass", "--transmission", "0.5"), 2, out var opts, out var err), err);
            Assert.Equal(0.5, opts.Transmission!.Value, 6);
        }

        [Fact]
        public void Ior_Absorption_And_Color_Parse()
        {
            Assert.True(BatchOptions.TryParse(
                BaseArgs("--transmission", "0.7", "--ior", "2.4", "--absorption-dist", "0.6", "--absorption-color", "#FF66CCFF"),
                2, out var opts, out var err), err);
            Assert.Equal(2.4, opts.Ior!.Value, 6);
            Assert.Equal(0.6, opts.AbsorptionDist!.Value, 6);
            Assert.Equal(0xFF66CCFFu, opts.AbsorptionColor!.Value);
        }

        [Fact]
        public void InternalMarch_Flag_Parses_And_Arms_Glass()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--glass-internal-march"), 2, out var opts, out var err), err);
            Assert.True(opts.GlassInternalMarch);
            Assert.Equal(0.9, opts.Transmission!.Value, 6);   // the march only bites with glass on
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Transmission_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--transmission", "1.5"), 2, out _, out var err));
            Assert.Contains("transmission", err);
        }

        [Fact]
        public void Ior_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--transmission", "0.5", "--ior", "0.5"), 2, out _, out var err));
            Assert.Contains("ior", err);
        }

        [Fact]
        public void AbsorptionDist_RejectsNonPositive()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--transmission", "0.5", "--absorption-dist", "0"), 2, out _, out var err));
            Assert.Contains("absorption-dist", err);
        }

        [Fact]
        public void Defaults_Leave_Glass_Fields_Null()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.Transmission);
            Assert.Null(opts.Ior);
            Assert.Null(opts.AbsorptionDist);
            Assert.Null(opts.AbsorptionColor);
            Assert.False(opts.GlassInternalMarch);
        }

        // NOTE: BatchRenderer.BuildFractalParameters (the opts→fp.Lighting apply)
        // lives in the WinExe assembly, not referenced by this test project — the
        // same boundary S6VideoReliefBatchTests documents. The apply is build-
        // verified; these tests lock the parse grammar + the CLI round-trip.

        [Fact]
        public void Builder_Omits_Glass_When_Opaque()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                Transmission = 0.0,
            });
            Assert.DoesNotContain("--transmission", cmd);
            Assert.DoesNotContain("--glass", cmd);
        }

        [Fact]
        public void Builder_Omits_Glass_When_Not_Raymarch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = false,
                Transmission = 0.8,
            });
            Assert.DoesNotContain("--transmission", cmd);
        }

        [Fact]
        public void Builder_RoundTrips_Glass()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                Transmission = 0.8, Ior = 2.4, AbsorptionDistance = 0.6,
                AbsorptionColor = 0xFF66CCFFu, GlassInternalMarch = true,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(0.8, opts.Transmission!.Value, 6);
            Assert.Equal(2.4, opts.Ior!.Value, 6);
            Assert.Equal(0.6, opts.AbsorptionDist!.Value, 6);
            Assert.Equal(0xFF66CCFFu, opts.AbsorptionColor!.Value);
            Assert.True(opts.GlassInternalMarch);
        }

        [Fact]
        public void Builder_Omits_Ior_And_Absorption_At_Defaults()
        {
            // Transmissive but every other glass knob at its default → only
            // --transmission is emitted (ior 1.5 / dist 1.0 / clear-white omitted).
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                Transmission = 0.6,   // Ior/AbsorptionDistance/AbsorptionColor default
            });
            Assert.Contains("--transmission", cmd);
            Assert.DoesNotContain("--ior", cmd);
            Assert.DoesNotContain("--absorption-dist", cmd);
            Assert.DoesNotContain("--absorption-color", cmd);
        }
    }
}
