// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// #251 / IDEA-6 — the Acid Warp auto-VJ ambient loop's timing state machine.
    /// Owns *when* to advance and *to which pattern*, decoupled from rendering:
    /// the shell ticks it on a wall-clock timer and, on an advance, drives the
    /// fade-to-black crossfade + pattern swap around it. Colour cycling runs
    /// independently (IDEA-1 palette rotation), so a locked loop keeps cycling —
    /// this director simply stops drawing the next pattern.
    ///
    /// <para>Patterns come draw-without-replacement from an
    /// <see cref="AcidWarpPlaylist"/> (classic-first optional, no back-to-back
    /// repeats). Controls: <see cref="Locked"/>, <see cref="Paused"/>, and a
    /// manual <see cref="RequestNext"/> that advances even while locked/paused.</para>
    /// </summary>
    public sealed class AcidWarpAmbientDirector
    {
        private readonly AcidWarpPlaylist _playlist;
        private int _holdMs;
        private int _elapsedMs;
        private bool _nextRequested;

        /// <param name="playlist">Pattern source (shuffled, no repeats).</param>
        /// <param name="holdMs">Per-pattern hold before auto-advance; floored at
        /// 100 ms so a runaway config can't spin the loop.</param>
        public AcidWarpAmbientDirector(AcidWarpPlaylist playlist, int holdMs)
        {
            _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
            _holdMs = Math.Max(100, holdMs);
            CurrentPattern = _playlist.Next();   // seed the first displayed pattern
        }

        /// <summary>Pattern index currently being displayed.</summary>
        public int CurrentPattern { get; private set; }

        /// <summary>When true, auto-advance is frozen (geometry held); colour
        /// cycling is external and keeps running. A <see cref="RequestNext"/>
        /// still advances.</summary>
        public bool Locked { get; set; }

        /// <summary>When true, the loop is paused: auto-advance is frozen and the
        /// hold clock does not accrue. A <see cref="RequestNext"/> still advances.</summary>
        public bool Paused { get; set; }

        /// <summary>Per-pattern hold in ms before auto-advance (floored at 100).</summary>
        public int HoldMs
        {
            get => _holdMs;
            set => _holdMs = Math.Max(100, value);
        }

        /// <summary>Milliseconds elapsed on the current pattern's hold. Exposed
        /// for the shell's progress affordance and for tests.</summary>
        public int ElapsedMs => _elapsedMs;

        /// <summary>Force an advance on the next <see cref="Tick"/>, regardless of
        /// <see cref="Locked"/> / <see cref="Paused"/> (the manual "next" control).</summary>
        public void RequestNext() => _nextRequested = true;

        /// <summary>
        /// Advance the hold clock by <paramref name="dtMs"/>. Returns true and
        /// moves <see cref="CurrentPattern"/> to the next playlist entry when an
        /// advance fires: immediately on a pending <see cref="RequestNext"/>, or
        /// automatically once the hold elapses (unless <see cref="Locked"/> or
        /// <see cref="Paused"/>). Returns false — pattern unchanged — otherwise.
        /// </summary>
        public bool Tick(int dtMs)
        {
            if (_nextRequested)
            {
                _nextRequested = false;
                Advance();
                return true;
            }
            if (Locked || Paused) return false;      // hold clock frozen
            if (dtMs > 0) _elapsedMs += dtMs;
            if (_elapsedMs < _holdMs) return false;
            Advance();
            return true;
        }

        private void Advance()
        {
            CurrentPattern = _playlist.Next();
            _elapsedMs = 0;
        }
    }
}
