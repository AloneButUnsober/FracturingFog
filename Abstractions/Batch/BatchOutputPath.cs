// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Batch/BatchOutputPath.cs
// #1543 — which `--out` values name an image file rather than a directory.

using System.IO;

namespace FracturingFog.Batch
{
    public static class BatchOutputPath
    {
        /// <summary>True when <paramref name="path"/> ends in an extension the batch
        /// writes (every one BatchRenderer.GuessImageFormat recognises). Anything else is
        /// treated as a directory and gets a synthesized `.png` name; before #1543 a
        /// typed `foo.exr` / `foo.jpg` fell into that branch (roadmap S7, #394).</summary>
        public static bool HasImageExtension(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".tif" or ".tiff" or ".bmp"
                or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".exr";
    }
}
