// Server/Guard/RequestLimits.cs
// Bounds the server rejects requests outside of. Image width × height cap
// covers 32K posters. Video seconds cap matches the local BatchOptions
// limit (already enforced for headless --batch). Queue depth is the
// number of accepted-but-running renders; excess conns hit busy.

namespace FracturingFog.Server.Guard;

public sealed class RequestLimits
{
    public int MinWidth  { get; init; } = 16;
    public int MaxWidth  { get; init; } = 32768;
    public int MinHeight { get; init; } = 16;
    public int MaxHeight { get; init; } = 32768;

    /// <summary>Total pixel ceiling (width × height). 32K × 32K hits
    /// 4 GB of BGRA alone; the calculator scratch doubles that. 64 M
    /// (≈ 8K × 8K) keeps the worst-case render under ~768 MB on the host.</summary>
    public long MaxPixels { get; init; } = 64L * 1024L * 1024L;

    public double MinVideoSeconds { get; init; } = 0.5;
    public double MaxVideoSeconds { get; init; } = 600.0;

    public int MinVideoFps { get; init; } = 1;
    public int MaxVideoFps { get; init; } = 240;

    public int MaxQueueDepth { get; init; } = 1;

    public static readonly RequestLimits Default = new();
}
