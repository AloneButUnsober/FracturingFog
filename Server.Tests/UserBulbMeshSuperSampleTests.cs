// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using Xunit;
using FracturingFog.Export;

namespace FracturingFog.Server.Tests;

// #112 follow-up — corner supersampling. Box-averages an s×s×s DE stencil per
// grid corner to antialias sub-cell filaments into continuous surface. ss=1
// must be byte-identical to the un-supersampled default (regression guard);
// ss>1 must actually change a high-frequency field (wiring is live).
public class UserBulbMeshSuperSampleTests
{
    // High-frequency bumpy sphere: the sin term oscillates faster than a coarse
    // cell, so point-sampling (ss=1) aliases it and averaging (ss>1) smooths it.
    private static readonly SampleDistance Bumpy =
        (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0
                     - 0.25 * Math.Sin(18.0 * x) * Math.Sin(18.0 * y) * Math.Sin(18.0 * z);

    [Fact]
    public void SuperSamples1_IdenticalToDefault()
    {
        string a = Path.Combine(Path.GetTempPath(), $"ss_def_{Guid.NewGuid():N}.obj");
        string b = Path.Combine(Path.GetTempPath(), $"ss_one_{Guid.NewGuid():N}.obj");
        try
        {
            UserBulbMeshExporter.ExportMarchingCubes(a, Bumpy, 0, 0, 0, 2.0, 40); // default ss=1
            UserBulbMeshExporter.ExportMarchingCubes(b, Bumpy, 0, 0, 0, 2.0, 40, 0.5, false, 1);
            Assert.Equal(File.ReadAllText(a), File.ReadAllText(b));
        }
        finally
        {
            if (File.Exists(a)) File.Delete(a);
            if (File.Exists(b)) File.Delete(b);
        }
    }

    [Fact]
    public void SuperSamples2_ChangesHighFrequencyField()
    {
        string one = Path.Combine(Path.GetTempPath(), $"ss1_{Guid.NewGuid():N}.obj");
        string two = Path.Combine(Path.GetTempPath(), $"ss2_{Guid.NewGuid():N}.obj");
        try
        {
            int t1 = UserBulbMeshExporter.ExportMarchingCubes(one, Bumpy, 0, 0, 0, 2.0, 40, 0.5, false, 1);
            int t2 = UserBulbMeshExporter.ExportMarchingCubes(two, Bumpy, 0, 0, 0, 2.0, 40, 0.5, false, 2);
            Assert.True(t1 > 0 && t2 > 0, "both should produce a mesh");
            Assert.NotEqual(File.ReadAllText(one), File.ReadAllText(two));
        }
        finally
        {
            if (File.Exists(one)) File.Delete(one);
            if (File.Exists(two)) File.Delete(two);
        }
    }
}
