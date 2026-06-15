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
using System.Threading.Tasks;

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

        return outBuf;
    }
}
