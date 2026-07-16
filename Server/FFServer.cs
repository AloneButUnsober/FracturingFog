// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

using FracturingFog.Server.Cluster;
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

    /// <summary>Optional cluster-mode hook. When non-null, the
    /// FFServer routes worker.* and cluster.* methods through this
    /// coordinator (role-gated). Null = legacy single-server behaviour
    /// — render.image / render.video only.</summary>
    public IClusterCoordinator? Coordinator { get; init; }

    /// <summary>Maximum quiet time between requests on an authenticated
    /// session before the server closes the socket.</summary>
    public TimeSpan IdleReadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private readonly IFractalRenderEngine _engine;
    private readonly ServerTrust _trust;
    private readonly RemoteCertificateValidationCallback _clientValidator;
    private readonly SslProtocols _enabledTlsProtocols;
    private readonly X509RevocationMode _revocationMode;
    private readonly SemaphoreSlim _queueGate;
    private readonly EndpointRateLimiter _rateLimiter;
    private readonly RoleAwareRateLimiter _roleLimiter;

    /// <summary>Outer cap on accepted-but-still-open TCP connections,
    /// including ones still in the TLS handshake. Bounds memory + thread
    /// load against TLS-exhaustion / SYN-flood pressure.</summary>
    private readonly SemaphoreSlim _connectionGate;

    public FFServer(ServerConfig config, IFractalRenderEngine engine, ServerTrust trust)
    {
        Config = config;
        _engine = engine;
        _trust = trust;
        _revocationMode = ParseRevocationMode(config.RevocationCheckMode);
        _clientValidator = ServerCertLoader.BuildClientValidator(
            trust.TrustedClientCAs, config.AllowedClientThumbprints, _revocationMode,
            trust.IntermediateClientCAs);
        _enabledTlsProtocols = config.RequireTls13 ? SslProtocols.Tls13 : (SslProtocols.Tls12 | SslProtocols.Tls13);
        _queueGate = new SemaphoreSlim(Math.Max(1, config.QueueDepth));
        _connectionGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentConnections));
        _rateLimiter = new EndpointRateLimiter(config.RateLimitPerMinute, config.RateLimitBurst);
        _roleLimiter = new RoleAwareRateLimiter(
            clientPerMinute:         config.ClientCallPerMinute,
            clientBurst:             config.ClientCallBurst,
            workerTileNextPerMinute: config.WorkerTileNextPerMinute,
            workerTileNextBurst:     config.WorkerTileNextBurst);
    }

    /// <summary>D-6c1 — swap the per-role rate-limit knobs at runtime.
    /// Wired from ClusterEntry into <see cref="ClusterCoordinator.ApplyRoleLimiterChange"/>
    /// so cluster.config.set applies without bouncing the master. Per-key
    /// bucket state (in-flight tokens) is preserved across the swap.</summary>
    public void ReconfigureRoleLimiter(
        int clientPerMinute, int clientBurst,
        int workerTileNextPerMinute, int workerTileNextBurst)
    {
        _roleLimiter.Reconfigure(
            clientPerMinute, clientBurst,
            workerTileNextPerMinute, workerTileNextBurst);
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

            // Per-IP token bucket — close the socket immediately for IPs
            // that exceed their sustained-accept budget. Runs BEFORE the
            // global connection gate so abusive IPs cannot occupy slots
            // that legitimate IPs need.
            if (_rateLimiter.Enabled &&
                !_rateLimiter.TryAccept(tcp.Client.RemoteEndPoint as IPEndPoint))
            {
                try { tcp.Close(); } catch { }
                continue;
            }

            // NoDelay: JSON-RPC envelopes are small (≤ 1 MB chunks); Nagle's
            // 200 ms wait stacks with every status poll round-trip.
            // KeepAlive: long renders behind NAT silently lose the
            // connection if the OS sees nothing for the keepalive window.
            try { tcp.NoDelay = true; } catch { }
            try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

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
                    EnabledSslProtocols = _enabledTlsProtocols,
                    CertificateRevocationCheckMode = _revocationMode,
                    RemoteCertificateValidationCallback = _clientValidator,
                }, handshakeCts.Token).ConfigureAwait(false);
            }

            // ssl.RemoteCertificate may be a plain X509Certificate (not
            // X509Certificate2) on some runtimes — `as X509Certificate2`
            // would silently null out the audit thumbprint. Wrap so the
            // session log always records who authenticated.
            string? clientThumb = null;
            CertRole clientRole = CertRole.Client;
            if (ssl.RemoteCertificate != null)
            {
                try
                {
                    using var c2 = ssl.RemoteCertificate as X509Certificate2
                        ?? new X509Certificate2(ssl.RemoteCertificate);
                    clientThumb = c2.Thumbprint;
                    // Parse the role OU once at handshake-completion so
                    // every subsequent dispatch sees the same answer and
                    // a misissued cert is refused at the first method
                    // call rather than partway through a render.
                    try { clientRole = CertRoleParser.FromCertificate(c2); }
                    catch (InvalidOperationException ex)
                    {
                        // Unknown role suffix — close the session before
                        // any method runs. Client sees an EndOfStream;
                        // operator sees the reason in the session log.
                        sessLog = SessionLogger.Open(
                            Config.LogDir ?? ServerConfig.DefaultLogDir(),
                            remoteStr, clientThumb);
                        sessLog.Err($"cert role refused: {ex.Message}");
                        return;
                    }
                }
                catch { /* leave null + Client */ }
            }
            sessLog = SessionLogger.Open(
                Config.LogDir ?? ServerConfig.DefaultLogDir(),
                remoteStr,
                clientThumb);

            sessLog.Info($"session opened, tls={ssl.SslProtocol}, cipher={ssl.NegotiatedCipherSuite}, role={clientRole}");

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

                await DispatchAsync(env, ssl, sessLog, clientRole, clientThumb ?? "",
                    remote?.Address?.ToString() ?? "(unknown)", ct).ConfigureAwait(false);
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

    private async Task DispatchAsync(MessageEnvelope env, SslStream ssl, SessionLogger log,
        CertRole role, string thumbprint, string remoteIp, CancellationToken ct)
    {
        if (env.Kind != "request" || string.IsNullOrEmpty(env.Method))
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request", "envelope kind/method missing", ct).ConfigureAwait(false);
            return;
        }

        // D-6c — per-method per-role rate gate. server.status (cheap, used
        // by liveness probes) bypasses the gate so a probe loop cannot lock
        // itself out. Worker tile.next + client RPCs flow through.
        if (!string.Equals(env.Method, "server.status", StringComparison.Ordinal))
        {
            string limiterKey = role == CertRole.Worker ? thumbprint : remoteIp;
            if (_roleLimiter.TryAccept(role, limiterKey, env.Method!) == RoleLimiterDecision.RefusedRate)
            {
                log.Warn($"rate-limited: role={role} method='{env.Method}' key={limiterKey}");
                Metrics.RecordFailure("rate-limited", $"role={role} method={env.Method}");
                await ReplyErrorAsync(ssl, env.Id, "rate-limited",
                    $"rate limit exceeded for role={role} method='{env.Method}'", ct).ConfigureAwait(false);
                return;
            }
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
                if (Coordinator != null && IsClusterMethod(env.Method!))
                {
                    await DispatchClusterAsync(env, ssl, log, role, thumbprint, ct).ConfigureAwait(false);
                    break;
                }
                await ReplyErrorAsync(ssl, env.Id, "bad-request",
                    $"unknown method '{env.Method}'", ct).ConfigureAwait(false);
                break;
        }
    }

    private static bool IsClusterMethod(string method)
        => method.StartsWith("worker.", System.StringComparison.Ordinal)
        || method.StartsWith("cluster.", System.StringComparison.Ordinal)
        || method.StartsWith("tile.", System.StringComparison.Ordinal)
        || method.StartsWith("job.", System.StringComparison.Ordinal);

    private async Task DispatchClusterAsync(MessageEnvelope env, SslStream ssl, SessionLogger log,
        CertRole role, string thumbprint, CancellationToken ct)
    {
        string method = env.Method!;

        // Role gate. The coordinator never sees a method it should refuse
        // — keeps role policy in one place and saves the coordinator from
        // re-implementing the check per method.
        bool ok = method switch
        {
            _ when method.StartsWith("worker.", System.StringComparison.Ordinal) => role == CertRole.Worker,
            _ when method.StartsWith("tile.",   System.StringComparison.Ordinal) => role == CertRole.Worker,
            _ when method.StartsWith("cluster.", System.StringComparison.Ordinal) => role == CertRole.Admin,
            _ when method.StartsWith("job.",     System.StringComparison.Ordinal) => role is CertRole.Client or CertRole.Admin,
            _ => false,
        };
        if (!ok)
        {
            log.Warn($"cluster method '{method}' refused for role={role}");
            await ReplyErrorAsync(ssl, env.Id, "forbidden-role",
                $"method '{method}' not permitted for role={role}", ct).ConfigureAwait(false);
            return;
        }

        ClusterDispatchOutcome outcome;
        try
        {
            outcome = await Coordinator!.HandleAsync(
                method, env.Params, role, thumbprint, ct, env.Binary).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // session shutdown — swallow; the outer finally closes the socket
            return;
        }
        catch (System.Exception ex)
        {
            log.Err($"cluster method '{method}' threw: {ex.GetType().Name}: {ex.Message}");
            await ReplyErrorAsync(ssl, env.Id, "internal",
                $"{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        if (!outcome.Handled)
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request",
                $"unknown method '{method}'", ct).ConfigureAwait(false);
            return;
        }
        if (outcome.ErrorCode != null)
        {
            await ReplyErrorAsync(ssl, env.Id, outcome.ErrorCode, outcome.ErrorMessage ?? "", ct).ConfigureAwait(false);
            return;
        }
        await ReplyResultAsync(ssl, env.Id, outcome.Result ?? new { }, ct).ConfigureAwait(false);

        // Streaming follow-up (e.g. job.fetch): coordinator hands us a
        // file path + chunk count, FFServer streams the bytes using the
        // same chunked path as render.image/video so client-side
        // reassembly logic is shared.
        if (!string.IsNullOrEmpty(outcome.StreamFilePath) && outcome.StreamChunkCount > 0)
        {
            try
            {
                await StreamArtifactChunksAsync(ssl, env.Id, outcome.StreamFilePath!,
                    outcome.StreamChunkCount, log, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Err($"cluster stream '{method}' failed mid-stream: {ex.GetType().Name}: {ex.Message}");
            }
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

    /// <summary>Maximum length of any string field on an incoming
    /// <see cref="RenderRequestDto"/>. Hard cap so an attacker cannot
    /// ship a 200 MB RegionName inside the 256 MB frame envelope and
    /// force expensive lookups + log lines per request.</summary>
    public const int MaxDtoStringLength = 256;

    /// <summary>Maximum length of a single log line. Defends per-session
    /// log files against being filled by attacker-supplied DTO content
    /// (regionName, themeName, errorMessage echoed back into a log line).</summary>
    public const int MaxLogLineLength = 512;

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

        if (!ValidateDtoStringLengths(req, out string? lengthErr))
        {
            await ReplyErrorAsync(ssl, env.Id, "bad-request", lengthErr!, ct).ConfigureAwait(false);
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

        // Inline theme / region payloads carried for transient use when the
        // server's local registry does not have the named entry. Validate
        // size + shape up-front so the engine never sees an oversize or
        // malformed blob. The engine still performs the full deserialize
        // (Models types are main-exe-only) — this is a defensive gate.
        if (!string.IsNullOrEmpty(req.ThemeJson))
        {
            try { ThemePayloadValidator.Validate(req.ThemeJson); }
            catch (ServerProtocolException ex)
            {
                log.Warn($"theme payload refused: [{ex.Code}] {ex.Message}");
                Metrics.RecordFailure(ex.Code, ex.Message);
                await ReplyErrorAsync(ssl, env.Id, ex.Code, ex.Message, ct).ConfigureAwait(false);
                return;
            }
        }
        if (!string.IsNullOrEmpty(req.RegionJson))
        {
            try { RegionPayloadValidator.Validate(req.RegionJson); }
            catch (ServerProtocolException ex)
            {
                log.Warn($"region payload refused: [{ex.Code}] {ex.Message}");
                Metrics.RecordFailure(ex.Code, ex.Message);
                await ReplyErrorAsync(ssl, env.Id, ex.Code, ex.Message, ct).ConfigureAwait(false);
                return;
            }
        }

        // Poster-mode resolution: when all three poster fields are positive
        // and this is an image request, recompute Width/Height from
        // inches×dpi *before* the limit checks below. Mirrors the local
        // poster dialog (Hosting/AvaloniaDialogs.cs ShowPosterAsync) so the
        // same inputs produce the same pixel dims locally and remotely.
        // Portrait is consumed downstream as a 90° rotate flag — saved file
        // dims still equal (width, height) post-rotate, so the limit gate
        // can use the post-resolution Width/Height as-is.
        bool isImageMethod = string.Equals(env.Method, "render.image", StringComparison.Ordinal);
        if (isImageMethod
            && req.PosterInchesW is double posterW && posterW > 0
            && req.PosterInchesH is double posterH && posterH > 0
            && req.PosterDpi is int posterDpi && posterDpi > 0)
        {
            long calcW = (long)Math.Ceiling(posterW * posterDpi);
            long calcH = (long)Math.Ceiling(posterH * posterDpi);
            if (calcW > int.MaxValue || calcH > int.MaxValue)
            {
                await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                    $"poster pixel dims overflow int32 ({calcW}×{calcH})", ct).ConfigureAwait(false);
                return;
            }
            req.Width = (int)calcW;
            req.Height = (int)calcH;
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

        // MaxIterations gate: the inner calculator loop runs once per
        // escape-test per pixel. Without this an attacker who hits the
        // pixel cap can still ask for iterations=10^9 and pin the worker
        // for hours under the queue gate. Region-driven iterations also
        // pass through here when the request inlines them.
        if (req.Iterations is int reqIter && reqIter > Limits.MaxIterations)
        {
            await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                $"iterations={reqIter:N0} exceeds host cap {Limits.MaxIterations:N0}", ct).ConfigureAwait(false);
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
            // Aggregate frame-pixel budget. A request that passes
            // MaxPixels (single-frame) + MaxVideoSeconds (duration) +
            // MaxVideoFps individually can still ask for 8K × 600s × 240fps
            // ≈ 36 trillion pixels of work. This is the single check that
            // closes that gap.
            long framePixels = pixels * (long)Math.Ceiling(req.VideoSeconds * req.VideoFps);
            if (framePixels > Limits.MaxVideoFramePixels)
            {
                await ReplyErrorAsync(ssl, env.Id, "limit-exceeded",
                    $"video pixel-budget {framePixels:N0} exceeds host cap " +
                    $"{Limits.MaxVideoFramePixels:N0} (w×h×seconds×fps)", ct).ConfigureAwait(false);
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

        log.Info(TruncateLogLine(
            $"render begin: method={env.Method} type={parsedType} {req.Width}x{req.Height} " +
            $"region={req.RegionName ?? "(coords)"} theme={req.ThemeName} timeoutMin={minutes}"));

        using var inFlight = Metrics.BeginRender();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));

        RenderArtifact? artifact = null;
        Exception? failure = null;
        try
        {
            artifact = await _engine.RenderAsync(
                req, workDir, new SessionLoggerAdapter(log), cts.Token)
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

            // Hash once up front so the same digest goes on the inline
            // response AND on streamed responses' RenderResponseDto, before
            // any chunks ship. Client verifies after assembly.
            string artifactHash;
            try { artifactHash = await ComputeFileSha256Base64Async(artifact.FilePath, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Metrics.RecordFailure("internal", $"hash artifact: {ex.Message}");
                await ReplyErrorAsync(ssl, env.Id, "internal",
                    $"could not hash rendered artifact: {ex.Message}", ct).ConfigureAwait(false);
                TryCleanWorkDir(workDir);
                return;
            }
            resp.ArtifactSha256 = artifactHash;

            if (fileSize > InlineSingleEnvelopeThreshold)
            {
                resp.Streaming = true;
                resp.TotalBytes = fileSize;
                resp.ChunkCount = (int)((fileSize + ChunkBytes - 1) / ChunkBytes);
                Metrics.RecordSuccess();
                log.Info($"render done: elapsedMs={artifact.ElapsedMs} bytes=streamed " +
                         $"size={fileSize:N0} chunks={resp.ChunkCount} sha256={artifactHash[..Math.Min(16, artifactHash.Length)]}…");
                // Wrap stream + reply in try/finally so a client disconnect
                // mid-chunk does not leak the workdir. Without this the
                // exception unwinds past TryCleanWorkDir and the per-job
                // PNG frame folder lives on disk until WorkDirSweeper
                // (next server start) catches it.
                try
                {
                    await ReplyResultAsync(ssl, env.Id, resp, ct).ConfigureAwait(false);
                    await StreamArtifactChunksAsync(ssl, env.Id, artifact.FilePath, resp.ChunkCount, log, ct).ConfigureAwait(false);
                }
                finally { TryCleanWorkDir(workDir); }
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
            // Return only the workdir-relative tail so server.SavedPath
            // does not leak the host's filesystem layout (e.g. %APPDATA%
            // paths or per-OS user-profile prefixes) to authenticated
            // clients. The client treats SavedPath as an opaque token
            // it hands back to the operator anyway — the full path was
            // never actionable on the client side.
            resp.SavedPath = StripWorkDirPrefix(artifact.FilePath, workDir);
            resp.FrameFolderPath = artifact.FrameFolderPath != null
                ? StripWorkDirPrefix(artifact.FrameFolderPath, workDir)
                : null;
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

                string chunkSha = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, read)));

                var payload = new ChunkDto
                {
                    Seq = seq,
                    Total = totalChunks,
                    BytesBase64 = Convert.ToBase64String(buf, 0, read),
                    Sha256 = chunkSha,
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

    private static string StripWorkDirPrefix(string fullPath, string workDir)
    {
        try
        {
            string norm = Path.GetFullPath(fullPath);
            string root = Path.GetFullPath(workDir);
            if (norm.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rel = norm[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.Combine(Path.GetFileName(root), rel);
            }
        }
        catch { }
        // Fall back to the bare filename — never echo the host's full
        // %APPDATA% prefix even if normalization fails.
        return Path.GetFileName(fullPath);
    }

    private static X509RevocationMode ParseRevocationMode(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return X509RevocationMode.NoCheck;
        return s.Trim().ToLowerInvariant() switch
        {
            "online"  => X509RevocationMode.Online,
            "offline" => X509RevocationMode.Offline,
            _         => X509RevocationMode.NoCheck,
        };
    }

    private static bool ValidateDtoStringLengths(RenderRequestDto req, out string? err)
    {
        // Reject any string field that exceeds MaxDtoStringLength. The
        // outer frame cap is 256 MB; without this an attacker could ship
        // a single 200 MB RegionName / ThemeName / OutputName and force
        // expensive resolution + log lines per request.
        if (Long(req.Mode, "mode")) { err = TooLong("mode"); return false; }
        if (Long(req.RegionName, "regionName")) { err = TooLong("regionName"); return false; }
        if (Long(req.FractalType, "fractalType")) { err = TooLong("fractalType"); return false; }
        if (Long(req.ThemeName, "themeName")) { err = TooLong("themeName"); return false; }
        if (Long(req.QualityName, "qualityName")) { err = TooLong("qualityName"); return false; }
        if (Long(req.OutputName, "outputName")) { err = TooLong("outputName"); return false; }
        if (Long(req.Lossless, "lossless")) { err = TooLong("lossless"); return false; }
        if (Long(req.ReturnMode, "returnMode")) { err = TooLong("returnMode"); return false; }
        err = null;
        return true;

        static bool Long(string? s, string _) => s != null && s.Length > MaxDtoStringLength;
        static string TooLong(string field) =>
            $"field '{field}' exceeds {MaxDtoStringLength}-char limit";
    }

    private static string TruncateLogLine(string s)
    {
        if (s.Length <= MaxLogLineLength) return s;
        return s[..MaxLogLineLength] + "…[truncated]";
    }

    private static async Task<string> ComputeFileSha256Base64Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            while (true)
            {
                int n = await fs.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(sha.Hash!);
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
