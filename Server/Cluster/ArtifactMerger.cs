// Server/Cluster/ArtifactMerger.cs
// Owns the merge buffer for a single image job. Each tile delivery
// decodes the tile (via injected IClusterImageCodec) into BGRA and
// pastes the rect at (offX, offY). When TilesAccepted == TilesTotal
// the merger writes the final PNG via the codec.
//
// Memory model: BGRA buffer is allocated as a single byte[] on the
// managed heap. For 8K (7680×4320×4) ≈ 132 MB per active job. The
// dev-plan §7 "memory-mapped merge buffer" upgrade lands in D-3 when
// concurrent-job count + max image size jointly push us past the LOH
// budget; for now a flat byte[] keeps the merge code obvious and the
// per-tile paste branch-free.
//
// Threading: per-instance lock around the buffer; tiles arrive on
// arbitrary threads from the dispatcher. Disposable so the buffer can
// be released eagerly once the artifact is on disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FracturingFog.Server.Cluster;

public sealed class ArtifactMerger : IDisposable
{
    private readonly object _lock = new();
    private readonly byte[] _bgra;
    private readonly bool[] _tileSeen;
    private readonly IClusterImageCodec _codec;
    private int _tilesAccepted;
    private bool _disposed;

    public int Width  { get; }
    public int Height { get; }
    public int TilesTotal { get; }

    public int TilesAccepted => Volatile.Read(ref _tilesAccepted);
    public bool IsComplete   => TilesAccepted == TilesTotal;

    public ArtifactMerger(int width, int height, int tilesTotal, IClusterImageCodec codec)
    {
        if (width  <= 0 || height <= 0) throw new ArgumentException(
            $"invalid image dims {width}×{height}");
        if (tilesTotal <= 0) throw new ArgumentException(
            $"invalid tile count {tilesTotal}");

        long bufBytes = (long)width * height * 4;
        if (bufBytes > int.MaxValue) throw new ArgumentException(
            $"image too large for D-2 flat buffer: {width}×{height}×4 = {bufBytes:N0} bytes");

        Width       = width;
        Height      = height;
        TilesTotal  = tilesTotal;
        _bgra       = new byte[bufBytes];
        _tileSeen   = new bool[tilesTotal];
        _codec      = codec;
    }

    /// <summary>Paste a decoded BGRA tile at (offX, offY). Tile bounds
    /// must lie inside the image. Returns false (without throwing) if
    /// the tile id was already pasted — re-deliveries from a retried
    /// worker are ignored rather than racing.</summary>
    public bool TryMergePngTile(int tileId, int offX, int offY, int expectedW, int expectedH, byte[] tilePng)
    {
        ThrowIfDisposed();
        ValidateRect(tileId, offX, offY, expectedW, expectedH);

        // Decode happens outside the lock — codecs can be slow and the
        // lock only protects the paste + bookkeeping.
        byte[] tileBgra = _codec.DecodePngToBgra(tilePng, out int decodedW, out int decodedH);
        if (decodedW != expectedW || decodedH != expectedH)
            throw new InvalidDataException(
                $"tile {tileId}: codec produced {decodedW}×{decodedH}, expected {expectedW}×{expectedH}");
        long want = (long)expectedW * expectedH * 4;
        if (tileBgra.LongLength != want)
            throw new InvalidDataException(
                $"tile {tileId}: codec returned {tileBgra.LongLength} bytes, expected {want}");

        return PasteAndAccount(tileId, offX, offY, expectedW, expectedH, tileBgra);
    }

    /// <summary>Same as <see cref="TryMergePngTile"/> but for an already-
    /// decoded raw BGRA byte buffer (D-3 binary tile path uses this).</summary>
    public bool TryMergeRgbaTile(int tileId, int offX, int offY, int expectedW, int expectedH, byte[] tileBgra)
    {
        ThrowIfDisposed();
        ValidateRect(tileId, offX, offY, expectedW, expectedH);

        long want = (long)expectedW * expectedH * 4;
        if (tileBgra.LongLength != want)
            throw new InvalidDataException(
                $"tile {tileId}: raw payload was {tileBgra.LongLength} bytes, expected {want}");

        return PasteAndAccount(tileId, offX, offY, expectedW, expectedH, tileBgra);
    }

    private bool PasteAndAccount(int tileId, int offX, int offY, int tW, int tH, byte[] tileBgra)
    {
        lock (_lock)
        {
            if (_tileSeen[tileId]) return false;

            int strideDst = Width * 4;
            int strideSrc = tW * 4;
            for (int row = 0; row < tH; row++)
            {
                int srcIx = row * strideSrc;
                int dstIx = (offY + row) * strideDst + offX * 4;
                Buffer.BlockCopy(tileBgra, srcIx, _bgra, dstIx, strideSrc);
            }
            _tileSeen[tileId] = true;
            _tilesAccepted++;
            return true;
        }
    }

    private void ValidateRect(int tileId, int offX, int offY, int tW, int tH)
    {
        if (tileId < 0 || tileId >= TilesTotal)
            throw new ArgumentOutOfRangeException(nameof(tileId),
                $"tileId {tileId} outside [0..{TilesTotal})");
        if (offX < 0 || offY < 0 || tW <= 0 || tH <= 0
            || offX + tW > Width || offY + tH > Height)
            throw new ArgumentException(
                $"tile {tileId} rect ({offX},{offY},{tW},{tH}) escapes image {Width}×{Height}");
    }

    /// <summary>Write the merged image to <paramref name="outPath"/> as
    /// PNG. Requires <see cref="IsComplete"/> — caller must check.</summary>
    public void WritePng(string outPath)
    {
        ThrowIfDisposed();
        if (!IsComplete) throw new InvalidOperationException(
            $"cannot write: only {TilesAccepted}/{TilesTotal} tiles accepted");
        _codec.EncodeBgraToPng(_bgra, Width, Height, outPath);
    }

    /// <summary>Diagnostic — list tile ids still missing.</summary>
    public IReadOnlyList<int> MissingTileIds()
    {
        lock (_lock)
        {
            var miss = new List<int>();
            for (int i = 0; i < _tileSeen.Length; i++) if (!_tileSeen[i]) miss.Add(i);
            return miss;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ArtifactMerger));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The byte[] is GC-owned; nothing to free explicitly. Disposing
        // exists so callers can null the merger reference and ensure no
        // late tile.deliver tries to use it after artifact eviction.
    }
}
