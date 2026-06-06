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

    // Quad-precision lower limbs of the centre. Only the Mandelbrot calculator
    // consumes these; alt calculators read CenterX/CenterY (Hi) only. Required
    // so a deep-zoom saved region (e.g. "Deep Julias" at zoom 2.3e19) survives
    // the wire — without these the server renders at the Hi-word location only
    // and the image lands at a visibly different coordinate than the UI render.
    [JsonPropertyName("centerXLo")] public double CenterXLo { get; set; }
    [JsonPropertyName("centerX2")]  public double CenterX2  { get; set; }
    [JsonPropertyName("centerX3")]  public double CenterX3  { get; set; }
    [JsonPropertyName("centerYLo")] public double CenterYLo { get; set; }
    [JsonPropertyName("centerY2")]  public double CenterY2  { get; set; }
    [JsonPropertyName("centerY3")]  public double CenterY3  { get; set; }

    [JsonPropertyName("themeName")]   public string ThemeName   { get; set; } = "HSV";
    [JsonPropertyName("qualityName")] public string QualityName { get; set; } = "Standard";

    /// <summary>Optional client-provided ColorThemeData JSON. The server first
    /// resolves <see cref="ThemeName"/> against its own theme registry; if the
    /// name is unknown AND this field is present, the engine parses the JSON
    /// through <c>ThemePayloadValidator</c> and uses it as a transient theme
    /// for this render. Refused if it exceeds the byte cap or contains
    /// disallowed fields. Algorithmic themes cannot be transported this way —
    /// only themes round-trippable through <c>ColorThemeData</c>.</summary>
    [JsonPropertyName("themeJson")]
    public string? ThemeJson { get; set; }

    /// <summary>Optional client-provided FractalRegion JSON. The server first
    /// resolves <see cref="RegionName"/> against its own region library; if the
    /// name is unknown AND this field is present, the engine parses the JSON
    /// through <c>RegionPayloadValidator</c> and uses it as a transient region
    /// for this render. Refused if its FractalType is on the allowlist-block
    /// list (UserEquation / Sandbox / UserBulb) or if it carries any
    /// user-authored-code fields (UserBulbSource / UserEquationName /
    /// SandboxName / UserBulbName).</summary>
    [JsonPropertyName("regionJson")]
    public string? RegionJson { get; set; }

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

    // ── Poster mode (image only) ────────────────────────────────────────────
    // When PosterInchesW / PosterInchesH / PosterDpi are all positive the
    // server treats this as a poster-style render: pixel Width/Height are
    // computed as inches × dpi (overriding the Width/Height fields above),
    // the saved PNG carries that DPI in its metadata so print drivers honour
    // the intended physical size, and PosterPortrait drives a 90° clockwise
    // rotation of the final image. Ignored in video mode.

    [JsonPropertyName("posterInchesW")] public double? PosterInchesW { get; set; }
    [JsonPropertyName("posterInchesH")] public double? PosterInchesH { get; set; }
    [JsonPropertyName("posterDpi")]     public int?    PosterDpi     { get; set; }
    [JsonPropertyName("posterPortrait")] public bool   PosterPortrait { get; set; }

    // ── Custom watermark ────────────────────────────────────────────────────
    //
    // When UseClientWatermark is true AND ClientWatermarkJson is a valid
    // WatermarkDef payload AND the server's WatermarkMode permits client
    // overrides (Client mode), the engine composites the client's watermark
    // onto the saved file in place of the default region/theme branding.
    // Refused by WatermarkPayloadValidator if the JSON is malformed or
    // exceeds the byte cap.

    /// <summary>Master flag: the client wants its custom watermark applied to
    /// the response image / video frames. Server still has the final say (see
    /// <c>ServerConfig.WatermarkMode</c>).</summary>
    [JsonPropertyName("useClientWatermark")]
    public bool UseClientWatermark { get; set; }

    /// <summary>JSON-serialised <c>WatermarkDef</c>. Null or empty disables the
    /// client override even when <see cref="UseClientWatermark"/> is true.</summary>
    [JsonPropertyName("clientWatermarkJson")]
    public string? ClientWatermarkJson { get; set; }
}
