using System;
using FracturingFog.Abstractions.Animation;

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

        foreach (var animator in data.ToAnimators(target))
        {
            _bus.RegisterDynamic(animator);
        }
        _bus.Refresh();
    }
}
