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
    }
}
