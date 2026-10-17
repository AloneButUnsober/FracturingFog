// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DynamicResolution.cs
//
// #1333 — frame-time-driven resolution for the interactive progressive preview
// (games' dynamic resolution scaling). The Wave 2.5 ladder always started a drag
// at ¼ resolution: a fast card showed coarse blocks it didn't need, a slow card
// could still miss frame time at ¼ on a deep or relief view. Here the first stage
// is chosen from the measured cost of recent frames.
//
// Levels are half-octave steps of the linear divisor: level L renders at
// 1 / 2^(L/2) of the view's width and height (0 = full, 2 = ½, 4 = ¼, 6 = ⅛). The
// ladder after the first stage is unchanged in spirit: each later stage halves
// the divisor (L − 2) until the full-resolution final, so a drag that starts at ¼
// still goes ¼ → ½ → full. Level 0 means no preview: the final frame is cheap
// enough to render directly.
//
// Cost model. A preview stage costs a fixed part plus a part per preview pixel:
// the calculation and the preview relief scale with the preview, while drawing it
// to the screen (the #1272 lattice resample at screen size) and the upload don't.
// The host times the two parts separately. A final frame costs per pixel only
// (it adds MSAA, the post-frame tail and full-resolution relief, so it predicts a
// level-0 frame). Each is a smoothed average of recent frames; a per-pixel sample
// more than 3× away from its estimate replaces it (a deep-zoom tier or type
// change shouldn't take many frames to learn) and scales the other per-pixel
// estimate by the same factor, so a jump seen on a final frame also moves the
// next preview. Until a preview stage has been measured, a preview starts at ¼ (a
// final frame's per-pixel cost says nothing about the preview's fixed part), and
// only a final frame cheap enough to need no preview changes that.
//
// Choice: the finest level whose predicted time meets the target. Hysteresis: the
// level moves coarser only when the current one is predicted over 1.25 × target,
// and finer only to a level predicted at or under 0.75 × target. At a constant
// cost this cannot oscillate: the prediction falls with the level, so after a
// coarsening move the level is the finest that meets the target, the next finer
// one is over it and no refine fires; after a refining move the new level is
// under 0.75 × target, so no coarsening fires.
//
// Interaction only: the final frame is the same full-resolution render as before,
// so no output (screen, export or batch) changes.

using System;

namespace FracturingFog.Rendering
{
    public sealed class DynamicResolution
    {
        /// <summary>Coarsest level: ⅛ of the width and height.</summary>
        public const int MaxLevel = 6;
        /// <summary>The first stage before any frame was measured (and with the
        /// controller off): ¼, the pre-#1333 ladder.</summary>
        public const int DefaultLevel = 4;
        /// <summary>Move coarser when the current level is predicted over this × target.</summary>
        public const double CoarsenAbove = 1.25;
        /// <summary>Move finer only to a level predicted at or under this × target.</summary>
        public const double RefineAtOrBelow = 0.75;
        /// <summary>Weight of a new sample in the smoothed estimate.</summary>
        public const double Smoothing = 0.5;
        /// <summary>A sample this far (either way) from the estimate replaces it.</summary>
        public const double JumpRatio = 3.0;
        /// <summary>Smallest preview axis, as the progressive sidecars always had
        /// (keeps BLA / SA well-behaved in tiny windows).</summary>
        public const int MinPreviewAxis = 64;

        private readonly object _gate = new();
        private double _previewMsPerPx = double.NaN;
        private double _previewFixedMs;
        private double _finalMsPerPx = double.NaN;
        private int _level = DefaultLevel;
        private double _targetMs = 33.0;

        /// <summary>On (default): the first stage follows the measured cost. Off:
        /// always <see cref="DefaultLevel"/> (the pre-#1333 ladder).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Frame-time target for an interactive frame, ms (clamped 4 … 500).</summary>
        public double TargetMs
        {
            get { lock (_gate) return _targetMs; }
            set { lock (_gate) _targetMs = Math.Clamp(double.IsFinite(value) ? value : 33.0, 4.0, 500.0); }
        }

        /// <summary>The level the last <see cref="Choose"/> settled on.</summary>
        public int Level { get { lock (_gate) return _level; } }

        public double PreviewMsPerPixel { get { lock (_gate) return _previewMsPerPx; } }
        /// <summary>The smoothed fixed part of a preview stage (screen resample + upload), ms.</summary>
        public double PreviewFixedMs { get { lock (_gate) return _previewFixedMs; } }
        public double FinalMsPerPixel { get { lock (_gate) return _finalMsPerPx; } }

        /// <summary>Divisor of the width and height at <paramref name="level"/>.</summary>
        public static double Divisor(int level) => level switch
        {
            <= 0 => 1.0,
            1 => Math.Sqrt(2.0),
            2 => 2.0,
            3 => 2.0 * Math.Sqrt(2.0),
            4 => 4.0,
            5 => 4.0 * Math.Sqrt(2.0),
            _ => 8.0,
        };

        /// <summary>Preview size for a <paramref name="w"/>×<paramref name="h"/> view
        /// at <paramref name="level"/>. Whole-octave levels divide exactly as the
        /// pre-#1333 sidecars did (w / 4, w / 2); every axis is at least
        /// <see cref="MinPreviewAxis"/>.</summary>
        public static (int W, int H) PreviewDims(int w, int h, int level)
        {
            if (level <= 0) return (w, h);
            int pw, ph;
            if ((level & 1) == 0)
            {
                int d = 1 << (Math.Min(level, MaxLevel) / 2);
                pw = w / d; ph = h / d;
            }
            else
            {
                double d = Divisor(level);
                pw = (int)Math.Round(w / d); ph = (int)Math.Round(h / d);
            }
            return (Math.Max(MinPreviewAxis, pw), Math.Max(MinPreviewAxis, ph));
        }

        /// <summary>The stage after a preview at <paramref name="level"/>: half the
        /// divisor, or 0 (the full-resolution final).</summary>
        public static int NextLevel(int level) => level > 2 ? level - 2 : 0;

        /// <summary>Forget the measurements (a fractal type change): back to
        /// <see cref="DefaultLevel"/> until frames are measured again.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _previewMsPerPx = _finalMsPerPx = double.NaN;
                _previewFixedMs = 0;
                _level = DefaultLevel;
            }
        }

        /// <summary>Record a finished frame: <paramref name="pixels"/> rendered in
        /// <paramref name="ms"/>. <paramref name="final"/> = a full-resolution final
        /// frame, else a preview stage.</summary>
        public void Record(int pixels, double ms, bool final)
        {
            if (pixels <= 0 || !(ms >= 0) || !double.IsFinite(ms)) return;
            lock (_gate) RecordPerPixelLocked(ms / pixels, final);
        }

        /// <summary>Record a preview stage of <paramref name="pixels"/>: the part that
        /// scales with them (<paramref name="perPixelMs"/>: calculation, relief) and
        /// the part that doesn't (<paramref name="fixedMs"/>: drawing it to the
        /// screen, upload).</summary>
        public void RecordPreview(int pixels, double perPixelMs, double fixedMs)
        {
            if (pixels <= 0 || !(perPixelMs >= 0) || !double.IsFinite(perPixelMs)
                || !(fixedMs >= 0) || !double.IsFinite(fixedMs)) return;
            lock (_gate)
            {
                bool first = double.IsNaN(_previewMsPerPx);
                RecordPerPixelLocked(perPixelMs / pixels, final: false);
                _previewFixedMs = first ? fixedMs : _previewFixedMs + Smoothing * (fixedMs - _previewFixedMs);
            }
        }

        private void RecordPerPixelLocked(double c, bool final)
        {
            ref double est = ref final ? ref _finalMsPerPx : ref _previewMsPerPx;
            ref double other = ref final ? ref _previewMsPerPx : ref _finalMsPerPx;
            if (double.IsNaN(est) || !(est > 0)) est = c;
            else if (c > est * JumpRatio || c < est / JumpRatio)
            {
                // The view's cost jumped: the other kind of frame jumps with it,
                // so a jump seen on a final frame also moves the next preview.
                if (other > 0) other *= c / est;
                est = c;
            }
            else est += Smoothing * (c - est);
        }

        /// <summary>Predicted ms for the first stage at <paramref name="level"/>;
        /// NaN before anything was measured.</summary>
        public double Predict(int w, int h, int level)
        {
            lock (_gate) return PredictLocked(w, h, level);
        }

        private double PredictLocked(int w, int h, int level)
        {
            var (pw, ph) = PreviewDims(w, h, level);
            double px = (double)pw * ph;
            // A level-0 frame is a final frame (falling back on the preview's
            // per-pixel cost until a final was measured); a preview stage is a
            // preview, NaN until one was measured.
            if (level <= 0)
                return (double.IsNaN(_finalMsPerPx) ? _previewMsPerPx : _finalMsPerPx) * px;
            return _previewFixedMs + _previewMsPerPx * px;
        }

        /// <summary>The first stage for an interactive frame of a
        /// <paramref name="w"/>×<paramref name="h"/> view.</summary>
        public int Choose(int w, int h)
        {
            if (!Enabled) return DefaultLevel;
            lock (_gate)
            {
                if (double.IsNaN(PredictLocked(w, h, 0))) return _level;   // nothing measured yet
                double t = _targetMs;
                int cur = _level;
                if (cur < MaxLevel && PredictLocked(w, h, cur) > t * CoarsenAbove)
                {
                    if (double.IsNaN(_previewMsPerPx)) { _level = DefaultLevel; return _level; }   // no preview measured yet
                    int l = cur + 1;
                    while (l < MaxLevel && PredictLocked(w, h, l) > t) l++;
                    _level = l;
                }
                else
                {
                    for (int l = 0; l < cur; l++)
                        if (PredictLocked(w, h, l) <= t * RefineAtOrBelow) { _level = l; break; }
                }
                return _level;
            }
        }
    }
}
