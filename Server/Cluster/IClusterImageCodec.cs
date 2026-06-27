// Server/Cluster/IClusterImageCodec.cs
// Pixel codec boundary for the cluster merge path. The Server library
// is UI-free and platform-free — it never references SkiaSharp directly.
// The hosting WinExe / Avalonia shell registers a concrete impl
// (typically Skia-backed) when constructing the coordinator. Tests
// register a trivial raw-RGBA-with-header codec.

namespace FracturingFog.Server.Cluster;

public interface IClusterImageCodec
{
    /// <summary>Decode a PNG byte stream into a packed BGRA32 pixel
    /// buffer. Throws on malformed input. The returned buffer's length
    /// must be exactly width × height × 4.</summary>
    byte[] DecodePngToBgra(byte[] png, out int width, out int height);

    /// <summary>Encode a packed BGRA32 pixel buffer (length = width ×
    /// height × 4) into a PNG file at <paramref name="outPath"/>. The
    /// hosting impl may also embed DPI metadata or other side-channels
    /// — D-2 callers do not require them.</summary>
    void EncodeBgraToPng(byte[] bgra, int width, int height, string outPath);
}
