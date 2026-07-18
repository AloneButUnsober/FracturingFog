// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes3D/PostProcessThemes.cs
//
// Post-process colour themes — each produces a Lambert-shaded base colour
// per-pixel via the gradient + surface normal, then runs a second full-screen
// pass that reads neighbour samples from the float aux buffers (smooth /
// normals) and modulates the final ARGB output.  Implements
// IPostProcessColorMap so the calculator invokes PostProcess() once after
// the main render completes.
//
//   • EmbossBumpMap        — Sobel edge detection on smooth buffer.  Brightens
//                            pixels facing the light, darkens the opposite
//                            face.  Etched topographic look.
//
//   • AmbientOcclusionMap  — Samples a ring of neighbours; concavity (mean
//                            neighbour smooth value > centre) darkens the
//                            pixel.  Lambert relief in Map() makes the 3D
//                            shape visible regardless of AO contribution.
//
//   • SoftShadowMap        — Marches a ray in light direction across the
//                            smoothBuffer-as-heightmap.  Slope-relative
//                            comparison removes the global gradient bias so
//                            only LOCAL surface features (cusps, filaments)
//                            cast shadows.  Lambert relief in Map() provides
//                            the baseline 3D shape.
//
// All three share the same gradient (brightened from the original sandstone
// palette so multiplicative darkening retains visible variation).

using FracturingFog.Interefaces;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace FracturingFog.Models
{
    // =========================================================================
    // Shared helpers
    // =========================================================================

    internal static class PostProcessHelpers
    {
        /// <summary>Unpacks ARGB uint into normalized RGB floats.</summary>
        public static void UnpackARGB(uint c, out float r, out float g, out float b)
        {
            r = ((c >> 16) & 0xFF) / 255f;
            g = ((c >>  8) & 0xFF) / 255f;
            b = ( c        & 0xFF) / 255f;
        }

        /// <summary>Packs normalized RGB floats back into ARGB uint, alpha=0xFF.</summary>
        public static uint PackARGB(float r, float g, float b)
        {
            byte R = (byte)(System.Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(System.Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(System.Math.Clamp(b, 0f, 1f) * 255f);
            return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }

        /// <summary>True if pixel is interior (smooth == 0 → undefined height).</summary>
        public static bool IsInterior(float smooth) => smooth <= 0f;

        // Brighter sandstone — leaves headroom for darkening passes.
        public static void AddSandstoneStops(System.Collections.Generic.List<ColorStop> stops)
        {
            stops.Add(new ColorStop(0.00f, Color.FromArgb( 90,  60,  40)));
            stops.Add(new ColorStop(0.30f, Color.FromArgb(180, 130,  85)));
            stops.Add(new ColorStop(0.60f, Color.FromArgb(225, 195, 145)));
            stops.Add(new ColorStop(0.85f, Color.FromArgb(240, 225, 195)));
            stops.Add(new ColorStop(1.00f, Color.FromArgb(250, 245, 230)));
        }

        // ── Lambert pre-shading ───────────────────────────────────────────────
        // Bakes a one-light Lambert + ambient term into the gradient albedo so
        // AO / Shadow post-process themes have a 3D base to darken from.
        // Light direction matches the post-process light vector used by the
        // emboss / shadow passes (upper-right, mild elevation).
        public const float PreLx = 0.50f;
        public const float PreLy = -0.45f;
        public const float PreLz = 0.74f;
        public const float PreSteepness = 1.4f;
        public const float PreAmbient = 0.40f;

        public static int LambertShadeARGB(int albedo, float nx, float ny)
        {
            float ry = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + PreSteepness * PreSteepness);
            float Nx = nx / len, Ny = ry / len, Nz = PreSteepness / len;
            float diff = MathF.Max(0f, Nx * PreLx + Ny * PreLy + Nz * PreLz);
            float k = PreAmbient + (1f - PreAmbient) * diff;

            float aR = ((albedo >> 16) & 0xFF) / 255f;
            float aG = ((albedo >>  8) & 0xFF) / 255f;
            float aB = ( albedo        & 0xFF) / 255f;
            byte R = (byte)(System.Math.Clamp(aR * k, 0f, 1f) * 255f);
            byte G = (byte)(System.Math.Clamp(aG * k, 0f, 1f) * 255f);
            byte B = (byte)(System.Math.Clamp(aB * k, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }

    // =========================================================================
    // EmbossBumpMap — Sobel edge detection
    // =========================================================================

    /// <summary>
    /// Sobel emboss / bump mapping.  Treats the smooth-iteration field as a
    /// height map and applies a 3×3 Sobel operator to compute its 2D gradient.
    /// The gradient dot light direction modulates pixel brightness, producing
    /// an etched topographic relief over the base gradient colour.
    /// </summary>
    public sealed class EmbossBumpMap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Emboss Bump";
        public static string Category => "Post-Process";
        public static string Description =>
            "Sobel edge detection on the smooth iteration field, applied as " +
            "bump-style brightness modulation.  Etched topographic look.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.ThreeDEffect | ColorMapFeatures.UsesPostProcess;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // 2D light direction in screen space.
        private const float LightDx = 0.707f;   // +x (right)
        private const float LightDy = -0.707f;  // -y (up, screen space y down)

        /// <summary>Embossing strength: amplitude of brightness swing.</summary>
        private const float Strength = 0.55f;

        public EmbossBumpMap()
        {
            PostProcessHelpers.AddSandstoneStops(Stops);
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            // Raw smooth diffs (no invIter scaling) — typical Mandelbrot smooth
            // gradient is ~1-5 units/pixel near the set, so Sobel response is
            // already in a sensible scale.  EmbossScale below tunes the amplitude.
            const float EmbossScale = 0.15f;

            Parallel.For(1, height - 1, y =>
            {
                int rowBase = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = rowBase + x;
                    float s = smooth[idx];
                    if (PostProcessHelpers.IsInterior(s)) continue;

                    // Sobel 3×3 over raw smooth buffer.
                    float h00 = smooth[idx - width - 1];
                    float h01 = smooth[idx - width    ];
                    float h02 = smooth[idx - width + 1];
                    float h10 = smooth[idx          - 1];
                    float h12 = smooth[idx          + 1];
                    float h20 = smooth[idx + width - 1];
                    float h21 = smooth[idx + width    ];
                    float h22 = smooth[idx + width + 1];

                    float gx = (h02 + 2f * h12 + h22) - (h00 + 2f * h10 + h20);
                    float gy = (h20 + 2f * h21 + h22) - (h00 + 2f * h01 + h02);

                    float emboss = (gx * LightDx + gy * LightDy) * EmbossScale;
                    emboss = System.Math.Clamp(emboss, -1f, 1f) * Strength;

                    float k = 1f + emboss;

                    PostProcessHelpers.UnpackARGB(colorBuf[idx], out float r, out float g, out float b);
                    colorBuf[idx] = PostProcessHelpers.PackARGB(r * k, g * k, b * k);
                }
            });
        }
    }

    // =========================================================================
    // AmbientOcclusionMap — neighbourhood concavity
    // =========================================================================

    /// <summary>
    /// Screen-space ambient occlusion.  Map() bakes a Lambert relief term so the
    /// pixel has 3D shape from the surface normal alone.  PostProcess samples a
    /// ring of neighbours and darkens pixels where the local mean smooth value
    /// is higher than the centre (concavity), producing crevice darkening on
    /// top of the Lambert base.
    /// </summary>
    public sealed class AmbientOcclusionMap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Ambient Occlusion";
        public static string Category => "Post-Process";
        public static string Description =>
            "Lambert-shaded gradient + screen-space ambient occlusion driven " +
            "by neighbourhood concavity.  Darkens crevices and concave regions.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect |
            ColorMapFeatures.UsesPostProcess;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        /// <summary>Sampling ring radius in pixels.</summary>
        private const int Radius = 5;
        /// <summary>Max darkening at full occlusion.</summary>
        private const float DarkenAmount = 0.60f;
        /// <summary>
        /// Height-rise scale.  Maps mean-neighbour-rise in smooth units to a
        /// concavity factor in [0,1].  Tuned so a ~3-unit rise gives ~1.0.
        /// </summary>
        private const float Sensitivity = 0.35f;

        // 16 ring offsets pre-rotated around (0,0).
        private static readonly (int dx, int dy)[] RingOffsets;

        static AmbientOcclusionMap()
        {
            const int n = 16;
            RingOffsets = new (int, int)[n];
            for (int i = 0; i < n; i++)
            {
                double a = (i / (double)n) * 2.0 * Math.PI;
                RingOffsets[i] = ((int)Math.Round(Math.Cos(a) * Radius),
                                  (int)Math.Round(Math.Sin(a) * Radius));
            }
        }

        public AmbientOcclusionMap()
        {
            PostProcessHelpers.AddSandstoneStops(Stops);
        }

        // Bake Lambert relief into base colour so 3D shape shows regardless of
        // post-process contribution.  Three-arg overload still goes through the
        // flat gradient (used for swatches without normal data).
        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);
            float t = iterations > 0 ? smooth / iterations : 0f;
            int albedo = MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
            return PostProcessHelpers.LambertShadeARGB(albedo, nx, ny);
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            int ringLen = RingOffsets.Length;

            Parallel.For(Radius, height - Radius, y =>
            {
                int rowBase = y * width;
                for (int x = Radius; x < width - Radius; x++)
                {
                    int idx = rowBase + x;
                    float sCenter = smooth[idx];
                    if (PostProcessHelpers.IsInterior(sCenter)) continue;

                    // Compute local mean of exterior neighbours.
                    float sum = 0f;
                    int count = 0;
                    for (int k = 0; k < ringLen; k++)
                    {
                        var (dx, dy) = RingOffsets[k];
                        int nIdx = idx + dy * width + dx;
                        float sNb = smooth[nIdx];
                        if (PostProcessHelpers.IsInterior(sNb)) continue;
                        sum += sNb;
                        count++;
                    }
                    if (count == 0) continue;
                    float mean = sum / count;

                    // Concavity = how much higher the neighbourhood is than us.
                    // Positive = valley → occluded; negative = ridge → exposed.
                    float rise = mean - sCenter;
                    float occ = System.Math.Clamp(rise * Sensitivity, 0f, 1f);
                    float k2 = 1f - DarkenAmount * occ;

                    PostProcessHelpers.UnpackARGB(colorBuf[idx], out float r, out float g, out float b);
                    colorBuf[idx] = PostProcessHelpers.PackARGB(r * k2, g * k2, b * k2);
                }
            });
        }
    }

    // =========================================================================
    // SoftShadowMap — slope-relative heightmap ray marching
    // =========================================================================

    /// <summary>
    /// Soft shadows from a single directional light.  Map() bakes a Lambert
    /// relief into the gradient so the surface shape is visible everywhere.
    /// PostProcess marches a ray across the smooth-iteration heightmap and
    /// SUBTRACTS the expected gradient slope along the light direction — so
    /// only LOCAL bumps cast shadows, not the global escape-potential gradient
    /// that runs across the whole image.
    /// </summary>
    public sealed class SoftShadowMap : GradientColorMap, IPostProcessColorMap
    {
        public static string Name => "Soft Shadow";
        public static string Category => "Post-Process";
        public static string Description =>
            "Lambert-shaded gradient + slope-relative heightmap ray-march " +
            "from a single directional light.  Produces soft cast shadows from " +
            "local surface features (filaments, cusps) without the monotonic " +
            "bias from the global escape-potential gradient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect |
            ColorMapFeatures.UsesPostProcess;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Screen-space light direction (sun toward upper right).
        private const float LightDx = 0.707f;
        private const float LightDy = -0.707f;
        /// <summary>Maximum march distance in pixels.</summary>
        private const int MaxSteps = 18;
        /// <summary>Step length in pixels (>=1).</summary>
        private const int StepSize = 2;
        /// <summary>Penumbra softness factor — IQ technique.</summary>
        private const float Softness = 0.6f;
        /// <summary>Max darkening at full shadow.</summary>
        private const float ShadowAmount = 0.55f;

        public SoftShadowMap()
        {
            PostProcessHelpers.AddSandstoneStops(Stops);
        }

        // Lambert-shaded base color.
        public int Map(float smooth, float distance, int iterations, float nx, float ny)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);
            float t = iterations > 0 ? smooth / iterations : 0f;
            int albedo = MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
            return PostProcessHelpers.LambertShadeARGB(albedo, nx, ny);
        }

        public void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                                int width, int height, int iterations)
        {
            // Pre-compute global slope estimate along light direction so we can
            // subtract it from the ray-march comparison — leaves only LOCAL
            // surface features to cast shadows.
            float globalSlope = ComputeGlobalSlope(smooth, width, height);

            Parallel.For(0, height, y =>
            {
                int rowBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowBase + x;
                    float sCenter = smooth[idx];
                    if (PostProcessHelpers.IsInterior(sCenter)) continue;

                    float minClearance = float.MaxValue;

                    for (int s = 1; s <= MaxSteps; s++)
                    {
                        int dist = s * StepSize;
                        int sx = x + (int)(LightDx * dist);
                        int sy = y + (int)(LightDy * dist);
                        if ((uint)sx >= (uint)width || (uint)sy >= (uint)height) break;

                        int sIdx = sy * width + sx;
                        float sSample = smooth[sIdx];
                        if (PostProcessHelpers.IsInterior(sSample)) continue;

                        // Expected smooth at this step given the GLOBAL gradient.
                        // The shadow only fires when the actual sample is higher
                        // than this expected baseline — i.e. a local feature.
                        float expected = sCenter + globalSlope * dist;
                        float excess = sSample - expected;

                        // IQ penumbra: clearance = -excess / dist; smaller = darker.
                        float clearance = -excess * Softness / dist;
                        if (clearance < minClearance) minClearance = clearance;
                    }

                    float shadow = 0f;
                    if (minClearance < 1f)
                        shadow = System.Math.Clamp(1f - minClearance, 0f, 1f);

                    float k = 1f - ShadowAmount * shadow;

                    PostProcessHelpers.UnpackARGB(colorBuf[idx], out float r, out float g, out float b);
                    colorBuf[idx] = PostProcessHelpers.PackARGB(r * k, g * k, b * k);
                }
            });
        }

        // Estimate average smooth-buffer slope along light direction, in
        // smooth-units per pixel.  Sampled from a small grid of exterior pixels.
        private static float ComputeGlobalSlope(float[] smooth, int width, int height)
        {
            const int Samples = 64;
            int margin = 8;
            int step = 8;
            float sum = 0f;
            int count = 0;
            var rng = new Random(0);
            for (int i = 0; i < Samples; i++)
            {
                int x = margin + rng.Next(width - 2 * margin);
                int y = margin + rng.Next(height - 2 * margin);
                int idx = y * width + x;
                int ax = x + (int)(LightDx * step);
                int ay = y + (int)(LightDy * step);
                if ((uint)ax >= (uint)width || (uint)ay >= (uint)height) continue;
                int aIdx = ay * width + ax;
                float a = smooth[idx], b = smooth[aIdx];
                if (a <= 0f || b <= 0f) continue;
                sum += (b - a) / step;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }
    }
}
