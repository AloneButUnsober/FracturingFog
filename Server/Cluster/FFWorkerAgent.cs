// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/FFWorkerAgent.cs
// Worker-side outbound connection to the master. Owns the lifetime of
// one cluster session: connect → mTLS handshake → worker.register →
// loop {heartbeat | tile.next long-poll} → reconnect on disconnect.
//
// Phase D-1 implements connect, register, heartbeat, and the tile.next
// no-op loop (master always returns WaitAgain). Phase D-2 will wire
// real tile execution into the OnTileAssigned hook.
//
// Threading model: one background Task per FFWorkerAgent instance.
// Public start/stop are thread-safe. Internal loop is single-threaded
// so framing reads + writes serialise naturally.

using System;
using System.Collections.Generic;
using System.IO;
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

namespace FracturingFog.Server.Cluster;

public sealed class FFWorkerAgent : IAsyncDisposable
{
    public sealed class Options
    {
        public required string MasterHost { get; init; }
        public required int    MasterPort { get; init; }
        public required string WorkerCertPath { get; init; }
        public string? WorkerCertPassword { get; init; }
        public string? MasterCaCertPath { get; init; }
        public string? ExpectedMasterHostName { get; init; }

        public required WorkerRegisterDto Identity { get; init; }

        /// <summary>Local render engine — same shape used by FFServer's
        /// single-server path. Null = the worker logs the tile and ships
        /// tile.error "no-engine" so the master can retry elsewhere; only
        /// useful for connection-only smoke tests.</summary>
        public IFractalRenderEngine? Engine { get; init; }

        /// <summary>Optional image codec. When set, the worker decodes the
        /// engine's PNG output into raw BGRA and ships it via the binary
        /// envelope trailer (D-3 raw-RGBA path) — saves a base64 expansion
        /// on the wire and a decode on the master. When null, the worker
        /// falls back to D-2 base64-PNG delivery.</summary>
        public IClusterImageCodec? Codec { get; init; }

        /// <summary>Workdir root for per-tile scratch files. Each tile
        /// gets a fresh subdirectory; the engine writes a PNG there
        /// which the worker reads, ships, then deletes.</summary>
        public string? WorkDirRoot { get; init; }

        /// <summary>Initial reconnect delay after a failed handshake or
        /// dropped connection. Doubles up to <see cref="MaxBackoff"/> on
        /// repeated failure; resets on a successful register.</summary>
        public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxBackoff     { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Heartbeat cadence the worker uses if the master ack
        /// did not advertise one. Master normally tells us; this is the
        /// fallback only.</summary>
        public TimeSpan FallbackHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    }

    private readonly Options _opts;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runner;
    private long _nextId;

    /// <summary>WorkerId issued by the master at the most recent
    /// successful register. Empty before the first register completes.</summary>
    public string CurrentWorkerId { get; private set; } = "";

    /// <summary>True after register, false after disconnect. Reflects the
    /// session state, not the long-term liveness — UI uses this for the
    /// "connected to master" indicator.</summary>
    public bool IsRegistered { get; private set; }

    public FFWorkerAgent(Options opts) { _opts = opts; }

    public void Start()
    {
        if (_runner != null) throw new InvalidOperationException("agent already started");
        _runner = Task.Run(() => RunForeverAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_runner != null)
        {
            try { await _runner.ConfigureAwait(false); } catch { }
        }
        try { _cts.Dispose(); } catch { }
    }

    private async Task RunForeverAsync(CancellationToken ct)
    {
        TimeSpan backoff = _opts.InitialBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(ct).ConfigureAwait(false);
                // Clean disconnect (master closed). Reset backoff so the
                // next session starts immediately.
                backoff = _opts.InitialBackoff;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[worker] session error: {ex.GetType().Name}: {ex.Message}");
                // Exponential backoff capped at MaxBackoff. Cleared
                // every successful register; here we widen.
                backoff = TimeSpan.FromMilliseconds(
                    Math.Min(_opts.MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
            }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        var clientCert = X509CertificateLoader.LoadPkcs12FromFile(
            _opts.WorkerCertPath, _opts.WorkerCertPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        X509Certificate2Collection trustedMasterCAs = new();
        if (!string.IsNullOrWhiteSpace(_opts.MasterCaCertPath))
        {
            trustedMasterCAs.Add(X509CertificateLoader.LoadPkcs12FromFile(
                _opts.MasterCaCertPath!, password: null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet));
        }

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_opts.MasterHost, _opts.MasterPort, ct).ConfigureAwait(false);
        try { tcp.NoDelay = true; } catch { }
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

        RemoteCertificateValidationCallback validate = (s, presented, chain, errors) =>
        {
            if (trustedMasterCAs.Count == 0) return errors == SslPolicyErrors.None;
            if (presented is null) return false;
            using var custom = new System.Security.Cryptography.X509Certificates.X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 ca in trustedMasterCAs)
                custom.ChainPolicy.CustomTrustStore.Add(ca);
            var leaf = presented as X509Certificate2 ?? new X509Certificate2(presented);
            return custom.Build(leaf);
        };

        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            userCertificateValidationCallback: validate);

        var clientCol = new X509CertificateCollection { clientCert };
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _opts.ExpectedMasterHostName ?? _opts.MasterHost,
            ClientCertificates = clientCol,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = validate,
        }, ct).ConfigureAwait(false);

        // ── worker.register ──
        var regDto = _opts.Identity;
        if (!string.IsNullOrEmpty(CurrentWorkerId))
            regDto.ResumeWorkerId = CurrentWorkerId;  // reuse the same id on reconnect
        var ack = await CallAsync<WorkerRegisterAckDto>(ssl, "worker.register", regDto, ct).ConfigureAwait(false);
        CurrentWorkerId = ack.WorkerId;
        IsRegistered = true;
        Console.WriteLine($"[worker] registered as {ack.WorkerId} (heartbeat={ack.HeartbeatIntervalSeconds}s, hold={ack.TileNextHoldSeconds}s)");

        TimeSpan heartbeatInterval = ack.HeartbeatIntervalSeconds > 0
            ? TimeSpan.FromSeconds(ack.HeartbeatIntervalSeconds)
            : _opts.FallbackHeartbeatInterval;

        try
        {
            // The protocol model: this loop owns the socket. We alternate
            // between heartbeats (cheap, fast) and a single tile.next
            // long-poll. Heartbeats fall due first; tile.next runs in
            // between and is what dominates wait-time.
            DateTime nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan untilHeartbeat = nextHeartbeat - DateTime.UtcNow;
                if (untilHeartbeat <= TimeSpan.Zero)
                {
                    await SendHeartbeatAsync(ssl, ct).ConfigureAwait(false);
                    nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                    continue;
                }

                // Long-poll. Master holds the call for up to TileNextHold
                // before returning WaitAgain. We re-check the cancellation
                // token after the call returns.
                var tileResult = await CallAsync<TileNextResultDto>(ssl, "tile.next",
                    new HeartbeatDto { WorkerId = CurrentWorkerId }, ct).ConfigureAwait(false);

                if (tileResult.Shutdown)
                {
                    Console.WriteLine("[worker] master requested shutdown");
                    return;
                }

                if (tileResult.Tile is { } tile)
                {
                    await ExecuteAndDeliverAsync(ssl, tile, ct).ConfigureAwait(false);
                }
                // Else (WaitAgain=true && Tile==null): immediately loop
                // — heartbeat next then another tile.next long-poll.
            }
        }
        finally
        {
            IsRegistered = false;
        }
    }

    private async Task ExecuteAndDeliverAsync(System.Net.Security.SslStream ssl,
        TileJobDto tile, CancellationToken ct)
    {
        if (_opts.Engine is null)
        {
            Console.Error.WriteLine($"[worker] no engine wired; refusing tile {tile.JobId}/{tile.TileId}");
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "engine-failed",
                Message  = "worker has no engine wired",
            }, ct).ConfigureAwait(false);
            return;
        }

        // D-4 — frame-range tiles run their own path. Render each frame
        // in the range with smoothstep-interpolated zoom, pack the PNGs
        // into one FRMS trailer, deliver in one tile.deliver call.
        if (tile.FrameRange != null)
        {
            await ExecuteAndDeliverFramesAsync(ssl, tile, tile.FrameRange, ct).ConfigureAwait(false);
            return;
        }

        string root = _opts.WorkDirRoot ?? Path.Combine(Path.GetTempPath(), "ff-worker");
        string workDir = Path.Combine(root,
            $"job-{tile.JobId}-t{tile.TileId}-{Guid.NewGuid():N}".Substring(0, 48));
        Directory.CreateDirectory(workDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        RenderArtifact? artifact = null;
        try
        {
            artifact = await _opts.Engine.RenderAsync(
                tile.Render, workDir, new ConsoleSessionLog($"tile-{tile.JobId}/{tile.TileId}"), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] tile render failed: {ex.GetType().Name}: {ex.Message}");
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "engine-failed",
                Message  = $"{ex.GetType().Name}: {ex.Message}",
            }, ct).ConfigureAwait(false);
            TryClean(workDir);
            return;
        }
        sw.Stop();

        byte[] payload;
        try { payload = await File.ReadAllBytesAsync(artifact.FilePath, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "engine-failed",
                Message  = $"reading tile output: {ex.Message}",
            }, ct).ConfigureAwait(false);
            TryClean(workDir);
            return;
        }

        // D-3: when a codec is wired, decode the engine's PNG into raw
        // BGRA and ship it via the binary envelope trailer. Saves the
        // 33 % base64 expansion + a JSON-string traversal at the master.
        // The master's ArtifactMerger already accepts raw BGRA via
        // TryMergeRgbaTile, so no merger change is needed.
        byte[] wireBytes;
        string payloadKind;
        if (_opts.Codec != null)
        {
            try
            {
                wireBytes = _opts.Codec.DecodePngToBgra(payload, out int dw, out int dh);
                if (dw != tile.Render.Width || dh != tile.Render.Height)
                    throw new InvalidDataException(
                        $"codec decoded {dw}x{dh}, expected {tile.Render.Width}x{tile.Render.Height}");
                payloadKind = "rgba";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[worker] codec decode failed, falling back to PNG: {ex.Message}");
                wireBytes = payload;
                payloadKind = "png";
            }
        }
        else
        {
            wireBytes = payload;
            payloadKind = "png";
        }

        string sha = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(wireBytes));
        var deliverDto = new TileDeliverDto
        {
            WorkerId    = CurrentWorkerId,
            JobId       = tile.JobId,
            TileId      = tile.TileId,
            PayloadKind = payloadKind,
            Width       = tile.Render.Width,
            Height      = tile.Render.Height,
            // Binary trailer path leaves BytesBase64 empty — server
            // prefers the trailer when present.
            BytesBase64 = "",
            Sha256      = sha,
            RenderMs    = sw.ElapsedMilliseconds,
        };
        var ack = await CallAsync<TileDeliverAckDto>(ssl, "tile.deliver", deliverDto, ct, binaryTrailer: wireBytes).ConfigureAwait(false);

        if (!ack.Accepted)
            Console.Error.WriteLine($"[worker] master refused tile {tile.JobId}/{tile.TileId}: {ack.RefuseReason}");

        TryClean(workDir);
    }

    /// <summary>D-4 — render every frame in <paramref name="range"/>,
    /// pack the resulting PNGs into a FRMS trailer, ship via one
    /// tile.deliver call with PayloadKind="frames". Math mirrors
    /// BatchRenderer.RenderVideo so a cluster-rendered video is
    /// frame-for-frame identical to the single-server output.</summary>
    private async Task ExecuteAndDeliverFramesAsync(
        System.Net.Security.SslStream ssl, TileJobDto tile,
        FrameRangeDto range, CancellationToken ct)
    {
        if (range.StartFrame < 0 || range.EndFrame <= range.StartFrame || range.TotalFrames < 2)
        {
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "bad-request",
                Message  = $"invalid frame range [{range.StartFrame},{range.EndFrame}) of {range.TotalFrames}",
            }, ct).ConfigureAwait(false);
            return;
        }

        string root = _opts.WorkDirRoot ?? Path.Combine(Path.GetTempPath(), "ff-worker");
        string workDirBase = Path.Combine(root,
            $"job-{tile.JobId}-t{tile.TileId}-{Guid.NewGuid():N}".Substring(0, 48));
        Directory.CreateDirectory(workDirBase);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var packed = new List<FramesPayloadCodec.Frame>(range.EndFrame - range.StartFrame);

        try
        {
            for (int f = range.StartFrame; f < range.EndFrame; f++)
            {
                ct.ThrowIfCancellationRequested();

                double t = range.TotalFrames == 1 ? 1.0 : (double)f / (range.TotalFrames - 1);
                double te = t * t * (3.0 - 2.0 * t);
                double zoomF = Math.Exp(range.LogStartZoom + range.LogZoomDelta * te);

                var perFrame = CloneRender(tile.Render);
                perFrame.Zoom = zoomF;

                string frameDir = Path.Combine(workDirBase, $"f{f:D6}");
                Directory.CreateDirectory(frameDir);
                RenderArtifact art = await _opts.Engine!.RenderAsync(
                    perFrame, frameDir,
                    new ConsoleSessionLog($"frame-{tile.JobId}/{tile.TileId}/{f}"),
                    ct).ConfigureAwait(false);
                byte[] png = await File.ReadAllBytesAsync(art.FilePath, ct).ConfigureAwait(false);
                packed.Add(new FramesPayloadCodec.Frame(f, png));

                try { Directory.Delete(frameDir, recursive: true); } catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryClean(workDirBase);
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[worker] frame render failed: {ex.GetType().Name}: {ex.Message}");
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "engine-failed",
                Message  = $"{ex.GetType().Name}: {ex.Message}",
            }, ct).ConfigureAwait(false);
            TryClean(workDirBase);
            return;
        }
        sw.Stop();

        byte[] trailer;
        try { trailer = FramesPayloadCodec.Pack(packed); }
        catch (Exception ex)
        {
            await CallVoidAsync(ssl, "tile.error", new TileErrorDto
            {
                WorkerId = CurrentWorkerId,
                JobId    = tile.JobId,
                TileId   = tile.TileId,
                Code     = "engine-failed",
                Message  = $"packing frames: {ex.Message}",
            }, ct).ConfigureAwait(false);
            TryClean(workDirBase);
            return;
        }

        string sha = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(trailer));
        var deliverDto = new TileDeliverDto
        {
            WorkerId    = CurrentWorkerId,
            JobId       = tile.JobId,
            TileId      = tile.TileId,
            PayloadKind = "frames",
            Width       = tile.Render.Width,
            Height      = tile.Render.Height,
            BytesBase64 = "",
            Sha256      = sha,
            RenderMs    = sw.ElapsedMilliseconds,
        };
        var ack = await CallAsync<TileDeliverAckDto>(
            ssl, "tile.deliver", deliverDto, ct, binaryTrailer: trailer).ConfigureAwait(false);

        if (!ack.Accepted)
            Console.Error.WriteLine(
                $"[worker] master refused frames tile {tile.JobId}/{tile.TileId}: {ack.RefuseReason}");

        TryClean(workDirBase);
    }

    private static RenderRequestDto CloneRender(RenderRequestDto src) => new()
    {
        Mode                 = src.Mode,
        RegionName           = src.RegionName,
        FractalType          = src.FractalType,
        CenterX              = src.CenterX,
        CenterY              = src.CenterY,
        Zoom                 = src.Zoom,
        Iterations           = src.Iterations,
        CenterXLo            = src.CenterXLo,
        CenterX2             = src.CenterX2,
        CenterX3             = src.CenterX3,
        CenterYLo            = src.CenterYLo,
        CenterY2             = src.CenterY2,
        CenterY3             = src.CenterY3,
        ThemeName            = src.ThemeName,
        QualityName          = src.QualityName,
        ThemeJson            = src.ThemeJson,
        RegionJson           = src.RegionJson,
        Width                = src.Width,
        Height               = src.Height,
        OutputName           = src.OutputName,
        ReturnMode           = src.ReturnMode,
        SuppressDecorations  = src.SuppressDecorations,
    };

    private static void TryClean(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private async Task CallVoidAsync(System.Net.Security.SslStream ssl, string method, object payload, CancellationToken ct)
    {
        try { await CallAsync<object>(ssl, method, payload, ct).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[worker] {method} failed: {ex.Message}"); }
    }

    private sealed class ConsoleSessionLog : ISessionLog
    {
        private readonly string _prefix;
        public ConsoleSessionLog(string prefix) { _prefix = prefix; }
        public void Info(string line) => Console.WriteLine($"[{_prefix}] {line}");
        public void Warn(string line) => Console.WriteLine($"[{_prefix}] WARN {line}");
        public void Err (string line) => Console.Error.WriteLine($"[{_prefix}] ERR {line}");
    }

    private Task SendHeartbeatAsync(SslStream ssl, CancellationToken ct)
        => CallAsync<HeartbeatAckDto>(ssl, "worker.heartbeat", new HeartbeatDto
        {
            WorkerId      = CurrentWorkerId,
            TilesInFlight = 0,                     // D-1 has no tile execution
            CpuPercent    = -1,
            FreeRamBytes  = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        }, ct);

    private async Task<TResult> CallAsync<TResult>(
        SslStream ssl, string method, object payload, CancellationToken ct,
        byte[]? binaryTrailer = null)
    {
        long id = Interlocked.Increment(ref _nextId);
        var env = new MessageEnvelope
        {
            Kind   = "request",
            Id     = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Method = method,
            Params = JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts),
            Binary = binaryTrailer,
        };
        await JsonRpcFraming.WriteAsync(ssl, env, ct: ct).ConfigureAwait(false);

        var resp = await JsonRpcFraming.ReadAsync(ssl, ct: ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("master closed connection");

        if (resp.Error is JsonElement errEl)
        {
            var err = errEl.Deserialize<ErrorDto>(JsonRpcFraming.JsonOpts)
                ?? new ErrorDto { Code = "internal", Message = "(no error body)" };
            throw new InvalidOperationException(
                $"master refused {method}: [{err.Code}] {err.Message}");
        }
        if (resp.Result is not JsonElement resEl)
            throw new InvalidDataException($"{method}: response missing result");

        return resEl.Deserialize<TResult>(JsonRpcFraming.JsonOpts)
            ?? throw new InvalidDataException($"{method}: result deserialised to null");
    }
}
