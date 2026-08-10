// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;
using FracturingFog.Export;

namespace FracturingFog.Server.Tests;

// #112 follow-up — crease-angle normals. A cube (Chebyshev DE) has flat axis-
// aligned faces meeting at hard 90° edges. Smooth-everything (180) averages
// across those edges → rounded, diagonal normals. A low crease angle splits the
// edge → each face keeps its axis-aligned normal (facets stay crisp) and the
// vertex count grows.
public class UserBulbMeshCreaseTests
{
    // Chebyshev "box": iso-surface is a cube; faces are axis planes.
    private static readonly SampleDistance Box =
        (x, y, z) => Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) - 1.0;

    private static (int vCount, double axisFrac) ReadNormals(string path)
    {
        int v = 0, n = 0, axis = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal)) v++;
            else if (line.StartsWith("vn ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double nx = Math.Abs(double.Parse(p[1], CultureInfo.InvariantCulture));
                double ny = Math.Abs(double.Parse(p[2], CultureInfo.InvariantCulture));
                double nz = Math.Abs(double.Parse(p[3], CultureInfo.InvariantCulture));
                if (Math.Max(nx, Math.Max(ny, nz)) > 0.99) axis++;
                n++;
            }
        }
        return (v, n > 0 ? (double)axis / n : 0.0);
    }

    [Fact]
    public void Crease_KeepsBoxFacets_SmoothRoundsThem()
    {
        string smooth = Path.Combine(Path.GetTempPath(), $"cr_sm_{Guid.NewGuid():N}.obj");
        string crease = Path.Combine(Path.GetTempPath(), $"cr_hd_{Guid.NewGuid():N}.obj");
        try
        {
            // 180 = smooth everything (crease pass skipped); 30 = keep facets.
            UserBulbMeshExporter.ExportMarchingCubes(smooth, Box, 0, 0, 0, 2.0, 32, 0.5, false, 1, 180.0);
            UserBulbMeshExporter.ExportMarchingCubes(crease, Box, 0, 0, 0, 2.0, 32, 0.5, false, 1, 30.0);

            var (vSmooth, axisSmooth) = ReadNormals(smooth);
            var (vCrease, axisCrease) = ReadNormals(crease);

            // Primary proof: creasing SPLITS the shared hard-edge/corner vertices
            // (each side keeps its own axis-aligned normal), so the crease mesh has
            // strictly more vertices than the fully-welded smooth mesh.
            Assert.True(vCrease > vSmooth,
                $"crease should split hard edges into extra vertices ({vCrease} vs {vSmooth})");
            // Creasing never rounds a flat facet, so it is at least as axis-aligned
            // as smoothing, and the flat box faces keep most normals on-axis.
            Assert.True(axisCrease >= axisSmooth,
                $"crease ({axisCrease:P1}) should be no less axis-aligned than smooth ({axisSmooth:P1})");
            Assert.True(axisCrease > 0.6,
                $"crease should keep most facets axis-aligned ({axisCrease:P1})");
        }
        finally
        {
            if (File.Exists(smooth)) File.Delete(smooth);
            if (File.Exists(crease)) File.Delete(crease);
        }
    }
}
