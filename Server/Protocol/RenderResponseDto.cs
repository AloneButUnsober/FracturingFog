// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Protocol/RenderResponseDto.cs
// Response payload for render.image and render.video. Exactly one of
// PngBytesBase64 / Mp4BytesBase64 / SavedPath is populated, depending on
// the request's ReturnMode and the rendered mode.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Protocol;

public sealed class RenderResponseDto
{
    /// <summary>Inline PNG bytes, base64. Set when mode=image and
    /// returnMode=inline.</summary>
    [JsonPropertyName("pngBytesBase64")]
    public string? PngBytesBase64 { get; set; }

    /// <summary>Inline video bytes, base64 (MP4 or MKV depending on
    /// lossless preset). Set when mode=video and returnMode=inline.</summary>
    [JsonPropertyName("mp4BytesBase64")]
    public string? Mp4BytesBase64 { get; set; }

    /// <summary>Absolute path of the file the server kept after rendering.
    /// Set when returnMode=saved-path. The server makes no promise about
    /// network reachability — caller must already have a way to fetch it.</summary>
    [JsonPropertyName("savedPath")]
    public string? SavedPath { get; set; }

    /// <summary>For mode=video and keepFrames=true, the absolute path of
    /// the per-frame PNG folder the server retained.</summary>
    [JsonPropertyName("frameFolderPath")]
    public string? FrameFolderPath { get; set; }

    [JsonPropertyName("width")]     public int     Width     { get; set; }
    [JsonPropertyName("height")]    public int     Height    { get; set; }
    [JsonPropertyName("elapsedMs")] public long    ElapsedMs { get; set; }
    [JsonPropertyName("framesWritten")] public int FramesWritten { get; set; }

    /// <summary>When true the response envelope is followed by
    /// <see cref="ChunkCount"/> chunk envelopes (Kind = "chunk", Result =
    /// <see cref="ChunkDto"/>). The receiver must read them in order
    /// and concatenate. Inline byte fields are empty on streamed
    /// responses — the receiver populates them after assembly. The server
    /// only streams when the artifact exceeds the inline threshold (16 MB
    /// by default) so small renders still ship in a single envelope.</summary>
    [JsonPropertyName("streaming")] public bool Streaming { get; set; }

    /// <summary>Set together with <see cref="Streaming"/>. Used by the
    /// client to allocate the assembly buffer exactly once.</summary>
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }

    /// <summary>Number of chunk envelopes the receiver should expect.</summary>
    [JsonPropertyName("chunkCount")] public int ChunkCount { get; set; }

    /// <summary>Base64-encoded SHA-256 of the entire rendered artifact.
    /// Set for both inline AND streamed responses. The client computes
    /// the same hash over the bytes it assembled and MUST refuse the
    /// result on mismatch. Catches in-process corruption that TLS
    /// cannot — for example, an artifact that was rewritten on disk
    /// between hash compute and send, or a ChunkDto.Sha256 that was
    /// accidentally computed over a stale buffer.</summary>
    [JsonPropertyName("artifactSha256")]
    public string? ArtifactSha256 { get; set; }
}
