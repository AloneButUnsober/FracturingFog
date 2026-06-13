// Imaging/ImageFormatBridge.cs
//
// Windows-only converter that maps System.Drawing.Imaging.ImageFormat to the
// engine's portable FracturingFog.Imaging.ImageFileFormat. The four WinExe
// save-path call sites (BatchRenderer, ImageCapture, ServerHost, the Avalonia
// poster bootstrap) historically derived the format from the file extension
// using ImageFormat literals; the Phase X.A engine carve flipped
// PosterRequest.Format and FractalRenderHost.CreatePosterRequest's signature
// to ImageFileFormat so the engine no longer references GDI+ statics.
//
// Lives in the WinExe (and is [SupportedOSPlatform("windows")]) because the
// ImageFormat.Png / .Jpeg / .Bmp / .Tiff static accessors are Windows-only.

using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace FracturingFog.Imaging
{
    [SupportedOSPlatform("windows")]
    internal static class ImageFormatBridge
    {
        public static ImageFileFormat ToFileFormat(this ImageFormat fmt)
        {
            if (fmt == ImageFormat.Png)  return ImageFileFormat.Png;
            if (fmt == ImageFormat.Jpeg) return ImageFileFormat.Jpeg;
            if (fmt == ImageFormat.Bmp)  return ImageFileFormat.Bmp;
            if (fmt == ImageFormat.Gif)  return ImageFileFormat.Gif;
            if (fmt == ImageFormat.Tiff) return ImageFileFormat.Tiff;
            return ImageFileFormat.Auto;
        }
    }
}
