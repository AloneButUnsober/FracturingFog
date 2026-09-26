// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// MeshExportCapabilities is the single list behind the params-panel Export Mesh
// button, RaymarchMeshSampler and the --meshexport gate. The UI used to keep its
// own list and offered the button for Coquaternion with no sampler behind it
// ("no distance-estimated surface" on click).
public sealed class MeshExportCapabilityTests
{
    [Fact]
    public void EveryExportableType_BuildsADistanceEstimator()
    {
        foreach (var t in MeshExportCapabilities.Types)
        {
            Assert.True(RaymarchMeshSampler.IsMeshExportable(t), $"{t}");
            Assert.NotNull(RaymarchMeshSampler.For(t, new FractalParameters()));
        }
    }

    [Fact]
    public void EveryRaymarch3DType_IsExportable_ExceptUserBulb()
    {
        // A new 3D DE type must opt into mesh export (or be added here with a reason).
        var raymarch = Enum.GetValues<FractalType>()
            .Where(t => FractalMotionCapabilities.MotionClass(t) == FractalMotionClass.Raymarch3D)
            .Where(t => t != FractalType.UserBulb)   // compiled-kernel DE, exports via its editor
            .ToArray();
        Assert.NotEmpty(raymarch);
        foreach (var t in raymarch)
            Assert.True(MeshExportCapabilities.IsMeshExportable(t), $"{t} is a DE raymarcher without mesh export");
    }

    [Fact]
    public void Coquaternion_SamplerDE_MatchesTheRender()
    {
        var p = new FractalParameters { CoquaternionSliceW = 0.2, CoquaternionIterations = 9, CoquaternionBailout = 20 };
        var de = RaymarchMeshSampler.For(FractalType.Coquaternion, p)!;
        // What CoquaternionMandelbrotCalculator.Calculate builds for the same params.
        var render = new CoquaternionMandelbrotCalculator.De(0.2, Math.Max(4.0, 20.0), Math.Max(2, 9));
        var rng = new Random(853);
        for (int i = 0; i < 200; i++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2, z = rng.NextDouble() * 4 - 2;
            Assert.Equal(render.Evaluate(x, y, z), de.Evaluate(x, y, z), 12);
        }
    }

    [Fact]
    public void Coquaternion_ExportsTriangles()
    {
        var p = new FractalParameters();
        var de = RaymarchMeshSampler.For(FractalType.Coquaternion, p)!;
        string path = Path.Combine(Path.GetTempPath(), $"ff-coq-mesh-{Guid.NewGuid():N}.obj");
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, de, 0, 0, 0, RaymarchMeshSampler.SuggestedRange(FractalType.Coquaternion, p), 40);
            Assert.True(tris > 100, $"only {tris} triangles");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
