// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// #251 / IDEA-6 — the Acid Warp auto-VJ playlist. Yields pattern indices in
    /// a draw-without-replacement order (every pattern once per cycle, no
    /// back-to-back repeats across the reshuffle boundary — via
    /// <see cref="ShuffleBag{T}"/>). When constructed with
    /// <c>startWithClassic</c>, the very first draw is Noah Spurrier's classic
    /// intro pattern (#250) before the shuffle begins.
    ///
    /// <para>This is the deterministic core of the ambient loop; the shell drives
    /// the hold + palette-cycle + fade-to-black crossfade timing around it.</para>
    /// </summary>
    public sealed class AcidWarpPlaylist
    {
        private readonly ShuffleBag<int> _bag;
        private readonly List<int> _items;
        private bool _pendingClassic;

        /// <param name="rng">Bounded RNG (<c>next(n)</c> → [0,n)); seed for a
        /// reproducible show.</param>
        /// <param name="patternCount">Number of patterns to rotate through
        /// (<see cref="FractalParameters.AcidWarpPatternCount"/>).</param>
        /// <param name="startWithClassic">When true, the first
        /// <see cref="Next"/> returns the classic intro pattern.</param>
        public AcidWarpPlaylist(Func<int, int> rng, int patternCount, bool startWithClassic)
        {
            if (patternCount < 1) patternCount = 1;
            _bag = new ShuffleBag<int>(rng ?? throw new ArgumentNullException(nameof(rng)));
            _items = new List<int>(patternCount);
            for (int i = 0; i < patternCount; i++) _items.Add(i);
            _pendingClassic = startWithClassic;
        }

        /// <summary>Next pattern index to display.</summary>
        public int Next()
        {
            if (_pendingClassic)
            {
                _pendingClassic = false;
                return AcidWarpIntro.ClassicPattern;
            }
            return _bag.Draw(_items);
        }
    }
}
