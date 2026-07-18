// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Logging;
using FracturingFog.Server.Protocol;
using FracturingFog.Server.Tls;
using FracturingFog.Server.Wire;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

/// <summary>
/// D-6e — Phase D-6 acceptance stress test. Drives the in-process
/// ClusterCoordinator with the §9 D-6 workload: 50 concurrent client
/// connections submit 200 jobs total against an 8-worker pool. Every
/// job must reach <c>ready</c> with a fetchable artifact and no
/// admission errors. The point of the test is to catch dispatcher
/// deadlock / queue starvation / status regressions under contention
/// — not to benchmark wall-clock. A real network/TLS stack would
/// dominate the run-time for an effect the coordinator surface
/// already represents faithfully (the existing
/// <see cref="ClusterEndToEndImageTests"/> exercise the same call shape).
/// </summary>
public sealed class StressTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly TileDispatcher _disp;
    private readonly ClusterCoordinator _coord;

    public StressTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), $"ff-stress-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logDir);
        _log      = new ClusterLogger(_logDir);
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _jobs     = new JobStore(Path.Combine(_root, "jobs"));
        _disp     = new TileDispatcher { MaxAttempts = 2 };
        _coord    = new ClusterCoordinator(_registry, _log)
        {
            Jobs           = _jobs,
            Dispatcher     = _disp,
            Codec          = new RawHeaderCodec(),
            EngineBuildSha = "",
            // Tight long-poll so an idle worker round-trips quickly. The
            // stress run finishes well inside a few seconds when the
            // dispatcher is healthy; padding the hold would only mask a
            // regression behind the timeout.
            TileNextHold   = TimeSpan.FromMilliseconds(100),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement ToParams(object payload)
        => JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts);

    private async Task<string> RegisterWorkerAsync(string thumb, string name)
    {
        var dto = new WorkerRegisterDto
        {
            WorkerName = name,
            OsPlatform = "stress",
            LogicalCores = 4,
            ProtocolVersion = "1",
            EngineBuildSha = "",
            PreferredTilePixels = 64,
            MaxConcurrentTiles = 1,
        };
        var outcome = await _coord.HandleAsync("worker.register",
            ToParams(dto), CertRole.Worker, thumb, CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Null(outcome.ErrorCode);
        return Assert.IsType<WorkerRegisterAckDto>(outcome.Result).WorkerId;
    }

    [Fact]
    public async Task Cluster_Sustains_50_Clients_8_Workers_200_Jobs()
    {
        const int numWorkers = 8;
        const int numClients = 50;
        const int totalJobs  = 200;
        const int jobsPerClient = totalJobs / numClients;  // 4

        // ── workers ───────────────────────────────────────────────────
        string[] thumbs    = new string[numWorkers];
        string[] workerIds = new string[numWorkers];
        for (int i = 0; i < numWorkers; i++)
        {
            thumbs[i]    = $"WORKER-THUMB-{i:D2}";
            workerIds[i] = await RegisterWorkerAsync(thumbs[i], $"w{i:D2}");
        }

        // Global wall-clock guard. Sized so a CI box that's swapping
        // still fails fast on a real deadlock instead of hanging the
        // whole test run.
        using var globalCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(globalCts.Token);

        long tilesDelivered = 0;
        var workerTasks = new Task[numWorkers];
        for (int w = 0; w < numWorkers; w++)
        {
            int wi = w;
            workerTasks[wi] = Task.Run(async () =>
            {
                while (!workerCts.IsCancellationRequested)
                {
                    ClusterDispatchOutcome next;
                    try
                    {
                        next = await _coord.HandleAsync("tile.next",
                            ToParams(new HeartbeatDto { WorkerId = workerIds[wi] }),
                            CertRole.Worker, thumbs[wi], workerCts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    if (next.ErrorCode != null) continue;
                    var res = (TileNextResultDto)next.Result!;
                    if (res.WaitAgain || res.Tile is null) continue;

                    var tile = res.Tile;
                    byte[] payload = RawHeaderCodec.BuildTile(
                        tile.Render.Width, tile.Render.Height,
                        fillR: (byte)(wi * 31), fillG: 64, fillB: 128);
                    string sha = Convert.ToBase64String(SHA256.HashData(payload));

                    try
                    {
                        await _coord.HandleAsync("tile.deliver",
                            ToParams(new TileDeliverDto
                            {
                                WorkerId    = workerIds[wi],
                                JobId       = tile.JobId,
                                TileId      = tile.TileId,
                                PayloadKind = "png",
                                Width       = tile.Render.Width,
                                Height      = tile.Render.Height,
                                BytesBase64 = Convert.ToBase64String(payload),
                                Sha256      = sha,
                                RenderMs    = 1,
                            }),
                            CertRole.Worker, thumbs[wi], workerCts.Token);
                        Interlocked.Increment(ref tilesDelivered);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }, workerCts.Token);
        }

        // ── clients ───────────────────────────────────────────────────
        // Each client submits jobsPerClient jobs in sequence, then
        // polls each to terminal status, then fetches each. Mirrors the
        // single-client lifecycle in ClusterEndToEndImageTests so any
        // regression that breaks at scale also breaks the focused test.
        var clientTasks = new Task<List<string>>[numClients];
        var sw = Stopwatch.StartNew();
        for (int c = 0; c < numClients; c++)
        {
            int ci = c;
            clientTasks[ci] = Task.Run(async () =>
            {
                var jobIds = new List<string>(jobsPerClient);

                for (int j = 0; j < jobsPerClient; j++)
                {
                    // Vary centre per (client, job) so two submissions
                    // are never byte-identical — exercises the planner
                    // + JobStore key-uniqueness path.
                    var submit = new JobSubmitDto
                    {
                        Request = new RenderRequestDto
                        {
                            Mode = "image", FractalType = "Mandelbrot",
                            Width = 64, Height = 64,
                            CenterX = ci * 0.01 + j * 0.001,
                            CenterY = j * 0.005,
                            Zoom = 1.0,
                        },
                        TilePixelsHint = 64,
                    };
                    var ackOut = await _coord.HandleAsync("job.submit",
                        ToParams(submit), CertRole.Client,
                        thumbprint: $"client-{ci:D2}", globalCts.Token);
                    Assert.True(ackOut.Handled);
                    Assert.Null(ackOut.ErrorCode);
                    var ack = Assert.IsType<JobAckDto>(ackOut.Result);
                    Assert.Equal(1, ack.TileCount);
                    jobIds.Add(ack.JobId);
                }

                foreach (var jid in jobIds)
                {
                    JobStatusDto? last = null;
                    while (!globalCts.IsCancellationRequested)
                    {
                        var sOut = await _coord.HandleAsync("job.status",
                            ToParams(new JobStatusRequestDto { JobId = jid }),
                            CertRole.Client, $"client-{ci:D2}", globalCts.Token);
                        last = Assert.IsType<JobStatusDto>(sOut.Result);
                        if (last.JobState == "ready") break;
                        Assert.NotEqual("failed",    last.JobState);
                        Assert.NotEqual("cancelled", last.JobState);
                        await Task.Delay(15, globalCts.Token);
                    }
                    Assert.NotNull(last);
                    Assert.Equal("ready", last!.JobState);
                    Assert.True(last.ArtifactReady);
                    Assert.Equal(1, last.TilesDone);
                }

                foreach (var jid in jobIds)
                {
                    var fOut = await _coord.HandleAsync("job.fetch",
                        ToParams(new JobFetchRequestDto { JobId = jid }),
                        CertRole.Client, $"client-{ci:D2}", globalCts.Token);
                    Assert.True(fOut.Handled);
                    Assert.Null(fOut.ErrorCode);
                    Assert.NotNull(fOut.StreamFilePath);
                    Assert.True(File.Exists(fOut.StreamFilePath!));
                    var fAck = Assert.IsType<JobFetchAckDto>(fOut.Result);
                    Assert.Equal("png", fAck.ArtifactExt);
                    Assert.True(fAck.TotalBytes > 0);
                }

                return jobIds;
            }, globalCts.Token);
        }

        List<string>[] perClientIds;
        try
        {
            perClientIds = await Task.WhenAll(clientTasks);
        }
        finally
        {
            workerCts.Cancel();
            try { await Task.WhenAll(workerTasks); } catch { /* expected cancellation */ }
        }
        sw.Stop();

        var allIds = perClientIds.SelectMany(x => x).ToList();
        Assert.Equal(totalJobs, allIds.Count);
        Assert.Equal(totalJobs, allIds.Distinct().Count());
        Assert.True(Interlocked.Read(ref tilesDelivered) >= totalJobs,
            $"expected ≥ {totalJobs} tiles delivered, got {tilesDelivered}");

        // Cross-check against the persisted job store — confirms each
        // job actually transitioned to "ready" on disk, not just in
        // the in-memory coordinator response.
        foreach (var id in allIds)
        {
            var st = _jobs.ReadStatus(id);
            Assert.NotNull(st);
            Assert.Equal("ready", st!.JobState);
        }

        Console.WriteLine(
            $"stress: {totalJobs} jobs, {numClients} clients, {numWorkers} workers, " +
            $"{tilesDelivered} tile deliveries in {sw.Elapsed.TotalSeconds:F2}s");
    }
}
