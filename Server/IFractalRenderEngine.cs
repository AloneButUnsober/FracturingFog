// Server/IFractalRenderEngine.cs
// Engine boundary. The Server library is UI-free and platform-free; the
// actual fractal rendering lives in the main WinExe (PosterRenderer +
// Mp4Writer + FfmpegEncoder + 14 calculator classes). The WinExe registers
// a concrete IFractalRenderEngine when it calls FFServer.RunAsync so this
// assembly stays compile-clean against just Abstractions.

using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server;

public interface IFractalRenderEngine
{
    /// <summary>
    /// Renders <paramref name="request"/> into <paramref name="workDir"/> and
    /// returns the path the server should hand back (inline-bytes mode reads
    /// from the path then deletes; saved-path mode returns it as-is).
    /// Async so the implementation can await ffmpeg encode + frame IO without
    /// pinning a thread-pool slot inside the per-job queue gate.
    /// </summary>
    Task<RenderArtifact> RenderAsync(
        RenderRequestDto request,
        string workDir,
        ISessionLog log,
        CancellationToken ct);
}

public sealed class RenderArtifact
{
    /// <summary>.png for image mode. .mp4 / .mkv for video mode.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>For video mode when keepFrames=true. Null otherwise.</summary>
    public string? FrameFolderPath { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public int FramesWritten { get; set; }
    public long ElapsedMs { get; set; }
}

public interface ISessionLog
{
    void Info(string line);
    void Warn(string line);
    void Err(string line);
}
