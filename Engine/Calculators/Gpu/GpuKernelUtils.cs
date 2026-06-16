// GpuKernelUtils.cs
//
// P7 infra — kernel-callable static helpers shared across per-fractal ILGPU
// kernels under Engine/Calculators/Gpu/. Pulled from UserBulbGpuCalculator's
// kernel inner loops so the second, third, ... per-fractal kernel can reuse
// the same ray construction + sphere clip + cheap-palette code paths.
//
// Constraints: every method here must be ILGPU-kernel-callable. That means:
//   * No managed references (no string, no class instances, no delegate).
//   * No Math.Pow with non-const exponent on the GPU path (CUDA backend
//     refuses to JIT). Use Math.Sqrt + manual multiplies, or the dedicated
//     pow approximations.
//   * No 'out' parameters (ILGPU's IR-level inlining loses track). Use
//     return tuples or pack into the value tuple positions.
//   * No exception throw.
//
// Pattern: methods take primitives or the shared GpuRaymarchParams struct.
// They never touch ArrayView<T> — that stays in the per-fractal kernel so
// each kernel controls its own output layout (uint color, optional depth /
// normal G-buffer once 12b lands).

using System;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Kernel-side helpers shared by every per-fractal GPU calculator.
/// All methods are ILGPU-kernel-compatible — see file comment for the rules
/// they obey.</summary>
internal static class GpuKernelUtils
{
    /// <summary>Construct the primary ray direction for pixel <c>(x, y)</c>
    /// from the camera basis baked into <paramref name="p"/>. Returns the
    /// unit direction as (rdx, rdy, rdz). Honors <see
    /// cref="GpuRaymarchParams.PanU"/> / <see cref="GpuRaymarchParams.PanV"/>.
    /// Mirrors the CPU calculator's per-pixel ray construction so GPU and
    /// CPU paths produce visually matched rays.</summary>
    public static (double rdx, double rdy, double rdz) BuildPrimaryRay(
        int x, int y, in GpuRaymarchParams p)
    {
        double u = (2.0 * (x + 0.5) / p.Width - 1.0) * p.FovScale * p.Aspect + p.PanU;
        double v = (1.0 - 2.0 * (y + 0.5) / p.Height) * p.FovScale + p.PanV;
        double rdx = p.RightX * u + p.UpX * v + p.FwdX;
        double rdy = p.RightY * u + p.UpY * v + p.FwdY;
        double rdz = p.RightZ * u + p.UpZ * v + p.FwdZ;
        double rl = 1.0 / Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);
        return (rdx * rl, rdy * rl, rdz * rl);
    }

    /// <summary>Sphere-clip the primary ray against the cull radius in
    /// <paramref name="p"/>. Returns <c>hit = false</c> when the ray misses
    /// the bounding sphere (caller writes <see cref="GpuRaymarchParams.InSetColor"/>);
    /// otherwise returns the entry / exit ray-t values so the per-fractal
    /// sphere-trace loop starts at <c>tEn</c> and bails past <c>tEx</c>.
    /// When <see cref="GpuRaymarchParams.CullRadiusSq"/> is zero the clip is
    /// disabled — returns <c>(true, 0, double.MaxValue)</c>.</summary>
    public static (bool hit, double tEn, double tEx) SphereClip(
        double rdx, double rdy, double rdz, in GpuRaymarchParams p)
    {
        if (p.CullRadiusSq <= 0.0) return (true, 0.0, double.MaxValue);
        double ocx = p.CamX - p.TargetX;
        double ocy = p.CamY - p.TargetY;
        double ocz = p.CamZ - p.TargetZ;
        double bS = ocx * rdx + ocy * rdy + ocz * rdz;
        double cS = ocx * ocx + ocy * ocy + ocz * ocz - p.CullRadiusSq;
        double disc = bS * bS - cS;
        if (disc < 0) return (false, 0.0, 0.0);
        double sq = Math.Sqrt(disc);
        double tEx = -bS + sq;
        if (tEx < 0) return (false, 0.0, 0.0);
        double tEn = Math.Max(0.0, -bS - sq);
        return (true, tEn, tEx);
    }

    /// <summary>Hash a hit into a deterministic ARGB color using the
    /// cheap-palette pattern from <c>UserBulbGpuCalculator</c>: shade by
    /// step-depth + total ray length, hue from a phase-shifted sine
    /// cascade. Acceptable until the full <c>ShadingPipeline</c> ports to
    /// GPU (deferred sub-phase). Lambert diffuse already factored into
    /// <paramref name="shade"/> by the caller (gives caller control over
    /// ambient floor / hemisphere modulation).</summary>
    public static uint CheapPalette(int hitStep, int maxSteps, double tTotal, double shade)
    {
        double t = hitStep / (double)maxSteps + tTotal * 0.05;
        t -= Math.Floor(t);
        uint r = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283)));
        uint g = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 2.094)));
        uint b = (uint)Math.Min(255.0, 255.0 * shade * (0.5 + 0.5 * Math.Sin(t * 6.283 + 4.188)));
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    /// <summary>Lambert diffuse with ambient floor for the cheap-shading
    /// path. <paramref name="ambient"/> is the floor (0.15 matches the CPU
    /// pipeline's default ambient term); diffuse is scaled into the
    /// remaining 1 - ambient range.</summary>
    public static double LambertShade(
        double nx, double ny, double nz,
        double lx, double ly, double lz,
        double ambient)
    {
        double diffuse = Math.Max(0.0, nx * lx + ny * ly + nz * lz);
        return ambient + diffuse * (1.0 - ambient);
    }

    /// <summary>P7c.1 — unshaded cheap-palette albedo. Returns the RGB hue
    /// cascade from <see cref="CheapPalette"/> with no Lambert / shadow / ao
    /// multiplier applied — the new GPU shade path applies lighting to this
    /// albedo via <see cref="ComposePixel"/>. Channels are bytes-as-double in
    /// [0, 255]. Acceptable surface color until a color-map / driver GPU port
    /// lands (separate phase).</summary>
    public static (double aR, double aG, double aB) CheapAlbedo(
        int hitStep, int maxSteps, double tTotal)
    {
        double t = hitStep / (double)maxSteps + tTotal * 0.05;
        t -= Math.Floor(t);
        double aR = 255.0 * (0.5 + 0.5 * Math.Sin(t * 6.283));
        double aG = 255.0 * (0.5 + 0.5 * Math.Sin(t * 6.283 + 2.094));
        double aB = 255.0 * (0.5 + 0.5 * Math.Sin(t * 6.283 + 4.188));
        return (aR, aG, aB);
    }

    /// <summary>P7c.1 — vertical sky gradient sampled by ray-up component.
    /// Mirrors <c>ShadingPipeline.SkyColor</c> but takes the top/bot colors
    /// as channel doubles (so the kernel doesn't bit-unpack a uint per
    /// pixel). Returns bytes-as-double channels in [0, 255].</summary>
    public static (double sR, double sG, double sB) SkyGradient(
        double rdy,
        double topR, double topG, double topB,
        double botR, double botG, double botB)
    {
        double t = Math.Clamp(0.5 * (rdy + 1.0), 0, 1);
        return (
            (1.0 - t) * botR + t * topR,
            (1.0 - t) * botG + t * topG,
            (1.0 - t) * botB + t * topB);
    }

    /// <summary>P7c.2 — pre-fog surface composition. Returns the lit (br, bg, bb)
    /// triplet in bytes-as-double [0, 255] BEFORE any fog math runs. Used by
    /// per-fractal kernels that need to insert a volumetric in-scatter loop
    /// (which has to live in the per-fractal kernel so SoftShadow can call
    /// that fractal's DE) between surface shade and fog. Pair with
    /// <see cref="PackBgra"/> when fog is off or the kernel applies its own fog.</summary>
    public static (double br, double bg, double bb) ComposeSurfaceNoFog(
        in GpuShadingParams sp,
        double nx, double ny, double nz,
        double sh1, double sh2, double sh3,
        double ao,
        double aR, double aG, double aB)
    {
        double sR = 0, sG = 0, sB = 0;
        if (sp.L1I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L1X + ny * sp.L1Y + nz * sp.L1Z);
            double w = sp.L1I * sh1 * dot;
            sR += sp.L1R * w; sG += sp.L1G * w; sB += sp.L1B * w;
        }
        if (sp.L2I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L2X + ny * sp.L2Y + nz * sp.L2Z);
            double w = sp.L2I * sh2 * dot;
            sR += sp.L2R * w; sG += sp.L2G * w; sB += sp.L2B * w;
        }
        if (sp.L3I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L3X + ny * sp.L3Y + nz * sp.L3Z);
            double w = sp.L3I * sh3 * dot;
            sR += sp.L3R * w; sG += sp.L3G * w; sB += sp.L3B * w;
        }
        double amb = sp.AmbientStrength;
        sR = amb + (sR / 255.0) * (1.0 - amb);
        sG = amb + (sG / 255.0) * (1.0 - amb);
        sB = amb + (sB / 255.0) * (1.0 - amb);
        sR *= ao; sG *= ao; sB *= ao;
        return (aR * sR, aG * sG, aB * sB);
    }

    /// <summary>P7c.2 — pack 0..255 doubles into a BGRA uint with clamp.</summary>
    public static uint PackBgra(double br, double bg, double bb)
    {
        uint R = (uint)Math.Clamp(br, 0.0, 255.0);
        uint G = (uint)Math.Clamp(bg, 0.0, 255.0);
        uint B = (uint)Math.Clamp(bb, 0.0, 255.0);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }

    /// <summary>P7c.2 — scalar exp-fog tint applied to a surface (br, bg, bb).
    /// Mirrors the legacy <c>ShadingPipeline.Shade</c> fall-through path
    /// (<c>VolumeSteps == 0 &amp;&amp; FogDensity &gt; 0</c>). Returns the
    /// tinted surface; pair with <see cref="PackBgra"/>.</summary>
    public static (double br, double bg, double bb) ApplyScalarFog(
        in GpuShadingParams sp,
        double br, double bg, double bb,
        double rdy, double tTotal)
    {
        if (sp.FogDensity <= 0) return (br, bg, bb);
        double fogF = 1.0 - Math.Exp(-tTotal * sp.FogDensity);
        var (skyR, skyG, skyB) = SkyGradient(rdy,
            sp.SkyTopR, sp.SkyTopG, sp.SkyTopB,
            sp.SkyBotR, sp.SkyBotG, sp.SkyBotB);
        return (
            br * (1.0 - fogF) + skyR * fogF,
            bg * (1.0 - fogF) + skyG * fogF,
            bb * (1.0 - fogF) + skyB * fogF);
    }

    /// <summary>P7c.2 — Padé(2,2) approximation of exp(-x). ~1e-4 accuracy on
    /// x ∈ [0, 1]; caller falls back to <c>Math.Exp</c> outside the trust band.
    /// Mirrors <c>ShadingPipeline.ExpNegSmall</c>.</summary>
    public static double ExpNegSmall(double x)
    {
        double num = 12.0 - 6.0 * x + x * x;
        double den = 12.0 + 6.0 * x + x * x;
        return num / den;
    }

    /// <summary>P7c.2 — integer hash → [0, 1] scalar. Same constants as
    /// <c>ShadingPipeline.Hash3D</c> so GPU and CPU FBM agree.</summary>
    public static double Hash3D(int ix, int iy, int iz)
    {
        unchecked
        {
            uint h = (uint)(ix * 374761393 + iy * 668265263 + iz * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777215.0;
        }
    }

    /// <summary>P7c.2 — smoothed trilinear value noise [0, 1]. Mirrors
    /// <c>ShadingPipeline.ValueNoise3D</c>.</summary>
    public static double ValueNoise3D(double x, double y, double z)
    {
        int ix = (int)Math.Floor(x);
        int iy = (int)Math.Floor(y);
        int iz = (int)Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        double ux = fx * fx * (3.0 - 2.0 * fx);
        double uy = fy * fy * (3.0 - 2.0 * fy);
        double uz = fz * fz * (3.0 - 2.0 * fz);
        double c000 = Hash3D(ix,     iy,     iz);
        double c100 = Hash3D(ix + 1, iy,     iz);
        double c010 = Hash3D(ix,     iy + 1, iz);
        double c110 = Hash3D(ix + 1, iy + 1, iz);
        double c001 = Hash3D(ix,     iy,     iz + 1);
        double c101 = Hash3D(ix + 1, iy,     iz + 1);
        double c011 = Hash3D(ix,     iy + 1, iz + 1);
        double c111 = Hash3D(ix + 1, iy + 1, iz + 1);
        double x00 = c000 + (c100 - c000) * ux;
        double x10 = c010 + (c110 - c010) * ux;
        double x01 = c001 + (c101 - c001) * ux;
        double x11 = c011 + (c111 - c011) * ux;
        double y0 = x00 + (x10 - x00) * uy;
        double y1 = x01 + (x11 - x01) * uy;
        return y0 + (y1 - y0) * uz;
    }

    /// <summary>P7c.2 — N-octave fbm cloud sample. Octaves clamped to [1, 6].
    /// Mirrors <c>ShadingPipeline.FbmCloud3D</c>.</summary>
    public static double FbmCloud3D(double x, double y, double z, int octaves)
    {
        if (octaves < 1) octaves = 1;
        else if (octaves > 6) octaves = 6;
        double v = 0.0;
        double amp = 0.5;
        double freq = 1.0;
        for (int i = 0; i < octaves; i++)
        {
            v += amp * ValueNoise3D(x * freq, y * freq, z * freq);
            freq *= 2.0;
            amp *= 0.5;
        }
        return v;
    }

    /// <summary>P7c.2 — density multiplier for the volumetric in-scatter loop.
    /// Returns 1.0 when noise is off. Mirrors
    /// <c>ShadingPipeline.VolumetricDensityMul</c>.</summary>
    public static double VolumetricDensityMul(
        double sx, double sy, double sz, in GpuShadingParams sp)
    {
        if (sp.VolumeNoiseAmount <= 0) return 1.0;
        double t = sp.SceneTime * sp.VolumeNoiseSpeed;
        double scale = sp.VolumeNoiseScale;
        int oct = sp.VolumeNoiseOctaves <= 0 ? 3 : sp.VolumeNoiseOctaves;
        double n = FbmCloud3D(
            sx * scale + t,
            sy * scale + t * 0.3,
            sz * scale + t * 0.7,
            oct);
        double mul = 1.0 + sp.VolumeNoiseAmount * (2.0 * n - 1.0);
        return Math.Max(0.0, mul);
    }

    /// <summary>P7c.2 — cloud self-shadow transmittance toward a directional
    /// light. Marches a fixed 2.0 world-unit length from (sx, sy, sz) along
    /// (lx, ly, lz), accumulates FBM extinction, returns
    /// exp(-strength · accum). Returns 1.0 when off. Mirrors
    /// <c>ShadingPipeline.CloudSelfShadow</c>.</summary>
    public static double CloudSelfShadow(
        double sx, double sy, double sz,
        double lx, double ly, double lz,
        in GpuShadingParams sp)
    {
        if (sp.VolumeSelfShadow <= 0 || sp.VolumeSelfShadowSteps <= 0
            || sp.VolumeNoiseAmount <= 0) return 1.0;
        int steps = sp.VolumeSelfShadowSteps;
        if (steps > 16) steps = 16;
        const double marchLen = 2.0;
        double stepSz = marchLen / steps;
        double t = sp.SceneTime * sp.VolumeNoiseSpeed;
        double scale = sp.VolumeNoiseScale;
        int oct = sp.VolumeNoiseOctaves <= 0 ? 3 : sp.VolumeNoiseOctaves;
        double accum = 0;
        for (int k = 1; k <= steps; k++)
        {
            double px = sx + lx * stepSz * k;
            double py = sy + ly * stepSz * k;
            double pz = sz + lz * stepSz * k;
            double n = FbmCloud3D(
                px * scale + t,
                py * scale + t * 0.3,
                pz * scale + t * 0.7,
                oct);
            double d = Math.Max(0.0, 1.0 + sp.VolumeNoiseAmount * (2.0 * n - 1.0));
            accum += d * stepSz;
        }
        return Math.Exp(-sp.VolumeSelfShadow * accum);
    }

    /// <summary>P7c.3 — reflect view ray about a surface normal. Returns the
    /// unit reflection direction (rrx, rry, rrz) for the inline reflect-march
    /// each kernel runs against its own DE.</summary>
    public static (double rrx, double rry, double rrz) Reflect3D(
        double rdx, double rdy, double rdz,
        double nx, double ny, double nz)
    {
        double rdotn = rdx * nx + rdy * ny + rdz * nz;
        return (rdx - 2.0 * rdotn * nx,
                rdy - 2.0 * rdotn * ny,
                rdz - 2.0 * rdotn * nz);
    }

    /// <summary>P7c.3 — Schlick Fresnel weight for the one-bounce reflection
    /// mix. F0 ramps from 0.04 (dielectric) to 1.0 (metal) by
    /// <paramref name="metallic"/>. Returns the final mix coefficient
    /// <c>reflectStrength · F</c>; caller multiplies the bounce color by it.</summary>
    public static double FresnelMix(
        double nx, double ny, double nz,
        double rdx, double rdy, double rdz,
        double metallic, double reflectStrength)
    {
        double NdotV = Math.Max(0.0, -(nx * rdx + ny * rdy + nz * rdz));
        double f0 = 0.04 + 0.96 * metallic;
        double omv = 1.0 - NdotV;
        double Fc = omv * omv * omv * omv * omv;
        double F = f0 + (1.0 - f0) * Fc;
        return reflectStrength * F;
    }

    /// <summary>P7c.3 — bounce color for a reflect-march. On hit returns the
    /// sky-tint along the bounce direction attenuated by <c>exp(-tR·0.15)</c>
    /// (cheap env-proxy until IBL GPU port lands); on miss returns the sky
    /// gradient directly. Matches the CPU pipe's reflection block intent —
    /// HDRI env sampling is unavailable on GPU so sky-tint stands in.</summary>
    public static (double rR, double rG, double rB) ReflectShade(
        bool hit, double hitTr, double rry,
        in GpuShadingParams sp)
    {
        var (sR, sG, sB) = SkyGradient(rry,
            sp.SkyTopR, sp.SkyTopG, sp.SkyTopB,
            sp.SkyBotR, sp.SkyBotG, sp.SkyBotB);
        if (hit)
        {
            double atten = Math.Exp(-hitTr * 0.15);
            return (sR * atten, sG * atten, sB * atten);
        }
        return (sR, sG, sB);
    }

    /// <summary>P7c.4 — Cook-Torrance GGX specular accumulator for one
    /// directional light. Mirrors <c>ShadingPipeline.AccumulateSpec</c> with
    /// light color taken as three pre-unpacked bytes-as-double channels so the
    /// kernel doesn't bit-unpack uint per pixel. Intensity already includes
    /// shadow factor. Result accumulates into specR/G/B in 0..255 byte space;
    /// the surrounding clamp/tonemap absorbs any overshoot.</summary>
    public static (double specR, double specG, double specB) GgxSpecLight(
        double intensity,
        double lR, double lG, double lB,
        double lx, double ly, double lz,
        double nx, double ny, double nz,
        double vx, double vy, double vz,
        double NdotV, double a2, double k,
        double F0r, double F0g, double F0b,
        double specStrength)
    {
        if (intensity <= 0) return (0, 0, 0);
        double NdotL = nx * lx + ny * ly + nz * lz;
        if (NdotL <= 0) return (0, 0, 0);
        double hx = lx + vx, hy = ly + vy, hz = lz + vz;
        double hLen2 = hx * hx + hy * hy + hz * hz;
        if (hLen2 < 1e-12) return (0, 0, 0);
        double invH = 1.0 / Math.Sqrt(hLen2);
        hx *= invH; hy *= invH; hz *= invH;
        double NdotH = Math.Max(0.0, nx * hx + ny * hy + nz * hz);
        double VdotH = Math.Max(0.0, vx * hx + vy * hy + vz * hz);
        double denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
        double D = a2 / (Math.PI * denom * denom);
        double G1V = NdotV / (NdotV * (1.0 - k) + k);
        double G1L = NdotL / (NdotL * (1.0 - k) + k);
        double G = G1V * G1L;
        double omv = 1.0 - VdotH;
        double Fc = omv * omv * omv * omv * omv;
        double Fr = F0r + (1.0 - F0r) * Fc;
        double Fg = F0g + (1.0 - F0g) * Fc;
        double Fb = F0b + (1.0 - F0b) * Fc;
        double specBase = (D * G / Math.Max(4.0 * NdotV, 1e-4)) * specStrength * intensity;
        return (specBase * Fr * lR, specBase * Fg * lG, specBase * Fb * lB);
    }

    /// <summary>P7c.4 — Burley back-light SSS lobe for one directional light.
    /// Mirrors <c>ShadingPipeline.AccumulateSss</c> (distortion=0.3, power=4).
    /// Light color is pre-unpacked bytes-as-double; intensity already includes
    /// shadow factor.</summary>
    public static (double sssR, double sssG, double sssB) BurleySssLight(
        double intensity,
        double lR, double lG, double lB,
        double lx, double ly, double lz,
        double nx, double ny, double nz,
        double vx, double vy, double vz,
        double strength)
    {
        if (intensity <= 0) return (0, 0, 0);
        const double distortion = 0.3;
        double hx = lx + nx * distortion;
        double hy = ly + ny * distortion;
        double hz = lz + nz * distortion;
        double hLen2 = hx * hx + hy * hy + hz * hz;
        if (hLen2 < 1e-12) return (0, 0, 0);
        double invH = 1.0 / Math.Sqrt(hLen2);
        hx *= invH; hy *= invH; hz *= invH;
        double dot = -(vx * hx + vy * hy + vz * hz);
        if (dot <= 0) return (0, 0, 0);
        double lobe = dot * dot;
        lobe *= lobe;
        double s = lobe * strength * intensity;
        return (s * lR, s * lG, s * lB);
    }

    /// <summary>P7c.4 — procedural 2D sample dispatch. Mirrors
    /// <c>ShadingPipeline.SampleProc2D</c>. Kind passed as int:
    /// 1=Wood, 2=Marble, 3=Rock, 4=Checker; any other value returns 1.0
    /// (caller should have skipped on kind==0/None).</summary>
    public static double TriplanarSample2D(int kind, double u, double v)
    {
        if (kind == 1)
        {
            double r = Math.Sqrt(u * u + v * v);
            double wobble = 0.1 * Math.Sin(u * 0.3) * Math.Cos(v * 0.3);
            return 0.5 + 0.5 * Math.Sin((r + wobble) * 6.0);
        }
        if (kind == 2)
        {
            double turb = Math.Sin(v * 2.0 + Math.Sin(u * 4.0) * 1.5);
            return 0.5 + 0.5 * Math.Sin(u * 3.0 + turb * 2.0);
        }
        if (kind == 3)
        {
            double a = Math.Sin(u * 12.9898 + v * 78.233) * 43758.5453;
            double n = a - Math.Floor(a);
            return Math.Clamp(0.3 + 0.7 * n, 0, 1);
        }
        if (kind == 4)
        {
            int cu = (int)Math.Floor(u) & 1;
            int cv = (int)Math.Floor(v) & 1;
            return (cu ^ cv) == 0 ? 0.2 : 1.0;
        }
        return 1.0;
    }

    /// <summary>P7c.4 — apply triplanar procedural texture to a cheap-palette
    /// albedo. Mirrors <c>ShadingPipeline.ApplyTriplanar</c> but channels in
    /// bytes-as-double instead of packed uint. Returns the modulated albedo
    /// triplet; caller already gated on kind != 0 &amp;&amp; strength > 0.</summary>
    public static (double aR, double aG, double aB) ApplyTriplanar(
        double aR, double aG, double aB,
        in GpuShadingParams sp,
        double px, double py, double pz,
        double nx, double ny, double nz)
    {
        double s = sp.TriplanarScale;
        double wx = nx * nx, wy = ny * ny, wz = nz * nz;
        double sum = wx + wy + wz;
        if (sum < 1e-8) return (aR, aG, aB);
        double inv = 1.0 / sum;
        wx *= inv; wy *= inv; wz *= inv;
        int kind = sp.TriplanarKind;
        double txY = TriplanarSample2D(kind, py * s, pz * s);
        double txX = TriplanarSample2D(kind, px * s, pz * s);
        double txZ = TriplanarSample2D(kind, px * s, py * s);
        double v = wx * txY + wy * txX + wz * txZ;
        v = Math.Clamp(v, 0, 1);
        double Tr = sp.TriplanarTintR / 255.0;
        double Tg = sp.TriplanarTintG / 255.0;
        double Tb = sp.TriplanarTintB / 255.0;
        double mix = sp.TriplanarStrength;
        double tr = aR * Tr * v;
        double tg = aG * Tg * v;
        double tb = aB * Tb * v;
        return (aR * (1 - mix) + tr * mix,
                aG * (1 - mix) + tg * mix,
                aB * (1 - mix) + tb * mix);
    }

    /// <summary>P7c.4 — procedural caustics pattern in (x, z) world plane.
    /// Mirrors <c>ShadingPipeline.EvaluateCaustics</c> verbatim — two crossed
    /// sin-cascades raised to power 6 + 4× scale for sharp bright crests.</summary>
    public static double EvaluateCaustics(double x, double z, double scale, double time)
    {
        double s = scale;
        double a = Math.Sin(x * s + time) * Math.Sin(z * s * 1.3 + Math.Sin(x * s * 0.7) + time * 1.1);
        double b = Math.Sin(x * s * 1.7 + z * s * 0.5 + time * 0.9) * Math.Sin(z * s + time);
        double v = (a + b) * 0.5;
        v = 0.5 + 0.5 * v;
        double v2 = v * v;
        double v4 = v2 * v2;
        double v6 = v4 * v2;
        return v6 * 4.0;
    }

    /// <summary>P7c.4 — full PBR pre-fog composition. Replaces
    /// <see cref="ComposeSurfaceNoFog"/> on kernels that have shipped the full
    /// CPU pipeline parity (8/8 P7-pattern kernels in P7c.4). Walks triplanar
    /// modulation → 3-light diffuse + GGX spec + Burley SSS → AO → IBL ambient
    /// blend → metal-suppress-diffuse → albedo multiply → caustics. Reflection
    /// + volumetric/scalar fog stay inline in the kernel because they need to
    /// march the local fractal's DE (ILGPU can't take a struct-generic DE
    /// through LoadAutoGroupedStreamKernel).
    ///
    /// All P7c.4 knobs default-zero: with Roughness/Specular/SSS/Triplanar/
    /// Caustics/IBL all 0 + Metallic 0, the math here collapses to
    /// <see cref="ComposeSurfaceNoFog"/> bit-for-bit.</summary>
    public static (double br, double bg, double bb) ComposeSurfacePbr(
        in GpuShadingParams sp,
        double nx, double ny, double nz,
        double rdx, double rdy, double rdz,
        double px, double py, double pz,
        double sh1, double sh2, double sh3,
        double ao,
        double aR, double aG, double aB)
    {
        if (sp.TriplanarKind != 0 && sp.TriplanarStrength > 0)
        {
            (aR, aG, aB) = ApplyTriplanar(aR, aG, aB, in sp, px, py, pz, nx, ny, nz);
        }

        // Three-light Lambert accumulation (0..255 space). Matches
        // ShadingPipeline.AccumulateLight + the existing ComposeSurfaceNoFog
        // behaviour.
        double sR = 0, sG = 0, sB = 0;
        if (sp.L1I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L1X + ny * sp.L1Y + nz * sp.L1Z);
            double w = sp.L1I * sh1 * dot;
            sR += sp.L1R * w; sG += sp.L1G * w; sB += sp.L1B * w;
        }
        if (sp.L2I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L2X + ny * sp.L2Y + nz * sp.L2Z);
            double w = sp.L2I * sh2 * dot;
            sR += sp.L2R * w; sG += sp.L2G * w; sB += sp.L2B * w;
        }
        if (sp.L3I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L3X + ny * sp.L3Y + nz * sp.L3Z);
            double w = sp.L3I * sh3 * dot;
            sR += sp.L3R * w; sG += sp.L3G * w; sB += sp.L3B * w;
        }

        // GGX spec per-light. F0 ramps per-channel by Metallic so metals tint
        // their highlights with albedo.
        double specR = 0, specG = 0, specB = 0;
        if (sp.SpecularStrength > 0)
        {
            double vx = -rdx, vy = -rdy, vz = -rdz;
            double NdotV = Math.Max(0.0, nx * vx + ny * vy + nz * vz);
            double rough = Math.Max(0.05, sp.Roughness);
            double a = rough * rough;
            double a2 = a * a;
            double kg = (rough + 1.0) * (rough + 1.0) / 8.0;
            double Ar = aR / 255.0;
            double Ag = aG / 255.0;
            double Ab = aB / 255.0;
            double F0r = 0.04 + (Ar - 0.04) * sp.Metallic;
            double F0g = 0.04 + (Ag - 0.04) * sp.Metallic;
            double F0b = 0.04 + (Ab - 0.04) * sp.Metallic;
            var (s1R, s1G, s1B) = GgxSpecLight(sp.L1I * sh1, sp.L1R, sp.L1G, sp.L1B,
                sp.L1X, sp.L1Y, sp.L1Z, nx, ny, nz, vx, vy, vz,
                NdotV, a2, kg, F0r, F0g, F0b, sp.SpecularStrength);
            var (s2R, s2G, s2B) = GgxSpecLight(sp.L2I * sh2, sp.L2R, sp.L2G, sp.L2B,
                sp.L2X, sp.L2Y, sp.L2Z, nx, ny, nz, vx, vy, vz,
                NdotV, a2, kg, F0r, F0g, F0b, sp.SpecularStrength);
            var (s3R, s3G, s3B) = GgxSpecLight(sp.L3I * sh3, sp.L3R, sp.L3G, sp.L3B,
                sp.L3X, sp.L3Y, sp.L3Z, nx, ny, nz, vx, vy, vz,
                NdotV, a2, kg, F0r, F0g, F0b, sp.SpecularStrength);
            specR = s1R + s2R + s3R;
            specG = s1G + s2G + s3G;
            specB = s1B + s2B + s3B;
        }

        // Burley SSS backlight lobe per light.
        double sssR = 0, sssG = 0, sssB = 0;
        if (sp.SubSurfaceStrength > 0)
        {
            double vx = -rdx, vy = -rdy, vz = -rdz;
            var (s1R, s1G, s1B) = BurleySssLight(sp.L1I * sh1, sp.L1R, sp.L1G, sp.L1B,
                sp.L1X, sp.L1Y, sp.L1Z, nx, ny, nz, vx, vy, vz, sp.SubSurfaceStrength);
            var (s2R, s2G, s2B) = BurleySssLight(sp.L2I * sh2, sp.L2R, sp.L2G, sp.L2B,
                sp.L2X, sp.L2Y, sp.L2Z, nx, ny, nz, vx, vy, vz, sp.SubSurfaceStrength);
            var (s3R, s3G, s3B) = BurleySssLight(sp.L3I * sh3, sp.L3R, sp.L3G, sp.L3B,
                sp.L3X, sp.L3Y, sp.L3Z, nx, ny, nz, vx, vy, vz, sp.SubSurfaceStrength);
            sssR = s1R + s2R + s3R;
            sssG = s1G + s2G + s3G;
            sssB = s1B + s2B + s3B;
        }

        // IBL-modulated ambient via sky gradient at the surface normal.
        // HDRI env sampling stays GPU-blocked — gradient is the same MVP
        // fallback ShadingPipeline.SampleEnvAmbient hands back when SkyMode !=
        // Hdri or the environment name doesn't resolve.
        double ambR = sp.AmbientStrength;
        double ambG = sp.AmbientStrength;
        double ambB = sp.AmbientStrength;
        if (sp.IblStrength > 0)
        {
            var (eR, eG, eB) = SkyGradient(ny,
                sp.SkyTopR, sp.SkyTopG, sp.SkyTopB,
                sp.SkyBotR, sp.SkyBotG, sp.SkyBotB);
            double w = sp.IblStrength;
            ambR = ambR * (1.0 - w) + (eR / 255.0) * w;
            ambG = ambG * (1.0 - w) + (eG / 255.0) * w;
            ambB = ambB * (1.0 - w) + (eB / 255.0) * w;
        }

        // Metal suppresses diffuse on the spec-active path.
        double diffSuppress = sp.SpecularStrength > 0 ? (1.0 - sp.Metallic) : 1.0;
        sR = ambR + (sR / 255.0) * (1.0 - ambR) * diffSuppress;
        sG = ambG + (sG / 255.0) * (1.0 - ambG) * diffSuppress;
        sB = ambB + (sB / 255.0) * (1.0 - ambB) * diffSuppress;
        sR *= ao; sG *= ao; sB *= ao;

        double br = aR * sR + specR + sssR;
        double bg = aG * sG + specG + sssG;
        double bb = aB * sB + specB + sssB;

        // Caustics — upward-facing surface near the focusing plane gets the
        // pattern, weighted by Light1 intensity × shadow.
        if (sp.CausticsStrength > 0 && sp.L1I > 0)
        {
            double NdotUp = ny;
            if (NdotUp > 0)
            {
                double dy = py - sp.CausticsFloorY;
                double heightFall = Math.Exp(-Math.Abs(dy) * 2.0);
                double cTime = sp.SceneTime * sp.CausticsAnimSpeed;
                double cv = EvaluateCaustics(px, pz, sp.CausticsScale, cTime);
                double w = sp.CausticsStrength * cv * heightFall * NdotUp * sp.L1I * sh1;
                if (w > 0)
                {
                    br += sp.CausticsR * w;
                    bg += sp.CausticsG * w;
                    bb += sp.CausticsB * w;
                }
            }
        }

        return (br, bg, bb);
    }

    /// <summary>P7c.1 — full shade composition for the GPU path. Takes the
    /// per-fractal-precomputed soft-shadow factors + AO factor + cheap-palette
    /// albedo and walks the same 3-light Lambert + ambient + scalar fog the
    /// CPU pipeline does (subset — PBR / SSS / volumetric / reflection /
    /// triplanar / IBL / caustics deferred). Per-light SoftShadow / AO loops
    /// stay in each per-fractal kernel because they need that fractal's DE
    /// inlined (ILGPU doesn't take a closing-over delegate).</summary>
    public static uint ComposePixel(
        in GpuShadingParams sp,
        double nx, double ny, double nz,
        double sh1, double sh2, double sh3,
        double ao,
        double aR, double aG, double aB,
        double rdy, double tTotal)
    {
        // Three-light Lambert accumulation. Light color is bytes (0..255), so
        // accumulator is in 0..255 space (matches ShadingPipeline.AccumulateLight).
        double sR = 0, sG = 0, sB = 0;
        if (sp.L1I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L1X + ny * sp.L1Y + nz * sp.L1Z);
            double w = sp.L1I * sh1 * dot;
            sR += sp.L1R * w; sG += sp.L1G * w; sB += sp.L1B * w;
        }
        if (sp.L2I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L2X + ny * sp.L2Y + nz * sp.L2Z);
            double w = sp.L2I * sh2 * dot;
            sR += sp.L2R * w; sG += sp.L2G * w; sB += sp.L2B * w;
        }
        if (sp.L3I > 0)
        {
            double dot = Math.Max(0.0, nx * sp.L3X + ny * sp.L3Y + nz * sp.L3Z);
            double w = sp.L3I * sh3 * dot;
            sR += sp.L3R * w; sG += sp.L3G * w; sB += sp.L3B * w;
        }

        // Normalize diffuse to [0..1], add ambient floor, apply AO.
        double amb = sp.AmbientStrength;
        sR = amb + (sR / 255.0) * (1.0 - amb);
        sG = amb + (sG / 255.0) * (1.0 - amb);
        sB = amb + (sB / 255.0) * (1.0 - amb);
        sR *= ao; sG *= ao; sB *= ao;

        // Multiply by albedo bytes.
        double br = aR * sR;
        double bg = aG * sG;
        double bb = aB * sB;

        // Scalar exp fog with sky-gradient tint. Mirrors ShadingPipeline's
        // legacy fall-through (VolumeSteps == 0 && FogDensity > 0).
        if (sp.FogDensity > 0)
        {
            double fogF = 1.0 - Math.Exp(-tTotal * sp.FogDensity);
            var (skyR, skyG, skyB) = SkyGradient(rdy,
                sp.SkyTopR, sp.SkyTopG, sp.SkyTopB,
                sp.SkyBotR, sp.SkyBotG, sp.SkyBotB);
            br = br * (1.0 - fogF) + skyR * fogF;
            bg = bg * (1.0 - fogF) + skyG * fogF;
            bb = bb * (1.0 - fogF) + skyB * fogF;
        }

        uint R = (uint)Math.Clamp(br, 0.0, 255.0);
        uint G = (uint)Math.Clamp(bg, 0.0, 255.0);
        uint B = (uint)Math.Clamp(bb, 0.0, 255.0);
        return 0xFF000000u | (R << 16) | (G << 8) | B;
    }
}
