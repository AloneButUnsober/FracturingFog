// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S6 (#408) — froxel temporal reprojection. The froxel volume's per-cell
// scatter + extinction is exponentially blended with the previous frame's before
// integration so animated fog reads as a stable volume. Tests the blend math
// (FroxelHistory), the grid-key invalidation, and that the single-frame path stays
// byte-identical (null history / feedback 0).

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class FroxelTemporalTests
    {
        // ── FroxelHistory blend math ────────────────────────────────────────────
        [Fact]
        public void FirstFrame_PassesThrough_NoHistory()
        {
            var hist = new FroxelHistory();
            var scR = new[] { 1.0, 1.0 }; var scG = new[] { 1.0, 1.0 };
            var scB = new[] { 1.0, 1.0 }; var ext = new[] { 2.0, 2.0 };
            hist.BlendAndStore(scR, scG, scB, ext, 2, key: 42, feedback: 0.9);
            // No prior history → a=0 → unchanged.
            Assert.Equal(1.0, scR[0], 12);
            Assert.Equal(2.0, ext[1], 12);
        }

        [Fact]
        public void SecondFrame_ExponentialBlend()
        {
            var hist = new FroxelHistory();
            // Frame 1 seeds history = 1.
            var a = new[] { 1.0 }; var g1 = new[] { 1.0 }; var b1 = new[] { 1.0 }; var e1 = new[] { 1.0 };
            hist.BlendAndStore(a, g1, b1, e1, 1, key: 7, feedback: 0.5);
            // Frame 2: current 3, feedback 0.5 → 3*0.5 + 1*0.5 = 2.
            var c = new[] { 3.0 }; var g2 = new[] { 3.0 }; var b2 = new[] { 3.0 }; var e2 = new[] { 3.0 };
            hist.BlendAndStore(c, g2, b2, e2, 1, key: 7, feedback: 0.5);
            Assert.Equal(2.0, c[0], 12);
            Assert.Equal(2.0, e2[0], 12);
            // Frame 3: current 3 again → 3*0.5 + 2*0.5 = 2.5 (converging toward 3).
            var d = new[] { 3.0 }; var g3 = new[] { 3.0 }; var b3 = new[] { 3.0 }; var e3 = new[] { 3.0 };
            hist.BlendAndStore(d, g3, b3, e3, 1, key: 7, feedback: 0.5);
            Assert.Equal(2.5, d[0], 12);
        }

        [Fact]
        public void GridKeyMismatch_Resets()
        {
            var hist = new FroxelHistory();
            var a = new[] { 1.0 }; var g = new[] { 1.0 }; var b = new[] { 1.0 }; var e = new[] { 1.0 };
            hist.BlendAndStore(a, g, b, e, 1, key: 1, feedback: 0.9);
            // A different grid key (camera moved → near/far changed) invalidates history.
            var c = new[] { 3.0 }; var g2 = new[] { 3.0 }; var b2 = new[] { 3.0 }; var e2 = new[] { 3.0 };
            hist.BlendAndStore(c, g2, b2, e2, 1, key: 2, feedback: 0.9);
            Assert.Equal(3.0, c[0], 12);   // no blend — re-seeded
        }

        [Fact]
        public void FeedbackZero_NoBlend()
        {
            var hist = new FroxelHistory();
            var a = new[] { 1.0 }; var g = new[] { 1.0 }; var b = new[] { 1.0 }; var e = new[] { 1.0 };
            hist.BlendAndStore(a, g, b, e, 1, key: 5, feedback: 0.9);
            var c = new[] { 3.0 }; var g2 = new[] { 3.0 }; var b2 = new[] { 3.0 }; var e2 = new[] { 3.0 };
            hist.BlendAndStore(c, g2, b2, e2, 1, key: 5, feedback: 0.0);
            Assert.Equal(3.0, c[0], 12);   // feedback 0 → current only
        }

        [Fact]
        public void Reset_DropsHistory()
        {
            var hist = new FroxelHistory();
            var a = new[] { 1.0 }; var g = new[] { 1.0 }; var b = new[] { 1.0 }; var e = new[] { 1.0 };
            hist.BlendAndStore(a, g, b, e, 1, key: 9, feedback: 0.9);
            hist.Reset();
            var c = new[] { 3.0 }; var g2 = new[] { 3.0 }; var b2 = new[] { 3.0 }; var e2 = new[] { 3.0 };
            hist.BlendAndStore(c, g2, b2, e2, 1, key: 9, feedback: 0.9);
            Assert.Equal(3.0, c[0], 12);   // history dropped → re-seeded
        }

        [Fact]
        public void GridKey_MatchesForSameGrid_DiffersForMovedCamera()
        {
            var g1 = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            var g2 = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            var g3 = new FroxelGrid(24, 24, 48, 1.2, 5.0);   // near changed (camera moved)
            Assert.Equal(FroxelHistory.GridKey(g1), FroxelHistory.GridKey(g2));
            Assert.NotEqual(FroxelHistory.GridKey(g1), FroxelHistory.GridKey(g3));
        }

        // ── FroxelVolumePass single-frame byte-identity ─────────────────────────
        private static FroxelMedium Medium(double baseDensity = 0.6) => new()
        {
            BaseDensity = baseDensity, Extinction = 1.0, Anisotropy = 0.3,
            NoiseAmount = 0.2, NoiseScale = 0.5, NoiseOctaves = 3,
            ViewDx = 0, ViewDy = 0, ViewDz = 1, WorldExtent = 1.0,
            Lights = new[]
            {
                new FroxelLight { Type = 0, Color = 0xFFFFFFFFu, Intensity = 1.0, Lx = 0, Ly = 1, Lz = 0 },
            },
        };

        [Fact]
        public void Populate_NullHistory_ByteIdenticalToSingleFrame()
        {
            var grid = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            var m = Medium();
            const int w = 40, h = 30;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF808080u; depth[i] = 2.0f; }

            var passA = new FroxelVolumePass(grid);
            passA.Populate(m);
            var outA = passA.CompositeWorldDepth(beauty, depth, w, h);

            var passB = new FroxelVolumePass(grid);
            passB.Populate(m, null, 0.0, 0L);        // temporal overload, no history
            var outB = passB.CompositeWorldDepth(beauty, depth, w, h);

            Assert.Equal(outA, outB);   // byte-identical
        }

        [Fact]
        public void Populate_FirstTemporalFrame_MatchesSingleFrame()
        {
            var grid = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            var m = Medium();
            const int w = 40, h = 30;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF404060u; depth[i] = 2.5f; }

            var single = new FroxelVolumePass(grid);
            single.Populate(m);
            var outSingle = single.CompositeWorldDepth(beauty, depth, w, h);

            var hist = new FroxelHistory();
            var temporal = new FroxelVolumePass(grid);
            temporal.Populate(m, hist, 0.9, FroxelHistory.GridKey(grid));   // first frame → no history
            var outTemporal = temporal.CompositeWorldDepth(beauty, depth, w, h);

            Assert.Equal(outSingle, outTemporal);
        }

        [Fact]
        public void Temporal_SmoothsAnimatedFog()
        {
            // Frame 1 dense, frame 2 thin: the temporal frame-2 output must sit BETWEEN
            // the two single-frame outputs (history pulls it back toward frame 1),
            // proving the blend actually smooths animation.
            var grid = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            long key = FroxelHistory.GridKey(grid);
            const int w = 8, h = 8;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF202020u; depth[i] = 4.0f; }

            var mDense = Medium();
            var mThin = Medium(0.05);   // frame 2 much thinner fog

            // Single-frame frame-2 (thin) reference.
            var singleThin = new FroxelVolumePass(grid);
            singleThin.Populate(mThin);
            var outThin = singleThin.CompositeWorldDepth(beauty, depth, w, h);

            // Temporal: frame 1 dense seeds history, frame 2 thin blends with it.
            var hist = new FroxelHistory();
            var t1 = new FroxelVolumePass(grid);
            t1.Populate(mDense, hist, 0.8, key);
            _ = t1.CompositeWorldDepth(beauty, depth, w, h);
            var t2 = new FroxelVolumePass(grid);
            t2.Populate(mThin, hist, 0.8, key);
            var outTemporal2 = t2.CompositeWorldDepth(beauty, depth, w, h);

            // The dense single frame (frame 1) as the "brighter" bound.
            var singleDense = new FroxelVolumePass(grid);
            singleDense.Populate(mDense);
            var outDense = singleDense.CompositeWorldDepth(beauty, depth, w, h);

            // Pick a lit pixel and check the temporal green channel is between thin and
            // dense (history holds more scatter than the thin frame alone).
            int idx = (h / 2) * w + (w / 2);
            int gThin = (int)((outThin[idx] >> 8) & 0xFF);
            int gDense = (int)((outDense[idx] >> 8) & 0xFF);
            int gTemporal = (int)((outTemporal2[idx] >> 8) & 0xFF);
            Assert.True(gDense > gThin, "dense fog should scatter more than thin");
            Assert.True(gTemporal > gThin, "temporal frame-2 should retain some of the denser history");
            Assert.True(gTemporal <= gDense + 1, "temporal frame-2 should not exceed the dense frame");
        }

        [Fact]
        public void Determinism_SameInputsSameOutput()
        {
            var grid = new FroxelGrid(24, 24, 48, 1.0, 5.0);
            long key = FroxelHistory.GridKey(grid);
            var m = Medium();
            const int w = 16, h = 12;
            var beauty = new uint[w * h];
            var depth = new float[w * h];
            for (int i = 0; i < w * h; i++) { beauty[i] = 0xFF506070u; depth[i] = 3.0f; }

            uint[] Run()
            {
                var hist = new FroxelHistory();
                var p1 = new FroxelVolumePass(grid); p1.Populate(m, hist, 0.75, key);
                _ = p1.CompositeWorldDepth(beauty, depth, w, h);
                var p2 = new FroxelVolumePass(grid); p2.Populate(m, hist, 0.75, key);
                return p2.CompositeWorldDepth(beauty, depth, w, h);
            }
            Assert.Equal(Run(), Run());
        }
    }
}
