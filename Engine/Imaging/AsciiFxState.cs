// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiFxState.cs
//
// Mutable per-producer state for the stateful ASCII FX (#229) — effects that
// evolve across frames rather than being a pure function of one grid + clock:
// Matrix rain (falling drop positions), frame trails (previous frame), and the
// reveal transitions. The live pump and the animation recorder each own ONE of
// these and pass it to AsciiFxChain.Apply every frame; stateless effects ignore
// it. Not thread-safe — one owner per state.

using System;

namespace FracturingFog.Imaging
{
    /// <summary>Evolving state for the stateful <see cref="AsciiFxChain"/> effects.
    /// Create one per frame producer (live view / recorder) and reuse it across
    /// frames. Reset when the grid size changes or the animation restarts.</summary>
    public sealed class AsciiFxState
    {
        // Deterministic RNG so a recorded animation is reproducible frame-for-frame.
        private readonly Random _rng;
        private double _lastTime = double.NaN;

        /// <summary>Grid the state is currently sized for.</summary>
        public int Cols { get; private set; }
        public int Rows { get; private set; }

        // ── Matrix rain ───────────────────────────────────────────────────
        internal double[] RainHead = Array.Empty<double>();  // head row per column
        internal double[] RainSpeed = Array.Empty<double>(); // rows/sec per column
        internal int[] RainLen = Array.Empty<int>();          // trail length per column
        internal bool[] RainActive = Array.Empty<bool>();     // column carries a drop
        internal double[] Luma = Array.Empty<double>();       // scratch mask (cols*rows)
        private bool _rainInit;

        // ── Particles ─────────────────────────────────────────────────────
        internal double[] PartX = Array.Empty<double>();
        internal double[] PartY = Array.Empty<double>();
        internal double[] PartSway = Array.Empty<double>();   // per-particle sway phase
        private bool _partInit;

        public AsciiFxState(int seed = 0x5CA1E)
        {
            _rng = new Random(seed);
        }

        /// <summary>Reset all evolving state — call when the animation restarts so
        /// the next frame begins clean.</summary>
        public void Reset()
        {
            _lastTime = double.NaN;
            _rainInit = false;
            _partInit = false;
        }

        /// <summary>Ensure buffers match <paramref name="cols"/>×<paramref name="rows"/>;
        /// a size change reinitialises everything.</summary>
        internal void EnsureSize(int cols, int rows)
        {
            if (cols == Cols && rows == Rows && Luma.Length == cols * rows) return;
            Cols = cols; Rows = rows;
            Luma = new double[cols * rows];
            RainHead = new double[cols];
            RainSpeed = new double[cols];
            RainLen = new int[cols];
            RainActive = new bool[cols];
            _rainInit = false;
            _partInit = false;
        }

        internal bool ParticlesInitialised => _partInit;

        // Seed `count` particles at random positions with random sway phase.
        internal void InitParticles(int count)
        {
            PartX = new double[count];
            PartY = new double[count];
            PartSway = new double[count];
            for (int i = 0; i < count; i++)
            {
                PartX[i] = _rng.NextDouble() * Cols;
                PartY[i] = _rng.NextDouble() * Rows;
                PartSway[i] = _rng.NextDouble() * Math.PI * 2.0;
            }
            _partInit = true;
        }

        /// <summary>Advance the clock, returning the delta since the last frame
        /// clamped to a sane range (a seek / first frame yields one ~30fps step,
        /// never a huge jump that teleports the animation).</summary>
        internal double AdvanceClock(double timeSeconds)
        {
            double dt;
            if (double.IsNaN(_lastTime)) dt = 1.0 / 30.0;
            else dt = timeSeconds - _lastTime;
            if (dt <= 0 || dt > 0.5) dt = 1.0 / 30.0;
            _lastTime = timeSeconds;
            return dt;
        }

        internal Random Rng => _rng;

        internal void InitRain(double density)
        {
            for (int x = 0; x < Cols; x++)
            {
                RainActive[x] = _rng.NextDouble() < density;
                RespawnRainColumn(x, aboveOnly: false);
            }
            _rainInit = true;
        }

        internal bool RainInitialised => _rainInit;

        // Give a column a fresh drop: start above the top (so it falls in), random
        // speed and trail length. aboveOnly starts strictly off-screen for respawns.
        internal void RespawnRainColumn(int x, bool aboveOnly)
        {
            RainLen[x] = Math.Max(2, (int)(Rows * (0.3 + 0.4 * _rng.NextDouble())));
            RainSpeed[x] = 0.6 + 0.9 * _rng.NextDouble(); // multiplied by base speed
            // Respawn re-enters from just above the top; the initial fill is
            // staggered across the whole column so the first frame already rains.
            RainHead[x] = aboveOnly
                ? -RainLen[x] - _rng.NextDouble() * Rows * 0.5
                : _rng.NextDouble() * (Rows + RainLen[x]) - RainLen[x];
        }
    }
}
