// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Lighting/TemporalLighting.cs
//
// #1337 — temporal reuse of the 3D raymarchers' ambient occlusion and soft
// shadows (ShadingPipeline.Shade<TDe>, the CPU path) while the view moves.
//
// AO (the DE cone along the normal) and the soft shadows (one penumbra march per
// light) are deterministic and view-independent: they depend on the surface point,
// its normal, the lights and the scene, not on the camera. On a moving camera a
// pixel's surface point was, one frame earlier, under a nearby pixel, with the same
// AO and shadows. So during interaction each pixel recomputes them only every
// Period-th frame (an ordered-dither slot, so a quarter of the pixels a frame at the
// default 4), and otherwise takes them from the previous frame: its hit point is
// projected into the previous camera, and the nearest of the four surrounding
// samples is used when the point its values were computed at (carried along a
// chain of reuses) is within a pixel of this hit and its normal agrees; a
// disoccluded or new surface recomputes. A reused value carries its age; at Period
// frames it is recomputed regardless, so nothing goes stale.
//
// Measured (--temporallightingprobe, 480x270, a slow dolly + pan, the ten CPU
// families): 1.01-1.25x faster moving frames with AO 5 + one light's shadows,
// 1.20-1.55x with AO 8 + three lights' shadows at 64 steps; the moving frames are
// off by 0.1-0.65 levels on average, 0-1.5% of pixels by more than 8. The AO and
// shadows of a fractal surface change from one pixel to the next, which bounds both
// the reuse and the accuracy.
//
// Exactness: interactive frames are an approximation (a value from a point up to
// a pixel away). When the view settles the host renders one more frame in
// Settle mode, which reuses nothing — exactly today's frame — and stores it as the
// next interaction's history. Headless renders (batch, export) never have a cache
// and shade every pixel in full, unchanged.
//
// The cache is ambient (an AsyncLocal, which flows into the calculators'
// Parallel.For workers): the host installs it around a 3D Calculate (Use), each
// calculator declares its frame and pinhole camera (BeginFrame) before the pixel
// loop, and Shade<TDe> looks pixels up. A calculator that does not declare a frame
// (or declares DoF / a stereo eye / an AOV view) shades in full.

using System;
using System.Threading;
using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#1337 — the pinhole camera of a 3D raymarch frame: pixel (x, y) looks
/// along <c>R·u + U·v + F</c> from <c>O</c>, u = (2(x+½)/W − 1)·FovScale·Aspect + PanU,
/// v = (1 − 2(y+½)/H)·FovScale + PanV. R, U, F orthonormal.</summary>
public readonly record struct PinholeCamera(
    double Ox, double Oy, double Oz,
    double Rx, double Ry, double Rz,
    double Ux, double Uy, double Uz,
    double Fx, double Fy, double Fz,
    double FovScale, double Aspect, double PanU, double PanV)
{
    /// <summary>The continuous pixel position (x, y) of world point P in a
    /// <paramref name="w"/> × <paramref name="h"/> frame, and its distance from the
    /// camera. False when P is behind the camera.</summary>
    public bool Project(double px, double py, double pz, int w, int h, out double x, out double y, out double dist)
    {
        double dx = px - Ox, dy = py - Oy, dz = pz - Oz;
        double z = dx * Fx + dy * Fy + dz * Fz;
        dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        x = y = 0;
        if (z <= 1e-12) return false;
        double u = (dx * Rx + dy * Ry + dz * Rz) / z;
        double v = (dx * Ux + dy * Uy + dz * Uz) / z;
        x = ((u - PanU) / (FovScale * Aspect) + 1.0) * 0.5 * w - 0.5;
        y = (1.0 - (v - PanV) / FovScale) * 0.5 * h - 0.5;
        return true;
    }
}

/// <summary>#1337 — per-pixel AO / soft-shadow history of the live 3D view (see the
/// file header).</summary>
public sealed class TemporalLighting
{
    private static readonly AsyncLocal<TemporalLighting?> s_current = new();

    /// <summary>The cache installed for the current render (null = shade in full).</summary>
    public static TemporalLighting? Current => s_current.Value;

    /// <summary>Install <paramref name="cache"/> for the calls inside the scope.</summary>
    public static Scope Use(TemporalLighting? cache)
    {
        var prev = s_current.Value;
        s_current.Value = cache;
        return new Scope(prev);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly TemporalLighting? _prev;
        internal Scope(TemporalLighting? prev) => _prev = prev;
        public void Dispose() => s_current.Value = _prev;
    }

    /// <summary>Frames between full recomputes of a pixel (2..16, default 4).</summary>
    public int Period
    {
        get => _period;
        set => _period = Math.Clamp(value, 2, 16);
    }
    private int _period = 4;

    /// <summary>The next frame reuses nothing (and refreshes the history): the
    /// host's settle frame. Cleared by <see cref="BeginFrame"/>.</summary>
    public bool Settle { get; set; }

    /// <summary>Pixels of the last frame whose AO / shadows were computed, and taken
    /// from the history.</summary>
    public int LastComputed => _computed;
    public int LastReused => _reused;

    /// <summary>The last frame reused some pixel (the host then owes a settle frame).</summary>
    public bool LastFrameApproximate => _active && _reused > 0;

    /// <summary>The current frame looks pixels up / stores them.</summary>
    public bool Active => _active;

    // Double-buffered history: cur is written by this frame, prev read.
    private sealed class Frame
    {
        public int W, H;
        public float[] Depth = Array.Empty<float>();   // distance from the camera, NaN = no hit
        public uint[] Normal = Array.Empty<uint>();    // octahedral, 2 x 16 bit
        public Half[] Values = Array.Empty<Half>();    // AO, shadow 1, 2, 3
        public byte[] Age = Array.Empty<byte>();       // frames since computed
        public float[] Anchor = Array.Empty<float>();  // x, y, z where the values were computed
        public PinholeCamera Camera;
        public long Key;
        public bool Valid;

        public void Ensure(int w, int h)
        {
            int n = w * h;
            if (Depth.Length != n)
            {
                Depth = new float[n]; Normal = new uint[n]; Values = new Half[4 * n]; Age = new byte[n];
                Anchor = new float[3 * n];
            }
            W = w; H = h;
            Array.Fill(Depth, float.NaN);
        }
    }

    private Frame _cur = new(), _prev = new();
    private bool _active, _settle;
    private int _frame, _computed, _reused;
    private double _pixAngle;

    /// <summary>The scene key: what AO and shadows depend on besides the surface
    /// point — the lighting (<see cref="LightingFxData.SceneSignature"/>, the light
    /// orbit phase), the estimator's parameters and epsilon.</summary>
    public static long SceneKey(in LightingFxData fx, double eps, params double[] deParams)
    {
        var h = new HashCode();
        h.Add(fx.SceneSignature());
        h.Add(fx.SceneTime * fx.LightOrbitSpeed);
        h.Add(eps);
        foreach (double d in deParams) h.Add(d);
        return h.ToHashCode();
    }

    /// <summary><see cref="SceneKey(in LightingFxData, double, double[])"/> for a
    /// distance estimator struct without references: its bytes are its parameters.</summary>
    public static long SceneKey<TDe>(in LightingFxData fx, double eps, in TDe de) where TDe : unmanaged
    {
        var h = new HashCode();
        h.Add(SceneKey(in fx, eps));
        h.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            new ReadOnlySpan<TDe>(in de)));
        return h.ToHashCode();
    }

    /// <summary>Called by a calculator before its pixel loop, on the installed cache
    /// (no-op without one). <paramref name="usable"/> false (DoF, a stereo eye, an
    /// AOV view, …) shades this frame in full and drops the history.</summary>
    public static void BeginFrame(int width, int height, in PinholeCamera camera, long sceneKey, bool usable)
        => Current?.Begin(width, height, in camera, sceneKey, usable);

    private void Begin(int width, int height, in PinholeCamera camera, long sceneKey, bool usable)
    {
        _settle = Settle;
        Settle = false;
        _computed = _reused = 0;
        _frame++;
        (_cur, _prev) = (_prev, _cur);   // last frame's writes become this frame's history
        if (!usable || width <= 0 || height <= 0)
        {
            _active = false;
            _prev.Valid = _cur.Valid = false;
            return;
        }
        _active = true;
        _cur.Ensure(width, height);
        _cur.Camera = camera;
        _cur.Key = sceneKey;
        _cur.Valid = true;
        // History usable only for the same frame size and scene.
        if (_prev.Valid && (_prev.W != width || _prev.H != height || _prev.Key != sceneKey)) _prev.Valid = false;
        _pixAngle = 2.0 * camera.FovScale / height;
    }

    // Same-surface test of a previous sample: the point its values were computed at
    // within AnchorTol pixel widths (at the hit distance) of this hit, in 3D — which
    // also rejects a disocclusion, whose previous sample lies on a surface behind or
    // in front — and its normal within ~25°, so the two faces of a sharp ridge, a
    // pixel apart, never share values.
    private const double AnchorTol = 1.0, MinNormalDot = 0.9;
    // 4x4 ordered-dither ranks: each 4x4 block's pixels take every slot once.
    private static readonly byte[] s_bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    /// <summary>Shade<TDe>: take pixel <paramref name="pixel"/>'s AO and shadows from
    /// the history, or false (compute them, then <see cref="Store"/>).</summary>
    internal bool TryReuse(int pixel, in ShadingInputs i, out double ao, out double sh1, out double sh2, out double sh3)
    {
        ao = sh1 = sh2 = sh3 = 1.0;
        var cur = _cur; var prev = _prev;
        if (_settle || !prev.Valid || (uint)pixel >= (uint)cur.Depth.Length) return false;
        int x = pixel % cur.W, y = pixel / cur.W;
        if ((s_bayer[((y & 3) << 2) | (x & 3)] + _frame) % _period == 0) return false;   // this pixel's turn

        if (!prev.Camera.Project(i.Px, i.Py, i.Pz, prev.W, prev.H, out double fx, out double fy, out double dist)) return false;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        // One pixel's width at the hit distance, not the surface footprint: on a grazing
        // surface that is stretched along the view only, and scaling the bound by it
        // let a strafing camera reuse values from ~4 pixels away sideways.
        double foot = dist * _pixAngle;
        double anchorTol2 = AnchorTol * AnchorTol * foot * foot;
        // The four previous samples around the projected position: of those on the
        // same surface and young enough, the one computed nearest this hit.
        // Measured (Mandelbulb, AO 5 + shadows 32, a slowly orbiting camera): a
        // bilinear blend that needs all four, or a spread limit between them, cut the
        // reuse to 8–40% of pixels for little less error — the AO and shadows of a
        // fractal surface change from one pixel to the next.
        int best = -1; double bestErr = double.MaxValue;
        for (int k = 0; k < 4; k++)
        {
            int qx = x0 + (k & 1), qy = y0 + (k >> 1);
            if ((uint)qx >= (uint)prev.W || (uint)qy >= (uint)prev.H) continue;
            int q = qy * prev.W + qx;
            if (float.IsNaN(prev.Depth[q])) continue;   // no surface there
            if (prev.Age[q] + 1 >= _period) continue;   // too old: recompute
            DecodeNormal(prev.Normal[q], out double nx, out double ny, out double nz);
            if (nx * i.Nx + ny * i.Ny + nz * i.Nz < MinNormalDot) continue;
            // The values were computed at the sample's anchor, which a chain of reuses
            // carries along: it must still be within a pixel of this hit, or the
            // offsets of successive hops add up.
            double ax = prev.Anchor[3 * q] - i.Px, ay = prev.Anchor[3 * q + 1] - i.Py, az = prev.Anchor[3 * q + 2] - i.Pz;
            double err = ax * ax + ay * ay + az * az;
            if (err > anchorTol2) continue;
            if (err < bestErr) { best = q; bestErr = err; }
        }
        if (best < 0) return false;
        ao = (double)prev.Values[4 * best];
        sh1 = (double)prev.Values[4 * best + 1];
        sh2 = (double)prev.Values[4 * best + 2];
        sh3 = (double)prev.Values[4 * best + 3];
        Write(cur, pixel, in i, ao, sh1, sh2, sh3, (byte)(prev.Age[best] + 1),
              prev.Anchor[3 * best], prev.Anchor[3 * best + 1], prev.Anchor[3 * best + 2]);
        Interlocked.Increment(ref _reused);
        return true;
    }

    /// <summary>Shade<TDe>: record pixel <paramref name="pixel"/>'s computed AO and shadows.</summary>
    internal void Store(int pixel, in ShadingInputs i, double ao, double sh1, double sh2, double sh3)
    {
        if ((uint)pixel >= (uint)_cur.Depth.Length) return;
        Write(_cur, pixel, in i, ao, sh1, sh2, sh3, 0, (float)i.Px, (float)i.Py, (float)i.Pz);
        Interlocked.Increment(ref _computed);
    }

    private static void Write(Frame f, int pixel, in ShadingInputs i, double ao, double sh1, double sh2, double sh3, byte age,
                              float ax, float ay, float az)
    {
        f.Anchor[3 * pixel] = ax; f.Anchor[3 * pixel + 1] = ay; f.Anchor[3 * pixel + 2] = az;
        f.Depth[pixel] = (float)i.TotalT;
        f.Normal[pixel] = EncodeNormal(i.Nx, i.Ny, i.Nz);
        f.Values[4 * pixel] = (Half)ao;
        f.Values[4 * pixel + 1] = (Half)sh1;
        f.Values[4 * pixel + 2] = (Half)sh2;
        f.Values[4 * pixel + 3] = (Half)sh3;
        f.Age[pixel] = age;
    }

    /// <summary>Diagnostics (tests): whether the last frame stored pixel
    /// <paramref name="pixel"/> (a surface hit it shaded), and how many frames old its
    /// AO / shadow values are (0 = computed in that frame).</summary>
    public bool HasData(int pixel) => (uint)pixel < (uint)_cur.Depth.Length && !float.IsNaN(_cur.Depth[pixel]);
    public int AgeAt(int pixel) => _cur.Age[pixel];

    /// <summary>Drop the history (the next frame computes every pixel).</summary>
    public void Clear() { _prev.Valid = _cur.Valid = false; _active = false; }

    /// <summary>The host, before each render it installs the cache for: nothing is
    /// looked up until the calculator declares the frame (<see cref="BeginFrame"/>),
    /// so a render path that never does (a GPU trace, another family) shades in full.</summary>
    public void BeginRender() { _active = false; _computed = _reused = 0; }

    // Octahedral unit-vector encoding, 16 bits per component.
    internal static uint EncodeNormal(double x, double y, double z)
    {
        double s = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
        if (s <= 0) return 0x7FFF7FFFu;
        double u = x / s, v = y / s;
        if (z < 0)
        {
            double ou = (1 - Math.Abs(v)) * (u >= 0 ? 1 : -1);
            double ov = (1 - Math.Abs(u)) * (v >= 0 ? 1 : -1);
            u = ou; v = ov;
        }
        uint qu = (uint)Math.Clamp((int)Math.Round((u * 0.5 + 0.5) * 65535.0), 0, 65535);
        uint qv = (uint)Math.Clamp((int)Math.Round((v * 0.5 + 0.5) * 65535.0), 0, 65535);
        return (qu << 16) | qv;
    }

    internal static void DecodeNormal(uint e, out double x, out double y, out double z)
    {
        double u = (e >> 16) / 65535.0 * 2 - 1, v = (e & 0xFFFF) / 65535.0 * 2 - 1;
        z = 1 - Math.Abs(u) - Math.Abs(v);
        if (z < 0)
        {
            double ou = (1 - Math.Abs(v)) * (u >= 0 ? 1 : -1);
            double ov = (1 - Math.Abs(u)) * (v >= 0 ? 1 : -1);
            u = ou; v = ov;
        }
        x = u; y = v;
        double l = Math.Sqrt(x * x + y * y + z * z);
        x /= l; y /= l; z /= l;
    }
}
