// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
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

public sealed class ClusterEndToEndImageTests : IDisposable
{
    private readonly string _root;
    private readonly string _logDir;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly TileDispatcher _disp;
    private readonly ClusterCoordinator _coord;

    public ClusterEndToEndImageTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), $"ff-e2e-{Guid.NewGuid():N}");
        _logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logDir);
        _log      = new ClusterLogger(_logDir);
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _jobs     = new JobStore(Path.Combine(_root, "jobs"));
        _disp     = new TileDispatcher { MaxAttempts = 2 };
        _coord    = new ClusterCoordinator(_registry, _log)
        {
            Jobs       = _jobs,
            Dispatcher = _disp,
            Codec      = new RawHeaderCodec(),
            EngineBuildSha = "",
            TileNextHold = TimeSpan.FromSeconds(2),
        };
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement ToParams(object payload)
        => JsonSerializer.SerializeToElement(payload, JsonRpcFraming.JsonOpts);

    private async Task<string> RegisterWorkerAsync(string thumb)
    {
        var dto = new WorkerRegisterDto
        {
            WorkerName = "test-w",
            OsPlatform = "win",
            LogicalCores = 4,
            ProtocolVersion = "1",
            EngineBuildSha = "",
            PreferredTilePixels = 64,
        };
        var outcome = await _coord.HandleAsync("worker.register",
            ToParams(dto), CertRole.Worker, thumb, CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Null(outcome.ErrorCode);
        var ack = Assert.IsType<WorkerRegisterAckDto>(outcome.Result);
        return ack.WorkerId;
    }

    [Fact]
    public async Task Submit_Status_TileLoop_Fetch_Yields_Final_Artifact()
    {
        const string thumb = "AABBCC";
        string workerId = await RegisterWorkerAsync(thumb);

        // 128×64 image, 64px tiles → 2×1 = 2 tiles.
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbrot",
                Width = 128, Height = 64,
                CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
            TilePixelsHint = 64,
        };
        var submitOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, thumbprint: "", CancellationToken.None);
        Assert.True(submitOut.Handled); Assert.Null(submitOut.ErrorCode);
        var ack = Assert.IsType<JobAckDto>(submitOut.Result);
        Assert.Equal(2, ack.TileCount);

        // Tile loop. Each tile.next returns a tile; render produces a
        // per-tile coloured fill; tile.deliver pastes it.
        for (int i = 0; i < 4; i++)  // hard cap to avoid infinite loop on bug
        {
            var nextOut = await _coord.HandleAsync("tile.next",
                ToParams(new HeartbeatDto { WorkerId = workerId }),
                CertRole.Worker, thumb, CancellationToken.None);
            Assert.Null(nextOut.ErrorCode);
            var res = Assert.IsType<TileNextResultDto>(nextOut.Result);
            if (res.WaitAgain) break;
            Assert.NotNull(res.Tile);
            var tile = res.Tile!;

            byte[] payload = RawHeaderCodec.BuildTile(
                tile.Render.Width, tile.Render.Height,
                fillR: (byte)(tile.TileId * 100), fillG: 50, fillB: 200);
            string sha = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(payload));

            var delOut = await _coord.HandleAsync("tile.deliver",
                ToParams(new TileDeliverDto
                {
                    WorkerId = workerId, JobId = ack.JobId, TileId = tile.TileId,
                    PayloadKind = "png",
                    Width = tile.Render.Width, Height = tile.Render.Height,
                    BytesBase64 = Convert.ToBase64String(payload),
                    Sha256 = sha,
                    RenderMs = 1,
                }),
                CertRole.Worker, thumb, CancellationToken.None);
            Assert.Null(delOut.ErrorCode);
            var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
            Assert.True(delAck.Accepted, $"tile.deliver refused: {delAck.RefuseReason}");
        }

        // Poll status until terminal.
        JobStatusDto? status = null;
        for (int i = 0; i < 50; i++)
        {
            var sOut = await _coord.HandleAsync("job.status",
                ToParams(new JobStatusRequestDto { JobId = ack.JobId }),
                CertRole.Client, "", CancellationToken.None);
            status = Assert.IsType<JobStatusDto>(sOut.Result);
            if (status.JobState is "ready" or "failed" or "cancelled") break;
            await Task.Delay(20);
        }
        Assert.NotNull(status);
        Assert.Equal("ready", status!.JobState);
        Assert.True(status.ArtifactReady);
        Assert.Equal(2, status.TilesDone);

        // Fetch — returns OkStreaming with the artifact path.
        var fetchOut = await _coord.HandleAsync("job.fetch",
            ToParams(new JobFetchRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        Assert.True(fetchOut.Handled);
        Assert.Null(fetchOut.ErrorCode);
        Assert.NotNull(fetchOut.StreamFilePath);
        Assert.True(File.Exists(fetchOut.StreamFilePath!));

        var fAck = Assert.IsType<JobFetchAckDto>(fetchOut.Result);
        Assert.Equal("png", fAck.ArtifactExt);
        Assert.True(fAck.TotalBytes > 0);
        Assert.True(fAck.ChunkCount > 0);

        // Verify artifact bytes match the merged buffer dimension.
        byte[] artifact = File.ReadAllBytes(fetchOut.StreamFilePath!);
        // 12-byte header + 128*64*4 = 32768 → 32780 total
        Assert.Equal(12 + 128 * 64 * 4, artifact.Length);
        // Magic 0xFADEFACE little-endian = bytes [CE, FA, DE, FA]
        Assert.Equal(0xCEu, artifact[0]);
        Assert.Equal(0xFAu, artifact[1]);
    }

    [Fact]
    public async Task Submit_Status_TileLoop_Fetch_Yields_Final_Artifact_Via_Binary_Trailer()
    {
        // D-3 raw-RGBA path: tile.deliver carries the BGRA payload in the
        // envelope binary trailer instead of base64. BytesBase64 is empty;
        // the coordinator must prefer the trailer.
        const string thumb = "BBCCDD";
        string workerId = await RegisterWorkerAsync(thumb);

        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbrot",
                Width = 128, Height = 64,
                CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
            TilePixelsHint = 64,
        };
        var submitOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(submitOut.Result);
        Assert.Equal(2, ack.TileCount);

        for (int i = 0; i < 4; i++)
        {
            var nextOut = await _coord.HandleAsync("tile.next",
                ToParams(new HeartbeatDto { WorkerId = workerId }),
                CertRole.Worker, thumb, CancellationToken.None);
            var res = Assert.IsType<TileNextResultDto>(nextOut.Result);
            if (res.WaitAgain) break;
            var tile = res.Tile!;

            // Raw BGRA, NO header. This is what the worker would ship
            // after decoding its engine's PNG output to BGRA.
            int tw = tile.Render.Width, th = tile.Render.Height;
            byte[] bgra = new byte[tw * th * 4];
            for (int p = 0; p < tw * th; p++)
            {
                bgra[p * 4 + 0] = 200;                       // B
                bgra[p * 4 + 1] = 50;                        // G
                bgra[p * 4 + 2] = (byte)(tile.TileId * 100); // R
                bgra[p * 4 + 3] = 0xFF;                      // A
            }
            string sha = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(bgra));

            var delOut = await _coord.HandleAsync("tile.deliver",
                ToParams(new TileDeliverDto
                {
                    WorkerId = workerId, JobId = ack.JobId, TileId = tile.TileId,
                    PayloadKind = "rgba",
                    Width = tw, Height = th,
                    BytesBase64 = "",            // intentionally empty — trailer carries bytes
                    Sha256 = sha,
                    RenderMs = 1,
                }),
                CertRole.Worker, thumb, CancellationToken.None,
                binaryPayload: bgra);
            Assert.Null(delOut.ErrorCode);
            var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
            Assert.True(delAck.Accepted, $"tile.deliver refused: {delAck.RefuseReason}");
        }

        JobStatusDto? status = null;
        for (int i = 0; i < 50; i++)
        {
            var sOut = await _coord.HandleAsync("job.status",
                ToParams(new JobStatusRequestDto { JobId = ack.JobId }),
                CertRole.Client, "", CancellationToken.None);
            status = Assert.IsType<JobStatusDto>(sOut.Result);
            if (status.JobState is "ready" or "failed" or "cancelled") break;
            await Task.Delay(20);
        }
        Assert.Equal("ready", status!.JobState);
        Assert.True(status.ArtifactReady);
        Assert.Equal(2, status.TilesDone);

        var fetchOut = await _coord.HandleAsync("job.fetch",
            ToParams(new JobFetchRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        Assert.Null(fetchOut.ErrorCode);
        byte[] artifact = File.ReadAllBytes(fetchOut.StreamFilePath!);
        // RawHeaderCodec adds a 12-byte header + 128×64×4 BGRA bytes.
        Assert.Equal(12 + 128 * 64 * 4, artifact.Length);
    }

    [Fact]
    public async Task Tile_Deliver_Binary_Trailer_Sha_Mismatch_Is_Rejected()
    {
        const string thumb = "BADBAD";
        string workerId = await RegisterWorkerAsync(thumb);

        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbrot",
                Width = 64, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
            TilePixelsHint = 64,
        };
        var submitOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(submitOut.Result);

        var nextOut = await _coord.HandleAsync("tile.next",
            ToParams(new HeartbeatDto { WorkerId = workerId }),
            CertRole.Worker, thumb, CancellationToken.None);
        var tile = Assert.IsType<TileNextResultDto>(nextOut.Result).Tile!;
        byte[] bgra = new byte[tile.Render.Width * tile.Render.Height * 4];

        var delOut = await _coord.HandleAsync("tile.deliver",
            ToParams(new TileDeliverDto
            {
                WorkerId = workerId, JobId = ack.JobId, TileId = tile.TileId,
                PayloadKind = "rgba",
                Width = tile.Render.Width, Height = tile.Render.Height,
                BytesBase64 = "",
                Sha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",  // wrong
                RenderMs = 1,
            }),
            CertRole.Worker, thumb, CancellationToken.None,
            binaryPayload: bgra);
        var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
        Assert.False(delAck.Accepted);
        Assert.Equal("sha-mismatch", delAck.RefuseReason);
    }

    [Fact]
    public async Task Submit_Refuses_Untileable_Fractal()
    {
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbulb",
                Width = 128, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
        };
        var outcome = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Equal("untileable-fractal", outcome.ErrorCode);
    }

    [Fact]
    public async Task Submit_Refuses_Unknown_Mode()
    {
        // D-4 accepts video; an unknown mode string is still refused.
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "audio-reactive", FractalType = "Mandelbrot",
                Width = 128, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
        };
        var outcome = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Equal("unsupported-mode", outcome.ErrorCode);
    }

    [Fact]
    public async Task Status_Unknown_Job_Returns_Error()
    {
        var outcome = await _coord.HandleAsync("job.status",
            ToParams(new JobStatusRequestDto { JobId = "nope" }),
            CertRole.Client, "", CancellationToken.None);
        Assert.True(outcome.Handled);
        Assert.Equal("unknown-job", outcome.ErrorCode);
    }

    [Fact]
    public async Task Cancel_Sets_State_To_Cancelled_And_Retires()
    {
        const string thumb = "DDEEFF";
        await RegisterWorkerAsync(thumb);
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbrot",
                Width = 128, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
            TilePixelsHint = 64,
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);

        var cOut = await _coord.HandleAsync("job.cancel",
            ToParams(new JobCancelRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        var cAck = Assert.IsType<JobCancelAckDto>(cOut.Result);
        Assert.True(cAck.Cancelled);

        var sOut = await _coord.HandleAsync("job.status",
            ToParams(new JobStatusRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        var st = Assert.IsType<JobStatusDto>(sOut.Result);
        Assert.Equal("cancelled", st.JobState);
    }

    [Fact]
    public async Task Tile_Error_Fatal_Code_Fails_Job_Without_Retry()
    {
        const string thumb = "112233";
        string workerId = await RegisterWorkerAsync(thumb);
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode = "image", FractalType = "Mandelbrot",
                Width = 64, Height = 64, CenterX = 0, CenterY = 0, Zoom = 1.0,
            },
            TilePixelsHint = 64,
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);

        // Claim the tile then report a fatal error.
        var nextOut = await _coord.HandleAsync("tile.next",
            ToParams(new HeartbeatDto { WorkerId = workerId }),
            CertRole.Worker, thumb, CancellationToken.None);
        var tileRes = Assert.IsType<TileNextResultDto>(nextOut.Result);
        Assert.NotNull(tileRes.Tile);

        await _coord.HandleAsync("tile.error",
            ToParams(new TileErrorDto
            {
                WorkerId = workerId, JobId = ack.JobId, TileId = tileRes.Tile!.TileId,
                Code = "forbidden-fractal", Message = "boom",
            }),
            CertRole.Worker, thumb, CancellationToken.None);

        var sOut = await _coord.HandleAsync("job.status",
            ToParams(new JobStatusRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        var st = Assert.IsType<JobStatusDto>(sOut.Result);
        Assert.Equal("failed", st.JobState);
        Assert.Contains("forbidden-fractal", st.FailReason);
    }
}
