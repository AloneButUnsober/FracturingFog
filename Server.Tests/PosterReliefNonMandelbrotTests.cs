// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for the Relief-3D poster bug: PosterRenderer applied heightfield
// relief only on the Mandelbrot branch, so a poster/still of a Relief-3D scene
// on any non-Mandelbrot escape-time family (Tricorn, Julia, Burning Ship, …)
// silently fell back to the flat 2D themed colour. These render a Tricorn
// still with relief ON vs OFF and assert the output differs — pre-fix the two
// were byte-identical because relief was never applied.

using System.IO;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PosterReliefNonMandelbrotTests
{
    private static FractalParameters ReliefParams(bool enabled) => new()
    {
        Relief2DEnabled = enabled,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
    };

    private static byte[] RenderTricorn(bool relief, string path)
    {
        var req = new PosterRequest
        {
            FractalType = FractalType.Tricorn,
            CenterX = 0, CenterY = 0,
            Zoom = 1.0,
            MaxIterations = 200,
            Width = 160, Height = 120,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = ReliefParams(relief),
            Path = path,
            Format = ImageFileFormat.Png,
        };
        PosterRenderer.RenderToFile(req, default);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Tricorn_Poster_Applies_Relief_When_Enabled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ff-poster-relief-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] flat   = RenderTricorn(relief: false, Path.Combine(dir, "flat.png"));
            byte[] relief = RenderTricorn(relief: true,  Path.Combine(dir, "relief.png"));

            // Relief raymarch reshapes the image entirely (lit terrain + sky),
            // so the two encodes must differ. Byte-identical == relief was
            // silently skipped (the bug).
            Assert.False(System.Linq.Enumerable.SequenceEqual(flat, relief),
                "Tricorn relief poster is byte-identical to the flat render — relief was not applied.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
