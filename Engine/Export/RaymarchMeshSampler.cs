// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/RaymarchMeshSampler.cs
//
// Builds a CPU distance-estimator for any 3D distance-estimation raymarcher so
// UserBulbMeshExporter's marching-cubes / voxel export works for the whole
// family, not just the User Bulb (#101). The exporter core is already DE-
// agnostic (it takes a SampleDistance delegate); the only thing that was
// UserBulb-specific was *obtaining* the DE. This factory closes that gap.
//
// Each arm mirrors the DE-parameter derivation the matching calculator does at
// the top of its Calculate(): the raymarch scenes live in object space centred
// on the origin, so the DE here is in that same space — sample a cube of side
// 2*range about (0, 0, 0). If a calculator changes how it derives its DE
// params, update the matching arm (kept in one place so a new fractal is one
// switch arm, not a scatter of edits).
//
// UserBulb is intentionally excluded: its DE needs the compiled user kernel,
// which only the render host holds. The host supplies that sampler directly
// (FractalRenderHost.SampleUserBulbDE) — see AvaloniaShellBootstrap.

using System;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Export;

/// <summary>
/// Object-space DE factory for the 3D raymarcher family. See file header.
/// </summary>
public static class RaymarchMeshSampler
{
    /// <summary>True when <see cref="For"/> can build a mesh sampler for this
    /// type. UserBulb is excluded (compiled-kernel DE — host supplies it).</summary>
    public static bool IsMeshExportable(FractalType type) => type switch
    {
        FractalType.Mandelbulb
            or FractalType.Mandelbox
            or FractalType.Kifs
            or FractalType.QuaternionJulia
            or FractalType.QuaternionMandelbrot
            or FractalType.Kleinian
            or FractalType.BicomplexMandelbrot => true,
        _ => false,
    };

    /// <summary>
    /// Builds the object-space distance estimator for <paramref name="type"/>,
    /// or <c>null</c> if it isn't a supported raymarcher. The estimate is
    /// centred on the origin — pass this to <see cref="UserBulbMeshExporter"/>
    /// with centre (0, 0, 0) and a range from <see cref="SuggestedRange"/>.
    /// </summary>
    public static IDistanceEstimator? For(FractalType type, FractalParameters p)
    {
        switch (type)
        {
            case FractalType.Mandelbulb:
                return new MandelbulbDe(p.BulbPower, Math.Max(2, p.BulbIterations));

            case FractalType.Mandelbox:
            {
                double fixedR = Math.Max(1e-3, p.MandelboxFixedRadius);
                double minR   = Math.Max(1e-3, p.MandelboxMinRadius);
                double bail   = Math.Max(16.0, p.MandelboxBailout);
                return new MandelboxCalculator.De(
                    p.MandelboxScale, fixedR * fixedR, minR * minR, bail * bail,
                    Math.Max(2, p.MandelboxIterations));
            }

            case FractalType.QuaternionJulia:
                return new QuatJuliaCalculator.De(
                    p.QJuliaSliceW, p.QJuliaCX, p.QJuliaCY, p.QJuliaCZ, p.QJuliaCW,
                    Math.Max(4.0, p.QJuliaBailout), Math.Max(2, p.QJuliaIterations));

            case FractalType.QuaternionMandelbrot:
                return new QuatMandelbrotCalculator.De(
                    p.QMandelSliceZ, p.QMandelSliceW,
                    Math.Max(4.0, p.QMandelBailout), Math.Max(2, p.QMandelIterations));

            case FractalType.BicomplexMandelbrot:
                return new BicomplexMandelbrotCalculator.De(
                    p.BicomplexSliceW, p.BicomplexSliceAxis,
                    Math.Max(4.0, p.BicomplexBailout), Math.Max(2, p.BicomplexIterations));

            case FractalType.Kleinian:
            {
                double scaleK = Math.Max(0.25, p.KleinianSphereScale);
                double r = Math.Sqrt(2.0) * scaleK;
                double[] cx = { +scaleK, +scaleK, -scaleK, -scaleK };
                double[] cy = { +scaleK, -scaleK, +scaleK, -scaleK };
                double[] cz = { +scaleK, -scaleK, -scaleK, +scaleK };
                return new KleinianCalculator.De(cx, cy, cz, r, Math.Max(2, p.KleinianIterations));
            }

            case FractalType.Kifs:
            {
                var fold = p.KifsFold;
                double rawScale = p.KifsScale;
                double scale = rawScale > 0.0
                    ? rawScale
                    : (fold == KifsFoldKind.Menger ? 3.0 : 2.0);
                double ox = p.KifsOffsetX, oy = p.KifsOffsetY, oz = p.KifsOffsetZ;
                int iter = Math.Max(2, p.KifsIterations);
                return new DelegateDeAdapter((x, y, z) =>
                    KifsCalculator.ProbeDE(fold, x, y, z, scale, ox, oy, oz, iter));
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Object-space half-extent that bounds the fractal for the export cube.
    /// Mirrors each Calculate()'s setRadius so marching cubes doesn't clip the
    /// surface. Types whose set fits in the unit-2 ball return 2.0.
    /// </summary>
    public static double SuggestedRange(FractalType type, FractalParameters p) => type switch
    {
        FractalType.Mandelbox => 2.0 * Math.Abs(p.MandelboxScale) + 2.0,
        FractalType.Kleinian  => Math.Max(0.25, p.KleinianSphereScale)
                                 * (Math.Sqrt(3.0) + Math.Sqrt(2.0)),
        _ => 2.0,
    };
}
