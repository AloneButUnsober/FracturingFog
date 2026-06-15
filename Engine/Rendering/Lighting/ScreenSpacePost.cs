// ScreenSpacePost.cs
//
// Post-process passes that operate on the calculator's final color buffer
// + per-pixel G-buffer (depth + normal). Phase 4 ships SSAO; later phases
// (5 vol fog, 7 bloom/tonemap, 15 lens/chroma, 23 edge) plug additional
// passes into this same module.
//
// G-buffer contract
//   depthBuffer  : float[ width * height ]
//                  per pixel: ray-total-T at hit, +infinity for sky/miss.
//   normalBuffer : float[ 3 * width * height ]
//                  per pixel (n*3): nx, ny, nz. Zero vector for sky.
//
// Calculators allocate these alongside ColorBuffer in Resize() and the shared
// ShadingPipeline.WriteHit() helper fills them at hit time. Sky pixels stay
// at the default (+inf, 0, 0, 0) so SSAO automatically skips them.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Rendering.Lighting;

public static class ScreenSpacePost
{
    /// <summary>
    /// Sentinel "no surface" depth value. Sky / ray-miss pixels write this
    /// into the depth buffer so post-passes know to skip them.
    /// </summary>
    public const float DepthMiss = float.PositiveInfinity;

    /// <summary>
    /// Phase 8b — 2D SSAO. Synthesises a depth buffer from the smooth iteration
    /// count of an escape-time fractal and runs the standard <see cref="ApplySsao"/>
    /// pipeline on it. Pixels inside the set (iter ≥ maxIt) are treated as sky
    /// (depth = <see cref="DepthMiss"/>) so SSAO doesn't bleed into the interior.
    ///
    /// Synthetic depth: <c>smooth / maxIt</c> rescaled by <see cref="LightingFxData.SsaoRadius"/>
    /// so the existing radius knob still controls feel. Pixels close to the
    /// boundary (high smooth) become "deep" so the Vogel-disk samples around
    /// thin filaments register as occluders, darkening the filament neighbourhood.
    ///
    /// No-op when <c>SsaoSamples == 0</c> — bit-identical to Phase 7.
    /// </summary>
    public static void ApplySsao2D(
        uint[] colorBuffer,
        float[] smoothBuffer,
        int[] iterBuffer,
        int maxIt,
        int width, int height,
        in LightingFxData fx)
    {
        if (fx.SsaoSamples <= 0) return;
        int n = width * height;
        if (colorBuffer.Length < n || smoothBuffer.Length < n || iterBuffer.Length < n) return;
        if (maxIt <= 0) return;

        // Allocate scratch depth buffer. Scale smooth into [0, ~1] world units
        // so SsaoRadius is interpreted at the same magnitude as the 3D path
        // (where depth is ray-T in world units).
        var depthBuf = new float[n];
        var normalBuf = new float[3 * n]; // unused by current ApplySsao loop
        double invMaxIt = 1.0 / maxIt;
        for (int i = 0; i < n; i++)
        {
            // In-set pixels — treat as sky so SSAO leaves them alone.
            if (iterBuffer[i] >= maxIt)
            {
                depthBuf[i] = DepthMiss;
                continue;
            }
            // Exterior: deep filament = high smooth → high depth.
            double t = Math.Clamp(smoothBuffer[i] * invMaxIt, 0.0, 1.0);
            depthBuf[i] = (float)t;
        }
        ApplySsao(colorBuffer, depthBuf, normalBuf, width, height, in fx);
    }

    /// <summary>
    /// Apply screen-space AO over the G-buffer. Modulates ColorBuffer in
    /// place. Uses a fixed 16-sample Vogel-disk pattern rotated per-pixel
    /// by an interleaved-gradient hash so adjacent pixels sample different
    /// offsets (cheap noise pattern that the bilateral blur cleans up).
    /// </summary>
    /// <param name="colorBuffer">BGRA pixel array — modulated in place.</param>
    /// <param name="depthBuffer">Per-pixel ray-total-T. <see cref="DepthMiss"/> = sky.</param>
    /// <param name="normalBuffer">Packed nx,ny,nz per pixel (3 floats / px).</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="fx">Active LightingFxData. SsaoSamples=0 = no-op.</param>
    public static void ApplySsao(
        uint[] colorBuffer,
        float[] depthBuffer,
        float[] normalBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        if (fx.SsaoSamples <= 0) return;
        if (colorBuffer.Length < width * height) return;
        if (depthBuffer.Length < width * height) return;

        int samples = Math.Min(fx.SsaoSamples, 64);
        double radiusPixels = fx.SsaoRadius * Math.Min(width, height);
        double strength = fx.SsaoStrength;
        // Copy `in` fields to locals so lambdas can capture them. C# disallows
        // closures capturing `in` parameter scopes.
        double worldRadius = fx.SsaoRadius;

        // Phase 12 — GPU dispatcher. Single fused kernel (sample + composite,
        // no blur — adequate quality at the higher sample counts a GPU run
        // is happy with). Falls back to CPU below on any failure.
        if (fx.UseGpuPost)
        {
            if (GpuPostKernels.TryApplySsao(
                    colorBuffer, depthBuffer, width, height,
                    samples, radiusPixels, strength, worldRadius))
                return;
        }

        // Raw AO factor per pixel — bilateral-blurred next pass.
        var aoBuffer = new float[width * height];

        // Vogel-disk sample offsets (unit-disk Halton-spiral). Pre-compute up
        // to MAX so we don't allocate per pixel. golden_angle = 137.508 deg.
        const int MaxSamples = 64;
        var offsetsX = new double[MaxSamples];
        var offsetsY = new double[MaxSamples];
        const double GoldenAngle = 2.39996323;
        for (int s = 0; s < MaxSamples; s++)
        {
            double r = Math.Sqrt((s + 0.5) / MaxSamples);
            double a = s * GoldenAngle;
            offsetsX[s] = r * Math.Cos(a);
            offsetsY[s] = r * Math.Sin(a);
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float d0 = depthBuffer[idx];
                if (float.IsPositiveInfinity(d0))
                {
                    aoBuffer[idx] = 1.0f;
                    continue;
                }

                // Per-pixel rotation: interleaved-gradient hash (Jorge Jimenez).
                // Cheap, decorrelates adjacent samples.
                double rot = (52.9829189 * ((0.06711056 * x + 0.00583715 * y) % 1.0)) % 1.0;
                double cosR = Math.Cos(rot * Math.PI * 2.0);
                double sinR = Math.Sin(rot * Math.PI * 2.0);

                double occl = 0;
                int valid = 0;
                for (int s = 0; s < samples; s++)
                {
                    double ox = offsetsX[s] * cosR - offsetsY[s] * sinR;
                    double oy = offsetsX[s] * sinR + offsetsY[s] * cosR;
                    int sx = (int)(x + ox * radiusPixels);
                    int sy = (int)(y + oy * radiusPixels);
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                    float dS = depthBuffer[sy * width + sx];
                    if (float.IsPositiveInfinity(dS)) continue;
                    valid++;
                    double delta = d0 - dS;
                    // Positive delta = sample point is closer to camera than
                    // current pixel = potential occluder. Window the contribution
                    // by world-space radius so far-away surfaces don't count.
                    if (delta > 0 && delta < worldRadius)
                    {
                        // Weight by closeness to sweet-spot. Smoothstep keeps
                        // edge-of-disk samples from disappearing abruptly.
                        double w = 1.0 - delta / worldRadius;
                        occl += w;
                    }
                }
                double ao = valid > 0 ? 1.0 - strength * (occl / valid) : 1.0;
                aoBuffer[idx] = (float)Math.Clamp(ao, 0, 1);
            }
        });

        // 3×3 bilateral blur (depth-aware) to remove disk-sampling noise.
        var aoBlur = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float d0 = depthBuffer[idx];
                if (float.IsPositiveInfinity(d0))
                {
                    aoBlur[idx] = 1.0f;
                    continue;
                }
                double sum = 0, wSum = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy; if (yy < 0 || yy >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx; if (xx < 0 || xx >= width) continue;
                        int ii = yy * width + xx;
                        float dN = depthBuffer[ii];
                        if (float.IsPositiveInfinity(dN)) continue;
                        double depthW = Math.Exp(-Math.Abs(dN - d0) * 8.0);
                        sum += aoBuffer[ii] * depthW;
                        wSum += depthW;
                    }
                }
                aoBlur[idx] = (float)(wSum > 0 ? sum / wSum : 1.0);
            }
        });

        // Composite. SSAO multiplies the final color; strength gates the
        // effect so SsaoStrength=0 leaves pixels untouched even if SsaoSamples>0.
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (float.IsPositiveInfinity(depthBuffer[idx])) continue;
                float ao = aoBlur[idx];
                uint c = colorBuffer[idx];
                byte R = (byte)Math.Clamp(((c >> 16) & 0xFF) * ao, 0, 255);
                byte G = (byte)Math.Clamp(((c >> 8) & 0xFF) * ao, 0, 255);
                byte B = (byte)Math.Clamp((c & 0xFF) * ao, 0, 255);
                colorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }

    /// <summary>
    /// Initialise depth + normal buffers for a fresh render. Sky / ray-miss
    /// pixels must read as <see cref="DepthMiss"/>; calculators overwrite hit
    /// pixels during the raymarch loop.
    /// </summary>
    public static void ClearGBuffer(float[]? depthBuffer, float[]? normalBuffer)
    {
        if (depthBuffer is not null)
            Array.Fill(depthBuffer, DepthMiss);
        if (normalBuffer is not null)
            Array.Clear(normalBuffer, 0, normalBuffer.Length);
    }

    /// <summary>
    /// Initialise HDR buffer to NaN sentinels. Sky pixels stay NaN so the
    /// tonemap pass knows to use the calculator's ColorBuffer sky value rather
    /// than processing a zero HDR value.
    /// </summary>
    public static void ClearHdrBuffer(float[]? hdrBuffer)
    {
        if (hdrBuffer is null) return;
        Array.Fill(hdrBuffer, float.NaN);
    }

    /// <summary>
    /// Apply HDR tone-map + bloom pipeline. Reads the float HDR G-buffer
    /// populated by <see cref="ShadingPipeline.Shade"/>, runs threshold-bloom
    /// + 3-mip separable Gaussian blur, applies the chosen tone-map operator,
    /// gamma-encodes, and writes the byte ColorBuffer in place.
    ///
    /// Sky pixels (HDR = NaN sentinel) pass through unchanged from the
    /// calculator's prewritten ColorBuffer sky value — they don't emit bloom
    /// and aren't tonemapped (the calculator's sky path already produces
    /// display-ready bytes).
    ///
    /// No-op when <c>ToneMap == None &amp;&amp; BloomStrength == 0</c>.
    /// </summary>
    public static void ApplyToneMapBloom(
        uint[] colorBuffer,
        float[] hdrBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        bool wantBloom = fx.BloomStrength > 0;
        bool wantTonemap = fx.ToneMap != ToneMapOperator.None;
        bool wantLens = fx.ChromaticAberration > 0 || fx.LensDistortion != 0
                     || fx.Vignette > 0 || fx.LensTangentialX != 0
                     || fx.LensTangentialY != 0
                     || (fx.AnamorphicSqueeze != 0 && fx.AnamorphicSqueeze != 1.0);
        bool wantHud = fx.DebugHudFlags != 0;
        if (!wantBloom && !wantTonemap && !wantLens && !wantHud) return;

        int n = width * height;
        if (colorBuffer.Length < n) return;
        if ((wantBloom || wantTonemap) && hdrBuffer.Length < 3 * n) return;

        // Phase 15 / Phase 19 short-circuit: skip the HDR pipeline entirely if
        // only post-byte stages (lens, HUD) are active.
        if (!wantBloom && !wantTonemap)
        {
            if (wantLens) ApplyLensPost(colorBuffer, width, height, fx);
            if (wantHud)  ApplyDebugHud(colorBuffer, width, height, fx);
            return;
        }

        // ── Bloom build ────────────────────────────────────────────────────
        // 3-mip pyramid. Bright-pass at full res; blur + downsample successively;
        // bilinear upsample-add back to a single full-res emissive buffer.
        float[] emissive = new float[3 * n];
        double threshByteScale = fx.BloomThreshold * 255.0; // operator works on byte-scale luma

        // Step 1 — threshold pass to emissive (full res). Sky pixels skipped.
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int i3 = i * 3;
                float hr = hdrBuffer[i3];
                if (float.IsNaN(hr)) { emissive[i3] = 0; emissive[i3+1] = 0; emissive[i3+2] = 0; continue; }
                float hg = hdrBuffer[i3 + 1];
                float hb = hdrBuffer[i3 + 2];
                double luma = 0.299 * hr + 0.587 * hg + 0.114 * hb;
                if (luma > threshByteScale)
                {
                    emissive[i3]     = hr;
                    emissive[i3 + 1] = hg;
                    emissive[i3 + 2] = hb;
                }
            }
        });

        // Step 2 — 3 mip levels via box-downsample then 5-tap separable Gaussian.
        float[] blurred = wantBloom ? BuildBloomPyramid(emissive, width, height) : emissive;

        // ── Composite + tone map + gamma ──────────────────────────────────
        double expo = fx.Exposure;
        var op = fx.ToneMap;
        double bloomStr = fx.BloomStrength;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int i3 = i * 3;
                float hr = hdrBuffer[i3];
                if (float.IsNaN(hr)) continue;  // sky: keep ColorBuffer byte
                float hg = hdrBuffer[i3 + 1];
                float hb = hdrBuffer[i3 + 2];

                if (wantBloom)
                {
                    hr += (float)(blurred[i3]     * bloomStr);
                    hg += (float)(blurred[i3 + 1] * bloomStr);
                    hb += (float)(blurred[i3 + 2] * bloomStr);
                }

                // Convert byte-scale HDR to linear [0..N] with exposure.
                double linR = hr / 255.0 * expo;
                double linG = hg / 255.0 * expo;
                double linB = hb / 255.0 * expo;

                // Tone-map.
                double tmR, tmG, tmB;
                switch (op)
                {
                    case ToneMapOperator.Reinhard:
                        tmR = linR / (1.0 + linR);
                        tmG = linG / (1.0 + linG);
                        tmB = linB / (1.0 + linB);
                        break;
                    case ToneMapOperator.ReinhardExtended:
                    {
                        const double Lw2 = 16.0; // white² = 4²
                        tmR = linR * (1.0 + linR / Lw2) / (1.0 + linR);
                        tmG = linG * (1.0 + linG / Lw2) / (1.0 + linG);
                        tmB = linB * (1.0 + linB / Lw2) / (1.0 + linB);
                        break;
                    }
                    case ToneMapOperator.Aces:
                        (tmR, tmG, tmB) = AcesFilmic(linR, linG, linB);
                        break;
                    default:
                        tmR = linR; tmG = linG; tmB = linB;
                        break;
                }

                if (wantTonemap)
                {
                    // Gamma 2.2 encode after tonemap; matches sRGB approximation
                    // closely enough for free-form HDR pipelines.
                    tmR = Math.Pow(Math.Clamp(tmR, 0, 1), 1.0 / 2.2);
                    tmG = Math.Pow(Math.Clamp(tmG, 0, 1), 1.0 / 2.2);
                    tmB = Math.Pow(Math.Clamp(tmB, 0, 1), 1.0 / 2.2);
                }
                else
                {
                    // No tonemap, just bloom + clamp. Keep byte-scale.
                    tmR = Math.Clamp(linR, 0, 1);
                    tmG = Math.Clamp(linG, 0, 1);
                    tmB = Math.Clamp(linB, 0, 1);
                }

                byte R = (byte)Math.Clamp(tmR * 255.0, 0, 255);
                byte G = (byte)Math.Clamp(tmG * 255.0, 0, 255);
                byte B = (byte)Math.Clamp(tmB * 255.0, 0, 255);
                colorBuffer[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });

        // Phase 15 — lens distortion + chromatic aberration as final stage.
        // Runs after tonemap so the warp + colour fringing operates on display-
        // ready bytes (CA shifts post-gamma, which is the look most lens sims
        // target — pre-gamma CA looks washed out).
        if (wantLens)
        {
            ApplyLensPost(colorBuffer, width, height, fx);
        }

        // Phase 19 — debug HUD overlay. Drawn last so HUD pixels survive the
        // lens warp + tonemap stages above (HUD lives in screen space, not
        // scene space).
        if (wantHud)
        {
            ApplyDebugHud(colorBuffer, width, height, fx);
        }
    }

    /// <summary>
    /// Phase 15 — barrel/pincushion lens warp + radial RGB-fringe chromatic
    /// aberration. Operates on the display-ready ColorBuffer. Allocates one
    /// scratch copy of the buffer because every output pixel reads from a
    /// (possibly off-grid) source coordinate; we can't sample-in-place.
    ///
    /// Conventions
    ///   LensDistortion > 0 → pincushion (corners pull toward center).
    ///   LensDistortion &lt; 0 → barrel (corners push out from center).
    ///   ChromaticAberration in pixels at the image corner — interior pixels
    ///   scale linearly with radius so the centre stays sharp.
    ///
    /// Sky pixels go through the same warp (no depth check) — that's the
    /// physically correct behaviour for a lens: it warps everything in front
    /// of it, sky included.
    /// </summary>
    public static void ApplyLensPost(
        uint[] colorBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        bool wantLens = fx.ChromaticAberration > 0 || fx.LensDistortion != 0
                     || fx.Vignette > 0 || fx.LensTangentialX != 0
                     || fx.LensTangentialY != 0
                     || (fx.AnamorphicSqueeze != 0 && fx.AnamorphicSqueeze != 1.0);
        if (!wantLens) return;
        int n = width * height;
        if (colorBuffer.Length < n) return;

        // Snapshot — sample source from this, write to colorBuffer.
        uint[] src = (uint[])colorBuffer.Clone();

        double halfW = width * 0.5;
        double halfH = height * 0.5;
        // Normalize so the shorter image edge maps to r = 1. Keeps the warp
        // coefficient resolution-independent (k = 0.1 looks the same at 800px
        // and 1600px).
        double invShort = 1.0 / Math.Min(halfW, halfH);
        double k = fx.LensDistortion;
        double caPx = fx.ChromaticAberration;
        // Phase 15b knobs. AnamorphicSqueeze is interpreted post-radial-warp:
        // wuY *= 1/squeeze so that squeeze>1 stretches vertically.
        double vignette = Math.Clamp(fx.Vignette, 0.0, 1.0);
        double p1 = fx.LensTangentialX;
        double p2 = fx.LensTangentialY;
        double anaY = fx.AnamorphicSqueeze == 0 ? 1.0 : 1.0 / fx.AnamorphicSqueeze;

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double uX = (x - halfW) * invShort;
                double uY = (y - halfH) * invShort;
                double r2 = uX * uX + uY * uY;
                double r = Math.Sqrt(r2);

                // Radial distortion model: r' = r * (1 + k r²).
                double warp = 1.0 + k * r2;
                double wuX = uX * warp;
                double wuY = uY * warp * anaY;

                // Brown decentring (tangential) p1/p2 — Phase 15b.
                if (p1 != 0 || p2 != 0)
                {
                    wuX += 2.0 * p1 * uX * uY + p2 * (r2 + 2.0 * uX * uX);
                    wuY += p1 * (r2 + 2.0 * uY * uY) + 2.0 * p2 * uX * uY;
                }

                // Convert warped uv back to pixel coords (centre = halfW/halfH).
                double bx = wuX / invShort + halfW;
                double by = wuY / invShort + halfH;

                // Chromatic offset along radial direction; magnitude scales
                // linearly with r so the centre stays sharp. R outward, B
                // inward (matches real-glass dispersion sign on most lenses).
                double dirX = 0, dirY = 0;
                if (r > 1e-12) { dirX = uX / r; dirY = uY / r; }
                double rOff = caPx * r;

                double rx = bx + dirX * rOff;
                double ry = by + dirY * rOff;
                double bx2 = bx - dirX * rOff;
                double by2 = by - dirY * rOff;

                uint R = SampleChannelBilinear(src, width, height, rx,  ry,  16);
                uint G = SampleChannelBilinear(src, width, height, bx,  by,  8);
                uint B = SampleChannelBilinear(src, width, height, bx2, by2, 0);

                // Phase 15b — vignette. cos⁴-style radial darken applied to
                // the final (post-chromatic) RGB. r ranges 0 at centre to
                // ~1.41 at the warped image corner (sqrt(2)). Mapped so the
                // corner reaches (1 − vignette) of full brightness, centre
                // reaches 1.0 of full brightness.
                if (vignette > 0)
                {
                    double vr = Math.Min(r, 1.0);
                    double fall = 1.0 - vr * vr;
                    fall *= fall; // cos⁴-ish
                    double mul = 1.0 - vignette * (1.0 - fall);
                    R = (uint)Math.Clamp(R * mul, 0, 255);
                    G = (uint)Math.Clamp(G * mul, 0, 255);
                    B = (uint)Math.Clamp(B * mul, 0, 255);
                }

                colorBuffer[y * width + x] =
                    0xFF000000u | (R << 16) | (G << 8) | B;
            }
        });
    }

    /// <summary>Bilinear-sample a single 8-bit channel from a packed BGRA
    /// buffer, returning the channel value clamped to [0, 255]. <paramref
    /// name="shift"/> selects which channel: 16 = R, 8 = G, 0 = B.</summary>
    private static uint SampleChannelBilinear(
        uint[] src, int w, int h, double x, double y, int shift)
    {
        if (x < 0) x = 0; else if (x > w - 1) x = w - 1;
        if (y < 0) y = 0; else if (y > h - 1) y = h - 1;
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, w - 1);
        int y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0;
        double fy = y - y0;
        double c00 = (src[y0 * w + x0] >> shift) & 0xFFu;
        double c10 = (src[y0 * w + x1] >> shift) & 0xFFu;
        double c01 = (src[y1 * w + x0] >> shift) & 0xFFu;
        double c11 = (src[y1 * w + x1] >> shift) & 0xFFu;
        double v = c00 * (1 - fx) * (1 - fy)
                 + c10 * fx       * (1 - fy)
                 + c01 * (1 - fx) * fy
                 + c11 * fx       * fy;
        if (v < 0) v = 0; else if (v > 255) v = 255;
        return (uint)v;
    }

    /// <summary>
    /// Phase 19 — debug HUD overlay. Pure visual indicators (no font/text):
    ///   bit 0 (0x1) — light-direction compass in the top-right corner.
    ///       Top-down projection of each active light's world direction onto
    ///       the XZ plane, drawn as a colored line from the compass centre.
    ///       Line length scales with light intensity; line colour matches the
    ///       light colour. White circle outline + crosshair frame the compass.
    ///   bit 1 (0x2) — strength bars along the bottom edge. Five bars in a
    ///       fixed order: Ambient, AO, Fog (clamped to 1), Reflection, Caustics.
    ///       Distinct hues (yellow / cyan / white / orange / pink) so the
    ///       reader can disambiguate at a glance; red/green deliberately
    ///       avoided for colourblind safety.
    ///   bit 2 (0x4) — scene-time tick wheel in the top-left corner. Single
    ///       white hand rotates by <c>SceneTime mod 2π</c> so animation runs
    ///       are observable without scrubbing through a parameter panel.
    ///
    /// All overlays draw on a 50% black backdrop so they remain readable
    /// against bright fractals. Small renders (&lt; 128px in either axis) skip
    /// every overlay — the HUD would dominate the image otherwise.
    /// </summary>
    public static void ApplyDebugHud(
        uint[] colorBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        int flags = fx.DebugHudFlags;
        if (flags == 0) return;
        if (colorBuffer.Length < width * height) return;
        if (width < 128 || height < 128) return;

        if ((flags & 0x1) != 0) DrawLightCompass(colorBuffer, width, height, fx);
        if ((flags & 0x2) != 0) DrawParamBars(colorBuffer, width, height, fx);
        if ((flags & 0x4) != 0) DrawTimeClock(colorBuffer, width, height, fx);
    }

    private static void DrawLightCompass(
        uint[] buf, int w, int h, in LightingFxData fx)
    {
        const int box = 80;
        int x0 = w - box - 8;
        int y0 = 8;
        int cx = x0 + box / 2;
        int cy = y0 + box / 2;
        int radius = box / 2 - 4;
        FillRectAlpha(buf, w, h, x0, y0, x0 + box, y0 + box, 0xFF000000u, 0.5);
        DrawCircleOutline(buf, w, h, cx, cy, radius, 0xFFFFFFFFu);
        // Crosshair (4 small ticks).
        DrawLine(buf, w, h, cx, cy - 3, cx, cy + 3, 0xFFFFFFFFu);
        DrawLine(buf, w, h, cx - 3, cy, cx + 3, cy, 0xFFFFFFFFu);

        // Apply Phase 18 orbit so the compass tracks the animated direction.
        double orbitT = fx.SceneTime * fx.LightOrbitSpeed;
        DrawLightTick(buf, w, h, cx, cy, radius,
            fx.Light1.Theta + orbitT,        fx.Light1.Phi,
            fx.Light1.Intensity, fx.Light1.Color);
        DrawLightTick(buf, w, h, cx, cy, radius,
            fx.Light2.Theta + orbitT * 0.7,  fx.Light2.Phi,
            fx.Light2.Intensity, fx.Light2.Color);
        DrawLightTick(buf, w, h, cx, cy, radius,
            fx.Light3.Theta + orbitT * 1.3,  fx.Light3.Phi,
            fx.Light3.Intensity, fx.Light3.Color);
    }

    private static void DrawLightTick(
        uint[] buf, int w, int h,
        int cx, int cy, int radius,
        double theta, double phi, double intensity, uint color)
    {
        if (intensity <= 0) return;
        // Top-down projection: world dir = (sin(phi)·cos(theta), cos(phi),
        // sin(phi)·sin(theta)). Use X and Z components as compass axes; +Z
        // maps to compass up, so screen Y is negated.
        double sinPhi = Math.Sin(phi);
        double dx = sinPhi * Math.Cos(theta);
        double dz = sinPhi * Math.Sin(theta);
        double scale = Math.Clamp(intensity, 0.0, 1.5) * radius;
        int ex = cx + (int)Math.Round(dx * scale);
        int ey = cy - (int)Math.Round(dz * scale);
        DrawLine(buf, w, h, cx, cy, ex, ey, color);
        // Endpoint dot — 3×3 square in the same colour for legibility.
        FillRectAlpha(buf, w, h, ex - 1, ey - 1, ex + 2, ey + 2, color, 1.0);
    }

    private static void DrawParamBars(
        uint[] buf, int w, int h, in LightingFxData fx)
    {
        const int barW = 60, barH = 8, gap = 6;
        const int count = 5;
        int totalW = barW * count + gap * (count - 1);
        int x0 = 8;
        int y0 = h - barH - 8;
        // Backdrop.
        FillRectAlpha(buf, w, h, x0 - 4, y0 - 4, x0 + totalW + 4, y0 + barH + 4,
                      0xFF000000u, 0.5);

        // Order: Ambient, AO, Fog (capped 1), Reflection, Caustics.
        // Distinct hues — no red, no pure green (colourblind-safe).
        double[] vals = {
            fx.AmbientStrength,
            fx.AoStrength * (fx.AoSamples > 0 ? 1.0 : 0.0),
            Math.Min(fx.FogDensity, 1.0),
            fx.ReflectionStrength,
            fx.CausticsStrength,
        };
        uint[] colors = {
            0xFFFFCC00u, // amber
            0xFF00CCFFu, // cyan
            0xFFFFFFFFu, // white
            0xFFFF8800u, // orange
            0xFFFF88CCu, // pink
        };
        for (int i = 0; i < count; i++)
        {
            int bx = x0 + i * (barW + gap);
            // Empty channel — dim grey backdrop.
            FillRectAlpha(buf, w, h, bx, y0, bx + barW, y0 + barH,
                          0xFF404040u, 1.0);
            double v = Math.Clamp(vals[i], 0.0, 1.0);
            int fillW = (int)Math.Round(v * barW);
            if (fillW > 0)
                FillRectAlpha(buf, w, h, bx, y0, bx + fillW, y0 + barH,
                              colors[i], 1.0);
        }
    }

    private static void DrawTimeClock(
        uint[] buf, int w, int h, in LightingFxData fx)
    {
        const int box = 40;
        int x0 = 8;
        int y0 = 8;
        int cx = x0 + box / 2;
        int cy = y0 + box / 2;
        int radius = box / 2 - 2;
        FillRectAlpha(buf, w, h, x0, y0, x0 + box, y0 + box, 0xFF000000u, 0.5);
        DrawCircleOutline(buf, w, h, cx, cy, radius, 0xFFFFFFFFu);
        // Hand: angle = SceneTime mod 2π. Length = radius - 2.
        double a = fx.SceneTime % (2.0 * Math.PI);
        int ex = cx + (int)Math.Round(Math.Cos(a) * (radius - 2));
        int ey = cy - (int)Math.Round(Math.Sin(a) * (radius - 2));
        DrawLine(buf, w, h, cx, cy, ex, ey, 0xFFFFFFFFu);
    }

    // ── Tiny pixel-pusher primitives ──────────────────────────────────

    private static void FillRectAlpha(
        uint[] buf, int w, int h,
        int x0, int y0, int x1, int y1,
        uint color, double alpha)
    {
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 > w) x1 = w;
        if (y1 > h) y1 = h;
        if (x0 >= x1 || y0 >= y1) return;
        double a = Math.Clamp(alpha, 0.0, 1.0);
        double cR = (color >> 16) & 0xFF;
        double cG = (color >>  8) & 0xFF;
        double cB =  color        & 0xFF;
        for (int y = y0; y < y1; y++)
        {
            int row = y * w;
            for (int x = x0; x < x1; x++)
            {
                uint d = buf[row + x];
                double dR = (d >> 16) & 0xFF;
                double dG = (d >>  8) & 0xFF;
                double dB =  d        & 0xFF;
                byte R = (byte)(dR * (1 - a) + cR * a);
                byte G = (byte)(dG * (1 - a) + cG * a);
                byte B = (byte)(dB * (1 - a) + cB * a);
                buf[row + x] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }
    }

    private static void DrawLine(
        uint[] buf, int w, int h,
        int x0, int y0, int x1, int y1,
        uint color)
    {
        // Bresenham. No clip-line trickery — clamp inside the inner loop.
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        const int Limit = 4096; // sanity bound; HUD lines are short
        for (int i = 0; i < Limit; i++)
        {
            if ((uint)x < (uint)w && (uint)y < (uint)h)
                buf[y * w + x] = color;
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    private static void DrawCircleOutline(
        uint[] buf, int w, int h,
        int cx, int cy, int r, uint color)
    {
        // Midpoint circle algorithm. 8-way symmetry.
        int x = r, y = 0, err = 0;
        while (x >= y)
        {
            PlotSym(buf, w, h, cx, cy, x, y, color);
            y++;
            if (err <= 0) { err += 2 * y + 1; }
            else          { x--; err += 2 * (y - x) + 1; }
        }
    }

    private static void PlotSym(
        uint[] buf, int w, int h, int cx, int cy, int x, int y, uint color)
    {
        Plot(buf, w, h, cx + x, cy + y, color);
        Plot(buf, w, h, cx - x, cy + y, color);
        Plot(buf, w, h, cx + x, cy - y, color);
        Plot(buf, w, h, cx - x, cy - y, color);
        Plot(buf, w, h, cx + y, cy + x, color);
        Plot(buf, w, h, cx - y, cy + x, color);
        Plot(buf, w, h, cx + y, cy - x, color);
        Plot(buf, w, h, cx - y, cy - x, color);
    }

    private static void Plot(uint[] buf, int w, int h, int x, int y, uint color)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        buf[y * w + x] = color;
    }

    /// <summary>
    /// Build a 3-mip bloom pyramid: downsample-box at each level + a 5-tap
    /// separable Gaussian blur, then upsample-add back to full resolution.
    /// Returned buffer is full-res (3 floats / pixel) and may be added to the
    /// HDR composite scaled by BloomStrength.
    /// </summary>
    private static float[] BuildBloomPyramid(float[] src, int w, int h)
    {
        // Levels 1 (½) and 2 (¼). Skip a third level — diminishing returns at
        // typical UI resolutions and the pass cost stays bounded.
        int w1 = Math.Max(1, w / 2);
        int h1 = Math.Max(1, h / 2);
        int w2 = Math.Max(1, w / 4);
        int h2 = Math.Max(1, h / 4);

        float[] mip1 = DownsampleAndBlur(src, w, h, w1, h1);
        float[] mip2 = DownsampleAndBlur(mip1, w1, h1, w2, h2);

        // Upsample-add: bilinear sample mips, accumulate into a full-res
        // emissive buffer. Levels weighted by 1, 0.5, 0.25 so finer mips
        // dominate near-light pixels and coarser mips spread the halo.
        float[] outBuf = new float[3 * w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int i3 = i * 3;
                outBuf[i3]     = src[i3];
                outBuf[i3 + 1] = src[i3 + 1];
                outBuf[i3 + 2] = src[i3 + 2];
                AddBilinear(outBuf, i3, mip1, w1, h1, x * 0.5,  y * 0.5,  0.7);
                AddBilinear(outBuf, i3, mip2, w2, h2, x * 0.25, y * 0.25, 0.5);
            }
        });
        return outBuf;
    }

    private static float[] DownsampleAndBlur(float[] src, int srcW, int srcH, int dstW, int dstH)
    {
        // Box downsample
        float[] downA = new float[3 * dstW * dstH];
        Parallel.For(0, dstH, y =>
        {
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = Math.Min(srcW - 1, x * 2);
                int sy0 = Math.Min(srcH - 1, y * 2);
                int sx1 = Math.Min(srcW - 1, sx0 + 1);
                int sy1 = Math.Min(srcH - 1, sy0 + 1);
                int i00 = (sy0 * srcW + sx0) * 3;
                int i10 = (sy0 * srcW + sx1) * 3;
                int i01 = (sy1 * srcW + sx0) * 3;
                int i11 = (sy1 * srcW + sx1) * 3;
                int d = (y * dstW + x) * 3;
                downA[d]     = 0.25f * (src[i00]     + src[i10]     + src[i01]     + src[i11]);
                downA[d + 1] = 0.25f * (src[i00 + 1] + src[i10 + 1] + src[i01 + 1] + src[i11 + 1]);
                downA[d + 2] = 0.25f * (src[i00 + 2] + src[i10 + 2] + src[i01 + 2] + src[i11 + 2]);
            }
        });
        // 5-tap separable Gaussian (sigma ≈ 1). Horizontal then vertical.
        // Kernel [0.06, 0.244, 0.392, 0.244, 0.06].
        float[] tmp = new float[3 * dstW * dstH];
        float[] kernel = { 0.06f, 0.244f, 0.392f, 0.244f, 0.06f };
        Parallel.For(0, dstH, y =>
        {
            for (int x = 0; x < dstW; x++)
            {
                int o = (y * dstW + x) * 3;
                float r = 0, g = 0, b = 0;
                for (int t = -2; t <= 2; t++)
                {
                    int sx = Math.Clamp(x + t, 0, dstW - 1);
                    int si = (y * dstW + sx) * 3;
                    float kv = kernel[t + 2];
                    r += downA[si]     * kv;
                    g += downA[si + 1] * kv;
                    b += downA[si + 2] * kv;
                }
                tmp[o] = r; tmp[o + 1] = g; tmp[o + 2] = b;
            }
        });
        float[] outBuf = new float[3 * dstW * dstH];
        Parallel.For(0, dstH, y =>
        {
            for (int x = 0; x < dstW; x++)
            {
                int o = (y * dstW + x) * 3;
                float r = 0, g = 0, b = 0;
                for (int t = -2; t <= 2; t++)
                {
                    int sy = Math.Clamp(y + t, 0, dstH - 1);
                    int si = (sy * dstW + x) * 3;
                    float kv = kernel[t + 2];
                    r += tmp[si]     * kv;
                    g += tmp[si + 1] * kv;
                    b += tmp[si + 2] * kv;
                }
                outBuf[o] = r; outBuf[o + 1] = g; outBuf[o + 2] = b;
            }
        });
        return outBuf;
    }

    private static void AddBilinear(float[] dst, int dstI3, float[] src, int sw, int sh, double sx, double sy, double weight)
    {
        if (sw <= 0 || sh <= 0) return;
        int x0 = (int)Math.Clamp(Math.Floor(sx), 0, sw - 1);
        int y0 = (int)Math.Clamp(Math.Floor(sy), 0, sh - 1);
        int x1 = Math.Min(x0 + 1, sw - 1);
        int y1 = Math.Min(y0 + 1, sh - 1);
        double fx = Math.Clamp(sx - x0, 0, 1);
        double fy = Math.Clamp(sy - y0, 0, 1);
        int i00 = (y0 * sw + x0) * 3;
        int i10 = (y0 * sw + x1) * 3;
        int i01 = (y1 * sw + x0) * 3;
        int i11 = (y1 * sw + x1) * 3;
        double w00 = (1 - fx) * (1 - fy) * weight;
        double w10 = fx       * (1 - fy) * weight;
        double w01 = (1 - fx) * fy       * weight;
        double w11 = fx       * fy       * weight;
        dst[dstI3]     += (float)(src[i00]     * w00 + src[i10]     * w10 + src[i01]     * w01 + src[i11]     * w11);
        dst[dstI3 + 1] += (float)(src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11);
        dst[dstI3 + 2] += (float)(src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11);
    }

    /// <summary>
    /// Phase 21 — hex-bokeh depth-of-field. Per-pixel CoC blur with a hexagonal
    /// sample pattern that gives lens-shaped bokeh discs around bright highlights.
    /// Operates on the display-ready ColorBuffer; the host should call this
    /// <em>before</em> <see cref="ApplyToneMapBloom"/> if both are active so the
    /// blur sees pre-tonemap luminance (cleaner highlight bokeh). The engine
    /// slice here doesn't enforce that ordering — host wiring deferred to 21b.
    ///
    /// CoC model (thin-lens proxy)
    ///   <c>coc_px = aperture · |depth - focus| / depth · shortEdge</c>
    ///   The <c>shortEdge</c> term makes the look resolution-independent so a
    ///   given aperture value renders the same look at 800px and 1600px.
    ///
    /// Sample pattern
    ///   Hexagonal ring layout — 1 centre sample + 6r samples per ring r. Picks
    ///   ring count from DofSamples so values 7 / 19 / 37 / 61 hit ring boundaries
    ///   exactly; other values round up to the next ring.
    ///
    /// Bleed control
    ///   Naive gather smears sharp foreground over blurred background. Weight
    ///   each neighbour by whether its own CoC reaches the centre pixel:
    ///     • neighbour in front: contribute only if cocN > distFromCentre
    ///     • neighbour behind / sky: always contribute (it's the BG we want to blur in)
    ///   Imperfect (true scatter-based DoF stays cleaner at silhouettes) but
    ///   visibly better than uniform gather.
    ///
    /// Limitations / Phase 21b candidates
    ///   • Gather not scatter — hex rim on a bokeh disc reads as faceted at
    ///     very large CoC. Three-pass hex blur (McIntosh 2012) would fix this.
    ///   • Operates on byte-display values; bright sources clip to white before
    ///     the blur sees them so bokeh discs lack the bright-pixel "boost" you
    ///     get from pre-tonemap HDR DoF. Wire after Shade + before ToneMapBloom
    ///     once host plumbing lands.
    /// </summary>
    /// <param name="colorBuffer">BGRA pixel array — modulated in place.</param>
    /// <param name="depthBuffer">Per-pixel ray-total-T. <see cref="DepthMiss"/> = sky.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="fx">Active LightingFxData. DofAperture=0 or DofSamples=0 = no-op.</param>
    public static void ApplyDof(
        uint[] colorBuffer,
        float[] depthBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        if (fx.DofAperture <= 0 || fx.DofSamples <= 0) return;
        int n = width * height;
        if (colorBuffer.Length < n) return;
        if (depthBuffer.Length < n) return;

        // Ring layout — total = 1 + 3R(R+1). Pick smallest R that meets the
        // requested sample count; cap at 4 rings (61 samples) for sanity.
        int requested = Math.Min(fx.DofSamples, 61);
        int rings = 1;
        while (1 + 3 * rings * (rings + 1) < requested && rings < 4) rings++;
        int count = 1 + 3 * rings * (rings + 1);
        var offX = new double[count];
        var offY = new double[count];
        offX[0] = 0; offY[0] = 0;
        int k = 1;
        for (int r = 1; r <= rings; r++)
        {
            double rNorm = (double)r / rings;
            for (int side = 0; side < 6; side++)
            {
                double a0 = side * Math.PI / 3.0;
                double a1 = (side + 1) * Math.PI / 3.0;
                double cx0 = Math.Cos(a0), sy0 = Math.Sin(a0);
                double cx1 = Math.Cos(a1), sy1 = Math.Sin(a1);
                for (int t = 0; t < r; t++)
                {
                    double f = (double)t / r;
                    offX[k] = rNorm * (cx0 * (1 - f) + cx1 * f);
                    offY[k] = rNorm * (sy0 * (1 - f) + sy1 * f);
                    k++;
                }
            }
        }
        // Pre-compute each sample's distance-from-centre (in normalised CoC units)
        // for the bleed-control compare. Saves a sqrt per pixel per sample.
        var offDist = new double[count];
        for (int s = 0; s < count; s++)
            offDist[s] = Math.Sqrt(offX[s] * offX[s] + offY[s] * offY[s]);

        double focus = fx.DofFocusDistance;
        double aperture = fx.DofAperture;
        double shortEdge = Math.Min(width, height);
        double cocScale = aperture * shortEdge;

        // Per-pixel CoC pass. Cached so the gather loop doesn't re-derive it
        // for every neighbour read.
        var cocBuf = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float d = depthBuffer[idx];
                if (float.IsPositiveInfinity(d) || d <= 0) { cocBuf[idx] = 0; continue; }
                double coc = Math.Abs(d - focus) / d * cocScale;
                cocBuf[idx] = (float)coc;
            }
        });

        // Snapshot source for gather. In-place modulation would corrupt
        // neighbour reads.
        uint[] src = (uint[])colorBuffer.Clone();

        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                float d0 = depthBuffer[idx];
                float coc0 = cocBuf[idx];
                // Sub-pixel CoC = no perceptible blur; skip to save work and
                // avoid washing out sharp pixels with neighbour fringe.
                if (coc0 < 0.75f) continue;

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int s = 0; s < count; s++)
                {
                    int sx = (int)Math.Round(x + offX[s] * coc0);
                    int sy = (int)Math.Round(y + offY[s] * coc0);
                    if ((uint)sx >= (uint)width || (uint)sy >= (uint)height) continue;
                    int sidx = sy * width + sx;
                    float dN = depthBuffer[sidx];
                    float cocN = cocBuf[sidx];
                    double dist = offDist[s] * coc0; // sample distance in px

                    // Bleed control.
                    double w;
                    if (float.IsPositiveInfinity(dN))
                    {
                        // Sky behind the centre — only let it spread if the
                        // centre itself is blurry enough for sky to leak past.
                        w = coc0 > dist ? 1.0 : 0.0;
                    }
                    else if (dN < d0 - 1e-4)
                    {
                        // Neighbour in front of centre — contributes only if
                        // its CoC reaches the centre. Prevents sharp FG from
                        // smearing onto blurred BG.
                        w = cocN > dist ? 1.0 : 0.0;
                    }
                    else
                    {
                        // Neighbour at or behind centre — full contribution.
                        w = 1.0;
                    }
                    if (w <= 0) continue;

                    uint c = src[sidx];
                    sumR += ((c >> 16) & 0xFF) * w;
                    sumG += ((c >>  8) & 0xFF) * w;
                    sumB += ( c        & 0xFF) * w;
                    sumW += w;
                }
                if (sumW > 0)
                {
                    byte R = (byte)Math.Clamp(sumR / sumW, 0, 255);
                    byte G = (byte)Math.Clamp(sumG / sumW, 0, 255);
                    byte B = (byte)Math.Clamp(sumB / sumW, 0, 255);
                    colorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
                }
            }
        });
    }

    /// <summary>
    /// Phase 23 — Sobel-on-normal edge ink. 3×3 Sobel kernel on each component
    /// of the normal G-buffer; the per-pixel response magnitude (Euclidean sum
    /// of per-component gradient lengths) feeds an alpha-blend of <see
    /// cref="LightingFxData.EdgeColor"/> over the display-ready ColorBuffer.
    ///
    /// Why normals instead of depth + luma?
    ///   Depth-Sobel catches silhouettes only — shading creases on a single
    ///   convex surface vanish. Luma-Sobel catches lighting noise too, ink-ing
    ///   speckles instead of geometry. Normal-Sobel catches every direction-
    ///   change in the surface, which is the comic-book "draw the geometry"
    ///   look. Costs one extra pass over the normal buffer.
    ///
    /// Alpha formula
    ///   <c>α = clamp((|∇n| − threshold) / (1 − threshold), 0, 1) · strength</c>
    ///   • Below threshold → α = 0 (no ink).
    ///   • At threshold → α just above 0 (anti-alias-friendly soft entry).
    ///   • At |∇n| ≥ 1 → α saturates to <see cref="LightingFxData.EdgeStrength"/>
    ///     (a unit-disk normal change of 1 rad is already a hard crease).
    ///
    /// Sky pixels (depth = +∞) and pixels whose Sobel neighbours include any
    /// sky pixel are skipped — would otherwise ink the entire horizon since
    /// the sky normal (0,0,0) differs maximally from a surface normal.
    ///
    /// Limitations / Phase 23b candidates
    ///   • Runs after <see cref="ApplyToneMapBloom"/> so lens distortion warps
    ///     pixels but Sobel still samples the unwarped normal buffer — minor
    ///     misalignment for large lens k. For typical values reads fine.
    ///   • Per-component Sobel sums independently; an anisotropic kernel
    ///     (DoG or Frei-Chen) would weight diagonal edges more accurately.
    ///   • Threshold is global; a depth-aware threshold would let near
    ///     surfaces ink fewer micro-creases. Open if requested.
    /// </summary>
    /// <param name="colorBuffer">BGRA pixel array — modulated in place.</param>
    /// <param name="depthBuffer">Per-pixel ray-total-T. <see cref="DepthMiss"/> = sky.</param>
    /// <param name="normalBuffer">Packed nx,ny,nz per pixel (3 floats / px).</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="fx">Active LightingFxData. EdgeStrength=0 = no-op.</param>
    public static void ApplyEdgeInk(
        uint[] colorBuffer,
        float[] depthBuffer,
        float[] normalBuffer,
        int width, int height,
        in LightingFxData fx)
    {
        if (fx.EdgeStrength <= 0) return;
        int n = width * height;
        if (colorBuffer.Length < n) return;
        if (depthBuffer.Length < n) return;
        if (normalBuffer.Length < 3 * n) return;

        double strength = Math.Clamp(fx.EdgeStrength, 0.0, 1.0);
        double threshold = Math.Max(0.0, fx.EdgeThreshold);
        double range = Math.Max(1.0 - threshold, 1e-3); // div-by-zero guard
        uint inkColor = fx.EdgeColor;
        double inkR = (inkColor >> 16) & 0xFF;
        double inkG = (inkColor >>  8) & 0xFF;
        double inkB =  inkColor        & 0xFF;
        bool useFreiChen = fx.EdgeKernel == EdgeKernelMode.FreiChen;

        // Frei-Chen edge subspace — 4 orthonormal basis vectors covering
        // the 3×3 patch. Magnitude = √(Σ projection²). Normalised so that
        // an isolated unit-step edge produces magnitude 1, matching the
        // Sobel scale used by EdgeThreshold.
        // The 1/(2√2) scaling factor folds into each kernel; the final
        // result is a unit-norm gradient measure.
        const double s = 1.4142135623730951;        // √2
        const double k = 0.35355339059327373;       // 1/(2√2)

        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int idx = y * width + x;
                if (float.IsPositiveInfinity(depthBuffer[idx])) continue;

                // 8 neighbour indices (row-major) for Sobel.
                int i00 = ((y - 1) * width + (x - 1)) * 3;
                int i10 = ((y - 1) * width +  x     ) * 3;
                int i20 = ((y - 1) * width + (x + 1)) * 3;
                int i01 = ( y      * width + (x - 1)) * 3;
                int i21 = ( y      * width + (x + 1)) * 3;
                int i02 = ((y + 1) * width + (x - 1)) * 3;
                int i12 = ((y + 1) * width +  x     ) * 3;
                int i22 = ((y + 1) * width + (x + 1)) * 3;

                // Skip pixels with any sky neighbour — sky normal (0,0,0) would
                // saturate the gradient and ink the entire silhouette band.
                if (float.IsPositiveInfinity(depthBuffer[i00 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i10 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i20 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i01 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i21 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i02 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i12 / 3]) ||
                    float.IsPositiveInfinity(depthBuffer[i22 / 3]))
                    continue;

                // Per-component edge gradient. Sobel = horizontal + vertical
                // bands. Frei-Chen = 4-vector edge subspace (axial + diagonal).
                double sumProj2 = 0;
                // Centre tap (index i11) — needed by Frei-Chen line/avg
                // basis but not used here since we project only the edge
                // subspace. Skipped to save a load.
                for (int c = 0; c < 3; c++)
                {
                    double n00 = normalBuffer[i00 + c];
                    double n10 = normalBuffer[i10 + c];
                    double n20 = normalBuffer[i20 + c];
                    double n01 = normalBuffer[i01 + c];
                    double n21 = normalBuffer[i21 + c];
                    double n02 = normalBuffer[i02 + c];
                    double n12 = normalBuffer[i12 + c];
                    double n22 = normalBuffer[i22 + c];

                    if (useFreiChen)
                    {
                        // Frei-Chen edge subspace projections (each kernel
                        // already encodes its own row-and-column orientation).
                        //   f1: horizontal edge  [[1, √2, 1],[0,0,0],[-1,-√2,-1]] · k
                        //   f2: vertical edge    [[1, 0,-1],[√2,0,-√2],[1, 0,-1]] · k
                        //   f3: diag 45°         [[0,-1, √2],[1,0,-1],[-√2,1, 0]] · k
                        //   f4: diag 135°        [[√2,-1, 0],[-1,0, 1],[ 0, 1,-√2]] · k
                        double p1 = (n00 + s * n10 + n20 - n02 - s * n12 - n22) * k;
                        double p2 = (n00 + s * n01 + n02 - n20 - s * n21 - n22) * k;
                        double p3 = (-n10 + s * n20 + n01 - n21 - s * n02 + n12) * k;
                        double p4 = (s * n00 - n10 - n01 + n21 + n12 - s * n22) * k;
                        sumProj2 += p1 * p1 + p2 * p2 + p3 * p3 + p4 * p4;
                    }
                    else
                    {
                        // Sobel.
                        //   Gx = [-1 0 1; -2 0 2; -1 0 1]
                        //   Gy = [-1 -2 -1; 0 0 0; 1 2 1]
                        double gx = (-n00 + n20) + 2.0 * (-n01 + n21) + (-n02 + n22);
                        double gy = (-n00 - 2.0 * n10 - n20) + (n02 + 2.0 * n12 + n22);
                        sumProj2 += gx * gx + gy * gy;
                    }
                }
                double mag = Math.Sqrt(sumProj2);
                if (mag <= threshold) continue;

                double alpha = Math.Clamp((mag - threshold) / range, 0.0, 1.0) * strength;
                if (alpha <= 0) continue;

                uint d = colorBuffer[idx];
                double dR = (d >> 16) & 0xFF;
                double dG = (d >>  8) & 0xFF;
                double dB =  d        & 0xFF;
                byte R = (byte)Math.Clamp(dR * (1 - alpha) + inkR * alpha, 0, 255);
                byte G = (byte)Math.Clamp(dG * (1 - alpha) + inkG * alpha, 0, 255);
                byte B = (byte)Math.Clamp(dB * (1 - alpha) + inkB * alpha, 0, 255);
                colorBuffer[idx] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        });
    }

    /// <summary>
    /// ACES Filmic tone-map (Hill 2017 fit). Input/output linear [0..N].
    /// Achromatic version skips the input/output mat3 for cheaper per-pixel
    /// cost — visually close on per-channel data and faster on the CPU path.
    /// Saturates near 1 with a smooth shoulder; matches the look most
    /// cinematic pipelines target.
    /// </summary>
    private static (double R, double G, double B) AcesFilmic(double r, double g, double b)
    {
        return (AcesScalar(r), AcesScalar(g), AcesScalar(b));
    }

    private static double AcesScalar(double x)
    {
        // Narkowicz 2015 fast fit: x*(2.51x + 0.03) / (x*(2.43x + 0.59) + 0.14).
        const double A = 2.51, B = 0.03, C = 2.43, D = 0.59, E = 0.14;
        return Math.Clamp((x * (A * x + B)) / (x * (C * x + D) + E), 0, 1);
    }
}
