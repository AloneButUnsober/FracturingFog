// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Threading;
using Xunit;
using FracturingFog.Export;

namespace FracturingFog.Server.Tests;

// #269 follow-up — mesh export is cancellable via the busy chip. A cancelled
// token must stop the marching cubes and leave the target file untouched (no
// empty stub clobbering a prior export).
public class UserBulbMeshCancelTests
{
    private static readonly SampleDistance Sphere =
        (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;

    [Fact]
    public void CancelledToken_WritesNothing_ReturnsZero()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cancel_{Guid.NewGuid():N}.obj");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere, 0, 0, 0, 2.0, 48, 0.5, false, 1, 180.0, cts.Token);

            Assert.Equal(0, tris);
            Assert.False(File.Exists(path), "a cancelled export must not write (or clobber) the file");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LiveToken_ExportsNormally()
    {
        // Sanity: the same call with a live token still produces a mesh (the
        // cancel guard doesn't short-circuit the normal path).
        string path = Path.Combine(Path.GetTempPath(), $"live_{Guid.NewGuid():N}.obj");
        using var cts = new CancellationTokenSource();
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere, 0, 0, 0, 2.0, 48, 0.5, false, 1, 180.0, cts.Token);
            Assert.True(tris > 0);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
