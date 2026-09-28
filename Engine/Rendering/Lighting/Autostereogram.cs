// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Autostereogram.cs
//
// #1010 (S3 of the autostereogram epic #1014) — single-image stereograms
// ("Magic Eye"): one W × H image whose horizontally repeating texture encodes a
// depth map, seen in 3D by diverging (wall-eyed) or crossing the eyes. Depth is
// carried by pattern repetition alone — no colour discrimination at all — so,
// like SBS (#106), it suits the red/green colourblind owner.
//
// Algorithm: Thimbleby, Inglis & Witten, "Displaying 3D Images: Algorithms for
// Single-Image Random-Dot Stereograms", IEEE Computer 27(10), 1994 — the
// same[] constraint-linking method with hidden-surface removal. Per row: every
// visible depth sample links the two screen pixels its lines of sight pass
// through (separation s(z)), then the row is coloured right-to-left so each
// linked pixel copies its partner and unlinked pixels take a fresh pattern
// sample. Rows are independent, so the pass is row-parallel.
//
// Depth convention here: z in [0, 1], 0 = the far plane (sky / background),
// 1 = nearest. PrepareDepth turns a raymarcher / relief ray-distance buffer
// (+Infinity = sky) into that form.
//
// Pure: no host, no UI. Wiring (live / export / dialog) is #1011; batch flags
// #1012; animation #1013.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FracturingFog.Rendering.Lighting;

/// <summary>A tileable pattern image for a textured (non-random-dot)
/// autostereogram. Tiled horizontally and vertically; its width should be close
/// to the far-plane separation (<see cref="Autostereogram.FarSeparation"/>) so a
/// flat background repeats it seamlessly.</summary>
public sealed record AutostereoTexture(uint[] Pixels, int Width, int Height);

/// <summary>Options for <see cref="Autostereogram.Render"/>.</summary>
public sealed record AutostereoOptions
{
    /// <summary>Eye separation in output pixels (E in the paper). The far
    /// plane repeats every E/2 pixels, the near plane every (1-μ)E/(2-μ).
    /// For viewing on a screen about 1/8 of the image width is comfortable.</summary>
    public int EyeSeparationPx { get; init; } = 160;

    /// <summary>Depth of field μ: the fraction of the viewing distance the
    /// depth range spans. Larger = more depth but harder to fuse. Paper
    /// default 1/3.</summary>
    public double DepthOfField { get; init; } = 1.0 / 3.0;

    /// <summary>Dot colours for random-dot mode. Null or empty = black/white
    /// (maximum luminance contrast — easiest to fuse, colourblind safe).
    /// Ignored when <see cref="Texture"/> is set.</summary>
    public uint[]? DotColors { get; init; }

    /// <summary>Textured mode: unlinked pixels sample this tile instead of
    /// random dots (Magic-Eye style; a fractal-render strip works well).</summary>
    public AutostereoTexture? Texture { get; init; }

    /// <summary>Seed for the random dots. Same seed + same depth = same image.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Draw the two convergence guide dots near the top edge, spaced
    /// at the far-plane separation: fuse them into three and the picture
    /// resolves.</summary>
    public bool GuideDots { get; init; }
}

/// <summary>Options for <see cref="Autostereogram.PrepareDepth"/>.</summary>
public sealed record AutostereoDepthOptions
{
    /// <summary>Ray distance mapped to the near plane (z = 1). ≤ 0 = auto (a low
    /// percentile of the hit distances, so a few stray near hits don't flatten
    /// everything else).</summary>
    public double Near { get; init; }

    /// <summary>Ray distance mapped to the back of the object range. ≤ 0 = auto
    /// (a high percentile of the hit distances).</summary>
    public double Far { get; init; }

    /// <summary>Gap between the background (sky, z = 0) and the farthest
    /// object, as a fraction of the depth range: objects occupy
    /// [BackgroundGap, 1] so they stand off the background plane.</summary>
    public double BackgroundGap { get; init; } = 0.15;

    /// <summary>Box-blur radius in pixels applied to z. Softens depth
    /// discontinuities and fine fractal noise, which fuse poorly.</summary>
    public int BlurRadius { get; init; } = 2;

    /// <summary>Shaping exponent on z (1 = linear; &gt; 1 pushes mid depths back).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Quantise z to this many levels (0 or 1 = continuous). A few
    /// levels give the classic terraced look and cleaner fusion.</summary>
    public int Levels { get; init; }

    /// <summary>Invert depth for cross-eyed viewing: a wall-eyed stereogram
    /// viewed cross-eyed shows its depth reversed, so encoding 1 - z makes the
    /// cross-eyed view correct.</summary>
    public bool CrossEyed { get; init; }
}

public static class Autostereogram
{
    /// <summary>Screen separation (pixels) of the two image points of a depth
    /// sample z ∈ [0,1] (0 = far plane): <c>round((1 - μz)·E / (2 - μz))</c>.</summary>
    public static int Separation(double z, double mu, int eyeSepPx)
        => (int)Math.Round((1.0 - mu * z) * eyeSepPx / (2.0 - mu * z));

    /// <summary>Separation of the far plane (z = 0) — the repeat width of a flat
    /// background and the spacing of the guide dots.</summary>
    public static int FarSeparation(AutostereoOptions o)
        => Separation(0.0, ClampMu(o.DepthOfField), o.EyeSeparationPx);

    /// <summary>Turn a ray-distance buffer (+Infinity / non-positive = sky) into
    /// autostereogram depth z ∈ [0,1] (0 = far plane / sky, 1 = nearest):
    /// normalise the hit range, stand objects off the background, then shape
    /// (gamma), smooth (box blur), quantise and optionally invert for
    /// cross-eyed viewing.</summary>
    public static float[] PrepareDepth(float[] rayDistance, int w, int h, AutostereoDepthOptions o)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        int n = w * h;
        if (rayDistance == null || rayDistance.Length < n)
            throw new ArgumentException("depth buffer too small", nameof(rayDistance));

        (double near, double far) = ResolveRange(rayDistance, n, o);
        double span = Math.Max(1e-12, far - near);
        double gap = Math.Clamp(o.BackgroundGap, 0.0, 0.95);
        double gamma = o.Gamma > 0 ? o.Gamma : 1.0;

        var z = new float[n];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float d = rayDistance[row + x];
                if (!IsHit(d)) { z[row + x] = 0f; continue; }
                double t = Math.Clamp((far - d) / span, 0.0, 1.0);   // 1 = near
                if (gamma != 1.0) t = Math.Pow(t, gamma);
                z[row + x] = (float)(gap + (1.0 - gap) * t);
            }
        });

        if (o.BlurRadius > 0) BoxBlur(z, w, h, o.BlurRadius);

        if (o.Levels >= 2)
        {
            int l = o.Levels - 1;
            for (int i = 0; i < n; i++) z[i] = (float)(Math.Round(z[i] * l) / l);
        }
        if (o.CrossEyed)
            for (int i = 0; i < n; i++) z[i] = 1f - z[i];
        return z;
    }

    /// <summary>Encode a prepared depth map (z ∈ [0,1], 0 = far) as a W × H
    /// autostereogram (opaque BGRA).</summary>
    public static uint[] Render(float[] z, int w, int h, AutostereoOptions o)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (z == null || z.Length < w * h) throw new ArgumentException("depth map too small", nameof(z));
        if (o.EyeSeparationPx < 4) throw new ArgumentOutOfRangeException(nameof(o), "eye separation must be at least 4 px");
        double mu = ClampMu(o.DepthOfField);
        int e = o.EyeSeparationPx;
        uint[] dots = o.DotColors is { Length: > 0 } dc ? dc : new[] { 0xFF000000u, 0xFFFFFFFFu };
        var tex = o.Texture is { Width: > 0, Height: > 0 } t && t.Pixels.Length >= t.Width * t.Height ? t : null;

        var output = new uint[w * h];
        Parallel.For(0, h,
            () => (same: new int[w], pix: new uint[w]),
            (y, _, scratch) =>
            {
                RenderRow(z, y * w, w, y, mu, e, scratch.same, scratch.pix, dots, tex, o.Seed);
                Array.Copy(scratch.pix, 0, output, y * w, w);
                return scratch;
            },
            _ => { });

        if (o.GuideDots) DrawGuideDots(output, w, h, FarSeparation(o), e);
        return output;
    }

    /// <summary><see cref="PrepareDepth"/> then <see cref="Render"/>.</summary>
    public static uint[] FromRayDistance(float[] rayDistance, int w, int h,
        AutostereoDepthOptions depth, AutostereoOptions options)
        => Render(PrepareDepth(rayDistance, w, h, depth), w, h, options);

    /// <summary>Cut a pattern tile from an image (e.g. the mono fractal render):
    /// the centred vertical strip <paramref name="stripW"/> wide, full height —
    /// the "fractal texture" source for a textured stereogram.</summary>
    public static AutostereoTexture CutStrip(uint[] image, int w, int h, int stripW)
    {
        stripW = Math.Clamp(stripW, 1, w);
        int x0 = (w - stripW) / 2;
        var px = new uint[stripW * h];
        for (int y = 0; y < h; y++) Array.Copy(image, y * w + x0, px, y * stripW, stripW);
        return new AutostereoTexture(px, stripW, h);
    }

    private static double ClampMu(double mu) => Math.Clamp(mu, 0.01, 0.9);

    // ── Row encoder (Thimbleby / Inglis / Witten 1994) ───────────────────

    private static void RenderRow(float[] z, int row, int w, int y, double mu, int e,
        int[] same, uint[] pix, uint[] dots, AutostereoTexture? tex, int seed)
    {
        for (int x = 0; x < w; x++) same[x] = x;

        for (int x = 0; x < w; x++)
        {
            double zx = z[row + x];
            int s = Separation(zx, mu, e);
            // Alternate the rounding of an odd separation by row so the
            // half-pixel bias does not line up down the image (paper, §3).
            int left = x - (s + (s & y & 1)) / 2;
            int right = left + s;
            if (left < 0 || right >= w) continue;

            // Hidden-surface removal: the sample is visible to both eyes only if
            // nothing along either line of sight rises above it.
            bool visible = true;
            int t = 1;
            double zt;
            do
            {
                zt = zx + 2.0 * (2.0 - mu * zx) * t / (mu * e);
                if (x - t < 0 || x + t >= w) break;
                visible = z[row + x - t] < zt && z[row + x + t] < zt;
                t++;
            } while (visible && zt < 1.0);
            if (!visible) continue;

            // Record that pixels left and right must match, keeping each
            // same[] chain sorted (same[k] > k) so the right-to-left colouring
            // below resolves every constraint.
            for (int k = same[left]; k != left && k != right; k = same[left])
            {
                if (k < right) left = k;
                else { left = right; right = k; }
            }
            same[left] = right;
        }

        for (int x = w - 1; x >= 0; x--)
        {
            if (same[x] != x) { pix[x] = pix[same[x]]; continue; }
            if (tex != null)
            {
                int tx = x % tex.Width, ty = y % tex.Height;
                pix[x] = tex.Pixels[ty * tex.Width + tx] | 0xFF000000u;
            }
            else
            {
                pix[x] = dots[(int)(Hash(seed, x, y) % (uint)dots.Length)] | 0xFF000000u;
            }
        }
    }

    // Stateless per-pixel hash (SplitMix-style finaliser): row-parallel and
    // reproducible, and a fixed seed keeps a dot in place frame to frame.
    private static uint Hash(int seed, int x, int y)
    {
        ulong v = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL
                ^ (ulong)(uint)x * 0xBF58476D1CE4E5B9UL
                ^ (ulong)(uint)y * 0x94D049BB133111EBUL;
        v ^= v >> 30; v *= 0xBF58476D1CE4E5B9UL;
        v ^= v >> 27; v *= 0x94D049BB133111EBUL;
        v ^= v >> 31;
        return (uint)v;
    }

    // ── Depth preparation ────────────────────────────────────────────────

    private static bool IsHit(float d) => d > 0f && !float.IsInfinity(d) && !float.IsNaN(d);

    private static (double Near, double Far) ResolveRange(float[] d, int n, AutostereoDepthOptions o)
    {
        double near = o.Near, far = o.Far;
        if (near > 0 && far > near) return (near, far);

        // Robust auto range: 2nd / 98th percentile of up to ~64k sampled hits.
        int stride = Math.Max(1, n / 65536);
        var hits = new List<float>(Math.Min(n, 65536) + 1);
        for (int i = 0; i < n; i += stride) if (IsHit(d[i])) hits.Add(d[i]);
        if (hits.Count == 0) return (1.0, 2.0);   // all sky: nothing maps anyway
        hits.Sort();
        double lo = hits[(int)((hits.Count - 1) * 0.02)];
        double hi = hits[(int)((hits.Count - 1) * 0.98)];
        if (near <= 0) near = lo;
        if (far <= near) far = Math.Max(hi, near + 1e-6);
        return (near, far);
    }

    // Separable box blur, edge-clamped.
    private static void BoxBlur(float[] z, int w, int h, int r)
    {
        var tmp = new float[z.Length];
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -r; k <= r; k++) acc += z[row + Math.Clamp(x + k, 0, w - 1)];
                tmp[row + x] = acc * inv;
            }
        });
        Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                float acc = 0f;
                for (int k = -r; k <= r; k++) acc += tmp[Math.Clamp(y + k, 0, h - 1) * w + x];
                z[y * w + x] = acc * inv;
            }
        });
    }

    // Two convergence dots near the top, spaced at the far-plane separation:
    // black discs with a white rim so they read on any pattern.
    private static void DrawGuideDots(uint[] img, int w, int h, int farSep, int e)
    {
        int r = Math.Max(2, e / 40);
        int cy = Math.Min(h - r - 2, Math.Max(r + 2, h / 20));
        int cx0 = w / 2 - farSep / 2, cx1 = cx0 + farSep;
        foreach (int cx in new[] { cx0, cx1 })
            for (int dy = -r - 1; dy <= r + 1; dy++)
                for (int dx = -r - 1; dx <= r + 1; dx++)
                {
                    int px = cx + dx, py = cy + dy;
                    if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
                    int d2 = dx * dx + dy * dy;
                    if (d2 <= r * r) img[py * w + px] = 0xFF000000u;
                    else if (d2 <= (r + 1) * (r + 1)) img[py * w + px] = 0xFFFFFFFFu;
                }
    }
}
