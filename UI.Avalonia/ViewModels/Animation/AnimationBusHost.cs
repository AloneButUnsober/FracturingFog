using System;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.ViewModels.Animation;

/// <summary>
/// App-scoped owner of a long-lived <see cref="ParameterAnimationBus"/> used
/// for region-attached animations. Lives outside the FractalParams dialog
/// so animations play even when the dialog is closed. Initialised once by
/// the shell at startup with the render-trigger callback; subsequent
/// <see cref="Initialize"/> calls are no-ops.
/// <para>
/// Region recall calls <see cref="LoadRegionAnimation"/> to swap the bus's
/// dynamic animator set. The dialog-owned bus on
/// <c>FractalParamsViewModel</c> continues to own the Julia c orbit
/// independently — they run as two buses with two timers but share the
/// same render host, so a Julia animation toggle from the dialog and a
/// region animation attached on the active region coexist without
/// coordination. Unifying the two buses is Phase 4 work, when slideshow
/// integration makes the single-bus invariant easy to express.
/// </para>
/// </summary>
public static class AnimationBusHost
{
    private static ParameterAnimationBus? _bus;
    private static readonly object _gate = new();

    /// <summary>The shared region-animation bus, or null if
    /// <see cref="Initialize"/> hasn't been called yet (e.g. headless test
    /// process before shell startup).</summary>
    public static ParameterAnimationBus? Bus => _bus;

    /// <summary>Construct the bus with the supplied render-trigger callback.
    /// Idempotent — subsequent calls return without effect.</summary>
    public static ParameterAnimationBus Initialize(Action fire)
    {
        ArgumentNullException.ThrowIfNull(fire);
        lock (_gate)
        {
            _bus ??= new ParameterAnimationBus(fire);
            return _bus;
        }
    }

    /// <summary>Swap the dynamic animator set to the named animation,
    /// bound to <paramref name="target"/> (typically the active
    /// <c>FractalParameters</c>). Pass a null or empty name to clear
    /// without installing a new set. <paramref name="data"/> may be null
    /// when the caller couldn't resolve the name — the bus simply clears.
    /// No-op if the bus isn't initialised yet.</summary>
    public static void LoadRegionAnimation(AnimationData? data, object target)
    {
        if (_bus == null) return;

        _bus.ClearDynamic();

        if (data == null || target == null)
        {
            _bus.Refresh();
            return;
        }

        bool includesRaymarched3D = false;
        foreach (var animator in data.ToAnimators(target))
        {
            _bus.RegisterDynamic(animator);
            if (animator.Cost == AnimatableParamCost.Moderate) includesRaymarched3D = true;
        }

        _bus.Ceiling = ResolveCeiling(includesRaymarched3D);
        _bus.Refresh();
    }

    /// <summary>Scene Engine Roadmap Phase S6 — swap the dynamic animator set to
    /// one scene shot: its param-animation (if any) plus its keyframed orbit
    /// camera (if the shot carries a <see cref="SceneShot.Camera"/> and its
    /// fractal type supports one). This is where the S3
    /// <see cref="CameraTrackAnimator"/> — deferred at S3 with "bus registration
    /// is S6" — finally registers on the bus, so scene-camera motion inherits the
    /// same render-completion gate + animated-param ceiling as every other track.
    /// <paramref name="target"/> is the live <see cref="FractalParameters"/> the
    /// shot drives; <paramref name="shotAnimation"/> is the resolved
    /// param-animation asset (null = none). No-op if the bus isn't initialised.</summary>
    public static void LoadSceneShot(SceneShot shot, AnimationData? shotAnimation, FractalParameters target)
    {
        if (_bus == null) return;

        _bus.ClearDynamic();

        if (shot == null || target == null)
        {
            _bus.Refresh();
            return;
        }

        bool includesRaymarched3D = false;

        // Param-animation animators (same path as a region-attached animation).
        if (shotAnimation != null)
        {
            foreach (var animator in shotAnimation.ToAnimators(target))
            {
                _bus.RegisterDynamic(animator);
                if (animator.Cost == AnimatableParamCost.Moderate) includesRaymarched3D = true;
            }
        }

        // Keyframed orbit camera (S3). Only for the 3D-camera types with keys.
        if (shot.Camera != null
            && shot.Camera.Keys.Count > 0
            && CameraParamBinding.Supports(shot.FractalType))
        {
            var camera = new CameraTrackAnimator(shot.Camera, target, shot.FractalType)
            {
                Loop = true, // the shot loops its camera across its own window
            };
            _bus.RegisterDynamic(camera);
            includesRaymarched3D = true; // raymarched 3D — drop first under load
        }

        _bus.Ceiling = ResolveCeiling(includesRaymarched3D);
        _bus.Refresh();
    }

    // Cached so we hit disk once, not on every region jump.
    private static int _ceilingOverride = -1;

    /// <summary>Drop the cached ceiling override so the next region jump
    /// re-reads animation-settings.json. Call after the App Settings dialog
    /// saves a new override, otherwise the stale cached value sticks until
    /// process restart.</summary>
    public static void InvalidateCeilingCache() => _ceilingOverride = -1;

    /// <summary>Resolve the ceiling for the current leg: the user's manual
    /// override if set (&gt; 0), else the hardware-derived default from
    /// <see cref="AnimatedParamCeilingPolicy"/>.</summary>
    private static int ResolveCeiling(bool includesRaymarched3D)
    {
        if (_ceilingOverride < 0)
        {
            try { _ceilingOverride = AnimationSettingsStore.Load().AnimatedParamCeilingOverride; }
            catch { _ceilingOverride = 0; }
        }
        if (_ceilingOverride > 0) return _ceilingOverride;
        return AnimatedParamCeilingPolicy.DefaultCeiling(
            HardwareProfile.Detect(), includesRaymarched3D);
    }
}
