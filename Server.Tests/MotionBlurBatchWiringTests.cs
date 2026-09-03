// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, #389 / #398) — the user surface for
// vector motion blur: the --relief-motion-blur / --relief-motion-blur-samples batch
// flags + their command-builder emit. The MotionBlurFromVectors operator + the
// Relief2DMotionBlur* params + the render wiring already shipped (#640); these lock
// the batch grammar — parse (implying relief + raymarch), range-check, round-trip
// through the CLI builder, strength 0 stays omitted (byte-identical).

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class MotionBlurBatchWiringTests
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
        public void Strength_Parses_And_Implies_Relief_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--relief-motion-blur", "1.5"), 2, out var opts, out var err), err);
            Assert.Equal(1.5, opts.ReliefMotionBlur!.Value, 6);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Samples_Imply_Strength_On()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--relief-motion-blur-samples", "16"), 2, out var opts, out var err), err);
            Assert.Equal(16, opts.ReliefMotionBlurSamples!.Value);
            Assert.Equal(1.0, opts.ReliefMotionBlur!.Value, 6);   // samples turn the effect on
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Strength_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--relief-motion-blur", "5"), 2, out _, out var err));
            Assert.Contains("relief-motion-blur", err);
        }

        [Fact]
        public void Samples_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--relief-motion-blur-samples", "1"), 2, out _, out var err));
            Assert.Contains("relief-motion-blur-samples", err);
        }

        [Fact]
        public void Defaults_Leave_Fields_Null()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.ReliefMotionBlur);
            Assert.Null(opts.ReliefMotionBlurSamples);
        }

        [Fact]
        public void Builder_Omits_When_Off()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefMotionBlur = 0.0,
            });
            Assert.DoesNotContain("--relief-motion-blur", cmd);
        }

        [Fact]
        public void Builder_Omits_When_Not_Raymarch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = false,
                ReliefMotionBlur = 1.0,
            });
            Assert.DoesNotContain("--relief-motion-blur", cmd);
        }

        [Fact]
        public void Builder_Omits_Samples_At_Default()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefMotionBlur = 1.0,   // samples default 8
            });
            Assert.Contains("--relief-motion-blur", cmd);
            Assert.DoesNotContain("--relief-motion-blur-samples", cmd);
        }

        [Fact]
        public void Builder_RoundTrips_Strength_And_Samples()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefMotionBlur = 1.25, ReliefMotionBlurSamples = 16,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(1.25, opts.ReliefMotionBlur!.Value, 6);
            Assert.Equal(16, opts.ReliefMotionBlurSamples!.Value);
        }
    }
}
