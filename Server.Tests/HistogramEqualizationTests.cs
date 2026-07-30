// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #145 — Adaptive histogram-equalization port from MandelbrotCalculator to the
// escape-time family (shared HistogramEqualizer core). These exercise the newly
// non-stub EscapeTimeCalculator HE path plus a regression guard that the
// Mandelbrot refactor to the shared core still equalizes.

using System.Threading;

using FracturingFog;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests
{
    public class HistogramEqualizationTests
    {
        private static EscapeTimeCalculator MakeJulia(int w = 96, int h = 96)
        {
            var calc = new EscapeTimeCalculator(w, h)
            {
                FractalType = FractalType.Julia,
                FractalParameters = new FractalParameters(), // classic c = (-0.7, 0.27015)
                CenterX = 0.0,
                CenterY = 0.0,
                Zoom = 0.6,
                MaxIterations = 256,
                ColorMap = new HsvPalette(),
                Quality = QualityPreset.Draft, // AaSamples = 1 → single-sample coloring
            };
            calc.Calculate(CancellationToken.None);
            return calc;
        }

        [Fact]
        public void EscapeTime_BuildCdf_ReportsMonotonicCdfWithEscapedPixels()
        {
            var calc = MakeJulia();

            Assert.True(calc.BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter));
            Assert.NotNull(cdf);
            Assert.True(bins > 0);
            Assert.Equal(calc.MaxIterations, sourceMaxIter);

            // CDF is non-decreasing and normalized to ~1.0 at the top bin.
            for (int i = 1; i < bins; i++)
                Assert.True(cdf![i] >= cdf[i - 1], $"CDF not monotonic at bin {i}");
            Assert.True(cdf![bins - 1] > 0.999, $"CDF top = {cdf[bins - 1]}");
        }

        [Fact]
        public void EscapeTime_HeStrength1_RemapsALargeFractionOfPixels()
        {
            var calc = MakeJulia();
            var plain = (uint[])calc.ColorBuffer.Clone();

            calc.ApplyHistogramEqualization(1.0);

            int changed = 0;
            for (int i = 0; i < plain.Length; i++)
                if (plain[i] != calc.ColorBuffer[i]) changed++;

            // Full equalization redistributes the escaped bands — expect a large
            // fraction of pixels to move, not a handful.
            Assert.True(changed > plain.Length / 20, $"only {changed}/{plain.Length} pixels changed");
        }

        [Fact]
        public void EscapeTime_HeStrength0_MatchesPlainColoring()
        {
            var calc = MakeJulia();
            var plain = (uint[])calc.ColorBuffer.Clone();

            // Strength 0 blends 0% toward the CDF rank → the plain linear mapping.
            calc.ApplyHistogramEqualization(0.0);

            int diff = 0;
            for (int i = 0; i < plain.Length; i++)
                if (plain[i] != calc.ColorBuffer[i]) diff++;

            // Allow a hair of divergence for pixels whose smooth value sits on the
            // 0.9999999 clamp boundary; the overwhelming majority must be identical.
            Assert.True(diff <= plain.Length / 100, $"{diff}/{plain.Length} pixels diverged at strength 0");
        }

        [Fact]
        public void EscapeTime_He_IsDeterministic()
        {
            var a = MakeJulia();
            var b = MakeJulia();
            a.ApplyHistogramEqualization(0.75);
            b.ApplyHistogramEqualization(0.75);
            Assert.Equal(a.ColorBuffer, b.ColorBuffer);
        }

        [Fact]
        public void EscapeTime_LockedCdf_ReportsSaturationWhenSourceMaxIterTooLow()
        {
            var calc = MakeJulia();
            Assert.True(calc.BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter));

            // Re-apply the CDF pretending it was built at a quarter of the real
            // iteration ceiling: every smooth value above that ceiling saturates
            // into the last bin, which the apply pass must count.
            calc.ApplyHistogramEqualizationWithCdf(
                cdf!, bins, sourceMaxIter / 4, strength: 1.0, ditherIterStrength: 0.0,
                out long escapedCount, out long saturatedCount);

            Assert.True(escapedCount > 0);
            Assert.True(saturatedCount > 0, "expected some pixels to saturate against the lowered ceiling");
            Assert.True(saturatedCount <= escapedCount);
        }

        [Fact]
        public void Mandelbrot_He_StillEqualizesAfterSharedCoreRefactor()
        {
            var calc = new MandelbrotCalculator(96, 96)
            {
                CenterX = -0.5,
                CenterY = 0.0,
                Zoom = 0.55,
                MaxIterations = 256,
                ColorMap = new HsvPalette(),
                Quality = QualityPreset.Draft,
            };
            calc.Calculate(CancellationToken.None);
            var plain = (uint[])calc.ColorBuffer.Clone();

            Assert.True(calc.BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter));
            calc.ApplyHistogramEqualizationWithCdf(cdf!, bins, sourceMaxIter, 1.0);

            int changed = 0;
            for (int i = 0; i < plain.Length; i++)
                if (plain[i] != calc.ColorBuffer[i]) changed++;
            Assert.True(changed > plain.Length / 20, $"Mandelbrot HE changed only {changed} pixels");
        }
    }
}
