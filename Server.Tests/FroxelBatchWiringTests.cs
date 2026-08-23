// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S6 render wiring (3D-Rendering-Roadmap.md, #389 / #408): the
// --relief-froxel batch flag + its command-builder emit. Locks the user-facing
// wiring — the flag parses (implying relief + raymarch), defaults off, and
// round-trips through the CLI builder.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class FroxelBatchWiringTests
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
        public void Froxel_Parses_And_Implies_Relief_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--relief-froxel"), 2, out var opts, out var err), err);
            Assert.True(opts.ReliefFroxel);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Froxel_DefaultsOff()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.False(opts.ReliefFroxel);
        }

        [Fact]
        public void Builder_OmitsFroxel_WhenOff()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefFroxel = false,
            });
            Assert.DoesNotContain("--relief-froxel", cmd);
        }

        [Fact]
        public void Builder_OmitsFroxel_WhenNotRaymarch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = false,
                ReliefFroxel = true,
            });
            Assert.DoesNotContain("--relief-froxel", cmd);
        }

        [Fact]
        public void Builder_RoundTripsFroxel()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefFroxel = true,
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.True(opts.ReliefFroxel);
        }
    }
}
