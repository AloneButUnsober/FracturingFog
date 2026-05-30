// Server/FFServer.cs
// Accept loop, mTLS handshake, JSON-RPC dispatch. One connection at a time
// can be running a render (queue depth gate). Long-running renders run
// under a CancellationToken keyed to the per-job deadline.

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Guard;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;

namespace FracturingFog.Server;

public sealed class FFServer
{
    public ServerConfig Config { get; }
    public Metrics Metrics { get; } = new();
    public RequestLimits Limits { get; init; } = RequestLimits.Default;

    /// <summary>Maximum quiet time between requests on an authenticated
    /// session before the server closes the socket.</summary>
    public TimeSpan IdleReadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private readonly IFractalRenderEngine _engine;
    private readonly ServerTrust _trust;
    private readonly RemoteCertificateValidationCallback _clientValidator;
    private readonly SemaphoreSlim _queueGate;

    /// <summary>Outer cap on accepted-but-still-open TCP connections,
    /// including ones still in the TLS handshake. Bounds memory + thread
    /// load against TLS-exhaustion / SYN-flood pressure.</summary>
    private readonly SemaphoreSlim _connectionGate;

    public FFServer(ServerConfig config, IFractalRenderEngine engine, ServerTrust trust)
    {
        Config = config;
        _engine = engine;
        _trust = trust;
        _clientValidator = ServerCertLoader.BuildClientValidator(trust.TrustedClientCAs);
        _queueGate = new SemaphoreSlim(Math.Max(1, config.QueueDepth));
        _connectionGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentConnections));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        IPAddress bindAddr;
        try { bindAddr = IPAddress.Parse(Config.BindAddress); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"invalid bindAddress '{Config.BindAddress}': {ex.Message}", ex);
        }

        var listener = new TcpListener(bindAddr, Config.Port);
        listener.Start();
        Console.WriteLine($"listening on {bindAddr}:{Config.Port}" +
            (bindAddr.Equals(IPAddress.Loopback) ? " (loopback only — use --bind 0.0.0.0 to expose)" : ""));
        ct.Register(() => { try { listener.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)    { break; }

            // Drop the connection immediately if we are already at the
            // concurrency cap. Two-second wait so a brief surge does not
            // close legitimate clients, then close fast to avoid pinning
            // threads + buffers on the server.
            if (!await _connectionGate.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                try { tcp.Close(); } catch { }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(tcp, ct).ConfigureAwait(false); }
                finally { _connectionGate.Release(); }
            }, CancellationToken.None);
        }

        listener.Stop();
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        IPEndPoint? remote = tcp.Client.RemoteEndPoint as IPEndPoint;
        string remoteStr = remote?.ToString() ?? "(unknown)";
        SessionLogger? sessLog = null;
        SslStream? ssl = null;

        try
        {
            ssl = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: _clientValidator);

            // Handshake timeout — slow-TLS keeps a thread-pool slot pinned
            // and a half-open SslStream resident in memory until TCP-level
            // keepalive notices. Cap it at 10 s so attackers can't soak
            // accept-loop capacity by stalling between ClientHello records.
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _trust.ServerIdentity,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = _clientValidator,
                }, handshakeCts.Token).ConfigureAwait(false);
            }

            string? clientThumb = (ssl.RemoteCertificate as X509Certificate2)?.Thumbprint;
            sessLog = SessionLogger.Open(
                Config.LogDir ?? ServerConfig.DefaultLogDir(),
                remoteStr,
                clientThumb);

            sessLog.Info($"session opened, tls={ssl.SslProtocol}, cipher={ssl.NegotiatedCipherSuite}");

            while (!ct.IsCancellationRequested)
            {
                MessageEnvelope? env;
                // Idle read timeout — only applies to the wait BEFORE the
                // next request arrives. Once a request is being processed
                // (DispatchAsync below) the render's own CTS owns the deadline.
                using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idleCts.CancelAfter(IdleReadTimeout);
                    try { env = await JsonRpcFraming.ReadAsync(ssl, ct: idleCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        sessLog.Info("idle timeout, closing");
                        break;
                    }
                    catch (EndOfStreamException) { break; }
                    catch (InvalidDataException ex)
                    {
                        sessLog.Err($"frame error: {ex.Message}");
                        break;
                    }
                }
                if (env is null) break;

                await DispatchAsync(env, ssl, sessLog, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sessLog?.Err($"session aborted: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            sessLog?.Dispose();
            try { ssl?.Dispose(); } catch { }
            try { tcp.Close(); } catch { }
        }
    }

    private async Task DispatchAsync(MessageEnvelope env, SslStream ssl, SessionLogger log, CancellationToken ct)
    {
        if (env.Kind != "request" || string.IsNullOrEmpty(env.Method))
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request", "envelope kind/method missing", ct).ConfigureAwait(false);
            return;
        }

        switch (env.Method)
        {
            case "server.status":
                await ReplyResultAsync(ssl, env.Id, BuildStatus(), ct).ConfigureAwait(false);
                break;

            case "render.image":
            case "render.video":
                await HandleRenderAsync(env, ssl, log, ct).ConfigureAwait(false);
                break;

            default:
                await ReplyErrorAsync(ssl, env.Id, "bad-request",
                    $"unknown method '{env.Method}'", ct).ConfigureAwait(false);
                break;
        }
    }

    private ServerStatusDto BuildStatus() => new()
    {
        Port = Config.Port,
        UptimeSeconds = Metrics.UptimeSeconds,
        InFlight = Metrics.InFlight,
        Completed = Metrics.Completed,
        Failed = Metrics.Failed,
        LastErrorCode = Metrics.LastErrorCode,
        LastErrorMessage = Metrics.LastErrorMessage,
        MaxMinutes = Config.MaxMinutes,
        AllowOverride = Config.AllowOverride,
        QueueDepth = Config.QueueDepth,
    };

    private async Task HandleRenderAsync(MessageEnvelope env, SslStream ssl, SessionLogger log, CancellationToken ct)
    {
        RenderRequestDto? req;
        try { req = env.Params?.Deserialize<RenderRequestDto>(JsonRpcFraming.JsonOpts); }
        catch (Exception ex)
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request",
                $"invalid params: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }
        if (req is null)
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request", "missing params", ct).ConfigureAwait(false);
            return;
        }

        if (!FractalTypeAllowlist.IsAllowed(req.FractalType, out var parsedType))
        {
            log.Warn($"refused fractal type '{req.FractalType}'");
            Metrics.RecordFailure("forbidden-fractal", req.FractalType);
            await ReplyErrorAsync(ssl, env.Id, "forbidden-fractal",
                $"fractal type '{req.FractalType}' is not permitted for remote rendering", ct).ConfigureAwait(false);
            return;
        }

        if (req.Width < Limits.MinWidth || req.Width > Limits.MaxWidth ||
            req.Height < Limits.MinHeight || req.Height > Limits.MaxHeight)
        {
            await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                $"width×height out of bounds [{Limits.MinWidth}..{Limits.MaxWidth}]", ct).ConfigureAwait(false);
            return;
        }

        long pixels = (long)req.Width * req.Height;
        if (pixels > Limits.MaxPixels)
        {
            await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                $"width×height={pixels:N0} exceeds host pixel cap {Limits.MaxPixels:N0}", ct).ConfigureAwait(false);
            return;
        }

        bool isVideo = string.Equals(env.Method, "render.video", StringComparison.Ordinal);
        if (isVideo)
        {
            if (req.VideoSeconds < Limits.MinVideoSeconds || req.VideoSeconds > Limits.MaxVideoSeconds)
            {
                await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                    $"videoSeconds must be {Limits.MinVideoSeconds}..{Limits.MaxVideoSeconds}", ct).ConfigureAwait(false);
                return;
            }
            if (req.VideoFps < Limits.MinVideoFps || req.VideoFps > Limits.MaxVideoFps)
            {
                await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                    $"videoFps must be {Limits.MinVideoFps}..{Limits.MaxVideoFps}", ct).ConfigureAwait(false);
                return;
            }
        }

        if (!await _queueGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await ReplyErrorAsync(ssl, env.Id, "busy",
                "server queue is full, try again later", ct).ConfigureAwait(false);
            return;
        }

        int minutes = Config.MaxMinutes;
        if (req.RequestedMaxMinutes is int rm && rm > 0)
        {
            minutes = Config.AllowOverride ? rm : Math.Min(rm, Config.MaxMinutes);
        }

        string workDir = Path.Combine(
            Config.WorkDir ?? ServerConfig.DefaultWorkDir(),
            $"job-{DateTime.UtcNow:yyyyMMdd_HHmmss}-{Guid.NewGuid():N}".Substring(0, 40));
        Directory.CreateDirectory(workDir);

        log.Info($"render begin: method={env.Method} type={parsedType} {req.Width}x{req.Height} " +
                 $"region={req.RegionName ?? "(coords)"} theme={req.ThemeName} timeoutMin={minutes}");

        using var inFlight = Metrics.BeginRender();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));

        RenderArtifact? artifact = null;
        Exception? failure = null;
        try
        {
            artifact = await Task.Run(() => _engine.Render(
                req, workDir, new SessionLoggerAdapter(log), cts.Token), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Warn($"render timed out after {minutes} min");
            Metrics.RecordFailure("timeout", $"{minutes} min exceeded");
            await ReplyErrorAsync(ssl, env.Id, "timeout",
                $"render exceeded {minutes} minute(s)", ct).ConfigureAwait(false);
            failure = new TimeoutException();
        }
        catch (ServerProtocolException ex)
        {
            log.Warn($"render rejected: [{ex.Code}] {ex.Message}");
            Metrics.RecordFailure(ex.Code, ex.Message);
            await ReplyErrorAsync(ssl, env.Id, ex.Code, ex.Message, ct).ConfigureAwait(false);
            failure = ex;
        }
        catch (Exception ex)
        {
            log.Err($"render failed: {ex.GetType().Name}: {ex.Message}");
            Metrics.RecordFailure("render-failed", ex.Message);
            await ReplyErrorAsync(ssl, env.Id, "render-failed", ex.Message, ct).ConfigureAwait(false);
            failure = ex;
        }
        finally
        {
            _queueGate.Release();
        }

        if (failure != null || artifact is null)
        {
            TryCleanWorkDir(workDir);
            return;
        }

        var resp = new RenderResponseDto
        {
            Width = artifact.Width,
            Height = artifact.Height,
            ElapsedMs = artifact.ElapsedMs,
            FramesWritten = artifact.FramesWritten,
        };

        bool inline = string.Equals(req.ReturnMode, "inline", StringComparison.OrdinalIgnoreCase);
        if (inline)
        {
            long fileSize;
            try { fileSize = new FileInfo(artifact.FilePath).Length; }
            catch (Exception ex)
            {
                Metrics.RecordFailure("internal", $"stat artifact: {ex.Message}");
                await ReplyErrorAsync(ssl, env.Id, "internal",
                    $"could not stat rendered artifact: {ex.Message}", ct).ConfigureAwait(false);
                TryCleanWorkDir(workDir);
                return;
            }

            if (fileSize > InlineSingleEnvelopeThreshold)
            {
                resp.Streaming = true;
                resp.TotalBytes = fileSize;
                resp.ChunkCount = (int)((fileSize + ChunkBytes - 1) / ChunkBytes);
                Metrics.RecordSuccess();
                log.Info($"render done: elapsedMs={artifact.ElapsedMs} bytes=streamed " +
                         $"size={fileSize:N0} chunks={resp.ChunkCount}");
                await ReplyResultAsync(ssl, env.Id, resp, ct).ConfigureAwait(false);
                await StreamArtifactChunksAsync(ssl, env.Id, artifact.FilePath, resp.ChunkCount, log, ct).ConfigureAwait(false);
                TryCleanWorkDir(workDir);
                return;
            }

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(artifact.FilePath, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Metrics.RecordFailure("internal", $"read artifact: {ex.Message}");
                await ReplyErrorAsync(ssl, env.Id, "internal",
                    $"could not read rendered artifact: {ex.Message}", ct).ConfigureAwait(false);
                TryCleanWorkDir(workDir);
                return;
            }
            string b64 = Convert.ToBase64String(bytes);
            if (isVideo) resp.Mp4BytesBase64 = b64;
            else         resp.PngBytesBase64 = b64;
            TryCleanWorkDir(workDir);
        }
        else
        {
            resp.SavedPath = artifact.FilePath;
            resp.FrameFolderPath = artifact.FrameFolderPath;
        }

        Metrics.RecordSuccess();
        log.Info($"render done: elapsedMs={artifact.ElapsedMs} bytes={(inline ? "inline" : "saved")}");
        await ReplyResultAsync(ssl, env.Id, resp, ct).ConfigureAwait(false);
    }

    /// <summary>Size at which inline responses switch from a single result
    /// envelope to a streamed sequence of chunk envelopes. 16 MB keeps
    /// 4K-poster PNGs inline while video MP4s + 8K poster PNGs stream.</summary>
    public const long InlineSingleEnvelopeThreshold = 16L * 1024L * 1024L;
    public const int ChunkBytes = 1 * 1024 * 1024;

    private static async Task StreamArtifactChunksAsync(
        SslStream ssl, string id, string filePath, int totalChunks,
        SessionLogger log, CancellationToken ct)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: ChunkBytes, useAsync: true);
        byte[] buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            int seq = 0;
            while (true)
            {
                int read = await fs.ReadAsync(buf.AsMemory(0, ChunkBytes), ct).ConfigureAwait(false);
                if (read <= 0) break;

                var payload = new ChunkDto
                {
                    Seq = seq,
                    Total = totalChunks,
                    BytesBase64 = Convert.ToBase64String(buf, 0, read),
                };
                var chunkEnv = new MessageEnvelope
                {
                    Kind = "chunk",
                    Id = id,
                    Result = JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts),
                };
                await JsonRpcFraming.WriteAsync(ssl, chunkEnv, ct: ct).ConfigureAwait(false);
                seq++;
            }
            if (seq != totalChunks)
                log.Warn($"chunk count mismatch: emitted={seq} expected={totalChunks}");
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static void TryCleanWorkDir(string workDir)
    {
        try { Directory.Delete(workDir, recursive: true); } catch { }
    }

    private static Task ReplyResultAsync(SslStream ssl, string id, object payload, CancellationToken ct)
    {
        var env = new MessageEnvelope
        {
            Kind = "response",
            Id = id,
            Result = JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts),
        };
        return JsonRpcFraming.WriteAsync(ssl, env, ct: ct);
    }

    private static Task ReplyErrorAsync(SslStream ssl, string id, string code, string message, CancellationToken ct)
    {
        var env = new MessageEnvelope
        {
            Kind = "response",
            Id = id,
            Error = JsonSerializer.SerializeToElement(
                new ErrorDto { Code = code, Message = message },
                JsonRpcFraming.JsonOpts),
        };
        return JsonRpcFraming.WriteAsync(ssl, env, ct: ct);
    }
}
