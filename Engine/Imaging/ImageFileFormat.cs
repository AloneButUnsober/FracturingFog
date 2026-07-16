// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ImageFileFormat.cs
//
// Cross-platform encoded image format token used across the engine's save
// pipeline. Replaces System.Drawing.Imaging.ImageFormat in engine code —
// ImageFormat's static accessors (ImageFormat.Png / .Jpeg / .Bmp / .Tiff)
// are annotated [SupportedOSPlatform("windows")] and fire CA1416 when
// referenced from an engine that ships on Linux + macOS.
//
// The enum carries only the high-level format intent; codec specifics
// (PNG compression level, JPEG quality) live behind the encoder selection
// inside ImageExport.

namespace FracturingFog.Imaging
{
    /// <summary>
    /// Format selector for <see cref="ImageExport"/> save calls. Cross-platform
    /// alternative to <c>System.Drawing.Imaging.ImageFormat</c>.
    /// </summary>
    public enum ImageFileFormat
    {
        /// <summary>Infer from the file extension on the save path.
        /// Falls back to PNG when the extension is unknown.</summary>
        Auto = 0,
        Png,
        Jpeg,
        Bmp,
        Gif,
        Tiff,
        Webp,
    }
}
