// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/MeshRepair.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — export-time manifold
// auto-repair. The roadmap draws the line explicitly: auto-repair to GUARANTEE a
// manifold, correctly-wound solid ON EXPORT is in-lane; an interactive repair
// workbench is not. This is the former — a pure, appearance-preserving cleanup a
// caller can run before writing:
//
//   • drop DEGENERATE triangles (a repeated welded corner → zero area);
//   • drop DUPLICATE faces (the same welded triangle emitted twice);
//   • flood-fill a CONSISTENT winding so adjacent faces traverse their shared edge
//     in opposite directions (fixes flipped normals), then flip globally if the
//     signed volume is negative so the whole solid faces OUTWARD.
//
// It welds coincident vertices ONLY to reason about topology (a triangle soup would
// otherwise read as all-boundary); the OUTPUT keeps the original vertex indices, so
// per-vertex colour and normals are untouched — only bad triangles are removed and
// windings are corrected. It does NOT fill holes or cut non-manifold edges: those
// fabricate or delete geometry and belong to an interactive tool, not an export
// guarantee. On an already-clean, already-outward mesh it is a no-op.

using System;
using System.Collections.Generic;

namespace FracturingFog.Export;

/// <summary>What a <see cref="MeshRepair"/> pass changed.</summary>
public readonly record struct MeshRepairReport(
    int RemovedDegenerate,
    int RemovedDuplicate,
    int ReorientedFaces,
    bool GloballyFlipped)
{
    public bool ChangedAnything =>
        RemovedDegenerate > 0 || RemovedDuplicate > 0 || ReorientedFaces > 0 || GloballyFlipped;
}

/// <summary>Appearance-preserving export-time mesh repair: drop degenerate /
/// duplicate faces and make the winding consistent + outward (roadmap S9, #391).</summary>
public static class MeshRepair
{
    /// <summary>Return a cleaned, consistently-outward-wound triangle list over the
    /// SAME <paramref name="positions"/> (indices preserved, so colour/normals are
    /// untouched). <paramref name="weldEpsilon"/> snaps coincident vertices for the
    /// topology pass only.</summary>
    public static (List<(int A, int B, int C)> triangles, MeshRepairReport report)
        Repair(IReadOnlyList<(double X, double Y, double Z)> positions,
               IReadOnlyList<(int A, int B, int C)> triangles,
               double weldEpsilon = 1e-6)
    {
        double inv = weldEpsilon > 0 ? 1.0 / weldEpsilon : 1e6;

        // Weld: snap each position to a grid cell → canonical id. First original
        // vertex seen for an id is its representative position (for signed volume).
        var canon = new Dictionary<(long, long, long), int>();
        var weld = new int[positions.Count];
        var repPos = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var key = ((long)Math.Round(p.X * inv), (long)Math.Round(p.Y * inv), (long)Math.Round(p.Z * inv));
            if (!canon.TryGetValue(key, out int id))
            {
                id = repPos.Count;
                canon[key] = id;
                repPos.Add(p);
            }
            weld[i] = id;
        }

        // Keep list: original triangles minus degenerate + duplicate (by welded key).
        var kept = new List<(int A, int B, int C)>(triangles.Count);
        var wkept = new List<(int a, int b, int c)>(triangles.Count);
        var seen = new HashSet<(int, int, int)>();
        int removedDegenerate = 0, removedDuplicate = 0;
        foreach (var t in triangles)
        {
            int a = weld[t.A], b = weld[t.B], c = weld[t.C];
            if (a == b || b == c || a == c) { removedDegenerate++; continue; }
            // Duplicate key ignores winding (sorted triple), so a doubled face in
            // either orientation is dropped once.
            int lo = Math.Min(a, Math.Min(b, c));
            int hi = Math.Max(a, Math.Max(b, c));
            int mid = a + b + c - lo - hi;
            if (!seen.Add((lo, mid, hi))) { removedDuplicate++; continue; }
            kept.Add(t);
            wkept.Add((a, b, c));
        }

        int fc = kept.Count;
        var flip = new bool[fc];
        int reoriented = 0;

        if (fc > 0)
        {
            // Undirected welded edge → incident (face, traversesLoToHi) entries.
            var edgeFaces = new Dictionary<(int, int), List<(int face, bool loToHi)>>();
            void AddEdge(int u, int v, int f)
            {
                int lo = Math.Min(u, v), hi = Math.Max(u, v);
                bool loToHi = u < v;   // does this face traverse lo->hi in its winding?
                if (!edgeFaces.TryGetValue((lo, hi), out var list))
                    edgeFaces[(lo, hi)] = list = new List<(int, bool)>();
                list.Add((f, loToHi));
            }
            for (int f = 0; f < fc; f++)
            {
                var (a, b, c) = wkept[f];
                AddEdge(a, b, f); AddEdge(b, c, f); AddEdge(c, a, f);
            }

            // BFS flood-fill: neighbours across a shared edge must traverse it in
            // OPPOSITE directions once each face's flip is applied.
            var visited = new bool[fc];
            var queue = new Queue<int>();
            for (int s = 0; s < fc; s++)
            {
                if (visited[s]) continue;
                visited[s] = true;
                queue.Enqueue(s);
                while (queue.Count > 0)
                {
                    int f = queue.Dequeue();
                    var (a, b, c) = wkept[f];
                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    {
                        int lo = Math.Min(u, v), hi = Math.Max(u, v);
                        bool fLoToHi = (u < v) ^ flip[f];   // this face's effective dir
                        foreach (var (nf, nLoToHi) in edgeFaces[(lo, hi)])
                        {
                            if (nf == f) continue;
                            bool nEff = nLoToHi ^ flip[nf];
                            if (!visited[nf])
                            {
                                // Want nEff != fLoToHi → set neighbour flip.
                                flip[nf] = (nLoToHi == fLoToHi);
                                visited[nf] = true;
                                queue.Enqueue(nf);
                            }
                        }
                    }
                }
            }

            // Signed volume of the reoriented mesh (welded representative positions).
            double sv = 0.0;
            for (int f = 0; f < fc; f++)
            {
                var (a, b, c) = wkept[f];
                if (flip[f]) (b, c) = (c, b);
                var p0 = repPos[a]; var p1 = repPos[b]; var p2 = repPos[c];
                sv += p0.X * (p1.Y * p2.Z - p1.Z * p2.Y)
                    - p0.Y * (p1.X * p2.Z - p1.Z * p2.X)
                    + p0.Z * (p1.X * p2.Y - p1.Y * p2.X);
            }
            bool globalFlip = sv < 0.0;

            var outTris = new List<(int A, int B, int C)>(fc);
            for (int f = 0; f < fc; f++)
            {
                var t = kept[f];
                bool doFlip = flip[f] ^ globalFlip;
                if (doFlip) { reoriented++; outTris.Add((t.A, t.C, t.B)); }
                else outTris.Add(t);
            }
            return (outTris, new MeshRepairReport(removedDegenerate, removedDuplicate, reoriented, globalFlip));
        }

        return (kept, new MeshRepairReport(removedDegenerate, removedDuplicate, 0, false));
    }
}
