// Client/FFClientConnection.cs
// mTLS TCP + JSON-RPC consumer of the server protocol. One instance == one
// open connection. Methods are thread-unsafe; serialize calls from one
// caller. Used by both the Avalonia FFClient dialog and the headless
// --batch --remote path.

using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Wire;

namespace FracturingFog.Client;

public sealed class FFClientConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private long _nextId;

    private FFClientConnection(TcpClient tcp, SslStream ssl) { _tcp = tcp; _ssl = ssl; }

    public sealed class ConnectOptions
    {
        public required string Host { get; init; }
        public required int Port { get; init; }
        public required string ClientCertPath { get; init; }
        public string? ClientCertPassword { get; init; }
        public string? ServerCaCertPath { get; init; }
        public string? ExpectedServerHostName { get; init; }
    }

    public static async Task<FFClientConnection> ConnectAsync(ConnectOptions opts, CancellationToken ct)
    {
        var clientCert = X509CertificateLoader.LoadPkcs12FromFile(
            opts.ClientCertPath, opts.ClientCertPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        X509Certificate2Collection trustedServerCAs = new();
        if (!string.IsNullOrWhiteSpace(opts.ServerCaCertPath))
        {
            trustedServerCAs.Add(X509CertificateLoader.LoadPkcs12FromFile(
                opts.ServerCaCertPath, password: null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet));
        }

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(opts.Host, opts.Port, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            // Surface the dialed endpoint — most common cause of refusal is
            // a port mismatch between the saved connection entry and the
            // running server, and the bare SocketException message names
            // neither the host nor the port the client tried.
            throw new InvalidOperationException(
                $"TCP connect to {opts.Host}:{opts.Port} failed: {ex.SocketErrorCode} ({ex.Message}). " +
                $"Verify the saved connection's host/port match the running server (check Server… admin port).",
                ex);
        }

        // NoDelay: status-poll round-trips finish in one packet — Nagle's
        // 200 ms wait would dominate. KeepAlive: long renders sit idle on
        // the wire until the response envelope arrives; without keepalive
        // a NAT mapping can expire and the client never sees the result.
        try { tcp.NoDelay = true; } catch { }
        try { tcp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
            System.Net.Sockets.SocketOptionName.KeepAlive, true); } catch { }

        RemoteCertificateValidationCallback validate = (s, presented, chain, errors) =>
        {
            if (trustedServerCAs.Count == 0) return errors == SslPolicyErrors.None;
            if (presented is null) return false;
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 ca in trustedServerCAs)
                custom.ChainPolicy.CustomTrustStore.Add(ca);
            var leaf = presented as X509Certificate2 ?? new X509Certificate2(presented);
            return custom.Build(leaf);
        };

        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: validate);

        var clientCol = new X509CertificateCollection { clientCert };

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = opts.ExpectedServerHostName ?? opts.Host,
            ClientCertificates = clientCol,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = validate,
        }, ct).ConfigureAwait(false);

        return new FFClientConnection(tcp, ssl);
    }

    public Task<ServerStatusDto> GetStatusAsync(CancellationToken ct)
        => CallAsync<ServerStatusDto>("server.status", new { }, ct);

    public async Task<RenderResponseDto> RenderImageAsync(RenderRequestDto req, CancellationToken ct)
    {
        req.Mode = "image";
        var resp = await CallAsync<RenderResponseDto>("render.image", req, ct).ConfigureAwait(false);
        await AssembleStreamedBytesAsync(resp, isVideo: false, ct).ConfigureAwait(false);
        VerifyArtifactHash(resp, isVideo: false);
        return resp;
    }

    public async Task<RenderResponseDto> RenderVideoAsync(RenderRequestDto req, CancellationToken ct)
    {
        req.Mode = "video";
        var resp = await CallAsync<RenderResponseDto>("render.video", req, ct).ConfigureAwait(false);
        await AssembleStreamedBytesAsync(resp, isVideo: true, ct).ConfigureAwait(false);
        VerifyArtifactHash(resp, isVideo: true);
        return resp;
    }

    private async Task AssembleStreamedBytesAsync(RenderResponseDto resp, bool isVideo, CancellationToken ct)
    {
        if (!resp.Streaming || resp.ChunkCount <= 0) return;
        if (resp.TotalBytes <= 0 || resp.TotalBytes > int.MaxValue)
            throw new System.IO.InvalidDataException(
                $"streamed response declares invalid totalBytes={resp.TotalBytes}");

        using var ms = new System.IO.MemoryStream(checked((int)resp.TotalBytes));
        int nextSeq = 0;
        while (nextSeq < resp.ChunkCount)
        {
            var chunkEnv = await JsonRpcFraming.ReadAsync(_ssl, ct: ct).ConfigureAwait(false)
                ?? throw new System.IO.EndOfStreamException(
                    $"server closed mid-stream at chunk {nextSeq}/{resp.ChunkCount}");
            if (chunkEnv.Kind != "chunk" || chunkEnv.Result is not JsonElement chEl)
                throw new System.IO.InvalidDataException(
                    $"expected chunk envelope, got kind='{chunkEnv.Kind}'");
            var chunk = chEl.Deserialize<ChunkDto>(JsonRpcFraming.JsonOpts)
                ?? throw new System.IO.InvalidDataException("chunk payload null");
            if (chunk.Seq != nextSeq)
                throw new System.IO.InvalidDataException(
                    $"chunk out of order: expected seq={nextSeq}, got {chunk.Seq}");
            byte[] decoded = Convert.FromBase64String(chunk.BytesBase64);

            // Per-chunk integrity. Skipped only when the server elected
            // not to send a digest (older protocol). Mismatch means TLS
            // delivered bytes the server never blessed — refuse the result.
            if (!string.IsNullOrEmpty(chunk.Sha256))
            {
                string actual = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(decoded));
                if (!string.Equals(actual, chunk.Sha256, StringComparison.Ordinal))
                    throw new System.IO.InvalidDataException(
                        $"chunk {nextSeq}: sha256 mismatch (expected {chunk.Sha256}, got {actual})");
            }

            ms.Write(decoded, 0, decoded.Length);
            nextSeq++;
        }
        if (ms.Length != resp.TotalBytes)
            throw new System.IO.InvalidDataException(
                $"streamed byte count mismatch: assembled={ms.Length} declared={resp.TotalBytes}");

        string b64 = Convert.ToBase64String(ms.ToArray());
        if (isVideo) resp.Mp4BytesBase64 = b64;
        else         resp.PngBytesBase64 = b64;
    }

    private static void VerifyArtifactHash(RenderResponseDto resp, bool isVideo)
    {
        if (string.IsNullOrEmpty(resp.ArtifactSha256)) return;
        string? b64 = isVideo ? resp.Mp4BytesBase64 : resp.PngBytesBase64;
        if (string.IsNullOrEmpty(b64)) return; // saved-path mode — nothing inline to hash

        byte[] bytes = Convert.FromBase64String(b64);
        string actual = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(actual, resp.ArtifactSha256, StringComparison.Ordinal))
            throw new System.IO.InvalidDataException(
                $"artifact sha256 mismatch (expected {resp.ArtifactSha256}, got {actual})");
    }

    // internal so FFAdminConnection (same assembly) can call cluster.*
    // admin methods through the same TLS session without duplicating the
    // framing + error-envelope plumbing.
    internal async Task<TResult> CallAsync<TResult>(string method, object payload, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var env = new MessageEnvelope
        {
            Kind = "request",
            Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Method = method,
            Params = JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts),
        };
        await JsonRpcFraming.WriteAsync(_ssl, env, ct: ct).ConfigureAwait(false);

        var resp = await JsonRpcFraming.ReadAsync(_ssl, ct: ct).ConfigureAwait(false)
            ?? throw new System.IO.EndOfStreamException("server closed connection");

        if (resp.Error is JsonElement errEl)
        {
            var err = errEl.Deserialize<ErrorDto>(JsonRpcFraming.JsonOpts)
                ?? new ErrorDto { Code = "internal", Message = "(no error body)" };
            throw new FFServerException(err);
        }
        if (resp.Result is not JsonElement resEl)
            throw new System.IO.InvalidDataException("response missing result");

        return resEl.Deserialize<TResult>(JsonRpcFraming.JsonOpts)
            ?? throw new System.IO.InvalidDataException("response result deserialised to null");
    }

    // ── Cluster / distributed-rendering helpers (Phase D-2) ──────────────
    //
    // Same connection, same TLS session — these methods just call
    // master-side methods that the coordinator routes. The client cert's
    // role OU decides whether the master accepts: Client and Admin may
    // call job.*; Worker cannot.

    /// <summary>Submit a render job to a cluster master. Returns the
    /// freshly-issued JobId — the caller polls status via
    /// <see cref="GetJobStatusAsync"/> and fetches with
    /// <see cref="FetchJobArtifactAsync"/>.</summary>
    public Task<JobAckDto> SubmitJobAsync(JobSubmitDto submit, CancellationToken ct)
        => CallAsync<JobAckDto>("job.submit", submit, ct);

    public Task<JobStatusDto> GetJobStatusAsync(string jobId, CancellationToken ct)
        => CallAsync<JobStatusDto>("job.status", new JobStatusRequestDto { JobId = jobId }, ct);

    public Task<JobCancelAckDto> CancelJobAsync(string jobId, CancellationToken ct)
        => CallAsync<JobCancelAckDto>("job.cancel", new JobCancelRequestDto { JobId = jobId }, ct);

    /// <summary>Block until <paramref name="jobId"/> reaches a terminal
    /// state, polling every <paramref name="pollInterval"/>. Returns the
    /// final status. Throws via the underlying CallAsync on connection
    /// loss; the caller decides whether to reconnect + resume polling.</summary>
    public async Task<JobStatusDto> PollUntilTerminalAsync(
        string jobId, TimeSpan pollInterval, CancellationToken ct)
    {
        while (true)
        {
            var st = await GetJobStatusAsync(jobId, ct).ConfigureAwait(false);
            if (st.JobState is "ready" or "failed" or "cancelled") return st;
            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fetch the rendered artifact bytes once the job is ready.
    /// Replies with the ack envelope then reads N chunk envelopes off
    /// the same socket — same chunked path as
    /// <see cref="RenderImageAsync"/>. Verifies the per-chunk SHA-256
    /// and the whole-artifact SHA-256.</summary>
    public async Task<JobFetchResult> FetchJobArtifactAsync(string jobId, CancellationToken ct)
    {
        var ack = await CallAsync<JobFetchAckDto>("job.fetch",
            new JobFetchRequestDto { JobId = jobId }, ct).ConfigureAwait(false);

        if (ack.TotalBytes < 0 || ack.TotalBytes > int.MaxValue)
            throw new System.IO.InvalidDataException(
                $"job.fetch declared invalid totalBytes={ack.TotalBytes}");

        using var ms = new System.IO.MemoryStream(checked((int)ack.TotalBytes));
        for (int seq = 0; seq < ack.ChunkCount; seq++)
        {
            var chunkEnv = await JsonRpcFraming.ReadAsync(_ssl, ct: ct).ConfigureAwait(false)
                ?? throw new System.IO.EndOfStreamException(
                    $"master closed mid-stream at chunk {seq}/{ack.ChunkCount}");
            if (chunkEnv.Kind != "chunk" || chunkEnv.Result is not JsonElement chEl)
                throw new System.IO.InvalidDataException(
                    $"expected chunk envelope, got kind='{chunkEnv.Kind}'");
            var chunk = chEl.Deserialize<ChunkDto>(JsonRpcFraming.JsonOpts)
                ?? throw new System.IO.InvalidDataException("chunk payload null");
            if (chunk.Seq != seq)
                throw new System.IO.InvalidDataException(
                    $"chunk out of order: expected seq={seq}, got {chunk.Seq}");

            byte[] decoded = Convert.FromBase64String(chunk.BytesBase64);
            if (!string.IsNullOrEmpty(chunk.Sha256))
            {
                string actual = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(decoded));
                if (!string.Equals(actual, chunk.Sha256, StringComparison.Ordinal))
                    throw new System.IO.InvalidDataException(
                        $"chunk {seq}: sha256 mismatch (expected {chunk.Sha256}, got {actual})");
            }
            ms.Write(decoded, 0, decoded.Length);
        }

        if (ms.Length != ack.TotalBytes)
            throw new System.IO.InvalidDataException(
                $"job.fetch byte count mismatch: assembled={ms.Length} declared={ack.TotalBytes}");

        byte[] bytes = ms.ToArray();
        if (!string.IsNullOrEmpty(ack.Sha256))
        {
            string actual = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(bytes));
            if (!string.Equals(actual, ack.Sha256, StringComparison.Ordinal))
                throw new System.IO.InvalidDataException(
                    $"job.fetch artifact sha256 mismatch (expected {ack.Sha256}, got {actual})");
        }

        return new JobFetchResult
        {
            JobId       = ack.JobId,
            ArtifactExt = ack.ArtifactExt,
            Bytes       = bytes,
        };
    }

    public async ValueTask DisposeAsync()
    {
        try { await _ssl.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _tcp.Close(); } catch { }
    }
}

/// <summary>Client-side result of <see cref="FFClientConnection.FetchJobArtifactAsync"/>.
/// Bytes are the fully-assembled, hash-verified artifact (PNG / MP4 /
/// MKV); ArtifactExt drives the save-as default extension.</summary>
public sealed class JobFetchResult
{
    public required string JobId       { get; init; }
    public required string ArtifactExt { get; init; }
    public required byte[] Bytes       { get; init; }
}

public sealed class FFServerException : Exception
{
    public ErrorDto Error { get; }
    public FFServerException(ErrorDto err) : base($"[{err.Code}] {err.Message}") { Error = err; }
}
