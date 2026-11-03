// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1543 — a typed `--out foo.exr` / `.jpg` / `.jpeg` / `.gif` / `.webp` was taken for a
// directory: the batch created a folder of that name and wrote a synthesized `.png`
// inside it (roadmap S7, #394). Every extension the batch writes is now a file path.
// Mutant: the old four-extension check (.png/.tif/.tiff/.bmp) fails the theory for
// the five added extensions.

using FracturingFog.Batch;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1543TypedOutPathTests
{
    [Theory]
    [InlineData("shot.png")]
    [InlineData("shot.tif")]
    [InlineData("shot.tiff")]
    [InlineData("shot.bmp")]
    [InlineData("shot.jpg")]
    [InlineData("shot.jpeg")]
    [InlineData("shot.gif")]
    [InlineData("shot.webp")]
    [InlineData("shot.exr")]
    [InlineData(@"C:\renders\SHOT.EXR")]
    public void A_Typed_Image_Extension_Is_A_File(string path) =>
        Assert.True(BatchOutputPath.HasImageExtension(path), path);

    [Theory]
    [InlineData("renders")]
    [InlineData("renders/")]
    [InlineData("shot.txt")]
    [InlineData("v1.2")]
    public void Anything_Else_Is_A_Directory(string path) =>
        Assert.False(BatchOutputPath.HasImageExtension(path), path);
}
