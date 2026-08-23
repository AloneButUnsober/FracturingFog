// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S6 (#408) — per-light fog contribution mask (VolumeLightMask). A cleared
// bit drops that light from the fog in-scatter only; surfaces stay lit. Tests the
// default (all on = byte-identical), the batch --fog-light-mask flag, and the froxel
// BuildMedium honouring the mask.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class FogLightMaskTests
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
        public void Default_AllLightsFog()
        {
            Assert.Equal(0x7, LightingFxData.CreateDefault().VolumeLightMask);
        }

        [Fact]
        public void Mask_Parses()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--fog-light-mask", "5"), 2, out var opts, out var err), err);
            Assert.Equal(5, opts.FogLightMask);
        }

        [Fact]
        public void Mask_DefaultsNull()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.FogLightMask);
        }

        [Fact]
        public void Mask_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--fog-light-mask", "8"), 2, out _, out var err));
            Assert.Contains("fog-light-mask", err);
        }

        [Fact]
        public void Builder_OmitsMaskAtDefault()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1, FogLightMask = 0x7,
            });
            Assert.DoesNotContain("--fog-light-mask", cmd);
        }

        [Fact]
        public void Builder_RoundTripsMask()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
                FogLightMask = 0x5,   // Light1 + Light3, not Light2
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(0x5, opts.FogLightMask);
        }

        [Fact]
        public void BuildMedium_MaskDropsLightFromFogOnly()
        {
            var p = new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightScale = 1.4,
                Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 45, Relief2DCameraFovDeg = 55,
            };
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320.0 / 240, sy: 0.35, maxH: 1.0, p);
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.5;
            fx.Light1.Intensity = 1.0;
            fx.Light2.Intensity = 1.0;
            fx.Light3.Intensity = 1.0;
            fx.VolumeLightMask = 0x5;   // drop Light2 (bit 0x2) from the fog

            var m = FroxelCameraVolume.BuildMedium(in cam, in fx);
            Assert.NotNull(m.Lights);
            Assert.True(m.Lights![0].Intensity > 0, "Light1 still fogs");
            Assert.Equal(0.0, m.Lights[1].Intensity, 6);   // Light2 dropped from fog
            Assert.True(m.Lights[2].Intensity > 0, "Light3 still fogs");
        }
    }
}
