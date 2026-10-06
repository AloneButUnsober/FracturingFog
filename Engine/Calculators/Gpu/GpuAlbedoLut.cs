// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuAlbedoLut.cs — #1172 / GPU parity plan G2.2: the 3D GPU kernels' surface
// albedo from the active colour map, instead of a fixed sine rainbow.
//
// The CPU trace colours a hit with
//     ColorMap.Map(smooth, 0, 256, nx, ny),
//     smooth = hitStep * (stepScale / maxSteps) + t * depthScale
// (stepScale/depthScale 192/0.5 for most families, 256/4 for Mandelbulb).
// IColorMap is arbitrary .NET code, so the kernels can't call it. Instead the
// map is sampled here into a LUT over smooth ∈ [0, SMax] that the kernel
// interpolates (GpuKernelUtils.SurfaceAlbedo). Themes that also read the
// surface normal (relief / Phong-style themes) get a 9x9 grid over
// (nx, ny) ∈ [-1, 1]², interpolated bilinearly; the rest a 1D LUT.
//
// The LUT is cached per colour-map instance and rebuilt when a cheap
// fingerprint of the map (a handful of Map samples) or the range changes, so a
// static theme bakes once.

using System;
using System.Runtime.CompilerServices;
using FracturingFog.Interefaces;

namespace FracturingFog.Calculators.Gpu;

public static class GpuAlbedoLut
{
    /// <summary>LUT entries along smooth.</summary>
    public const int SmoothSamples = 1024;
    /// <summary>Normal-grid side for normal-dependent themes (odd, so n = 0 is a sample).</summary>
    public const int NormalGrid = 9;

    private sealed class Entry
    {
        public double SMax;
        public uint[] Fingerprint = Array.Empty<uint>();
        public uint[] Lut = Array.Empty<uint>();
        public int Normals;
    }

    private static readonly ConditionalWeakTable<IColorMap, Entry> s_cache = new();

    private static readonly float[] s_probeSmooth = { 0f, 13.7f, 61f, 128f, 199.5f, 251f };
    private static readonly (float nx, float ny)[] s_probeNormals = { (0f, 0f), (0.7f, 0f), (0f, -0.7f), (-0.5f, 0.6f) };

    /// <summary>Bake (or reuse) the albedo LUT for <paramref name="map"/> and
    /// describe it in <paramref name="sp"/>. <paramref name="stepScale"/> /
    /// <paramref name="depthScale"/> are the family's CPU smooth coefficients;
    /// <paramref name="sceneRadius"/> bounds the ray distance t.</summary>
    public static uint[] Bake(IColorMap? map, double stepScale, double depthScale, double sceneRadius,
        ref GpuShadingParams sp, double minSMax = 0.0)
    {
        if (map is null)
        {
            sp.AlbedoLutSmooth = 0;
            return GpuKernelUtils.PaletteOff;
        }
        // t can overshoot the scene radius by one march step; leave headroom.
        double sMax = stepScale + depthScale * Math.Max(1.0, sceneRadius) * 1.25 + 1.0;
        // #1173-F — a kernel that feeds its own smooth value (QuatMandel dual-orbit,
        // 0..255) needs the LUT to span that range too.
        if (sMax < minSMax) sMax = minSMax;

        var fp = Fingerprint(map);
        var e = s_cache.GetOrCreateValue(map);
        lock (e)
        {
            if (e.Lut.Length == 0 || e.SMax != sMax || !Same(e.Fingerprint, fp))
            {
                e.Normals = NormalDependent(fp) ? NormalGrid : 1;
                e.Lut = Sample(map, sMax, e.Normals);
                e.SMax = sMax;
                e.Fingerprint = fp;
            }
            sp.AlbedoLutSmooth = SmoothSamples;
            sp.AlbedoLutNormals = e.Normals;
            sp.AlbedoSMax = sMax;
            sp.AlbedoStepScale = stepScale;
            sp.AlbedoDepthScale = depthScale;
            return e.Lut;
        }
    }

    private static uint[] Sample(IColorMap map, double sMax, int normals)
    {
        var lut = new uint[SmoothSamples * normals * normals];
        double ds = sMax / (SmoothSamples - 1);
        for (int iy = 0; iy < normals; iy++)
        for (int ix = 0; ix < normals; ix++)
        {
            float nx = normals == 1 ? 0f : (float)(-1.0 + 2.0 * ix / (normals - 1));
            float ny = normals == 1 ? 0f : (float)(-1.0 + 2.0 * iy / (normals - 1));
            int baseIdx = (iy * normals + ix) * SmoothSamples;
            for (int i = 0; i < SmoothSamples; i++)
                lut[baseIdx + i] = (uint)map.Map((float)(i * ds), 0f, 256, nx, ny);
        }
        return lut;
    }

    private static uint[] Fingerprint(IColorMap map)
    {
        var fp = new uint[s_probeSmooth.Length * s_probeNormals.Length];
        int k = 0;
        foreach (var (nx, ny) in s_probeNormals)
            foreach (float s in s_probeSmooth)
                fp[k++] = (uint)map.Map(s, 0f, 256, nx, ny);
        return fp;
    }

    // The probes are laid out normal-major: block 0 is (0,0), the rest tilted.
    private static bool NormalDependent(uint[] fp)
    {
        int n = s_probeSmooth.Length;
        for (int b = 1; b < s_probeNormals.Length; b++)
            for (int i = 0; i < n; i++)
                if (fp[b * n + i] != fp[i]) return true;
        return false;
    }

    private static bool Same(uint[] a, uint[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
