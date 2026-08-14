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
    private static FractalParameters ReliefParams(bool enabled, double heightScale) => new()
    {
        Relief2DEnabled = enabled,
        Relief2DRaymarch = true,
        Relief2DHeightScale = heightScale,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
    };

    private static byte[] RenderTricorn(FractalParameters fp, string path)
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
            FractalParameters = fp,
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
            byte[] flat   = RenderTricorn(ReliefParams(false, 1.4), Path.Combine(dir, "flat.png"));
            byte[] relief = RenderTricorn(ReliefParams(true, 1.4),  Path.Combine(dir, "relief.png"));

            Assert.False(System.Linq.Enumerable.SequenceEqual(flat, relief),
                "Tricorn relief poster is byte-identical to the flat render — relief was not applied.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Tricorn_Poster_Relief_Has_Real_Height_Structure()
    {
        // A tilted flat plane (no terrain) is invariant to height scale; real
        // heightfield relief is not. Render the same Tricorn poster at a tall vs
        // near-flat height scale and require a substantial pixel difference —
        // proves the SAVED height field actually raises terrain, not just tilts
        // a 2D image onto an angled plane.
        string dir = Path.Combine(Path.GetTempPath(), "ff-poster-height-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] tall = RenderTricorn(ReliefParams(true, 1.5),  Path.Combine(dir, "tall.png"));
            byte[] flat = RenderTricorn(ReliefParams(true, 0.02), Path.Combine(dir, "flat.png"));
            Assert.False(System.Linq.Enumerable.SequenceEqual(tall, flat),
                "Tricorn relief poster is invariant to height scale — terrain is flat (no 3D structure).");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
