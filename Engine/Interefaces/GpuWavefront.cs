// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuWavefront.cs
//
// #1340 — the "wavefront" (persistent-thread) schedule of the GPU iteration
// kernels (MandelbrotKernelSource: the shallow escape-time HlslEntry and the
// deep-zoom CSPerturb / CSPerturbSA; D3D11 + Vulkan).
//
// One thread per pixel, a warp / wave runs until its slowest pixel finishes: a
// warp mixing 50-step and 50 000-step pixels idles most of its lanes. Under the
// wavefront schedule a fixed number of threads pull pixels from a global atomic
// counter (in 8×8-tile order, so a warp's lanes stay neighbours) and iterate them
// 32 steps at a time; a lane whose pixel finished takes the next one at the next
// chunk, so lanes stay busy while pixels are left. No per-pixel state leaves the
// registers, and both schedules are one compiled shader (a cbuffer flag), so each
// pixel runs the same instructions and every output is bit-identical.
//
// Shallow kernel: on by default. On a GT 710 at 1080p it is never slower (whole
// set at 512 iterations 1.1×) and 1.2–3.4× faster on views mixing fast and slow
// pixels (seahorse valley 1.5–1.7×, elephant valley 1.6× / 3.4× with the
// periodicity check, minibrot 1.9×, Phoenix 1.8×).
//
// Deep kernels: a refill there re-runs the per-pixel set-up (the SA skip search),
// lane by lane, so a frame whose warps are already balanced runs up to ~15%
// slower, while a boundary-heavy one gains (the "Marina" preset 1.7×). The gain
// is bounded by the warp waste of the frame — Σ over 8×8 tiles of 64 · max
// iteration / Σ iterations (WarpWaste) — which was ≤ 1.17 on every view that lost
// and 1.94 on Marina. So by default (Auto) the calculator measures it on each GPU
// deep frame and runs the next one in the wavefront schedule when it is at least
// PerturbWasteThreshold; the first frame of a session runs one thread per pixel.
//
// #1386 — a first frame can be seeded from a lower-resolution frame of the same
// view: the ¼ / ½ drag previews of the progressive ladder (the host hands their
// estimate to the full-resolution calculator, free), or, opt-in
// (FF_GPU_WAVEFRONT_SEED=1), a ¼-resolution seed dispatch the calculator runs
// itself when it has no hint for a nearby view (first frame, a jump, a batch
// still). A low-resolution frame's own warp waste overstates the full frame's
// (each 8×8 tile covers 4–64× the area: Deep Mini 1.14 full, 1.41 at ¼), and
// measured with tiles of the full frame's footprint (8/k) it understates it
// (subsampled tiles); EstimateFullWaste takes the geometric mean of the two,
// which tracked the full frame on every shipped deep preset (GT 710, 1080p:
// Marina 2.14 full → 1.73 at ¼ / 1.93 at ½; Deep Mini 1.14 → 1.23 / 1.17;
// Rosette 1.13 → 1.12 / 1.11; the rest ≈ 1.0), so the same threshold applies.
// The seed dispatch costs 6–15% of a deep frame and pays back only on
// boundary-heavy views (Marina 14.1 → ~8.5 s), hence opt-in.
//
// FF_GPU_WAVEFRONT=0 turns both off; FF_GPU_WAVEFRONT_PERTURB=0 / 1 forces the
// deep kernels off / on (default auto). Both kernel assemblies link their own
// MandelbrotKernelSource, so the knobs live here, in Engine, which both
// reference (like GpuPeriodicity).

using System;

namespace FracturingFog.Rendering
{
    /// <summary>#1340 — schedule of the GPU iteration kernels.</summary>
    public static class GpuWavefront
    {
        /// <summary>One thread per pixel (the pre-#1340 schedule).</summary>
        public const int Off = 0;
        /// <summary>Persistent threads pulling pixels from a counter.</summary>
        public const int On = 1;
        /// <summary>Deep kernels only: on when the previous frame's
        /// <see cref="WarpWaste"/> reached <see cref="PerturbWasteThreshold"/>.</summary>
        public const int Auto = 2;

        /// <summary>Process default of <see cref="Mode"/>: <see cref="On"/>, or
        /// <see cref="Off"/> when <c>FF_GPU_WAVEFRONT=0</c>.</summary>
        public static readonly int Default =
            Environment.GetEnvironmentVariable("FF_GPU_WAVEFRONT") == "0" ? Off : On;

        /// <summary>Process default of <see cref="PerturbMode"/>: <see cref="Auto"/>,
        /// or <c>FF_GPU_WAVEFRONT_PERTURB</c> = 0 / 1.</summary>
        public static readonly int PerturbDefault = Environment.GetEnvironmentVariable("FF_GPU_WAVEFRONT_PERTURB") switch
        {
            "0" => Off,
            "1" => On,
            _ => Auto,
        };

        /// <summary>Process default thread count: 2^18, or <c>FF_GPU_WAVEFRONT_THREADS</c>.
        /// Enough to fill a large GPU (128 SMs × 2048 resident threads); a GT 710
        /// saturates from ~2048 and loses nothing at 2^19.</summary>
        public static readonly int DefaultThreads =
            int.TryParse(Environment.GetEnvironmentVariable("FF_GPU_WAVEFRONT_THREADS"), out int t) && t > 0 ? t : 1 << 18;

        /// <summary>Deep kernels: pixels per thread of a band's dispatch. A band holds
        /// few pixels (the TDR budget, as few as 8 rows), so threads = pixels would
        /// leave no lane a second pixel to refill with. On a GT 710 1 to 8 time alike.</summary>
        public const int PerturbPixelsPerThread = 4;

        /// <summary>Deep kernels, <see cref="Auto"/>: the <see cref="WarpWaste"/> from
        /// which the next frame runs the wavefront schedule.</summary>
        public const double PerturbWasteThreshold = 1.4;

        /// <summary>#1386 — process default of <see cref="Seed"/>: off, or on with
        /// <c>FF_GPU_WAVEFRONT_SEED=1</c>.</summary>
        public static readonly bool SeedDefault = Environment.GetEnvironmentVariable("FF_GPU_WAVEFRONT_SEED") == "1";

        /// <summary>#1386 — the seed dispatch runs at 1/<see cref="SeedDivisor"/> of
        /// the frame's resolution per axis.</summary>
        public const int SeedDivisor = 4;

        /// <summary>#1386 — the full frame's <see cref="WarpWaste"/> estimated from a
        /// frame of the same view at 1/<paramref name="divisor"/> of its resolution per
        /// axis: the geometric mean of the low frame's waste over 8×8 tiles (an
        /// overestimate) and over tiles of the full frame's footprint, 8/divisor (an
        /// underestimate). A divisor ≤ 1 is the frame itself.</summary>
        public static double EstimateFullWaste(ReadOnlySpan<int> iter, int width, int height, double divisor)
        {
            double over = WarpWaste(iter, width, height, 8);
            if (!(divisor > 1)) return over;
            int tile = Math.Clamp((int)Math.Round(8 / divisor), 1, 8);
            return Math.Sqrt(over * WarpWaste(iter, width, height, tile));
        }

        // Thread-static like the other GPU test knobs, so a test (or a calculator's
        // hint) cannot leak into parallel work. 0 = no override; else value + 1.
        [ThreadStatic] private static int t_override;
        [ThreadStatic] private static int t_perturbOverride;
        [ThreadStatic] private static int t_threads;
        [ThreadStatic] private static bool t_perturbHint;
        [ThreadStatic] private static int t_seed;   // 0 = no override, 1 = off, 2 = on

        /// <summary>Shallow kernel (and master switch): the schedule the next dispatch
        /// on this thread uses. <see cref="Off"/> also turns the deep kernels off.</summary>
        public static int Mode
        {
            get => t_override != 0 ? t_override - 1 : Default;
            set => t_override = Math.Clamp(value, Off, On) + 1;
        }

        /// <summary>Deep kernels: <see cref="Off"/>, <see cref="On"/> or <see cref="Auto"/>.</summary>
        public static int PerturbMode
        {
            get => t_perturbOverride != 0 ? t_perturbOverride - 1 : PerturbDefault;
            set => t_perturbOverride = Math.Clamp(value, Off, Auto) + 1;
        }

        /// <summary>Deep kernels, <see cref="Auto"/>: set by the calculator around a
        /// dispatch (the previous frame's warp waste reached the threshold).</summary>
        public static bool PerturbHint
        {
            get => t_perturbHint;
            set => t_perturbHint = value;
        }

        /// <summary>#1386 — deep kernels, <see cref="Auto"/>: whether a calculator with no
        /// hint for a nearby view runs the ¼-resolution seed dispatch first.</summary>
        public static bool Seed
        {
            get => t_seed != 0 ? t_seed == 2 : SeedDefault;
            set => t_seed = value ? 2 : 1;
        }

        /// <summary>Whether the next deep dispatch on this thread runs the wavefront schedule.</summary>
        public static bool UsePerturb => Mode == On && PerturbMode switch
        {
            On => true,
            Auto => t_perturbHint,
            _ => false,
        };

        /// <summary>Threads a shallow wavefront dispatch on this thread launches (at
        /// most the pixel count).</summary>
        public static int Threads
        {
            get => t_threads > 0 ? t_threads : DefaultThreads;
            set => t_threads = Math.Max(0, value);
        }

        /// <summary>Thread groups of a shallow wavefront dispatch over
        /// <paramref name="pixels"/> pixels, <paramref name="groupThreads"/> threads
        /// each (a 1D grid).</summary>
        public static uint Groups(long pixels, int groupThreads)
            => GroupsFor(Math.Min(pixels, Threads), groupThreads);

        /// <summary>Thread groups of a deep wavefront dispatch over a band of
        /// <paramref name="pixels"/> pixels (<see cref="PerturbPixelsPerThread"/>).</summary>
        public static uint PerturbGroups(long pixels, int groupThreads)
            => GroupsFor(Math.Min((pixels + PerturbPixelsPerThread - 1) / PerturbPixelsPerThread, Threads), groupThreads);

        private static uint GroupsFor(long threads, int groupThreads)
            => (uint)Math.Clamp((threads + groupThreads - 1) / groupThreads, 1, 65535);

        /// <summary>The frame's warp waste: Σ over 8×8 tiles of (pixels · the tile's
        /// largest iteration) / Σ iterations. 1 = every 8×8 group's pixels take the
        /// same steps; one thread per pixel spends this many times the steps the
        /// pixels need (a group runs as long as its slowest pixel), which bounds what
        /// the wavefront schedule can win. 1 for an empty or all-zero frame.</summary>
        public static double WarpWaste(ReadOnlySpan<int> iter, int width, int height) => WarpWaste(iter, width, height, 8);

        /// <summary>#1386 — <see cref="WarpWaste(ReadOnlySpan{int},int,int)"/> over
        /// <paramref name="tile"/>×<paramref name="tile"/> tiles: a frame at 1/k of the
        /// resolution, measured with 8/k tiles, covers the same footprint per tile as
        /// the full frame's 8×8 groups.</summary>
        public static double WarpWaste(ReadOnlySpan<int> iter, int width, int height, int tile)
        {
            if (width <= 0 || height <= 0 || iter.Length < width * height || tile < 1) return 1.0;
            double busy = 0, need = 0;
            for (int ty = 0; ty < height; ty += tile)
            {
                int y1 = Math.Min(height, ty + tile);
                for (int tx = 0; tx < width; tx += tile)
                {
                    int x1 = Math.Min(width, tx + tile), max = 0;
                    long sum = 0;
                    for (int y = ty; y < y1; y++)
                    {
                        var row = iter.Slice(y * width + tx, x1 - tx);
                        foreach (int v in row) { int it = Math.Max(v, 0); sum += it; if (it > max) max = it; }
                    }
                    busy += (double)max * (y1 - ty) * (x1 - tx);
                    need += sum;
                }
            }
            return need > 0 ? busy / need : 1.0;
        }

        /// <summary>Drop this thread's overrides and hint (back to the defaults).</summary>
        public static void Reset() { t_override = 0; t_perturbOverride = 0; t_threads = 0; t_perturbHint = false; t_seed = 0; }
    }
}
