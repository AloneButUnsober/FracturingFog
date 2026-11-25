// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VoronoiElement.cs (#2025, Cosmic Web epic #2020)
//
// The third Cosmic Web generator: a Voronoi clustering model, after the description in
// R. van de Weygaert, "Voronoi Tessellations and the Cosmic Web: Spatial Patterns and Clustering
// across the Universe" (arXiv:0707.2877, ISVD 2007), Sec. 4.1. Nuclei are scattered at random in the
// box and their Voronoi tessellation is the skeleton of the web: its walls are walls of galaxies, its
// edges the filaments and its vertices the compact clusters. Each galaxy starts at a random place in
// the cell of its nearest nucleus and moves radially outward from that nucleus until it reaches a
// wall; a wall galaxy stops there, a filament galaxy goes on inside the wall (its motion across the
// wall suppressed) until it reaches an edge, and a cluster galaxy goes on along the edge until it
// reaches a vertex. Field galaxies stay where they started. Each structure is given a Gaussian width
// (the paper's R_W, R_F, R_V; FF uses one width, Thickness, for all three).
//
// FF's own parts: the exact geometry of each move (no tessellation is built): the wall is the first
// bisector plane of the nucleus and a neighbour the ray crosses, s_b = |v_b|^2 / (2 e . v_b); on the
// wall the galaxy moves away from the midpoint of the two nuclei, in the wall's plane, to the first
// bisector of a third neighbour; on the edge it moves along v_b x v_c, continuing its motion, to the
// first bisector of a fourth. Neighbours are the 64 nearest periodic images of the nuclei (Voronoi
// cells of random nuclei have about 15 neighbours on average), so the box is periodic. One SplitMix64
// stream per galaxy: the result does not depend on the thread count. The fractions of field, wall,
// filament and cluster galaxies are settings.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2025 — everything that changes the Voronoi points (the cache key).</summary>
public sealed record VoronoiSettings(int Seed, double BoxSize, int Nuclei, int Points,
    double Field, double Walls, double Filaments, double Clusters, double Thickness);

/// <summary>#2025 — which structure a Voronoi galaxy was placed on.</summary>
public enum VoronoiComponent : byte { Field, Wall, Filament, Cluster }

public static class VoronoiElement
{
    public const int MaxNuclei = 4096;
    public const int MaxPoints = 8_000_000;
    /// <summary>Periodic images kept as each nucleus's neighbour candidates.</summary>
    public const int Neighbours = 64;

    public static VoronoiSettings Sanitise(VoronoiSettings s) => s with
    {
        BoxSize = double.IsFinite(s.BoxSize) ? Math.Clamp(s.BoxSize, 1.0, 1e5) : 100.0,
        Nuclei = Math.Clamp(s.Nuclei, 2, MaxNuclei),
        Points = Math.Clamp(s.Points, 1, MaxPoints),
        Field = Frac(s.Field), Walls = Frac(s.Walls), Filaments = Frac(s.Filaments), Clusters = Frac(s.Clusters),
        Thickness = double.IsFinite(s.Thickness) ? Math.Clamp(s.Thickness, 0.0, 1e4) : 0.5,
    };

    private static double Frac(double f) => double.IsFinite(f) ? Math.Clamp(f, 0.0, 1.0) : 0.0;

    /// <summary>The nuclei (xyz interleaved, in [0, box)).</summary>
    public static double[] Nuclei(VoronoiSettings s)
    {
        var rng = new SplitMix64(SplitMix64.Mix((ulong)(uint)s.Seed, 0x2025UL));
        var n = new double[3 * s.Nuclei];
        for (int i = 0; i < n.Length; i++) n[i] = rng.NextDouble() * s.BoxSize;
        return n;
    }

    /// <summary>For each nucleus, the offsets (relative to it) of its <see cref="Neighbours"/> nearest periodic
    /// images of all nuclei, itself excluded at offset 0 (its own images at +-box are included).</summary>
    public static double[][] NeighbourOffsets(double[] nuclei, double box)
    {
        int m = nuclei.Length / 3;
        var result = new double[m][];
        Parallel.For(0, m, a =>
        {
            var cand = new (double D2, double X, double Y, double Z)[27 * m];
            int c = 0;
            for (int b = 0; b < m; b++)
                for (int iz = -1; iz <= 1; iz++)
                    for (int iy = -1; iy <= 1; iy++)
                        for (int ix = -1; ix <= 1; ix++)
                        {
                            double dx = MinImage(nuclei[3 * b] - nuclei[3 * a], box) + ix * box;
                            double dy = MinImage(nuclei[3 * b + 1] - nuclei[3 * a + 1], box) + iy * box;
                            double dz = MinImage(nuclei[3 * b + 2] - nuclei[3 * a + 2], box) + iz * box;
                            double d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 < 1e-24) continue;
                            cand[c++] = (d2, dx, dy, dz);
                        }
            Array.Sort(cand, 0, c, System.Collections.Generic.Comparer<(double, double, double, double)>.Create((p, q) => p.Item1.CompareTo(q.Item1)));
            int k = Math.Min(Neighbours, c);
            var o = new double[3 * k];
            for (int i = 0; i < k; i++) { o[3 * i] = cand[i].X; o[3 * i + 1] = cand[i].Y; o[3 * i + 2] = cand[i].Z; }
            result[a] = o;
        });
        return result;
    }

    public static double MinImage(double d, double box) => d - box * Math.Round(d / box);

    /// <summary>Index of the nucleus nearest to (x, y, z), periodic.</summary>
    public static int Nearest(double[] nuclei, double box, double x, double y, double z)
    {
        int best = 0;
        double bd = double.PositiveInfinity;
        for (int a = 0; a < nuclei.Length / 3; a++)
        {
            double dx = MinImage(x - nuclei[3 * a], box), dy = MinImage(y - nuclei[3 * a + 1], box), dz = MinImage(z - nuclei[3 * a + 2], box);
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 < bd) { bd = d2; best = a; }
        }
        return best;
    }

    /// <summary>The first bisector plane between the origin (the nucleus) and a neighbour that the ray
    /// p + t u (t &gt; 0) crosses, skipping the neighbours in <paramref name="skip1"/> / <paramref name="skip2"/>.
    /// Bisector of v: (q - v/2) . v = 0. Returns the neighbour index (or -1) and t.</summary>
    public static (int Index, double T) FirstWall(double[] nb, double px, double py, double pz, double ux, double uy, double uz, int skip1 = -1, int skip2 = -1)
    {
        int bi = -1;
        double bt = double.PositiveInfinity;
        for (int i = 0; i < nb.Length / 3; i++)
        {
            if (i == skip1 || i == skip2) continue;
            double vx = nb[3 * i], vy = nb[3 * i + 1], vz = nb[3 * i + 2];
            double den = ux * vx + uy * vy + uz * vz;
            if (den <= 1e-15) continue;
            double num = 0.5 * (vx * vx + vy * vy + vz * vz) - (px * vx + py * vy + pz * vz);
            double t = num / den;
            if (t < -1e-12) continue;
            if (t < bt) { bt = t; bi = i; }
        }
        return (bi, Math.Max(0, bt));
    }

    private static double Gauss(ref SplitMix64 rng)
    {
        // Box-Muller (Box & Muller 1958), one deviate.
        double u1 = rng.NextDouble(), u2 = rng.NextDouble();
        if (u1 < 1e-300) u1 = 1e-300;
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>One galaxy: its position (unwrapped, relative to the box origin) and the structure it reached.</summary>
    public static (double X, double Y, double Z, VoronoiComponent Component) Place(
        double[] nuclei, double[][] nbs, double box, VoronoiComponent target, double thickness, ref SplitMix64 rng)
    {
        double x = rng.NextDouble() * box, y = rng.NextDouble() * box, z = rng.NextDouble() * box;
        if (target == VoronoiComponent.Field) return (x, y, z, VoronoiComponent.Field);
        int a = Nearest(nuclei, box, x, y, z);
        double ax = nuclei[3 * a], ay = nuclei[3 * a + 1], az = nuclei[3 * a + 2];
        var nb = nbs[a];
        double ex = MinImage(x - ax, box), ey = MinImage(y - ay, box), ez = MinImage(z - az, box);
        if (ex * ex + ey * ey + ez * ez < 1e-24) { var (ux0, uy0, uz0) = rng.UnitVector(); ex = ux0; ey = uy0; ez = uz0; }

        // 1. Radially outward from the nucleus to the first wall.
        var (b, s) = FirstWall(nb, 0, 0, 0, ex, ey, ez);
        VoronoiComponent reached = VoronoiComponent.Wall;
        double px = s * ex, py = s * ey, pz = s * ez;
        if (b < 0) return (x, y, z, VoronoiComponent.Field);   // unbounded cell (too few nuclei): stays

        if (target >= VoronoiComponent.Filament)
        {
            // 2. Within the wall, away from the midpoint of the two nuclei, to the first edge.
            double vbx = nb[3 * b], vby = nb[3 * b + 1], vbz = nb[3 * b + 2];
            double vb2 = vbx * vbx + vby * vby + vbz * vbz;
            double ux = px - 0.5 * vbx, uy = py - 0.5 * vby, uz = pz - 0.5 * vbz;
            double dn = (ux * vbx + uy * vby + uz * vbz) / vb2;
            ux -= dn * vbx; uy -= dn * vby; uz -= dn * vbz;
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12)
            {
                var (rx, ry, rz) = rng.UnitVector();
                double rn = (rx * vbx + ry * vby + rz * vbz) / vb2;
                ux = rx - rn * vbx; uy = ry - rn * vby; uz = rz - rn * vbz;
                ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            }
            ux /= ul; uy /= ul; uz /= ul;
            var (c, t) = FirstWall(nb, px, py, pz, ux, uy, uz, b);
            if (c >= 0)
            {
                px += t * ux; py += t * uy; pz += t * uz;
                reached = VoronoiComponent.Filament;
                if (target == VoronoiComponent.Cluster)
                {
                    // 3. Along the edge (the line v_b x v_c), continuing the motion, to the first vertex.
                    double vcx = nb[3 * c], vcy = nb[3 * c + 1], vcz = nb[3 * c + 2];
                    double dx = vby * vcz - vbz * vcy, dy = vbz * vcx - vbx * vcz, dz = vbx * vcy - vby * vcx;
                    double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dl > 1e-15)
                    {
                        dx /= dl; dy /= dl; dz /= dl;
                        double dir = dx * ux + dy * uy + dz * uz;
                        if (dir < 0 || (dir == 0 && rng.NextDouble() < 0.5)) { dx = -dx; dy = -dy; dz = -dz; }
                        var (d, t2) = FirstWall(nb, px, py, pz, dx, dy, dz, b, c);
                        if (d >= 0) { px += t2 * dx; py += t2 * dy; pz += t2 * dz; reached = VoronoiComponent.Cluster; }
                    }
                }
            }
        }
        double jx = 0, jy = 0, jz = 0;
        if (thickness > 0) { jx = thickness * Gauss(ref rng); jy = thickness * Gauss(ref rng); jz = thickness * Gauss(ref rng); }
        return (ax + px + jx, ay + py + jy, az + pz + jz, reached);
    }

    /// <summary>The component of galaxy <paramref name="u"/> (uniform in [0,1)) by the normalised fractions.</summary>
    public static VoronoiComponent Pick(VoronoiSettings s, double u)
    {
        double total = s.Field + s.Walls + s.Filaments + s.Clusters;
        if (!(total > 0)) return VoronoiComponent.Field;
        double acc = s.Field / total;
        if (u < acc) return VoronoiComponent.Field;
        acc += s.Walls / total;
        if (u < acc) return VoronoiComponent.Wall;
        acc += s.Filaments / total;
        return u < acc ? VoronoiComponent.Filament : VoronoiComponent.Cluster;
    }

    /// <summary>Generates the point set (periodic box [0, box)^3). <paramref name="components"/> receives each
    /// galaxy's reached structure when given.</summary>
    public static CosmicWebPoints Generate(VoronoiSettings settings, CancellationToken ct = default, byte[]? components = null)
    {
        var s = Sanitise(settings);
        var nuclei = Nuclei(s);
        var nbs = NeighbourOffsets(nuclei, s.BoxSize);
        var xyz = new float[3 * s.Points];
        const int chunk = 4096;
        int chunks = (s.Points + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, ci =>
        {
            int end = Math.Min(s.Points, (ci + 1) * chunk);
            for (int i = ci * chunk; i < end; i++)
            {
                var rng = new SplitMix64(SplitMix64.Mix((ulong)(uint)s.Seed, 0x5EED0000UL + (ulong)i));
                var target = Pick(s, rng.NextDouble());
                var (x, y, z, comp) = Place(nuclei, nbs, s.BoxSize, target, s.Thickness, ref rng);
                xyz[3 * i] = (float)SoneiraPeebles.Wrap(x, s.BoxSize);
                xyz[3 * i + 1] = (float)SoneiraPeebles.Wrap(y, s.BoxSize);
                xyz[3 * i + 2] = (float)SoneiraPeebles.Wrap(z, s.BoxSize);
                if (components != null) components[i] = (byte)comp;
            }
        });
        return new CosmicWebPoints { Xyz = xyz, Count = s.Points, BoxSize = s.BoxSize, ClumpsRequested = s.Nuclei, ClumpsUsed = s.Nuclei };
    }
}
