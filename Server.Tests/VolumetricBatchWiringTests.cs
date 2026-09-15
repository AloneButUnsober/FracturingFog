// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #373 (slice of #363 / #64) — curated volumetric-lighting batch flags. The
// poster path already renders volumetric in-scatter from FractalParameters.
// Lighting (LightingFxData) for the 3D raymarchers + the relief raymarch; these
// flags just SET the look-defining knobs. Tests lock: each flag parses, the
// ranges validate, they apply onto fp.Lighting, and they round-trip through the
// CLI command builder. A scene with no fog (all defaults) emits nothing.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class VolumetricBatchWiringTests
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
        public void All_Flags_Parse()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(
                "--fog-density", "2.5",
                "--fog-height-falloff", "1.2",
                "--volume-steps", "32",
                "--volume-anisotropy", "-0.4",
                "--fog-color", "#66CCFF",
                "--volume-palette-strength", "0.7"), 2, out var o, out var err), err);

            Assert.Equal(2.5, o.FogDensity!.Value, 6);
            Assert.Equal(1.2, o.FogHeightFalloff!.Value, 6);
            Assert.Equal(32, o.VolumeSteps!.Value);
            Assert.Equal(-0.4, o.VolumeAnisotropy!.Value, 6);
            Assert.Equal(0xFF66CCFFu, o.FogColor!.Value);   // #RRGGBB → alpha forced FF
            Assert.Equal(0.7, o.VolumePaletteStrength!.Value, 6);
        }

        [Theory]
        [InlineData("--fog-density", "11")]
        [InlineData("--fog-height-falloff", "-1")]
        [InlineData("--volume-steps", "300")]
        [InlineData("--volume-anisotropy", "2")]
        [InlineData("--volume-anisotropy", "-2")]
        [InlineData("--volume-palette-strength", "1.5")]
        public void Out_Of_Range_Rejected(string flag, string val)
        {
            Assert.False(BatchOptions.TryParse(BaseArgs(flag, val), 2, out _, out var err));
            Assert.Contains(flag, err);
        }

        [Fact]
        public void Bad_Fog_Color_Rejected()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--fog-color", "teal"), 2, out _, out var err));
            Assert.Contains("--fog-color", err);
        }

        [Fact]
        public void Builder_RoundTrips_Volumetric()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbulb,
                CenterX = 0, CenterY = 0, Zoom = 1,
                FogDensity = 2.5,
                FogHeightFalloff = 1.2,
                VolumeSteps = 32,
                VolumeAnisotropy = -0.4,
                FogColor = 0xFF66CCFFu,
                VolumePaletteStrength = 0.7,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var o, out var err), err);
            Assert.Equal(2.5, o.FogDensity!.Value, 6);
            Assert.Equal(1.2, o.FogHeightFalloff!.Value, 6);
            Assert.Equal(32, o.VolumeSteps!.Value);
            Assert.Equal(-0.4, o.VolumeAnisotropy!.Value, 6);
            Assert.Equal(0xFF66CCFFu, o.FogColor!.Value);
            Assert.Equal(0.7, o.VolumePaletteStrength!.Value, 6);
        }

        [Fact]
        public void Defaults_Emit_No_Volumetric_Flags()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbulb, CenterX = 0, CenterY = 0, Zoom = 1,
            });
            Assert.DoesNotContain("--fog-density", cmd);
            Assert.DoesNotContain("--volume-steps", cmd);
            Assert.DoesNotContain("--volume-anisotropy", cmd);
            Assert.DoesNotContain("--fog-color", cmd);
            Assert.DoesNotContain("--volume-palette-strength", cmd);
            Assert.DoesNotContain("--fog-height-falloff", cmd);
        }
    }
}
