// Server/Cluster/IClusterCoordinator.cs
// Hook FFServer calls into for cluster-only protocol methods
// (worker.* and cluster.*). Default coordinator is null — FFServer falls
// back to its existing "unknown-method" behaviour and the single-server
// render path keeps working unchanged.
//
// The coordinator returns a small outcome record rather than calling
// FFServer's reply helpers directly so the wire-encoding stays inside
// FFServer and the coordinator can be exercised in tests without an
// SslStream.

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Tls;

namespace FracturingFog.Server.Cluster;

public interface IClusterCoordinator
{
    /// <summary>Dispatch a cluster method. Implementations may hold the
    /// task for the duration of a long-poll; FFServer's per-session loop
    /// is single-flight so this back-pressures the worker naturally.
    /// <paramref name="binaryPayload"/> carries the optional binary
    /// trailer the wire envelope advertised (D-3 raw-RGBA tile path);
    /// null when the caller used the JSON-only path.</summary>
    Task<ClusterDispatchOutcome> HandleAsync(
        string method,
        JsonElement? @params,
        CertRole role,
        string thumbprint,
        CancellationToken ct,
        byte[]? binaryPayload = null);
}

public readonly record struct ClusterDispatchOutcome(
    bool Handled,
    object? Result,
    string? ErrorCode,
    string? ErrorMessage,
    string? StreamFilePath = null,
    int     StreamChunkCount = 0)
{
    public static ClusterDispatchOutcome NotHandled => default;

    public static ClusterDispatchOutcome Ok(object result)
        => new(Handled: true, Result: result, ErrorCode: null, ErrorMessage: null);

    public static ClusterDispatchOutcome Err(string code, string message)
        => new(Handled: true, Result: null, ErrorCode: code, ErrorMessage: message);

    /// <summary>Result is the ack DTO; FFServer must additionally stream
    /// <paramref name="streamFilePath"/> in <paramref name="chunkCount"/>
    /// chunks after replying with the ack. Used for job.fetch so the
    /// chunked wire path stays in FFServer (where the SslStream lives).</summary>
    public static ClusterDispatchOutcome OkStreaming(object ack, string streamFilePath, int chunkCount)
        => new(Handled: true, Result: ack, ErrorCode: null, ErrorMessage: null,
               StreamFilePath: streamFilePath, StreamChunkCount: chunkCount);
}
