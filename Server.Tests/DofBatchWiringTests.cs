// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S3 integration follow-up (3D-Rendering-Roadmap.md, #389 / #400):
// the --dof-aperture / --dof-focus batch flags + their command-builder emit.
// The thin-lens CameraDof math already shipped and is wired into
// HeightfieldRaymarch2D; these tests lock the USER-FACING wiring: the flags parse
// (implying relief + raymarch, since DOF is perspective-only), range-check, and
// round-trip through the CLI builder. Aperture 0 stays omitted = pinhole =
// byte-identical.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class DofBatchWiringTests
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
        public void Aperture_Parses_And_Implies_Relief_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--dof-aperture", "0.25"), 2, out var opts, out var err), err);
            Assert.Equal(0.25, opts.ReliefDofAperture!.Value, 6);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Focus_Parses_And_Implies_Raymarch()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--dof-focus", "3.5"), 2, out var opts, out var err), err);
            Assert.Equal(3.5, opts.ReliefDofFocus!.Value, 6);
            Assert.True(opts.ReliefRaymarch);
        }

        [Fact]
        public void Aperture_RangeChecked()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--dof-aperture", "2"), 2, out _, out var err));
            Assert.Contains("dof-aperture", err);
        }

        [Fact]
        public void Focus_RejectsNegative()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--dof-focus", "-1"), 2, out _, out var err));
            Assert.Contains("dof-focus", err);
        }

        [Fact]
        public void Defaults_Leave_Fields_Null()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.ReliefDofAperture);
            Assert.Null(opts.ReliefDofFocus);
        }

        [Fact]
        public void Builder_Omits_Dof_At_Pinhole()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefDofAperture = 0.0, ReliefDofFocus = 0.0,
            });
            Assert.DoesNotContain("--dof-aperture", cmd);
            Assert.DoesNotContain("--dof-focus", cmd);
        }

        [Fact]
        public void Builder_Omits_Dof_When_Not_Raymarch()
        {
            // DOF is a raymarch-only knob; an emboss-relief snapshot never emits it
            // even if an aperture leaked in.
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = false,
                ReliefDofAperture = 0.3,
            });
            Assert.DoesNotContain("--dof-aperture", cmd);
        }

        [Fact]
        public void Builder_RoundTrips_Aperture_And_Focus()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true, ReliefRaymarch = true,
                ReliefDofAperture = 0.2, ReliefDofFocus = 4.0,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(0.2, opts.ReliefDofAperture!.Value, 6);
            Assert.Equal(4.0, opts.ReliefDofFocus!.Value, 6);
        }
    }
}
