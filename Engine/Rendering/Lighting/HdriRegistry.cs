// HdriRegistry.cs
//
// Phase 6b — Radiance .hdr (RGBE) equirectangular environment-map loader.
// Parses the classic Greg Ward .hdr format, decodes the RLE-compressed
// scanlines, and stores the result as a packed float[] of linear RGB.
//
// Public surface
//   HdriRegistry.TryGet(name, out HdriImage img)
//     Looks up a previously-loaded HDRI by case-insensitive name. Cache key
//     is whatever string the user routed through LightingFxData.EnvironmentName.
//
//   HdriRegistry.TryLoadFromFile(path, out HdriImage img)
//     Loads from a filesystem path. Cached on success so the next look-up
//     hits the in-memory copy. Name = Path.GetFileNameWithoutExtension(path).
//
// HdriImage.Sample(dirX, dirY, dirZ)
//   Equirectangular sampler — converts a world-space direction (assumed
//   roughly normalised) to (u, v) and bilinearly samples the float buffer.
//   Returns (R, G, B) in linear scene-referred units; caller applies
//   IblStrength + exposure as it sees fit.
//
// Notes
//   * The on-disk RGBE encoding is a 4-byte (R, G, B, E) pixel where the
//     linear value is e.g. R · 2^(E-128) / 256. Returning the decoded
//     float values matches the convention every other engine in the
//     graphics literature uses.
//   * Most .hdr files are tiny (a few MB) but we don't ration cache size;
//     trusting the user not to pile on hundreds of unique env maps in one
//     session. If that becomes a problem, an LRU eviction goes here.
//   * Y axis convention: U = atan2(z, x)/(2π) + 0.5, V = acos(y)/π so
//     pole-up = top of image. Matches the Blender / sIBL convention.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Decoded Radiance .hdr image — linear scene-referred RGB.</summary>
public sealed class HdriImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Packed RGB floats — index = (y · width + x) · 3.</summary>
    public float[] Data { get; }

    public HdriImage(int w, int h, float[] data)
    {
        Width = w;
        Height = h;
        Data = data;
    }

    /// <summary>Equirectangular sample. Returns linear RGB. Direction is
    /// expected to be roughly unit-length; callers that pass garbage get
    /// garbage back, no normalisation done here.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double R, double G, double B) Sample(double dirX, double dirY, double dirZ)
    {
        // atan2 returns [-π, π]; shift to [0, 1]. acos returns [0, π].
        double u = 0.5 + Math.Atan2(dirZ, dirX) * (1.0 / (2.0 * Math.PI));
        double v = Math.Acos(Math.Clamp(dirY, -1.0, 1.0)) * (1.0 / Math.PI);
        return SampleUv(u, v);
    }

    /// <summary>Bilinear UV sample. u wraps; v clamps (no antipodal wrap).</summary>
    public (double R, double G, double B) SampleUv(double u, double v)
    {
        // Wrap u so the seam at u=0 / u=1 stitches cleanly.
        u -= Math.Floor(u);
        if (v < 0) v = 0; else if (v > 1) v = 1;
        double fx = u * (Width - 1);
        double fy = v * (Height - 1);
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        int x1 = x0 + 1; if (x1 >= Width) x1 = 0;            // wrap
        int y1 = Math.Min(y0 + 1, Height - 1);
        double tx = fx - x0;
        double ty = fy - y0;
        int i00 = (y0 * Width + x0) * 3;
        int i10 = (y0 * Width + x1) * 3;
        int i01 = (y1 * Width + x0) * 3;
        int i11 = (y1 * Width + x1) * 3;
        double R = (1 - tx) * (1 - ty) * Data[i00]   + tx * (1 - ty) * Data[i10]
                 + (1 - tx) *      ty  * Data[i01]   + tx *      ty  * Data[i11];
        double G = (1 - tx) * (1 - ty) * Data[i00+1] + tx * (1 - ty) * Data[i10+1]
                 + (1 - tx) *      ty  * Data[i01+1] + tx *      ty  * Data[i11+1];
        double B = (1 - tx) * (1 - ty) * Data[i00+2] + tx * (1 - ty) * Data[i10+2]
                 + (1 - tx) *      ty  * Data[i01+2] + tx *      ty  * Data[i11+2];
        return (R, G, B);
    }
}

public static class HdriRegistry
{
    private static readonly ConcurrentDictionary<string, HdriImage> _byName
        = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? name, out HdriImage? image)
    {
        if (string.IsNullOrWhiteSpace(name)) { image = null; return false; }
        return _byName.TryGetValue(name, out image);
    }

    /// <summary>Register a pre-decoded image under a name. Used by tests
    /// and code paths that produce HDR data without a file (e.g. a
    /// procedural sky baked into an HDRI sampler).</summary>
    public static void Register(string name, HdriImage image)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _byName[name] = image;
    }

    /// <summary>Load a Radiance .hdr from disk, cache it, and return the
    /// decoded image. Returns false on parse error.</summary>
    public static bool TryLoadFromFile(string path, out HdriImage? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            image = ParseRadiance(fs);
            if (image != null)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                _byName[name] = image;
                return true;
            }
        }
        catch
        {
            // Surface failure as false — caller falls back to gradient sky.
        }
        return false;
    }

    /// <summary>Forget every cached HDRI. Useful when the user reloads
    /// presets or switches HDRI directories. Tests reset between cases.</summary>
    public static void ClearAll() => _byName.Clear();

    // ── Radiance parser ────────────────────────────────────────────────

    private static HdriImage? ParseRadiance(Stream s)
    {
        // Header is ASCII, terminated by a blank line, followed by a
        // resolution line like "-Y 512 +X 1024", then binary RLE pixel data.
        // We only support the common 32-bit_rle_rgbe / -Y/+X variant — the
        // rest of the spec (other axis orderings, uncompressed scanlines,
        // XYZE) is rare in the wild and we'd rather fail loudly than render
        // garbled colours.
        string? magic = ReadLine(s);
        if (magic == null || (!magic.StartsWith("#?RADIANCE") && !magic.StartsWith("#?RGBE")))
            return null;

        string? format = null;
        while (true)
        {
            string? line = ReadLine(s);
            if (line == null) return null;
            if (line.Length == 0) break;          // blank → end of header
            if (line.StartsWith("FORMAT=")) format = line.Substring(7).Trim();
            // Other header lines (EXPOSURE, COLORCORR, PRIMARIES) are
            // recognised but not consumed — they don't affect decoding to
            // linear RGB. We trust the producer applied them already.
        }
        if (format != null && !format.Equals("32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase))
            return null;

        string? resLine = ReadLine(s);
        if (resLine == null) return null;
        // Expected pattern: "-Y H +X W"
        var tokens = resLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 4) return null;
        if (tokens[0] != "-Y" || tokens[2] != "+X") return null;
        if (!int.TryParse(tokens[1], out int height)) return null;
        if (!int.TryParse(tokens[3], out int width)) return null;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;

        var rgbe = new byte[4 * width];
        var dataOut = new float[width * height * 3];

        for (int y = 0; y < height; y++)
        {
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0) return null;

            // Adaptive RLE scanlines (newer format) start with 0x02 0x02 + 2-byte
            // width. Older scanlines (or rows where width <= 8) write four
            // bytes per pixel uncompressed.
            int width2 = (b2 << 8) | b3;
            bool adaptive = b0 == 0x02 && b1 == 0x02 && (b2 & 0x80) == 0 && width2 == width;
            if (!adaptive)
            {
                // Fall through to old-format reader. Restore the four bytes
                // we already consumed as pixel 0 then read the remaining
                // (width-1)*4 bytes raw.
                rgbe[0] = (byte)b0;
                rgbe[1] = (byte)b1;
                rgbe[2] = (byte)b2;
                rgbe[3] = (byte)b3;
                int read = ReadExactly(s, rgbe, 4, 4 * width - 4);
                if (read != 4 * width - 4) return null;
            }
            else
            {
                // Adaptive RLE: 4 separate RLE passes, one per channel.
                for (int channel = 0; channel < 4; channel++)
                {
                    int x = 0;
                    while (x < width)
                    {
                        int count = s.ReadByte();
                        if (count < 0) return null;
                        if (count > 128)
                        {
                            int run = count - 128;
                            int val = s.ReadByte();
                            if (val < 0) return null;
                            if (x + run > width) return null;
                            for (int k = 0; k < run; k++)
                                rgbe[(x + k) * 4 + channel] = (byte)val;
                            x += run;
                        }
                        else
                        {
                            // Non-RLE run: read `count` literal bytes.
                            if (count == 0) return null;
                            if (x + count > width) return null;
                            for (int k = 0; k < count; k++)
                            {
                                int v = s.ReadByte();
                                if (v < 0) return null;
                                rgbe[(x + k) * 4 + channel] = (byte)v;
                            }
                            x += count;
                        }
                    }
                }
            }

            // Decode RGBE → linear float.
            int rowBase = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                byte e = rgbe[x * 4 + 3];
                if (e == 0)
                {
                    dataOut[rowBase + x * 3 + 0] = 0;
                    dataOut[rowBase + x * 3 + 1] = 0;
                    dataOut[rowBase + x * 3 + 2] = 0;
                }
                else
                {
                    double scale = Math.Pow(2.0, e - (128 + 8));
                    dataOut[rowBase + x * 3 + 0] = (float)(rgbe[x * 4 + 0] * scale);
                    dataOut[rowBase + x * 3 + 1] = (float)(rgbe[x * 4 + 1] * scale);
                    dataOut[rowBase + x * 3 + 2] = (float)(rgbe[x * 4 + 2] * scale);
                }
            }
        }

        return new HdriImage(width, height, dataOut);
    }

    private static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n') return sb.ToString();
            if (b != '\r') sb.Append((char)b);
        }
    }

    private static int ReadExactly(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
