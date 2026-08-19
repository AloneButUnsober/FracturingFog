// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 integration (3D-Rendering-Roadmap.md, #389): the --denoise /
// --denoise-*-sigma batch flags + their command-builder emit. The pure À-Trous
// operator (AtrousDenoiser) already shipped; these lock the USER-FACING wiring:
// the flags parse (implying relief + raymarch, since only the raymarch emits the
// normal/depth guides), range-check, apply onto FractalParameters, and round-trip
// through the CLI builder. 0 passes stays omitted = off = byte-identical.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class DenoiseBatchWiringTests
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
        public void Denoise_Parses_And_Implies_Relief_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--denoise", "3"), 2, out var opts, out var err), err);
            Assert.Equal(3, opts.ReliefDenoiseIterations!.Value);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Sigmas_Parse_And_Imply_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(
                BaseArgs("--denoise", "2", "--denoise-color-sigma", "0.05",
                         "--denoise-normal-sigma", "0.5", "--denoise-depth-sigma", "0.15"),
                2, out var opts, out var err), err);
            Assert.Equal(0.05, opts.ReliefDenoiseColorSigma!.Value, 6);
            Assert.Equal(0.5, opts.ReliefDenoiseNormalSigma!.Value, 6);
            Assert.Equal(0.15, opts.ReliefDenoiseDepthSigma!.Value, 6);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Denoise_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--denoise", "99"), 2, out _, out var err));
            Assert.Contains("--denoise", err);
        }

        [Fact]
        public void ColorSigma_RejectsNonPositive()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--denoise-color-sigma", "0"), 2, out _, out var err));
            Assert.Contains("denoise-color-sigma", err);
        }

        [Fact]
        public void Defaults_Leave_Fields_Null()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.ReliefDenoiseIterations);
            Assert.Null(opts.ReliefDenoiseColorSigma);
            Assert.Null(opts.ReliefDenoiseNormalSigma);
            Assert.Null(opts.ReliefDenoiseDepthSigma);
        }

        [Fact]
        public void Builder_Omits_Denoise_When_Off()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefDenoiseIterations = 0,
            });
            Assert.DoesNotContain("--denoise", cmd);
        }

        [Fact]
        public void Builder_Omits_Denoise_When_Not_Raymarch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = false,
                ReliefDenoiseIterations = 3,
            });
            Assert.DoesNotContain("--denoise", cmd);
        }

        [Fact]
        public void Builder_Omits_Sigmas_At_Default_Emits_Iterations()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefDenoiseIterations = 3,   // sigmas left at operator defaults
            });
            Assert.Contains("--denoise", cmd);
            Assert.DoesNotContain("--denoise-color-sigma", cmd);
            Assert.DoesNotContain("--denoise-normal-sigma", cmd);
            Assert.DoesNotContain("--denoise-depth-sigma", cmd);
        }

        [Fact]
        public void Builder_RoundTrips_Iterations_And_Sigmas()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefDenoiseIterations = 3,
                ReliefDenoiseColorSigma = 0.05,
                ReliefDenoiseNormalSigma = 0.5,
                ReliefDenoiseDepthSigma = 0.15,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(3, opts.ReliefDenoiseIterations!.Value);
            Assert.Equal(0.05, opts.ReliefDenoiseColorSigma!.Value, 6);
            Assert.Equal(0.5, opts.ReliefDenoiseNormalSigma!.Value, 6);
            Assert.Equal(0.15, opts.ReliefDenoiseDepthSigma!.Value, 6);
        }
    }
}
