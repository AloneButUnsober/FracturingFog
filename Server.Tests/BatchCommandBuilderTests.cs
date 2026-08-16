// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #361 (slice of #64) — CLI Command Builder MVP tests. Asserts the emitted
// `--batch` string is well-formed and that flags pair correctly with values.
//
// NOTE: the real Batch/BatchOptions parser lives in the WinExe root project,
// which this cross-plat test assembly cannot reference. #362 moves the flag
// grammar into a shared table consumed by both parser and builder; a true
// parse-round-trip test becomes possible then. Until then this verifies the
// emitted string is self-consistent via a generic flag→value map.

using System.Globalization;
using FracturingFog;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class BatchCommandBuilderTests
    {
        // Split a command line the way a shell would, honouring double quotes.
        private static string[] Tokenize(string cmd)
        {
            var list = new System.Collections.Generic.List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
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

        [Fact]
        public void Build_LeadsWithExeAndBatch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.StartsWith("FracturingFog --batch", cmd);
        }

        [Fact]
        public void Build_OmitsDefaultThemeAndQuality()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "HSV",
                QualityName = "Standard",
            });
            Assert.DoesNotContain("--theme", cmd);
            Assert.DoesNotContain("--quality", cmd);
        }

        [Fact]
        public void Build_EmitsNonDefaultThemeAndQuality()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "Fire",
                QualityName = "Ultra",
            });
            Assert.Contains("--theme Fire", cmd);
            Assert.Contains("--quality Ultra", cmd);
        }

        [Fact]
        public void Build_QuotesThemeNameWithSpaces()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "Chromostereopsis Ember",
            });
            Assert.Contains("--theme \"Chromostereopsis Ember\"", cmd);
        }

        [Fact]
        public void Build_OmitsNeutralPostFx()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.DoesNotContain("--brightness", cmd);
            Assert.DoesNotContain("--contrast", cmd);
            Assert.DoesNotContain("--adaptive", cmd);
        }

        [Fact]
        public void Build_EmitsNonNeutralPostFx()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Brightness = 12,
                Contrast = -8,
                HistogramEq = 40,
            });
            Assert.Contains("--brightness 12", cmd);
            Assert.Contains("--contrast -8", cmd);
            Assert.Contains("--adaptive 40", cmd);
        }

        [Fact]
        public void Build_EmitsFractalSpecificParamsForMatchingType()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Flame,
                Parameters = new FractalParameters
                {
                    FlamePresetName = "Swirl Gasket",
                    FlameIterations = 5_000_000,
                    FlameGamma = 2.4,
                    FlameVibrancy = 0.7,
                },
            });
            Assert.Contains("--flame-preset \"Swirl Gasket\"", cmd);
            Assert.Contains("--flame-iter 5000000", cmd);
            Assert.Contains("--flame-gamma 2.4", cmd);
            Assert.Contains("--flame-vibrancy 0.7", cmd);
        }

        [Fact]
        public void Build_DoesNotEmitParamsForUnrelatedType()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                Parameters = new FractalParameters { FlameGamma = 9.9, BulbPower = 3.0 },
            });
            Assert.DoesNotContain("--flame", cmd);
            Assert.DoesNotContain("--bulb-power", cmd);
        }

        [Fact]
        public void Build_EmitsOutputPlaceholder()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.Contains("--out", cmd);
            Assert.Contains("<OUTPUT.png>", cmd);
        }

        // Parse the emitted string into a flag→value map (every token starting
        // with "--" is a flag; the following non-flag token is its value).
        private static System.Collections.Generic.Dictionary<string, string> FlagMap(string cmd)
        {
            var argv = Tokenize(cmd);
            var map = new System.Collections.Generic.Dictionary<string, string>();
            for (int i = 0; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--")) continue;
                string val = (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) ? argv[i + 1] : "";
                map[argv[i]] = val;
            }
            return map;
        }

        // Fidelity: every emitted flag pairs with its intended value, and the
        // round-trippable numeric formatting parses back to the exact input.
        [Fact]
        public void Build_FlagsPairWithExpectedValues()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.BurningShip,
                CenterX = -1.7549,
                CenterY = -0.0104,
                Zoom = 12345.5,
                Iterations = 2048,
                ThemeName = "Fire",
                QualityName = "High",
                Width = 3840,
                Height = 2160,
                Brightness = 5,
                Contrast = -3,
                HistogramEq = 25,
            };

            var m = FlagMap(BatchCommandBuilder.Build(snap));

            Assert.Equal("BurningShip", m["--fractal"]);
            Assert.Equal(snap.CenterX, double.Parse(m["--x"], CultureInfo.InvariantCulture), 12);
            Assert.Equal(snap.CenterY, double.Parse(m["--y"], CultureInfo.InvariantCulture), 12);
            Assert.Equal(snap.Zoom, double.Parse(m["--zoom"], CultureInfo.InvariantCulture), 6);
            Assert.Equal("2048", m["--iter"]);
            Assert.Equal("Fire", m["--theme"]);
            Assert.Equal("High", m["--quality"]);
            Assert.Equal("3840", m["--width"]);
            Assert.Equal("2160", m["--height"]);
            Assert.Equal("5", m["--brightness"]);
            Assert.Equal("-3", m["--contrast"]);
            Assert.Equal("25", m["--adaptive"]);
            Assert.True(m.ContainsKey("--out"));
        }
    }
}
