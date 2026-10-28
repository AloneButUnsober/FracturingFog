// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BilliardRayCrossings.cs (#1508, slice S3 of #1505)
//
// Where Billiard Rays paths cross. Each ray, in drawn (sweep) order, is checked
// against the K rays drawn just before it (the window) and, counted separately,
// against itself. Only crossings inside the view are kept, since only they can be
// drawn.
//
// Search: a uniform grid of cells over the view, filled as the rays arrive. A ray's
// segments are first looked up and then added, each in the cells it passes through
// (the Amanatides-Woo cell walk, as in the density splat; a bounding box would put a
// long diagonal exit ray in most of the grid). Cells list segments in arrival order,
// so the rays that have left the window are a prefix that a per-cell start pointer
// skips, and periodic compaction frees. Two crossing segments both pass through the
// cell holding their crossing, so the pair is found; meeting it again in another
// shared cell is skipped. A crossing is a proper one: the segments' interiors cross
// (shared end points and parallel overlaps do not count).
//
// The finder lives for one render and sees every drawn ray in order, so a window
// that spans the calculator's chunk boundaries behaves exactly like one that does
// not, and the crossings come out in a fixed order (ray, segment, cell, entry)
// whatever the core count.

using System;
using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog;

/// <summary>One crossing of two Billiard Rays paths (#1508). The current ray is the later one in
/// drawn order; <see cref="IsSelf"/> when it crosses its own path.</summary>
/// <param name="X">World x of the crossing.</param>
/// <param name="Y">World y of the crossing.</param>
/// <param name="Current">Drawn index of the current (later) ray.</param>
/// <param name="Previous">Drawn index of the previous (earlier) ray; equals Current for a self-crossing.</param>
/// <param name="CurrentSegment">Segment of the current ray (segment k runs from point k - 1 to k, so k - 1 bounces precede it).</param>
/// <param name="PreviousSegment">Segment of the previous ray.</param>
/// <param name="Angle">Acute angle between the two segments, radians in [0, pi/2].</param>
/// <param name="CurrentFromEntry">Path length along the current ray from its start to the crossing.</param>
/// <param name="CurrentToExit">Path length along the current ray from the crossing to its end.</param>
/// <param name="PreviousFromEntry">The same along the previous ray.</param>
/// <param name="PreviousToExit">The same along the previous ray.</param>
public readonly record struct BilliardRayCrossing(
    double X, double Y, int Current, int Previous, int CurrentSegment, int PreviousSegment, double Angle,
    double CurrentFromEntry, double CurrentToExit, double PreviousFromEntry, double PreviousToExit)
{
    public bool IsSelf => Current == Previous;

    /// <summary>Current minus previous bounces at the crossing.</summary>
    public int DeltaN => CurrentSegment - PreviousSegment;
}

/// <summary>Finds the crossings of Billiard Rays paths fed in drawn order (#1508).</summary>
public sealed class BilliardCrossingFinder
{
    /// <summary>Most rays a window may hold (cost and memory grow with it).</summary>
    public const int MaxWindow = 50_000;

    private readonly int _window;
    private readonly double _x0, _x1, _y0, _y1, _cs;
    private readonly int _nx, _ny;
    private readonly List<int>?[] _cells;
    private readonly int[] _start;

    // Live segments; id = _baseId + position. Rays' first segment ids, for the window edge.
    private readonly List<Seg> _segs = new();
    private int _baseId;
    private readonly Queue<(int Ray, int FirstId)> _rayFirst = new();
    private int _liveFrom;               // first segment id still in the window
    private int _rays;                   // rays added so far (= the next drawn index)

    private readonly List<int> _walk = new(64);
    private readonly HashSet<int> _seen = new();

    private readonly record struct Seg(BilliardPoint A, BilliardPoint B, int Ray, int K, double From, double Total);

    /// <summary>A finder over the world rectangle [x0, x1) x [y0, y1) with square cells of
    /// <paramref name="cellSize"/>.</summary>
    public BilliardCrossingFinder(int window, double x0, double x1, double y0, double y1, double cellSize)
    {
        _window = Math.Clamp(window, 0, MaxWindow);
        _x0 = x0; _x1 = x1; _y0 = y0; _y1 = y1;
        // At most 1024 cells a side: Escaped mode's play-circle grid at a deep zoom would
        // otherwise hold millions of cells.
        _cs = Math.Max(cellSize, Math.Max(x1 - x0, y1 - y0) / 1024.0);
        _nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / _cs));
        _ny = Math.Max(1, (int)Math.Ceiling((y1 - y0) / _cs));
        _cells = new List<int>?[_nx * _ny];
        _start = new int[_nx * _ny];
    }

    /// <summary>Add the next drawn ray (its index is the number of rays added before it) and append
    /// its crossings with the window, and with itself, to <paramref name="output"/>.</summary>
    public void Add(IReadOnlyList<BilliardPoint> pts, List<BilliardRayCrossing> output)
    {
        int r = _rays++;
        // Retire rays that left the window: their segments are below _liveFrom.
        while (_rayFirst.Count > 0 && _rayFirst.Peek().Ray < r - _window) _rayFirst.Dequeue();
        _liveFrom = _rayFirst.Count > 0 ? _rayFirst.Peek().FirstId : _baseId + _segs.Count;
        if ((r & 1023) == 1023) Compact();
        _rayFirst.Enqueue((r, _baseId + _segs.Count));

        double total = 0;
        for (int k = 1; k < pts.Count; k++) total += Len(pts[k - 1], pts[k]);
        double from = 0;
        for (int k = 1; k < pts.Count; k++)
        {
            var a = pts[k - 1]; var b = pts[k];
            double la = Len(a, b);
            CellWalk(a, b);
            if (_walk.Count > 0)
            {
                _seen.Clear();
                foreach (int ci in _walk)
                {
                    var cell = _cells[ci];
                    if (cell == null) continue;
                    int e = _start[ci];
                    while (e < cell.Count && cell[e] < _liveFrom) e++;
                    _start[ci] = e;
                    for (; e < cell.Count; e++)
                    {
                        int id = cell[e];
                        var s2 = _segs[id - _baseId];
                        if (s2.Ray == r && s2.K == k - 1) continue;   // adjacent segment of this ray
                        if (!_seen.Add(id)) continue;                  // pair already met in another cell
                        if (!Cross(a, b, s2.A, s2.B, out double s, out double t)) continue;
                        double px = a.X + s * (b.X - a.X), py = a.Y + s * (b.Y - a.Y);
                        if (px < _x0 || px >= _x1 || py < _y0 || py >= _y1) continue;   // only crossings in view
                        double lb = Len(s2.A, s2.B);
                        double dot = Math.Abs((b.X - a.X) * (s2.B.X - s2.A.X) + (b.Y - a.Y) * (s2.B.Y - s2.A.Y)) / Math.Max(1e-300, la * lb);
                        double curFrom = from + s * la, prevFrom = s2.From + t * lb;
                        output.Add(new BilliardRayCrossing(px, py, r, s2.Ray, k, s2.K, Math.Acos(Math.Min(1.0, dot)),
                            curFrom, total - curFrom, prevFrom, s2.Total - prevFrom));
                    }
                }
                int newId = _baseId + _segs.Count;
                _segs.Add(new Seg(a, b, r, k, from, total));
                foreach (int ci in _walk) (_cells[ci] ??= new List<int>()).Add(newId);
            }
            from += la;
        }
    }

    // Drop retired segments from the table and every cell.
    private void Compact()
    {
        int drop = _liveFrom - _baseId;
        if (drop <= 0) return;
        for (int ci = 0; ci < _cells.Length; ci++)
        {
            var cell = _cells[ci];
            if (cell == null) continue;
            int e = 0;
            while (e < cell.Count && cell[e] < _liveFrom) e++;
            if (e > 0) cell.RemoveRange(0, e);
            _start[ci] = 0;
        }
        _segs.RemoveRange(0, drop);
        _baseId = _liveFrom;
    }

    private static double Len(BilliardPoint a, BilliardPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // The grid cells segment a-b passes through, in order, into _walk (clipped to the grid;
    // empty when it misses). Liang-Barsky clip in cell units, then the Amanatides-Woo walk.
    private void CellWalk(BilliardPoint a, BilliardPoint b)
    {
        _walk.Clear();
        double ax = (a.X - _x0) / _cs, ay = (a.Y - _y0) / _cs, dx = (b.X - a.X) / _cs, dy = (b.Y - a.Y) / _cs;
        if (!double.IsFinite(ax + ay + dx + dy)) return;
        double t0 = 0, t1 = 1;
        if (!Clip(-dx, ax, ref t0, ref t1) || !Clip(dx, _nx - ax, ref t0, ref t1)
            || !Clip(-dy, ay, ref t0, ref t1) || !Clip(dy, _ny - ay, ref t0, ref t1) || t1 < t0) return;
        double tm = t0 + Math.Min(1e-9, (t1 - t0) * 0.5);
        int ix = Math.Clamp((int)Math.Floor(ax + dx * tm), 0, _nx - 1);
        int iy = Math.Clamp((int)Math.Floor(ay + dy * tm), 0, _ny - 1);
        int sx = dx > 0 ? 1 : dx < 0 ? -1 : 0, sy = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        double tMaxX = sx != 0 ? ((ix + (sx > 0 ? 1 : 0)) - ax) / dx : double.PositiveInfinity;
        double tMaxY = sy != 0 ? ((iy + (sy > 0 ? 1 : 0)) - ay) / dy : double.PositiveInfinity;
        double tDx = sx != 0 ? Math.Abs(1.0 / dx) : double.PositiveInfinity;
        double tDy = sy != 0 ? Math.Abs(1.0 / dy) : double.PositiveInfinity;
        int guard = _nx + _ny + 4;
        while (guard-- > 0)
        {
            _walk.Add(iy * _nx + ix);
            if (Math.Min(tMaxX, tMaxY) >= t1) break;
            if (tMaxX <= tMaxY) { ix += sx; tMaxX += tDx; } else { iy += sy; tMaxY += tDy; }
            if (ix < 0 || ix >= _nx || iy < 0 || iy >= _ny) break;
        }
    }

    private static bool Clip(double pp, double q, ref double t0, ref double t1)
    {
        if (pp == 0) return q >= 0;
        double r = q / pp;
        if (pp < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    /// <summary>Proper crossing of a-b and c-d: a + s (b - a) = c + t (d - c) with s, t strictly inside (0, 1).</summary>
    public static bool Cross(BilliardPoint a, BilliardPoint b, BilliardPoint c, BilliardPoint d, out double s, out double t)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, qx = d.X - c.X, qy = d.Y - c.Y;
        double den = rx * qy - ry * qx;
        s = t = 0;
        if (Math.Abs(den) < 1e-300) return false;
        double ex = c.X - a.X, ey = c.Y - a.Y;
        s = (ex * qy - ey * qx) / den;
        t = (ex * ry - ey * rx) / den;
        const double eps = 1e-12;
        return s > eps && s < 1 - eps && t > eps && t < 1 - eps;
    }
}
