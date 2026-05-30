// Server/Protocol/RenderRequestDto.cs
// Wire DTO mirroring Batch/BatchOptions field-for-field. Stays in this
// assembly (not Abstractions) so the protocol can evolve independently of
// the in-process batch options surface.

using System.Text.Json.Serialization;

namespace FracturingFog.Server.Protocol;

public sealed class RenderRequestDto
{
    /// <summary>"image" or "video".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "image";

    /// <summary>Region name (case-insensitive). Server resolves via FractalRegionLibrary.</summary>
    [JsonPropertyName("regionName")]
    public string? RegionName { get; set; }

    /// <summary>Fractal type name. Mandelbrot, Julia, BurningShip, Tricorn,
    /// Multibrot, Phoenix, Newton, Nova, BuddhaBrot, IFS, LSystem,
    /// StrangeAttractor, Mandelbulb, TearDrop. UserEquation / Sandbox /
    /// UserBulb are refused by the FractalTypeAllowlist guard.</summary>
    [JsonPropertyName("fractalType")]
    public string FractalType { get; set; } = "Mandelbrot";

    [JsonPropertyName("centerX")] public double? CenterX { get; set; }
    [JsonPropertyName("centerY")] public double? CenterY { get; set; }
    [JsonPropertyName("zoom")]    public double? Zoom    { get; set; }
    [JsonPropertyName("iterations")] public int? Iterations { get; set; }

    [JsonPropertyName("themeName")]   public string ThemeName   { get; set; } = "HSV";
    [JsonPropertyName("qualityName")] public string QualityName { get; set; } = "Standard";

    [JsonPropertyName("width")]  public int Width  { get; set; } = 1920;
    [JsonPropertyName("height")] public int Height { get; set; } = 1080;

    /// <summary>Optional base filename for server-side saves. Auto-derived
    /// from region/coords when null.</summary>
    [JsonPropertyName("outputName")]
    public string? OutputName { get; set; }

    // Video-only.
    [JsonPropertyName("videoSeconds")]   public double VideoSeconds   { get; set; } = 20.0;
    [JsonPropertyName("videoFps")]       public int    VideoFps       { get; set; } = 30;
    [JsonPropertyName("videoStartZoom")] public double VideoStartZoom { get; set; } = 0.5;
    [JsonPropertyName("videoReverse")]   public bool   VideoReverse   { get; set; }

    /// <summary>"none" | "h264" | "ffv1" | "h264hq". Maps to BatchLossless.</summary>
    [JsonPropertyName("lossless")]
    public string Lossless { get; set; } = "none";

    /// <summary>If null, server picks: true unless lossless preset is set.</summary>
    [JsonPropertyName("keepFrames")]
    public bool? KeepFrames { get; set; }

    /// <summary>"inline" returns raw bytes in the response (PNG / MP4 / MKV).
    /// "saved-path" returns the absolute path on the server's filesystem and
    /// leaves the file in place for the caller to copy.</summary>
    [JsonPropertyName("returnMode")]
    public string ReturnMode { get; set; } = "inline";

    /// <summary>Client-requested timeout in minutes. Server enforces the
    /// smaller of (this, ServerConfig.MaxMinutes) unless AllowOverride is
    /// set and this exceeds the default.</summary>
    [JsonPropertyName("requestedMaxMinutes")]
    public int? RequestedMaxMinutes { get; set; }
}
