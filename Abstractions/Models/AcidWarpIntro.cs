// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Threading;

namespace FracturingFog.Models
{
    /// <summary>
    /// #250 — process-scoped gate for the classic Acid Warp intro. The first
    /// time the Acid Warp feature is started in a given app launch, the shell
    /// shows Noah Spurrier's original into-screen (the concentric-ring pattern
    /// at unit frequency, centred) before anything else. The gate fires ONCE per
    /// process: it is not persisted to disk and does not reset on repeated
    /// mode-entry — only a fresh launch shows the intro again.
    /// </summary>
    public static class AcidWarpIntro
    {
        // 0 = intro not yet shown this process; 1 = already shown.
        private static int _shown;

        /// <summary>The canonical Spurrier look: concentric-ring pattern
        /// (<see cref="FractalParameters.AcidWarpPattern"/> 0), unit frequency,
        /// centred.</summary>
        public const int ClassicPattern = 0;
        public const double ClassicFrequency = 1.0;

        /// <summary>Returns true exactly once per process — on the first call —
        /// and false on every subsequent call. Thread-safe.</summary>
        public static bool TryConsumeIntro()
            => Interlocked.Exchange(ref _shown, 1) == 0;

        /// <summary>Stamps the classic Spurrier configuration onto
        /// <paramref name="p"/> (pattern / frequency / centre).</summary>
        public static void ApplyClassic(FractalParameters p)
        {
            if (p == null) return;
            p.AcidWarpTitleCard = false;   // dissolve the wordmark
            p.AcidWarpPattern = ClassicPattern;
            p.AcidWarpFrequency = ClassicFrequency;
            p.AcidWarpCenterX = 0.0;
            p.AcidWarpCenterY = 0.0;
        }

        /// <summary>Test-only: re-arm the gate so a fresh launch can be
        /// simulated.</summary>
        public static void ResetForTests() => Interlocked.Exchange(ref _shown, 0);
    }
}
