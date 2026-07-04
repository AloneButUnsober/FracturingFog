// Server/Cluster/Protocol/JobSubmitDto.cs
// Client → Master, body of job.submit. Wraps a RenderRequestDto with
// distribution-only hints. The render fields themselves stay in
// RenderRequestDto so the single-server protocol can keep using the
// same DTO unchanged.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster.Protocol;

public sealed class JobSubmitDto
{
    /// <summary>The render the client wants done — same DTO used by
    /// render.image / render.video in the single-server protocol.</summary>
    [JsonPropertyName("request")]
    public RenderRequestDto Request { get; set; } = new();

    /// <summary>Operator priority hint, 0..100. Higher is more urgent.
    /// Master uses it for queue ordering only; never as a scheduling
    /// guarantee. Default 50.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;

    /// <summary>If &gt; 0, master tries to size tiles around this many
    /// pixels per side. 0 = let the planner pick from worker hints.</summary>
    [JsonPropertyName("tilePixelsHint")]
    public int TilePixelsHint { get; set; }

    /// <summary>D-4c — populated when <see cref="Request"/>.Mode is
    /// "slideshow". One entry per slide; each is a full image-mode
    /// <see cref="RenderRequestDto"/> the master fans out as one tile.
    /// The parent <see cref="Request"/> acts as a template — slide-level
    /// dims default from it when a slide leaves them at 0, and the
    /// fractal-type allowlist is enforced per slide.</summary>
    [JsonPropertyName("slides")]
    public List<RenderRequestDto>? Slides { get; set; }

    /// <summary>D-4c — default per-slide display duration (ms) written
    /// into the slides-manifest. Clients pick their own per-slide
    /// overrides via <see cref="SlideDisplayMs"/>. 0 = unspecified
    /// (caller's renderer picks a default).</summary>
    [JsonPropertyName("slideshowDefaultDisplayMs")]
    public int SlideshowDefaultDisplayMs { get; set; }

    /// <summary>D-4c — optional per-slide display ms override. When
    /// present, length must match <see cref="Slides"/>; a 0 entry means
    /// "use <see cref="SlideshowDefaultDisplayMs"/>".</summary>
    [JsonPropertyName("slideDisplayMs")]
    public List<int>? SlideDisplayMs { get; set; }
}
