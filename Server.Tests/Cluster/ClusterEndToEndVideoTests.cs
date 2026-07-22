// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/Cluster/ClusterEndToEndVideoTests.cs
// D-4a — end-to-end frame-range tile pipeline through the coordinator.
// Drives job.submit (video mode) → tile.next → tile.deliver
// (PayloadKind="frames") → job.status → job.fetch on the frames
// manifest stub. Worker-side rendering is faked: instead of a real
// engine, the test pre-builds the FRMS trailer per tile.

using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class ClusterEndToEndVideoTests : IDisposable
{
    private readonly string _root;
    private readonly ClusterLogger _log;
    private readonly WorkerRegistry _registry;
    private readonly JobStore _jobs;
    private readonly TileDispatcher _disp;
    private readonly ClusterCoordinator _coord;

    public ClusterEndToEndVideoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ff-e2e-vid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _log      = new ClusterLogger(Path.Combine(_root, "logs"));
        _registry = new WorkerRegistry { HeartbeatIntervalSeconds = 5 };
        _jobs     = new JobStore(Path.Combine(_root, "jobs"));
        _disp     = new TileDispatcher { MaxAttempts = 2 };
        _coord    = new ClusterCoordinator(_registry, _log)
        {
            Jobs       = _jobs,
            Dispatcher = _disp,
            Codec      = new RawHeaderCodec(),     // unused on video path but harmless
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
            WorkerName       = "vid-w",
            OsPlatform       = "win",
            LogicalCores     = 4,
            ProtocolVersion  = "1",
            EngineBuildSha   = "",
            PreferredTilePixels = 64,
        };
        var outcome = await _coord.HandleAsync("worker.register",
            ToParams(dto), CertRole.Worker, thumb, CancellationToken.None);
        Assert.Null(outcome.ErrorCode);
        return Assert.IsType<WorkerRegisterAckDto>(outcome.Result).WorkerId;
    }

    private static byte[] FakePng(int frameIndex)
    {
        // Tiny "PNG" stand-in: a fixed magic + frame id LE so the
        // coordinator can persist it but the test can identify each
        // frame on disk.
        var b = new byte[16];
        b[0] = (byte)'P'; b[1] = (byte)'N'; b[2] = (byte)'G'; b[3] = 0;
        BitConverter.GetBytes(frameIndex).CopyTo(b, 4);
        BitConverter.GetBytes(frameIndex * 7).CopyTo(b, 8);
        return b;
    }

    [Fact]
    public async Task Submit_Video_Job_Plans_Frame_Range_Tiles()
    {
        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode            = "video",
                FractalType     = "Mandelbrot",
                Width           = 320,
                Height          = 180,
                CenterX         = -0.75,
                CenterY         = 0.0,
                Zoom            = 4.0,
                VideoSeconds    = 2.0,
                VideoFps        = 30,
                VideoStartZoom  = 0.5,
            },
            TilePixelsHint = 20,   // doubles as frames-per-tile hint in video mode
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        Assert.Null(ackOut.ErrorCode);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);
        Assert.Equal(3, ack.TileCount);   // 60 frames / 20 per tile

        // status.json carries TotalFrames now
        var st = _jobs.ReadStatus(ack.JobId);
        Assert.NotNull(st);
        Assert.Equal(60, st!.TotalFrames);
    }

    [Fact]
    public async Task Video_Frame_Tiles_Deliver_Frames_To_Disk_And_Finalise()
    {
        const string thumb = "VVAABB";
        string workerId = await RegisterWorkerAsync(thumb);

        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode            = "video",
                FractalType     = "Mandelbrot",
                Width           = 128,
                Height          = 64,
                CenterX         = -0.75,
                CenterY         = 0.0,
                Zoom            = 4.0,
                VideoSeconds    = 1.0,
                VideoFps        = 10,
                VideoStartZoom  = 0.5,
            },
            TilePixelsHint = 5,    // 10 frames / 5 per tile = 2 tiles
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);
        Assert.Equal(2, ack.TileCount);

        // Tile loop — each tile delivers a FRMS trailer with fake PNGs.
        for (int i = 0; i < 4; i++)
        {
            var nextOut = await _coord.HandleAsync("tile.next",
                ToParams(new HeartbeatDto { WorkerId = workerId }),
                CertRole.Worker, thumb, CancellationToken.None);
            var res = Assert.IsType<TileNextResultDto>(nextOut.Result);
            if (res.WaitAgain) break;
            var tile = res.Tile!;
            Assert.NotNull(tile.FrameRange);
            var fr = tile.FrameRange!;

            var frames = new List<FramesPayloadCodec.Frame>();
            for (int f = fr.StartFrame; f < fr.EndFrame; f++)
                frames.Add(new FramesPayloadCodec.Frame(f, FakePng(f)));
            byte[] trailer = FramesPayloadCodec.Pack(frames);
            string sha = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(trailer));

            var delOut = await _coord.HandleAsync("tile.deliver",
                ToParams(new TileDeliverDto
                {
                    WorkerId    = workerId,
                    JobId       = ack.JobId,
                    TileId      = tile.TileId,
                    PayloadKind = "frames",
                    Width       = tile.Render.Width,
                    Height      = tile.Render.Height,
                    BytesBase64 = "",
                    Sha256      = sha,
                    RenderMs    = 1,
                }),
                CertRole.Worker, thumb, CancellationToken.None,
                binaryPayload: trailer);
            Assert.Null(delOut.ErrorCode);
            var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
            Assert.True(delAck.Accepted, $"tile.deliver refused: {delAck.RefuseReason}");
        }

        // Status should reach "ready" with the frames-manifest artifact.
        JobStatusDto? status = null;
        for (int i = 0; i < 50; i++)
        {
            var sOut = await _coord.HandleAsync("job.status",
                ToParams(new JobStatusRequestDto { JobId = ack.JobId }),
                CertRole.Client, "", CancellationToken.None);
            status = Assert.IsType<JobStatusDto>(sOut.Result);
            if (status.JobState is "ready" or "failed" or "cancelled") break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.Equal("ready", status!.JobState);
        Assert.True(status.ArtifactReady);

        // All 10 frames must exist on disk under the job's frames dir.
        Assert.Equal(10, _jobs.CountFrames(ack.JobId));
        for (int f = 0; f < 10; f++) Assert.True(_jobs.FrameExists(ack.JobId, f));

        // job.fetch returns the manifest path.
        var fetchOut = await _coord.HandleAsync("job.fetch",
            ToParams(new JobFetchRequestDto { JobId = ack.JobId }),
            CertRole.Client, "", CancellationToken.None);
        Assert.Null(fetchOut.ErrorCode);
        var fAck = Assert.IsType<JobFetchAckDto>(fetchOut.Result);
        Assert.Equal("frames-manifest.json", fAck.ArtifactExt);
        Assert.True(fAck.TotalBytes > 0);
        Assert.True(File.Exists(fetchOut.StreamFilePath!));

        string manifest = File.ReadAllText(fetchOut.StreamFilePath!);
        Assert.Contains("\"frames\": 10", manifest);
    }

    [Fact]
    public async Task Video_Tile_Deliver_Rejects_Frame_Count_Mismatch()
    {
        const string thumb = "VVCCDD";
        string workerId = await RegisterWorkerAsync(thumb);

        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode            = "video",
                FractalType     = "Mandelbrot",
                Width           = 64,
                Height          = 64,
                CenterX         = 0,
                CenterY         = 0,
                Zoom            = 2.0,
                VideoSeconds    = 1.0,
                VideoFps        = 10,
                VideoStartZoom  = 0.5,
            },
            TilePixelsHint = 5,
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);

        var nextOut = await _coord.HandleAsync("tile.next",
            ToParams(new HeartbeatDto { WorkerId = workerId }),
            CertRole.Worker, thumb, CancellationToken.None);
        var tile = Assert.IsType<TileNextResultDto>(nextOut.Result).Tile!;
        var fr = tile.FrameRange!;

        // Pack only HALF the frames.
        int half = (fr.EndFrame - fr.StartFrame) / 2;
        var frames = new List<FramesPayloadCodec.Frame>();
        for (int f = fr.StartFrame; f < fr.StartFrame + half; f++)
            frames.Add(new FramesPayloadCodec.Frame(f, FakePng(f)));
        byte[] trailer = FramesPayloadCodec.Pack(frames);
        string sha = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(trailer));

        var delOut = await _coord.HandleAsync("tile.deliver",
            ToParams(new TileDeliverDto
            {
                WorkerId    = workerId,
                JobId       = ack.JobId,
                TileId      = tile.TileId,
                PayloadKind = "frames",
                Width       = tile.Render.Width,
                Height      = tile.Render.Height,
                BytesBase64 = "",
                Sha256      = sha,
                RenderMs    = 1,
            }),
            CertRole.Worker, thumb, CancellationToken.None,
            binaryPayload: trailer);
        var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
        Assert.False(delAck.Accepted);
        Assert.Equal("frame-count-mismatch", delAck.RefuseReason);
    }

    [Fact]
    public async Task Video_Tile_Deliver_Rejects_Frame_Out_Of_Range()
    {
        const string thumb = "VVEEFF";
        string workerId = await RegisterWorkerAsync(thumb);

        var submit = new JobSubmitDto
        {
            Request = new RenderRequestDto
            {
                Mode            = "video",
                FractalType     = "Mandelbrot",
                Width           = 64,
                Height          = 64,
                CenterX         = 0,
                CenterY         = 0,
                Zoom            = 2.0,
                VideoSeconds    = 1.0,
                VideoFps        = 10,
                VideoStartZoom  = 0.5,
            },
            TilePixelsHint = 10,   // single 10-frame tile
        };
        var ackOut = await _coord.HandleAsync("job.submit", ToParams(submit),
            CertRole.Client, "", CancellationToken.None);
        var ack = Assert.IsType<JobAckDto>(ackOut.Result);

        var nextOut = await _coord.HandleAsync("tile.next",
            ToParams(new HeartbeatDto { WorkerId = workerId }),
            CertRole.Worker, thumb, CancellationToken.None);
        var tile = Assert.IsType<TileNextResultDto>(nextOut.Result).Tile!;
        var fr = tile.FrameRange!;

        // Right count but one frame's index is outside the range.
        var frames = new List<FramesPayloadCodec.Frame>();
        for (int f = fr.StartFrame; f < fr.EndFrame - 1; f++)
            frames.Add(new FramesPayloadCodec.Frame(f, FakePng(f)));
        frames.Add(new FramesPayloadCodec.Frame(fr.EndFrame + 100, FakePng(999)));
        byte[] trailer = FramesPayloadCodec.Pack(frames);
        string sha = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(trailer));

        var delOut = await _coord.HandleAsync("tile.deliver",
            ToParams(new TileDeliverDto
            {
                WorkerId    = workerId,
                JobId       = ack.JobId,
                TileId      = tile.TileId,
                PayloadKind = "frames",
                Width       = tile.Render.Width,
                Height      = tile.Render.Height,
                BytesBase64 = "",
                Sha256      = sha,
                RenderMs    = 1,
            }),
            CertRole.Worker, thumb, CancellationToken.None,
            binaryPayload: trailer);
        var delAck = Assert.IsType<TileDeliverAckDto>(delOut.Result);
        Assert.False(delAck.Accepted);
        Assert.Equal("frame-out-of-range", delAck.RefuseReason);
    }
}
