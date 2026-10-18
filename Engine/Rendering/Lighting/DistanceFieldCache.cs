// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Lighting/DistanceFieldCache.cs
//
// #1336 — a sparse distance-field cache for interactive 3D camera moves (the CPU
// raymarch path of KIFS and User Bulb).
//
// Every 3D frame evaluates the fractal DE along every ray, so orbiting or dollying
// the camera over an unchanged fractal pays the full formula cost each frame. While
// only the camera moves, the DE is the same function, so it is baked once into a
// sparse brick volume and the interactive frames march that instead:
//
//  - a probe pass (a 32-cell grid over the family's bounding cube) shrinks the
//    domain to the box that holds the surface;
//  - a coarse grid of cubic cells (BrickSize voxels on a side) stores the DE at the
//    cell corners. A cell is EMPTY when the DE cannot fall to the march's hit
//    threshold inside it: its smallest corner, less the local DE gradient (the
//    largest corner difference along a grid edge in the 4x4x4 corners around it — a
//    cell's own 8 can all read the same distance to a feature at its centre) times
//    its diagonal with a margin, is still above the threshold. A fractal DE is often far from a true distance (User
//    Bulb's 0.5·r/dr is ~16x too small away from the bulb), so a test against the
//    distance itself (a lower bound) would call most of the cube near. The distance
//    in an EMPTY cell is the trilinear blend of its corners;
//  - every other cell is NEAR and gets a brick: (BrickSize+1)^3 DE samples,
//    trilinearly interpolated (a Lipschitz-1 DE interpolates to within half a voxel
//    diagonal). Bricks bake nearest the camera first, on background threads below
//    normal priority; a cell whose brick is not baked yet (or is over the memory
//    budget) evaluates the true DE, so the cache is usable as soon as the coarse
//    grid is, and gets faster as the bricks land (progressive, coarse first).
//
// The voxel size follows the view: the smallest power-of-two level whose voxel is at
// most TargetVoxelPixels pixels at the view distance (64..512 voxels across the
// bounding cube). A frame uses a field only while its voxel is within
// MaxVoxelPixels pixels; closer views march the true DE.
//
// Measured (--distancefieldprobe, 480x270, an orbit, default lighting): User Bulb
// z^8 (numerical DE) 444 -> 29 ms a frame (15x), KIFS Menger / Octahedron 1.5x,
// MandelboxRot 3.8x; the bake takes 0.07-0.65 s for KIFS and ~3 s for User Bulb, in
// the background. The preview's surface is the trilinear blend's zero set, so a
// feature thinner than a voxel can vanish from it (a shell — the iso-surface a
// fraction of a voxel out — inflated User Bulb into a ball: its DE gradient spikes
// near the surface). The other CPU families and the GPU kernels: #1390.
//
// Exactness: an interactive frame that marched the cache is a preview (its detail is
// bounded by the voxel size). The host renders one Settle frame without the cache
// when the view stops, which is exactly today's frame; batch, export and every
// headless render never install a cache.
//
// The cache is ambient (an AsyncLocal): the host installs it around the live view's
// 3D Calculate (Use); a family asks for its field (Acquire) with a key of its DE
// parameters, its bounding cube and the view distance, before its pixel loop.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#1336 — the live view's baked distance fields (see the file header).</summary>
public sealed class DistanceFieldCache
{
    /// <summary>Voxels on a brick's edge.</summary>
    public const int BrickSize = 8;
    /// <summary>Resolution bounds, in voxels across the family's bounding cube.</summary>
    public const int MinLevel = 5, MaxLevel = 8;   // 2·2^level voxels: 64..512
    /// <summary>The level chosen has a voxel of at most this many pixels at the view distance.</summary>
    public const double TargetVoxelPixels = 1.5;
    /// <summary>A field is used while its voxel is at most this many pixels.</summary>
    public const double MaxVoxelPixels = 4.0;

    private static readonly AsyncLocal<DistanceFieldCache?> s_current = new();

    /// <summary>The cache installed for this render (null = march the true DE).</summary>
    public static DistanceFieldCache? Current => s_current.Value;

    /// <summary>Installs <paramref name="cache"/> for the calling flow until the scope is disposed.</summary>
    public static Scope Use(DistanceFieldCache? cache)
    {
        var prev = s_current.Value;
        s_current.Value = cache;
        return new Scope(prev);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly DistanceFieldCache? _prev;
        internal Scope(DistanceFieldCache? prev) => _prev = prev;
        public void Dispose() => s_current.Value = _prev;
    }

    private readonly object _lock = new();
    private readonly List<DistanceField> _fields = new();   // ≤ 2: the level in use + the one baking

    /// <summary>Bake inline in <see cref="Acquire"/> (tests, probes).</summary>
    public bool SynchronousBake { get; set; }

    /// <summary>Background bake threads.</summary>
    public int BakeThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Memory budget for one field's bricks, bytes.</summary>
    public long BudgetBytes { get; set; } = 96L << 20;

    /// <summary>True when the last render marched a cached field (an approximate frame).</summary>
    public bool LastFrameApproximate { get; private set; }

    /// <summary>The field the last render marched (null = none).</summary>
    public DistanceField? LastField { get; private set; }

    /// <summary>Called by the host before each render it installs the cache for.</summary>
    public void BeginRender()
    {
        LastFrameApproximate = false;
        LastField = null;
    }

    /// <summary>Drops every field (cancelling their bakes).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var f in _fields) f.Cancel();
            _fields.Clear();
        }
        LastField = null;
    }

    /// <summary>The level whose voxel is at most <see cref="TargetVoxelPixels"/> pixels
    /// for a cube of half-size <paramref name="half"/> seen from <paramref name="viewDistance"/>,
    /// <paramref name="pixelAngle"/> = world units per pixel per unit distance.</summary>
    public static int LevelFor(double half, double viewDistance, double pixelAngle)
    {
        double target = TargetVoxelPixels * viewDistance * pixelAngle;
        int level = MinLevel;
        while (level < MaxLevel && half / (1 << level) > target) level++;
        return level;
    }

    /// <summary>The field to march this frame, or null (march the true DE). Starts a
    /// bake when the key or the wanted level has none. <paramref name="deKey"/> covers
    /// every DE parameter; the cube (<paramref name="cx"/>, <paramref name="cy"/>,
    /// <paramref name="cz"/>, <paramref name="half"/>) bounds the surface;
    /// <paramref name="hitEps"/> is the march's hit threshold (a cell whose corners are
    /// all below it is interior: its brick bakes last); the camera
    /// position orders the bricks; <paramref name="viewDistance"/> ×
    /// <paramref name="pixelAngle"/> is a pixel's size at the subject.</summary>
    public DistanceField? Acquire(long deKey, double cx, double cy, double cz, double half, double hitEps,
        double camX, double camY, double camZ, double viewDistance, double pixelAngle,
        Func<double, double, double, double> de)
    {
        if (!(half > 0) || !(viewDistance > 0) || !(pixelAngle > 0)) return null;
        int level = LevelFor(half, viewDistance, pixelAngle);
        double maxVoxel = MaxVoxelPixels * viewDistance * pixelAngle;
        DistanceField? baking = null, pick = null;
        lock (_lock)
        {
            // A new scene drops every field of the old one.
            for (int i = _fields.Count - 1; i >= 0; i--)
                if (_fields[i].DeKey != deKey || _fields[i].Cx != cx || _fields[i].Cy != cy
                    || _fields[i].Cz != cz || _fields[i].Half != half || _fields[i].HitEps != hitEps)
                { _fields[i].Cancel(); _fields.RemoveAt(i); }

            DistanceField? want = null;
            foreach (var f in _fields) if (f.Level == level) want = f;
            if (want is null)
            {
                // Keep at most one other level (the one serving while this one bakes).
                while (_fields.Count >= 2) { _fields[0].Cancel(); _fields.RemoveAt(0); }
                want = new DistanceField(deKey, level, cx, cy, cz, half, hitEps, de);
                _fields.Add(want);
                baking = want;
            }

            // The wanted level once it is complete; until then a complete field that is
            // fine enough; else the wanted level's progressive state (coarse grid ready).
            if (want.Complete) pick = want;
            else
            {
                foreach (var f in _fields)
                    if (f != want && f.Complete && f.Voxel <= maxVoxel && (pick is null || f.Level > pick.Level))
                        pick = f;
                if (pick is null && want.CoarseReady) pick = want;
            }
        }
        if (baking is not null)
        {
            if (SynchronousBake) { baking.Bake(camX, camY, camZ, BakeThreads, BudgetBytes); if (baking.Voxel <= maxVoxel) pick = baking; }
            else baking.StartBackground(camX, camY, camZ, BakeThreads, BudgetBytes);
        }
        if (pick is null || pick.Voxel > maxVoxel) return null;
        LastFrameApproximate = true;
        LastField = pick;
        return pick;
    }
}

/// <summary>#1336 — one baked sparse distance field (see <see cref="DistanceFieldCache"/>).
/// <see cref="Sample"/> is safe to call from any thread while the bake runs.</summary>
public sealed class DistanceField
{
    private const int B = DistanceFieldCache.BrickSize;
    private const int B1 = B + 1;
    private const int ProbeCells = 32;
    // The corner differences under-read the DE's gradient inside a cell: a cell is
    // EMPTY only when its smallest corner clears the hit threshold by this many
    // (gradient x diagonal).
    private const double EmptyMargin = 1.5;

    private const byte KindEmpty = 0, KindNear = 1, KindInterior = 2;

    public long DeKey { get; }
    public int Level { get; }
    public double Cx { get; }
    public double Cy { get; }
    public double Cz { get; }
    public double Half { get; }
    public double HitEps { get; }
    /// <summary>The voxel edge, world units.</summary>
    public double Voxel { get; }

    private readonly Func<double, double, double, double> _de;
    private readonly CancellationTokenSource _cts = new();

    // Grid (valid once CoarseReady): origin, cell edge, dims, corner DE, cell kinds, bricks.
    private double _ox, _oy, _oz, _cell, _invCell;
    private int _gx, _gy, _gz;
    private float[] _corner = Array.Empty<float>();
    private byte[] _kind = Array.Empty<byte>();
    private float[]?[] _brick = Array.Empty<float[]?>();
    private int[] _order = Array.Empty<int>();   // NEAR cells, nearest the camera first, within the budget
    private volatile bool _coarseReady, _complete;
    private int _baked;

    internal DistanceField(long deKey, int level, double cx, double cy, double cz, double half, double hitEps,
        Func<double, double, double, double> de)
    {
        DeKey = deKey; Level = level; Cx = cx; Cy = cy; Cz = cz; Half = half; HitEps = hitEps; _de = de;
        Voxel = half / (1 << level);
    }

    /// <summary>The coarse grid is built: <see cref="Sample"/> may be called.</summary>
    public bool CoarseReady => _coarseReady;
    /// <summary>Every brick within the budget is baked.</summary>
    public bool Complete => _complete;
    /// <summary>Cells that may hold surface (bricks wanted), interior ones included.</summary>
    public int NearCells { get; private set; }
    /// <summary>Of <see cref="NearCells"/>, those with every corner inside the set.</summary>
    public int InteriorCells { get; private set; }
    /// <summary>NEAR cells that will get a brick (the rest evaluate the true DE).</summary>
    public int BudgetedBricks => _order.Length;
    public int BakedBricks => Volatile.Read(ref _baked);
    public int GridX => _gx;
    public int GridY => _gy;
    public int GridZ => _gz;
    /// <summary>DE evaluations the bake made.</summary>
    public long BakeEvaluations { get; private set; }
    public double CoarseMs { get; private set; }
    public double BakeMs { get; private set; }
    public long BrickBytes => (long)BudgetedBricks * B1 * B1 * B1 * sizeof(float);

    internal void Cancel() => _cts.Cancel();

    internal void StartBackground(double camX, double camY, double camZ, int threads, long budget)
    {
        var t = new Thread(() =>
        {
            // A DE that throws (a User Bulb formula) leaves the field unusable, never
            // the process down: the frames keep marching the true DE.
            try { Bake(camX, camY, camZ, threads, budget); }
            catch (Exception) { }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "DistanceFieldBake" };
        t.Start();
    }

    /// <summary>Builds the field: the probe, the coarse grid, then the bricks on
    /// <paramref name="threads"/> threads (the caller's included).</summary>
    internal void Bake(double camX, double camY, double camZ, int threads, long budget)
    {
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();
        long evals = 0;

        // 1. Probe: the box of probe cells that may hold the surface, padded by one.
        double pc = 2 * Half / ProbeCells;
        int pn = ProbeCells + 1;
        var probe = new float[pn * pn * pn];
        Run(pn, threads, k =>
        {
            for (int j = 0; j < pn && !ct.IsCancellationRequested; j++)
                for (int i = 0; i < pn; i++)
                    probe[(k * pn + j) * pn + i] = (float)_de(Cx - Half + i * pc, Cy - Half + j * pc, Cz - Half + k * pc);
        }, ct);
        evals += (long)pn * pn * pn;
        int lo0 = ProbeCells, lo1 = ProbeCells, lo2 = ProbeCells, hi0 = -1, hi1 = -1, hi2 = -1;
        for (int k = 0; k < ProbeCells; k++)
            for (int j = 0; j < ProbeCells; j++)
                for (int i = 0; i < ProbeCells; i++)
                {
                    if (Classify(probe, pn, pn, pn, i, j, k, HitEps) == KindEmpty) continue;
                    lo0 = Math.Min(lo0, i); hi0 = Math.Max(hi0, i);
                    lo1 = Math.Min(lo1, j); hi1 = Math.Max(hi1, j);
                    lo2 = Math.Min(lo2, k); hi2 = Math.Max(hi2, k);
                }
        if (hi0 < 0)
        {
            // No surface in the cube: an all-empty one-cell grid (rays leave it).
            lo0 = lo1 = lo2 = 0; hi0 = hi1 = hi2 = -1;
        }
        else
        {
            lo0 = Math.Max(0, lo0 - 1); lo1 = Math.Max(0, lo1 - 1); lo2 = Math.Max(0, lo2 - 1);
            hi0 = Math.Min(ProbeCells - 1, hi0 + 1); hi1 = Math.Min(ProbeCells - 1, hi1 + 1); hi2 = Math.Min(ProbeCells - 1, hi2 + 1);
        }

        // 2. Coarse grid over that box: cubic cells of B voxels, corner DE, kinds.
        double cell = Voxel * B;
        double bx0 = Cx - Half + lo0 * pc, by0 = Cy - Half + lo1 * pc, bz0 = Cz - Half + lo2 * pc;
        int gx = Math.Max(1, (int)Math.Ceiling((hi0 - lo0 + 1) * pc / cell - 1e-9));
        int gy = Math.Max(1, (int)Math.Ceiling((hi1 - lo1 + 1) * pc / cell - 1e-9));
        int gz = Math.Max(1, (int)Math.Ceiling((hi2 - lo2 + 1) * pc / cell - 1e-9));
        int cxn = gx + 1, cyn = gy + 1, czn = gz + 1;
        var corner = new float[cxn * cyn * czn];
        bool none = hi0 < 0;
        if (!none)
        {
            Run(czn, threads, k =>
            {
                for (int j = 0; j < cyn && !ct.IsCancellationRequested; j++)
                    for (int i = 0; i < cxn; i++)
                        corner[(k * cyn + j) * cxn + i] = (float)_de(bx0 + i * cell, by0 + j * cell, bz0 + k * cell);
            }, ct);
            evals += (long)cxn * cyn * czn;
        }
        else Array.Fill(corner, float.MaxValue);

        var kind = new byte[gx * gy * gz];
        var near = new List<(double D, int C)>();
        for (int k = 0; k < gz; k++)
            for (int j = 0; j < gy; j++)
                for (int i = 0; i < gx; i++)
                {
                    byte kc = none ? KindEmpty : Classify(corner, cxn, cyn, czn, i, j, k, HitEps);
                    if (kc == KindEmpty) continue;
                    int c = (k * gy + j) * gx + i;
                    // INTERIOR cells (every corner already a hit: inside the set) may still
                    // hold surface (a hole smaller than the cell), so they get a brick too,
                    // but after every surface cell: a solid body has far more of them.
                    bool interior = kc == KindInterior;
                    kind[c] = kc;
                    double dx = bx0 + (i + 0.5) * cell - camX, dy = by0 + (j + 0.5) * cell - camY, dz = bz0 + (k + 0.5) * cell - camZ;
                    near.Add((dx * dx + dy * dy + dz * dz + (interior ? 1e300 : 0), c));
                }
        near.Sort((a, b) => a.D.CompareTo(b.D));
        InteriorCells = 0;
        foreach (var n in near) if (kind[n.C] == KindInterior) InteriorCells++;
        long perBrick = (long)B1 * B1 * B1 * sizeof(float);
        int budgeted = (int)Math.Min(near.Count, Math.Max(0, budget / perBrick));
        var order = new int[budgeted];
        for (int n = 0; n < budgeted; n++) order[n] = near[n].C;

        _ox = bx0; _oy = by0; _oz = bz0; _cell = cell; _invCell = 1.0 / cell;
        _gx = gx; _gy = gy; _gz = gz;
        _corner = corner; _kind = kind; _brick = new float[]?[gx * gy * gz]; _order = order;
        NearCells = near.Count;
        CoarseMs = sw.Elapsed.TotalMilliseconds;
        _coarseReady = true;   // volatile: publishes the grid above

        // 3. Bricks, nearest the camera first. (The preview surface is the blend's zero
        // set: a feature thinner than a voxel can vanish from it. A shell — the iso-surface
        // at a fraction of a voxel times the brick's DE gradient — was measured and
        // rejected: User Bulb's DE spikes near the surface, and the shell inflated the
        // bulb into a ball.)
        int next = -1;
        Run(threads, threads, _ =>
        {
            while (true)
            {
                int n = Interlocked.Increment(ref next);
                if (n >= order.Length || ct.IsCancellationRequested) return;
                int c = order[n];
                int i = c % gx, j = c / gx % gy, k = c / (gx * gy);
                double x0 = bx0 + i * cell, y0 = by0 + j * cell, z0 = bz0 + k * cell;
                var brick = new float[B1 * B1 * B1];
                // A cancelled bake stops within a row of samples (a DE can be slow).
                for (int bk = 0; bk < B1; bk++)
                    for (int bj = 0; bj < B1; bj++)
                    {
                        if (ct.IsCancellationRequested) return;
                        for (int bi = 0; bi < B1; bi++)
                            brick[(bk * B1 + bj) * B1 + bi] = (float)_de(x0 + bi * Voxel, y0 + bj * Voxel, z0 + bk * Voxel);
                    }
                Volatile.Write(ref _brick[c], brick);
                Interlocked.Increment(ref _baked);
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        evals += (long)order.Length * B1 * B1 * B1;
        BakeEvaluations = evals;
        BakeMs = sw.Elapsed.TotalMilliseconds;
        _complete = true;
    }

    /// <summary>A cell's kind from the corner DE values (see the file header): EMPTY
    /// when min − EmptyMargin·g·√3 &gt; hitEps, min over the cell's 8 corners and g the
    /// largest corner difference along a grid edge among the 4x4x4 corners around it (the
    /// DE's change across a cell, so no cell size enters); INTERIOR when every corner of
    /// the cell is below hitEps; else NEAR. <paramref name="nz"/> bounds the block.</summary>
    internal static byte Classify(float[] g, int nx, int ny, int nz, int i, int j, int k, double hitEps)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int n = 0; n < 8; n++)
        {
            double v = Corner(g, nx, ny, i + (n & 1), j + ((n >> 1) & 1), k + (n >> 2));
            min = Math.Min(min, v); max = Math.Max(max, v);
        }
        if (max < hitEps) return KindInterior;
        int i0 = Math.Max(0, i - 1), i1 = Math.Min(nx - 1, i + 2);
        int j0 = Math.Max(0, j - 1), j1 = Math.Min(ny - 1, j + 2);
        int k0 = Math.Max(0, k - 1), k1 = Math.Min(nz - 1, k + 2);
        double grad = 0;
        for (int kk = k0; kk <= k1; kk++)
            for (int jj = j0; jj <= j1; jj++)
                for (int ii = i0; ii <= i1; ii++)
                {
                    double v = Corner(g, nx, ny, ii, jj, kk);
                    if (ii < i1) grad = Math.Max(grad, Math.Abs(Corner(g, nx, ny, ii + 1, jj, kk) - v));
                    if (jj < j1) grad = Math.Max(grad, Math.Abs(Corner(g, nx, ny, ii, jj + 1, kk) - v));
                    if (kk < k1) grad = Math.Max(grad, Math.Abs(Corner(g, nx, ny, ii, jj, kk + 1) - v));
                }
        return min - EmptyMargin * grad * Math.Sqrt(3) > hitEps ? KindEmpty : KindNear;
    }

    // A corner's DE; a NaN reads as a hit (0), an infinity as far.
    private static double Corner(float[] g, int nx, int ny, int i, int j, int k)
    {
        double v = g[(k * ny + j) * nx + i];
        return double.IsFinite(v) ? v : v > 0 ? double.MaxValue / 4 : 0;
    }

    // Runs body(0..count-1) on up to `threads` threads (the caller's included).
    private static void Run(int count, int threads, Action<int> body, CancellationToken ct)
    {
        int workers = Math.Clamp(threads, 1, count);
        int next = -1;
        Exception? error = null;
        void Work()
        {
            try
            {
                int n;
                while ((n = Interlocked.Increment(ref next)) < count && !ct.IsCancellationRequested && error is null) body(n);
            }
            catch (Exception e) { Interlocked.CompareExchange(ref error, e, null); }
        }
        if (workers == 1)
        {
            Work();
            if (error is not null) throw error;
            ct.ThrowIfCancellationRequested();
            return;
        }
        var ts = new Thread[workers - 1];
        for (int t = 0; t < ts.Length; t++)
        {
            ts[t] = new Thread(Work) { IsBackground = true, Priority = Thread.CurrentThread.Priority };
            ts[t].Start();
        }
        Work();
        foreach (var t in ts) t.Join();
        if (error is not null) throw error;
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The cached distance at (x, y, z): trilinear in a baked brick, the true
    /// DE in a NEAR cell without one, the corner bound in an EMPTY cell, and the
    /// distance to the grid box (plus half a voxel, so a ray steps into it) outside.
    /// Requires <see cref="CoarseReady"/>.</summary>
    public double Sample(double x, double y, double z)
    {
        double fx = (x - _ox) * _invCell, fy = (y - _oy) * _invCell, fz = (z - _oz) * _invCell;
        if (!(fx >= 0 && fy >= 0 && fz >= 0 && fx < _gx && fy < _gy && fz < _gz))
            return OutsideDistance(x, y, z);
        int i = (int)fx, j = (int)fy, k = (int)fz;
        int c = (k * _gy + j) * _gx + i;
        var brick = Volatile.Read(ref _brick[c]);
        if (brick is not null)
        {
            double u = (fx - i) * B, v = (fy - j) * B, w = (fz - k) * B;
            int bi = Math.Min(B - 1, (int)u), bj = Math.Min(B - 1, (int)v), bk = Math.Min(B - 1, (int)w);
            double tu = u - bi, tv = v - bj, tw = w - bk;
            int b0 = (bk * B1 + bj) * B1 + bi;
            double c00 = brick[b0] + (brick[b0 + 1] - brick[b0]) * tu;
            double c10 = brick[b0 + B1] + (brick[b0 + B1 + 1] - brick[b0 + B1]) * tu;
            int b1 = b0 + B1 * B1;
            double c01 = brick[b1] + (brick[b1 + 1] - brick[b1]) * tu;
            double c11 = brick[b1 + B1] + (brick[b1 + B1 + 1] - brick[b1 + B1]) * tu;
            double c0 = c00 + (c10 - c00) * tv, c1 = c01 + (c11 - c01) * tv;
            return c0 + (c1 - c0) * tw;
        }
        if (_kind[c] == KindNear) return _de(x, y, z);
        if (_kind[c] == KindInterior) return 0;   // inside the set until its brick lands
        // EMPTY: the trilinear blend of the corners (at least the smallest corner, so
        // above the hit threshold: never a false hit).
        int cxn = _gx + 1, cyn = _gy + 1;
        double ax = fx - i, ay = fy - j, az = fz - k;
        int g0 = (k * cyn + j) * cxn + i, g1 = g0 + cxn * cyn;
        double e00 = _corner[g0] + (_corner[g0 + 1] - _corner[g0]) * ax;
        double e10 = _corner[g0 + cxn] + (_corner[g0 + cxn + 1] - _corner[g0 + cxn]) * ax;
        double e01 = _corner[g1] + (_corner[g1 + 1] - _corner[g1]) * ax;
        double e11 = _corner[g1 + cxn] + (_corner[g1 + cxn + 1] - _corner[g1 + cxn]) * ax;
        double e0 = e00 + (e10 - e00) * ay, e1 = e01 + (e11 - e01) * ay;
        return e0 + (e1 - e0) * az;
    }

    private double OutsideDistance(double x, double y, double z)
    {
        double ex = Math.Max(Math.Max(_ox - x, x - (_ox + _gx * _cell)), 0);
        double ey = Math.Max(Math.Max(_oy - y, y - (_oy + _gy * _cell)), 0);
        double ez = Math.Max(Math.Max(_oz - z, z - (_oz + _gz * _cell)), 0);
        return Math.Sqrt(ex * ex + ey * ey + ez * ez) + 0.5 * Voxel;
    }

    /// <summary>The kind of the cell holding (x, y, z) for tests: -1 outside the grid,
    /// 0 empty, 1 near without a brick, 2 baked brick.</summary>
    public int CellKindAt(double x, double y, double z)
    {
        double fx = (x - _ox) * _invCell, fy = (y - _oy) * _invCell, fz = (z - _oz) * _invCell;
        if (!(fx >= 0 && fy >= 0 && fz >= 0 && fx < _gx && fy < _gy && fz < _gz)) return -1;
        int c = ((int)fz * _gy + (int)fy) * _gx + (int)fx;
        return _brick[c] is not null ? 2 : _kind[c];
    }

    /// <summary>The grid box (min corner, max corner).</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) GridBox
        => (_ox, _oy, _oz, _ox + _gx * _cell, _oy + _gy * _cell, _oz + _gz * _cell);
}
