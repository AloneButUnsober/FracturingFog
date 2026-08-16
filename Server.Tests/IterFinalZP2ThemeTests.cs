// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #69 / #359 — "iter + final-z" combination colourings, P2 slice:
//   • IterPlusRatioMap        ("Iter + Real/Imag") — atan2 ratio channel
//   • IterRealImagRatioMap     ("Iter + Real + Imag + Ratio") — 4-way composite
//
// Design: Docs/Technical/Coloring-IterFinalZ-DesignPlan.md
//
// Covers the ratio pole (finalZi -> 0), bounded/deterministic output, and a
// deep-zoom parity smoke proving finalZ flows on BOTH the scalar and the
// HP/perturbation render paths (a path that zeroed finalZ would render a flat
// interior colour).

using System.Threading;

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests
{
    public class IterFinalZP2ThemeTests
    {
        private static IColorMap[] P2Themes() => new IColorMap[]
        {
            new IterPlusRatioMap { MaxIterations = 512 },
            new IterRealImagRatioMap { MaxIterations = 512 },
        };

        [Fact]
        public void BothP2Themes_AreRegisteredInBuiltIns()
        {
            Assert.Contains(ColorPalette.BuiltIns, m => m is IterPlusRatioMap);
            Assert.Contains(ColorPalette.BuiltIns, m => m is IterRealImagRatioMap);
        }

        [Theory]
        // finalZi exactly 0 (and near 0) with non-zero finalZr — the atan2 ratio
        // channel must yield a finite, opaque colour, never a divide-by-zero.
        [InlineData(4.0f, 0.0f)]
        [InlineData(-3.5f, 0.0f)]
        [InlineData(2.0f, 1e-30f)]
        [InlineData(-2.0f, -1e-30f)]
        public void RatioPole_ProducesFiniteOpaqueColor(float zr, float zi)
        {
            foreach (var m in P2Themes())
            {
                int c = m.Map(100f, 0.01f, m.MaxIterations, 0f, 0f, zr, zi, 0f, 0f);
                Assert.Equal(0xFFu, (uint)c >> 24);
            }
        }

        [Fact]
        public void InSetSentinel_ReturnsInSetColor()
        {
            foreach (var m in P2Themes())
            {
                int c = m.Map(0f, 0f, m.MaxIterations, 0f, 0f, 0f, 0f, 0f, 0f);
                Assert.Equal(unchecked((int)m.InSetColor), c);
            }
        }

        [Fact]
        public void FuzzChannels_NeverThrowsAndStaysOpaque()
        {
            var rng = new System.Random(9876);
            foreach (var m in P2Themes())
            {
                for (int i = 0; i < 5000; i++)
                {
                    float zr = (float)((rng.NextDouble() - 0.5) * 2e6);
                    float zi = (float)((rng.NextDouble() - 0.5) * 2e6);
                    if (rng.Next(8) == 0) zi = 0f;                 // force the pole
                    float smooth = (float)(rng.NextDouble() * m.MaxIterations);
                    if (zr == 0f && zi == 0f) continue;            // in-set sentinel
                    int c = m.Map(smooth, 0.01f, m.MaxIterations, 0f, 0f, zr, zi, 0f, 0f);
                    Assert.Equal(0xFFu, (uint)c >> 24);
                }
            }
        }

        [Fact]
        public void Deterministic_SameInputsSameOutput()
        {
            foreach (var m in P2Themes())
            {
                int a = m.Map(70f, 0.02f, m.MaxIterations, 0f, 0f, 1.5f, -0.0f, 0f, 0f);
                int b = m.Map(70f, 0.02f, m.MaxIterations, 0f, 0f, 1.5f, -0.0f, 0f, 0f);
                Assert.Equal(a, b);
            }
        }

        // Count escaped pixels (iter < maxIter) that carry a non-zero final z.
        // finalZ themes need this on every render path; a path that zeroed the
        // finalZ buffers would starve them and force a flat interior colour.
        private static int EscapedWithFinalZ(MandelbrotCalculator c)
        {
            int n = 0;
            for (int i = 0; i < c.IterationBuffer.Length; i++)
            {
                if (c.IterationBuffer[i] < c.MaxIterations &&
                    (c.FinalZrBuffer[i] != 0f || c.FinalZiBuffer[i] != 0f))
                    n++;
            }
            return n;
        }

        [Fact]
        public void DeepZoomParity_FinalZFlowsOnBothScalarAndHpPaths()
        {
            // Scalar (double-precision) path: shallow zoom over the boundary.
            var shallow = new MandelbrotCalculator(64, 64)
            {
                CenterX = -0.75,
                CenterY = 0.1,
                Zoom = 1.0,
                MaxIterations = 512,
                ColorMap = new IterRealImagRatioMap(),
                Quality = QualityPreset.Standard,
            };
            shallow.Calculate(CancellationToken.None);
            Assert.False(shallow.IsHighPrecisionActive);          // scalar path
            Assert.True(EscapedWithFinalZ(shallow) > 50,
                "scalar path did not populate finalZr/finalZi for escaped pixels");

            // HP / perturbation path: deep zoom (> 1e12) at the seahorse valley,
            // a location guaranteed to straddle the set boundary at depth.
            var deep = new MandelbrotCalculator(64, 64)
            {
                CenterX = -0.743643887037151,
                CenterY = 0.13182590420533,
                Zoom = 1e13,
                MaxIterations = 4096,
                ColorMap = new IterRealImagRatioMap(),
                Quality = QualityPreset.Standard,
            };
            deep.Calculate(CancellationToken.None);
            Assert.True(deep.IsHighPrecisionActive);               // HP path engaged
            Assert.True(EscapedWithFinalZ(deep) > 50,
                "HP / perturbation path did not populate finalZr/finalZi for escaped " +
                "pixels (FillAuxAndColorHP would be zeroing them)");
        }
    }
}
