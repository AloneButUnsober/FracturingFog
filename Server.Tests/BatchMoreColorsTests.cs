// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #55: the --more-colors batch flag is the CLI synonym of the "Slideshow: More
// Colors" context-menu item — it flips the slideshow cadence to Color Focus
// (8 themes per region instead of 3, BatchRenderer). The flag was added with
// the shared flag table work (#362) but had no regression test; this locks the
// flag, its British-spelling alias, and the default.

using FracturingFog.Batch;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class BatchMoreColorsTests
    {
        private static string[] Args(params string[] extra)
        {
            var head = new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" };
            var all = new string[head.Length + extra.Length];
            head.CopyTo(all, 0);
            extra.CopyTo(all, head.Length);
            return all;
        }

        [Theory]
        [InlineData("--more-colors")]
        [InlineData("--more-colours")]   // British-spelling alias
        public void Flag_Sets_MoreColors(string flag)
        {
            Assert.True(BatchOptions.TryParse(Args(flag), 2, out var opts, out var err), err);
            Assert.True(opts.MoreColors);
        }

        [Fact]
        public void Default_Is_RegionFocus()
        {
            Assert.True(BatchOptions.TryParse(Args(), 2, out var opts, out var err), err);
            Assert.False(opts.MoreColors);
        }
    }
}
