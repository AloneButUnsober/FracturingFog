// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #69 / #358 — "iter + final-z" combination colourings (P1: iter+real, iter+imag).
// Design: Docs/Technical/Coloring-IterFinalZ-DesignPlan.md
//
// Covers: registry presence, in-set sentinel, bounded-index fuzz (no NaN/Inf,
// opaque alpha), and determinism.  The ratio-pole and deep-zoom parity smoke
// tests belong to the P2 slice (#359).

using System;

using FracturingFog.Interefaces;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests
{
    public class IterFinalZThemeTests
    {
        private static IColorMap[] Themes() => new IColorMap[]
        {
            new IterPlusRealMap { MaxIterations = 512 },
            new IterPlusImagMap { MaxIterations = 512 },
        };

        [Fact]
        public void BothThemes_AreRegisteredInBuiltIns()
        {
            Assert.Contains(ColorPalette.BuiltIns, m => m is IterPlusRealMap);
            Assert.Contains(ColorPalette.BuiltIns, m => m is IterPlusImagMap);
        }

        [Fact]
        public void InSetSentinel_ReturnsInSetColor()
        {
            foreach (var m in Themes())
            {
                // finalZr == 0 && finalZi == 0 is the interior sentinel the
                // calculator writes for in-set pixels.
                int c = m.Map(0f, 0f, m.MaxIterations, 0f, 0f, 0f, 0f, 0f, 0f);
                Assert.Equal(unchecked((int)m.InSetColor), c);
            }
        }

        [Theory]
        [InlineData(2f, 0.0001f)]
        [InlineData(-2f, 5f)]
        [InlineData(1e6f, -1e6f)]
        [InlineData(-1e6f, 1e-6f)]
        [InlineData(0.0f, 3.5f)]     // finalZr == 0 but finalZi != 0 → escaped, not in-set
        public void BoundedIndex_ProducesOpaqueColorNoNaN(float zr, float zi)
        {
            foreach (var m in Themes())
            {
                int c = m.Map(120f, 0.01f, m.MaxIterations, 0f, 0f, zr, zi, 0f, 0f);
                // Alpha channel must be fully opaque (PackArgb guarantees 0xFF).
                uint a = (uint)c >> 24;
                Assert.Equal(0xFFu, a);
            }
        }

        [Fact]
        public void FuzzChannels_NeverThrowsAndStaysOpaque()
        {
            var rng = new Random(1234);
            foreach (var m in Themes())
            {
                for (int i = 0; i < 5000; i++)
                {
                    // Wide range incl. tiny imag values near the ratio pole.
                    float zr = (float)((rng.NextDouble() - 0.5) * 2e6);
                    float zi = (float)((rng.NextDouble() - 0.5) * 2e6);
                    if (rng.Next(10) == 0) zi = (float)((rng.NextDouble() - 0.5) * 1e-8);
                    float smooth = (float)(rng.NextDouble() * m.MaxIterations);

                    // A zero/zero draw is the in-set sentinel; skip so we only
                    // fuzz the escaped-pixel path here.
                    if (zr == 0f && zi == 0f) continue;

                    int c = m.Map(smooth, 0.01f, m.MaxIterations, 0f, 0f, zr, zi, 0f, 0f);
                    Assert.Equal(0xFFu, (uint)c >> 24);
                }
            }
        }

        [Fact]
        public void Deterministic_SameInputsSameOutput()
        {
            foreach (var m in Themes())
            {
                int a = m.Map(87.5f, 0.02f, m.MaxIterations, 0f, 0f, 3.14f, -2.7f, 0f, 0f);
                int b = m.Map(87.5f, 0.02f, m.MaxIterations, 0f, 0f, 3.14f, -2.7f, 0f, 0f);
                Assert.Equal(a, b);
            }
        }

        [Fact]
        public void RealAndImag_DifferAcrossAsymmetricChannels()
        {
            // iter+real should react to finalZr; iter+imag should react to finalZi.
            // Feed a pixel where the two channels differ and confirm the two
            // themes disagree — proves each reads its own channel.
            var real = new IterPlusRealMap { MaxIterations = 512 };
            var imag = new IterPlusImagMap { MaxIterations = 512 };
            int cr = real.Map(60f, 0.01f, 512, 0f, 0f, 4.0f, 0.05f, 0f, 0f);
            int ci = imag.Map(60f, 0.01f, 512, 0f, 0f, 4.0f, 0.05f, 0f, 0f);
            Assert.NotEqual(cr, ci);
        }
    }
}
