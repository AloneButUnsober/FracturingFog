// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// StereoRender.cs
//
// Phase 20 — side-by-side stereo synthesis. Operates on a finished monocular
// render (color + depth buffer) and produces a doubled-width output buffer
// where the left half is the original camera-centered render and the right
// half is a synthetic right-eye view derived via depth-parallax warp.
//
// Why depth-parallax (fake) instead of two real renders?
//   Each calculator owns its own camera (per CLAUDE.md: scene scale differs
//   per fractal). True per-eye render needs camera-offset plumbing inside
//   all 7 raymarchers — invasive, doubles render time. The depth buffer is
//   already populated by ShadingPipeline.Shade for SSAO/etc., so we get
//   stereo from a single render at minimal extra cost. Tradeoff: occlusion
//   seams at depth jumps (no info on the back of objects); the hole-fill
//   pass papers over them. Phase 20b (deferred) will add true per-eye
//   render for users who want the higher quality at 2× render time.
//
// Output layout
//   uint[outW × height] where outW = width × 2. Pixel (x, y) for x < width
//   is the left eye; pixel (x, y) for x in [width, 2·width) is the right eye.
//   Standard "FullSBS" layout — phone-VR / Cardboard viewers expect this.
//
// Host wiring
//   ApplyStereoSideBySide should be called by the host *after* ApplyToneMap-
//   Bloom (display-ready bytes in colorBuffer). The host swaps its display
//   target to the returned doubled-width buffer when non-null. Phase 20
//   ships engine slice only; host plumbing deferred to Phase 20b.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

public static class StereoRender
{
    /// <summary>
    /// Synthesize side-by-side stereo from a monocular color + depth render.
    /// Returns a doubled-width buffer or <c>null</c> when stereo is off
    /// (<c>fx.StereoEyeSeparation &lt;= 0</c>). Sky pixels (DepthMiss) stay
    /// at the original column in the right eye so the horizon doesn't shear.
    /// </summary>
    /// <param name="colorBuffer">BGRA pixel array, sized width·height.</param>
    /// <param name="depthBuffer">Per-pixel TotalT. <see cref="ScreenSpacePost.DepthMiss"/>
    /// = sky / ray miss; stays unshifted.</param>
    /// <param name="width">Source image width.</param>
    /// <param name="height">Source image height.</param>
    /// <param name="fx">Active lighting block — drives EyeSeparation + FOV.</param>
    /// <returns>Doubled-width buffer (left=original, right=parallax) or null.</returns>
    public static uint[]? ApplyStereoSideBySide(
        uint[] colorBuffer,
        float[] depthBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        double eyeSep = fx.StereoEyeSeparation;
        if (eyeSep <= 0) return null;
        int n = width * height;
        if (colorBuffer.Length < n) return null;
        if (depthBuffer.Length < n) return null;

        // Focal length in pixels = (width/2) / tan(fov/2).
        // 60° FOV → focal ≈ 0.866 · width; 90° FOV → focal = 0.5 · width.
        double fovRad = fx.StereoFovDegrees * Math.PI / 180.0;
        double focalPx = (width * 0.5) / Math.Tan(fovRad * 0.5);

        // Parallax comfort guard: cap the per-pixel warp shift so a very near
        // hit can't produce an un-fusable disparity (divergence → eye strain).
        // 0 disables the clamp.
        double maxShiftPx = fx.StereoMaxDisparity > 0
            ? fx.StereoMaxDisparity * width
            : double.PositiveInfinity;

        int outW = width * 2;
        uint[] outBuf = new uint[outW * height];

        // Per-row right-eye depth buffer for occlusion sort. Closer pixels
        // (smaller depth) overwrite distant ones at the same destination x.
        // One float[] per thread reused inside Parallel.For via a thread-local
        // factory; cheaper than allocating per row.
        Parallel.For<float[]>(
            0, height,
            () => new float[width], // thread-local right-depth scratch
            (y, _, rDepth) =>
            {
                for (int i = 0; i < width; i++) rDepth[i] = float.PositiveInfinity;
                int srcRow = y * width;
                int dstRowL = y * outW;
                int dstRowR = dstRowL + width;

                // Left eye = unchanged copy of the source row.
                Array.Copy(colorBuffer, srcRow, outBuf, dstRowL, width);

                // Right eye = forward-mapped parallax warp. Each source pixel
                // shifts left by shift(d) = eyeSep · focalPx / d. Sky pixels
                // (depth = +inf) shift = 0, staying in place. Depth compare
                // resolves overlapping writes (front-most pixel wins).
                for (int x = 0; x < width; x++)
                {
                    int sIdx = srcRow + x;
                    float d = depthBuffer[sIdx];
                    double shift;
                    if (float.IsPositiveInfinity(d) || d <= 0)
                        shift = 0.0;
                    else
                        shift = eyeSep * focalPx / d;
                    if (shift > maxShiftPx) shift = maxShiftPx;
                    int rx = x - (int)Math.Round(shift);
                    if ((uint)rx < (uint)width)
                    {
                        if (d < rDepth[rx])
                        {
                            outBuf[dstRowR + rx] = colorBuffer[sIdx];
                            rDepth[rx] = d;
                        }
                    }
                }

                // Hole-fill: any output pixel still default (0u) was missed by
                // the forward map (depth occluded by a closer pixel that
                // didn't shift past it, or no source pixel mapped there at
                // all). Inherit from the nearest filled neighbour to the
                // left; if the row starts empty, fall back to the source
                // pixel at the same column.
                uint prev = colorBuffer[srcRow];
                for (int x = 0; x < width; x++)
                {
                    uint c = outBuf[dstRowR + x];
                    if (c == 0u)
                        outBuf[dstRowR + x] = prev;
                    else
                        prev = c;
                }

                return rDepth;
            },
            _ => { });

        ApplyConvergence(outBuf, outW, width, height, fx.StereoConvergence);
        return outBuf;
    }

    /// <summary>Convergence via horizontal image translation (HIT). Shifts the
    /// two eye halves of an already-composited side-by-side buffer in opposite
    /// directions to move the zero-parallax plane. Positive
    /// <paramref name="convergenceFraction"/> (fraction of eye width) crosses
    /// the eyes — the left half moves right, the right half moves left — so the
    /// subject settles at the screen plane and nearer detail floats in front.
    /// Exposed columns are edge-clamped (replicate the border pixel) so no black
    /// border appears. No-op when the fraction rounds to a zero-pixel shift.
    /// </summary>
    /// <param name="sbs">Doubled-width SBS buffer (<paramref name="outW"/> = 2·width).</param>
    /// <param name="outW">Buffer stride (2·width).</param>
    /// <param name="width">Single-eye width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="convergenceFraction">HIT amount as a fraction of eye width.</param>
    public static void ApplyConvergence(
        uint[] sbs, int outW, int width, int height, double convergenceFraction)
    {
        if (convergenceFraction == 0.0) return;
        int half = (int)Math.Round(Math.Abs(convergenceFraction) * width * 0.5);
        if (half == 0) return;
        int signed = Math.Sign(convergenceFraction) * half;

        Parallel.For<uint[]>(
            0, height,
            () => new uint[width], // thread-local row scratch
            (y, _, tmp) =>
            {
                int lRow = y * outW;
                int rRow = lRow + width;
                ShiftRowClamped(sbs, lRow, width, +signed, tmp); // left eye → right
                ShiftRowClamped(sbs, rRow, width, -signed, tmp); // right eye → left
                return tmp;
            },
            _ => { });
    }

    /// <summary>Shift one eye's row horizontally by <paramref name="shift"/>
    /// columns (positive = content moves toward larger x), replicating the edge
    /// pixel into exposed columns. <paramref name="tmp"/> is a reusable
    /// width-length scratch buffer.</summary>
    private static void ShiftRowClamped(uint[] buf, int rowStart, int width, int shift, uint[] tmp)
    {
        if (shift == 0) return;
        Array.Copy(buf, rowStart, tmp, 0, width);
        for (int x = 0; x < width; x++)
        {
            int src = x - shift;
            if (src < 0) src = 0;
            else if (src >= width) src = width - 1;
            buf[rowStart + x] = tmp[src];
        }
    }

    /// <summary>Suggest a comfortable eye separation (world-unit IPD) for the
    /// Fake depth-parallax warp from the scene depth range. Picks the IPD at
    /// which the nearest surface hit produces exactly
    /// <see cref="LightingFxData.StereoMaxDisparity"/> of on-screen disparity,
    /// so nothing in the frame exceeds the fusion budget. Returns 0 when the
    /// depth buffer is all sky / invalid.</summary>
    public static double SuggestEyeSeparation(
        float[] depth, int width, int height, in LightingFxData fx)
    {
        int n = width * height;
        if (depth == null || depth.Length < n) return 0.0;
        double dMin = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            float d = depth[i];
            if (d > 0f && !float.IsPositiveInfinity(d) && d < dMin) dMin = d;
        }
        if (double.IsPositiveInfinity(dMin)) return 0.0;

        double fovRad = fx.StereoFovDegrees * Math.PI / 180.0;
        double focalPx = (width * 0.5) / Math.Tan(fovRad * 0.5);
        double maxDisp = fx.StereoMaxDisparity > 0 ? fx.StereoMaxDisparity : 0.03;
        double maxPx = maxDisp * width;
        // disparity(dMin) = eyeSep · focalPx / dMin == maxPx  ⇒  solve eyeSep.
        return maxPx * dMin / focalPx;
    }

    /// <summary>Phase 20b — true per-eye stereo orchestration.
    ///
    /// Renders the scene twice with the camera origin shifted by ±IPD/2 along
    /// the right basis (each 3D calculator picks up the offset via
    /// <see cref="LightingFxData.StereoEyeOffset"/>) and composites a doubled-
    /// width side-by-side buffer. Returns <c>null</c> when stereo is off
    /// (<c>fx.StereoMode != True</c> or <c>StereoEyeSeparation &lt;= 0</c>).
    ///
    /// Unlike <see cref="ApplyStereoSideBySide"/> (which warps a single mono
    /// render via depth-parallax), this path produces actual parallax on close
    /// objects at the cost of two full renders per frame.
    ///
    /// Callers supply two callbacks to keep this helper agnostic to the
    /// per-fractal calculator API: <paramref name="renderOnce"/> runs the
    /// calculator's full pipeline (the same call the host already uses for a
    /// mono frame), and <paramref name="snapshotColorBuffer"/> hands back the
    /// just-rendered buffer (cloned by this method so the next render can
    /// safely overwrite). <paramref name="fp"/> is mutated to set
    /// <see cref="LightingFxData.StereoEyeOffset"/> before each pass and is
    /// restored to its original value (including the original
    /// <see cref="LightingFxData.StereoEyeOffset"/>) in a <c>finally</c> block
    /// so a cancelled / faulted render does not leave the params in a stereo
    /// state.</summary>
    /// <param name="fp">Active fractal parameters. The
    /// <see cref="LightingFxData.StereoEyeOffset"/> field is set transiently
    /// to ±IPD/2 around the two render passes; the original Lighting value is
    /// restored before return.</param>
    /// <param name="renderOnce">Delegate that drives one render of the active
    /// calculator. Typically <c>ct => calc.Calculate(ct)</c>.</param>
    /// <param name="snapshotColorBuffer">Delegate that returns the calculator's
    /// current <c>ColorBuffer</c>. Called twice; the helper clones the first
    /// snapshot so the second render can safely reuse the buffer.</param>
    /// <param name="width">Source render width (mono). Output is 2 × this.</param>
    /// <param name="height">Source render height.</param>
    /// <param name="ct">Cancellation token threaded through to each render
    /// pass. If the first pass is cancelled the helper returns null without
    /// running the second.</param>
    /// <returns>Doubled-width side-by-side buffer (left = -IPD/2 eye, right =
    /// +IPD/2 eye) or <c>null</c> if stereo is off / cancelled.</returns>
    public static uint[]? RenderTrueStereo(
        FractalParameters fp,
        Action<CancellationToken> renderOnce,
        Func<uint[]> snapshotColorBuffer,
        int width, int height,
        CancellationToken ct)
    {
        if (fp == null) return null;
        var orig = fp.Lighting;
        if (orig.StereoMode != StereoMode.True) return null;
        double ipd = orig.StereoEyeSeparation;
        if (ipd <= 0) return null;
        if (width <= 0 || height <= 0) return null;

        uint[]? outBuf = null;
        try
        {
            // Left eye render with eye shifted by -IPD/2 along the right basis.
            var lf = orig;
            lf.StereoEyeOffset = -ipd * 0.5;
            fp.Lighting = lf;
            renderOnce(ct);
            if (ct.IsCancellationRequested) return null;
            var leftSrc = snapshotColorBuffer();
            if (leftSrc == null || leftSrc.Length < width * height) return null;
            var leftSnapshot = new uint[width * height];
            Array.Copy(leftSrc, leftSnapshot, width * height);

            // Right eye render with eye shifted by +IPD/2.
            var rf = orig;
            rf.StereoEyeOffset = +ipd * 0.5;
            fp.Lighting = rf;
            renderOnce(ct);
            if (ct.IsCancellationRequested) return null;
            var rightSrc = snapshotColorBuffer();
            if (rightSrc == null || rightSrc.Length < width * height) return null;

            int outW = width * 2;
            outBuf = new uint[outW * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * width;
                int dstRowL = y * outW;
                int dstRowR = dstRowL + width;
                Array.Copy(leftSnapshot, srcRow, outBuf, dstRowL, width);
                Array.Copy(rightSrc, srcRow, outBuf, dstRowR, width);
            }
            ApplyConvergence(outBuf, outW, width, height, orig.StereoConvergence);
            return outBuf;
        }
        finally
        {
            // Always restore — a cancelled or faulted render must not leave
            // the params in a stereo state.
            fp.Lighting = orig;
        }
    }
}
