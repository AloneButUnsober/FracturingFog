// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 export-time manifold repair (3D-Rendering-Roadmap §S9, #391) —
// the in-lane "guarantee a manifold, outward solid on export" pass. These lock:
//   • a mesh with a flipped face + a degenerate + a duplicate is repaired to a
//     closed, 2-manifold, OUTWARD solid, and the report names what it removed;
//   • repair is idempotent — a second pass on clean output changes nothing;
//   • a globally inward-wound solid is flipped outward.

using System.Collections.Generic;
using FracturingFog.Export;
using Xunit;

namespace FracturingFog.Server.Tests;

public class MeshRepairTests
{
    // Unit cube, 8 corners.
    private static List<(double X, double Y, double Z)> CubeVerts() => new()
    {
        (0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1),
    };

    // 12 outward-wound triangles (CCW seen from outside).
    private static List<(int A, int B, int C)> CubeTris() => new()
    {
        (1,2,6),(1,6,5),   // +X
        (0,4,7),(0,7,3),   // -X
        (3,7,6),(3,6,2),   // +Y
        (0,1,5),(0,5,4),   // -Y
        (4,5,6),(4,6,7),   // +Z
        (0,3,2),(0,2,1),   // -Z
    };

    [Fact]
    public void Base_Cube_Is_A_Valid_Outward_Solid()
    {
        var r = MeshValidator.Validate(CubeVerts(), CubeTris());
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.True(r.SignedVolume > 0, r.Summary());   // the fixtures are outward
    }

    [Fact]
    public void Repair_Fixes_Flipped_And_Drops_Bad_Faces()
    {
        var verts = CubeVerts();
        var tris = CubeTris();
        // Corrupt: flip one face (swap b,c), add a degenerate, add a duplicate.
        tris[0] = (tris[0].A, tris[0].C, tris[0].B);
        tris.Add((0, 0, 1));           // degenerate (repeated corner)
        tris.Add((1, 6, 5));           // duplicate of the original tris[1]

        var (fixedTris, rep) = MeshRepair.Repair(verts, tris);

        Assert.True(rep.RemovedDegenerate >= 1);
        Assert.True(rep.RemovedDuplicate >= 1);
        Assert.True(rep.ReorientedFaces >= 1);

        var r = MeshValidator.Validate(verts, fixedTris);
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.True(r.SignedVolume > 0, r.Summary());   // wound outward again
    }

    [Fact]
    public void Repair_Is_Idempotent_On_Clean_Output()
    {
        var verts = CubeVerts();
        var (once, _) = MeshRepair.Repair(verts, CubeTris());
        var (twice, rep2) = MeshRepair.Repair(verts, once);
        Assert.False(rep2.ChangedAnything);
        Assert.Equal(once.Count, twice.Count);
    }

    [Fact]
    public void Repair_Flips_A_Globally_Inward_Solid_Outward()
    {
        var verts = CubeVerts();
        var inward = new List<(int A, int B, int C)>();
        foreach (var t in CubeTris()) inward.Add((t.A, t.C, t.B));   // reverse every face

        var before = MeshValidator.Validate(verts, inward);
        Assert.True(before.SignedVolume < 0, before.Summary());       // inside-out

        var (fixedTris, rep) = MeshRepair.Repair(verts, inward);
        Assert.True(rep.GloballyFlipped);
        var after = MeshValidator.Validate(verts, fixedTris);
        Assert.True(after.IsClosedManifold, after.Summary());
        Assert.True(after.SignedVolume > 0, after.Summary());
    }
}
