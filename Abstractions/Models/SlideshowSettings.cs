// SlideshowSettings.cs
//
// Plain DTO consumed by both the legacy WinForms SlideshowSettingsDialog
// and the new Avalonia SlideshowSettingsView. Lives in the shared
// FracturingFog.Abstractions assembly so the Avalonia shell (which cannot
// reference the WinExe) can bind to it without duplication. The namespace
// stays FracturingFog.Models so existing WinForms consumers compile
// untouched after the file moved out of the WinExe project.

namespace FracturingFog.Models
{
    /// <summary>User-tunable slideshow timing + master toggles. Persisted via SlideshowSettingsStore.</summary>
    public sealed class SlideshowSettings
    {
        public bool UseExtremeRegions { get; set; }

        /// <summary>Total visible time for one region across all themes (region-focus mode).
        /// Divided evenly across themes-per-region to derive per-theme dwell.</summary>
        public int TotalDisplayMsPerRegion { get; set; } = 36_000;

        /// <summary>Cross-fade duration between color themes within a region.</summary>
        public int ColorThemeFadeMs { get; set; } = 2_000;

        /// <summary>Cross-fade duration between regions.</summary>
        public int RegionFadeMs { get; set; } = 2_000;

        /// <summary>Number of cross-fade steps. Step duration = fade ms / FadeSteps.</summary>
        public int FadeSteps { get; set; } = 22;

        /// <summary>True to honour each region's <c>EmbeddedWatermark</c>
        /// during the slideshow. When false (default) the slideshow uses
        /// whatever active watermark MainViewModel has resolved (the master
        /// "Use Custom Watermark" toggle still applies).</summary>
        public bool UseRegionWatermark { get; set; }

        /// <summary>When true, the slideshow engine captures every cross-fade
        /// step + dwell frame to a PNG sequence in a temp folder and offers
        /// Convert / Save / Cancel on Stop. ffmpeg post-encode reuses the same
        /// pipeline as Video Zoom's lossless flow.</summary>
        public bool RecordSlideshow { get; set; }

        /// <summary>Encode preset applied when the user picks Convert after a
        /// recorded slideshow: "HighQualityH264Mp4" | "LosslessH264Mp4" |
        /// "Ffv1Mkv". Default = HighQualityH264Mp4 (CRF 18, yuv420p).</summary>
        public string RecordEncodePreset { get; set; } = "HighQualityH264Mp4";
    }
}
