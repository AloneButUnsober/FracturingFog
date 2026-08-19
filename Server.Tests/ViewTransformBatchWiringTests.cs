// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 integration follow-up (3D-Rendering-Roadmap.md, #389 / #396):
// the --view-transform / --exposure batch flags + their command-builder emit.
// The S2 operator math already shipped (ViewTransformOps); these tests lock the
// USER-FACING wiring: the parser accepts the friendly names, range-checks
// exposure, and the CLI builder round-trips through the parser so a command
// reproduces the on-screen tonemap. Default (None / 0 EV) stays omitted =
// byte-identical.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class ViewTransformBatchWiringTests
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

        // A minimal valid argv the parser accepts (image mode needs coords + out).
        private static string[] BaseArgs(params string[] extra)
        {
            var head = new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" };
            var all = new string[head.Length + extra.Length];
            head.CopyTo(all, 0);
            extra.CopyTo(all, head.Length);
            return all;
        }

        [Theory]
        [InlineData("none", ViewTransform.None)]
        [InlineData("reinhard", ViewTransform.Reinhard)]
        [InlineData("aces", ViewTransform.AcesFilmic)]
        [InlineData("AcesFilmic", ViewTransform.AcesFilmic)]
        [InlineData("agx", ViewTransform.AgX)]
        [InlineData("AGX", ViewTransform.AgX)]
        [InlineData("filmic", ViewTransform.Filmic)]
        [InlineData("hable", ViewTransform.Filmic)]
        public void Parser_Accepts_Friendly_And_Enum_Names(string name, ViewTransform expected)
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--view-transform", name), 2, out var opts, out var err), err);
            Assert.Equal(expected, opts.ViewTransform);
        }

        [Fact]
        public void Parser_Tonemap_Alias_Works()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--tonemap", "agx"), 2, out var opts, out var err), err);
            Assert.Equal(ViewTransform.AgX, opts.ViewTransform);
        }

        [Fact]
        public void Parser_Rejects_Unknown_Transform()
        {
            Assert.False(BatchOptions.TryParse(BaseArgs("--view-transform", "kodak"), 2, out _, out var err));
            Assert.Contains("view-transform", err);
        }

        [Fact]
        public void Parser_Parses_Exposure_And_RangeChecks()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs("--exposure", "-2.5"), 2, out var opts, out var err), err);
            Assert.Equal(-2.5, opts.ViewExposureEv!.Value, 6);

            Assert.False(BatchOptions.TryParse(BaseArgs("--exposure", "99"), 2, out _, out var err2));
            Assert.Contains("exposure", err2);
        }

        [Fact]
        public void Parser_Defaults_Leave_Fields_Null()
        {
            Assert.True(BatchOptions.TryParse(BaseArgs(), 2, out var opts, out var err), err);
            Assert.Null(opts.ViewTransform);
            Assert.Null(opts.ViewExposureEv);
        }

        [Fact]
        public void Builder_Omits_Flags_At_Default()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                CenterX = 0, CenterY = 0, Zoom = 1,
                ViewTransform = ViewTransform.None,
                ViewExposureEv = 0.0,
            });
            Assert.DoesNotContain("--view-transform", cmd);
            Assert.DoesNotContain("--exposure", cmd);
        }

        [Fact]
        public void Builder_RoundTrips_Transform_And_Exposure()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ViewTransform = ViewTransform.AgX,
                ViewExposureEv = 1.5,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(ViewTransform.AgX, opts.ViewTransform);
            Assert.Equal(1.5, opts.ViewExposureEv!.Value, 6);
        }
    }
}
