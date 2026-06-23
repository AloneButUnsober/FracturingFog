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
using System.Threading.Tasks;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Decoded Radiance .hdr image — linear scene-referred RGB. Phase 16b
/// adds a box-downsample mip chain prefiltered at construction time so
/// roughness-convolved sampling picks the appropriate mip level (mip 0 = sharp,
/// mip N-1 ≈ 1×1 = uniform ambient). Storage cost ≈ 4/3 × the original buffer.</summary>
public sealed class HdriImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Packed RGB floats — index = (y · width + x) · 3. Mip level 0.</summary>
    public float[] Data { get; }

    /// <summary>Phase 16b — total mip level count including mip 0. Capped at 12
    /// so very large HDRIs don't waste cycles on irrelevant single-pixel mips.</summary>
    public int MipLevels { get; }

    /// <summary>Phase 16b — width/height per mip level. Indexed by mip level
    /// (0 = full resolution).</summary>
    public int[] MipWidths { get; }
    public int[] MipHeights { get; }

    /// <summary>Phase 16b — RGB float storage per mip level. Index 0 == <see
    /// cref="Data"/> so callers that pre-date the mip chain still resolve to the
    /// sharp image.</summary>
    public float[][] MipData { get; }

    public HdriImage(int w, int h, float[] data)
    {
        Width = w;
        Height = h;
        Data = data;

        // Phase 16b — build the box-downsample mip chain. Stop when the
        // smaller axis hits 1 (or after 12 levels — a 4096-tall HDRI tops
        // out at 13 mips; cap one short for headroom).
        int levels = 1;
        int mw = w, mh = h;
        while (Math.Min(mw, mh) > 1 && levels < 12)
        {
            levels++;
            mw = Math.Max(1, mw / 2);
            mh = Math.Max(1, mh / 2);
        }
        MipLevels = levels;
        MipWidths = new int[levels];
        MipHeights = new int[levels];
        MipData = new float[levels][];
        MipWidths[0] = w;
        MipHeights[0] = h;
        MipData[0] = data;
        for (int lvl = 1; lvl < levels; lvl++)
        {
            int pw = MipWidths[lvl - 1];
            int ph = MipHeights[lvl - 1];
            int nw = Math.Max(1, pw / 2);
            int nh = Math.Max(1, ph / 2);
            MipWidths[lvl] = nw;
            MipHeights[lvl] = nh;
            MipData[lvl] = BoxDownsample(MipData[lvl - 1], pw, ph, nw, nh);
        }
    }

    private static float[] BoxDownsample(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh * 3];
        // Sample 2×2 footprint per destination pixel (with edge clamp on the
        // tail rows / cols when the parent dim is odd). Cheap; not a perfect
        // Gaussian but matches GL_LINEAR_MIPMAP_LINEAR's expectation.
        for (int y = 0; y < dh; y++)
        {
            int sy0 = Math.Min(sh - 1, y * 2);
            int sy1 = Math.Min(sh - 1, sy0 + 1);
            for (int x = 0; x < dw; x++)
            {
                int sx0 = Math.Min(sw - 1, x * 2);
                int sx1 = Math.Min(sw - 1, sx0 + 1);
                int i00 = (sy0 * sw + sx0) * 3;
                int i10 = (sy0 * sw + sx1) * 3;
                int i01 = (sy1 * sw + sx0) * 3;
                int i11 = (sy1 * sw + sx1) * 3;
                int d = (y * dw + x) * 3;
                dst[d]     = 0.25f * (src[i00]     + src[i10]     + src[i01]     + src[i11]);
                dst[d + 1] = 0.25f * (src[i00 + 1] + src[i10 + 1] + src[i01 + 1] + src[i11 + 1]);
                dst[d + 2] = 0.25f * (src[i00 + 2] + src[i10 + 2] + src[i01 + 2] + src[i11 + 2]);
            }
        }
        return dst;
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

    /// <summary>Phase 16b — roughness-convolved equirectangular sample. Picks
    /// a mip by <c>roughness² · (MipLevels − 1)</c> and bilinearly samples the
    /// nearest mip below the fractional level. Roughness 0 = mip 0 (sharp);
    /// roughness 1 = mip N-1 (≈ uniform ambient).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (double R, double G, double B) Sample(double dirX, double dirY, double dirZ, double roughness)
    {
        double u = 0.5 + Math.Atan2(dirZ, dirX) * (1.0 / (2.0 * Math.PI));
        double v = Math.Acos(Math.Clamp(dirY, -1.0, 1.0)) * (1.0 / Math.PI);
        if (roughness <= 0 || MipLevels <= 1) return SampleUv(u, v);
        if (roughness > 1) roughness = 1;
        // Square the roughness so the perceptual midpoint lines up with a
        // mid-mip — matches the GGX prefiltered-IBL convention used by
        // Karis 2014 and the UE4 / Filament environment maps.
        double level = roughness * roughness * (MipLevels - 1);
        int lvl = (int)Math.Floor(level);
        if (lvl >= MipLevels - 1) lvl = MipLevels - 1;
        return SampleUvMip(u, v, lvl);
    }

    /// <summary>Bilinear UV sample. u wraps; v clamps (no antipodal wrap).</summary>
    public (double R, double G, double B) SampleUv(double u, double v)
        => SampleUvMip(u, v, 0);

    /// <summary>Phase 16b — bilinear UV sample at a specific mip level.</summary>
    public (double R, double G, double B) SampleUvMip(double u, double v, int mip)
    {
        if (mip < 0) mip = 0;
        else if (mip >= MipLevels) mip = MipLevels - 1;
        int mw = MipWidths[mip];
        int mh = MipHeights[mip];
        float[] buf = MipData[mip];
        u -= Math.Floor(u);
        if (v < 0) v = 0; else if (v > 1) v = 1;
        double fx = u * (mw - 1);
        double fy = v * (mh - 1);
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        int x1 = x0 + 1; if (x1 >= mw) x1 = 0;
        int y1 = Math.Min(y0 + 1, mh - 1);
        double tx = fx - x0;
        double ty = fy - y0;
        int i00 = (y0 * mw + x0) * 3;
        int i10 = (y0 * mw + x1) * 3;
        int i01 = (y1 * mw + x0) * 3;
        int i11 = (y1 * mw + x1) * 3;
        double R = (1 - tx) * (1 - ty) * buf[i00]   + tx * (1 - ty) * buf[i10]
                 + (1 - tx) *      ty  * buf[i01]   + tx *      ty  * buf[i11];
        double G = (1 - tx) * (1 - ty) * buf[i00+1] + tx * (1 - ty) * buf[i10+1]
                 + (1 - tx) *      ty  * buf[i01+1] + tx *      ty  * buf[i11+1];
        double B = (1 - tx) * (1 - ty) * buf[i00+2] + tx * (1 - ty) * buf[i10+2]
                 + (1 - tx) *      ty  * buf[i01+2] + tx *      ty  * buf[i11+2];
        return (R, G, B);
    }
}

public static class HdriRegistry
{
    private static readonly ConcurrentDictionary<string, HdriImage> _byName
        = new(StringComparer.OrdinalIgnoreCase);

    // Wave 4.3 — per-path parse gate. Concurrent first-hits on the per-pixel
    // render path would otherwise all open the file + parse N times before
    // the cache write completes; the gate funnels every concurrent caller
    // through a single Lazy parse so the work happens exactly once. Used by
    // both TryLoadFromFile (sync) and Preload (async).
    private static readonly ConcurrentDictionary<string, Lazy<HdriImage?>> _parseGate
        = new(StringComparer.OrdinalIgnoreCase);

    // Self-register with the abstractions-layer probe so the UI shell's
    // file-picker can pre-warm and surface load failures without taking a
    // project reference on the Engine. The first reference to any member of
    // HdriRegistry triggers the static constructor.
    static HdriRegistry()
    {
        HdriProbe.TryLoad = path => TryLoadFromFile(path, out _);
        // Wave 4.3 — fire-and-forget background preload. VM EnvironmentName
        // setter + preset-apply sites call this so the first render frame
        // finds the HDRI cached instead of racing N pixel-worker threads
        // through the same file parse.
        HdriProbe.Preload = path => Task.Run(() => TryLoadFromFile(path, out _));
    }

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

    /// <summary>Load an HDRI from disk, cache it, and return the decoded image.
    /// Dispatches by file extension: <c>.hdr</c> / <c>.pic</c> → Radiance RGBE,
    /// <c>.exr</c> → OpenEXR scanline. Returns false on parse error or
    /// unsupported format / compression.
    ///
    /// Hot-path note (Phase 16b hotfix): the cross-cache check by full path
    /// MUST stay first. The engine's <c>TryResolveHdri</c> is called per
    /// shaded pixel; when <c>EnvironmentName</c> holds an absolute path the
    /// bare-name cache key misses on every pixel, so without the path-keyed
    /// short-circuit the file is re-opened + re-decoded millions of times
    /// per frame and the render hangs. Cache under BOTH the bare name (for
    /// preset compatibility) and the full path (for picker-set paths).
    /// </summary>
    public static bool TryLoadFromFile(string path, out HdriImage? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Path-keyed cache check — short-circuits the per-pixel reparse.
        if (_byName.TryGetValue(path, out var cached) && cached is not null)
        {
            image = cached;
            return true;
        }
        if (!File.Exists(path)) return false;
        // Wave 4.3 — funnel concurrent first-hits through one Lazy parse.
        // N pixel-worker threads landing on the same uncached path would
        // otherwise each open + parse the file; the gate guarantees one
        // parse, all callers share the result.
        var gate = _parseGate.GetOrAdd(path, p => new Lazy<HdriImage?>(() => ParseUncached(p)));
        try
        {
            image = gate.Value;
        }
        catch
        {
            image = null;
        }
        // Once the parse settled (success or failure), drop the gate so a
        // later retry on a fixed file isn't wedged on a stale Lazy.
        _parseGate.TryRemove(path, out _);
        return image is not null;
    }

    private static HdriImage? ParseUncached(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = File.OpenRead(path);
            HdriImage? image = ext switch
            {
                ".hdr" or ".pic" => ParseRadiance(fs),
                ".exr"           => OpenExrReader.Parse(fs),
                _                => null,
            };
            if (image != null)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                _byName[name] = image;
                _byName[path] = image;
            }
            return image;
        }
        catch
        {
            return null;
        }
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
