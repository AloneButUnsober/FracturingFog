// AnimationSettings.cs
//
// App-global animation preferences (Animation Roadmap Phase 6). Lives in the
// shared FracturingFog.Abstractions assembly, namespace FracturingFog.Models,
// alongside SlideshowSettings so both shells can bind to it. Persisted via
// AnimationSettingsStore.

namespace FracturingFog.Models
{
    /// <summary>User-tunable, app-global animation settings. Distinct from a
    /// per-animation asset — these bound playback across every animation.</summary>
    public sealed class AnimationSettings
    {
        /// <summary>Manual override for the animated-param ceiling. <c>0</c>
        /// (default) = derive automatically from hardware + whether the leg
        /// animates a 3D-raymarched param
        /// (<c>AnimatedParamCeilingPolicy.DefaultCeiling</c>). A positive
        /// value pins the ceiling regardless of hardware; the bus drops the
        /// most expensive tracks beyond it.</summary>
        public int AnimatedParamCeilingOverride { get; set; }
    }
}
