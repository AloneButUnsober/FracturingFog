using System;
using System.Collections.Generic;
using global::Avalonia.Threading;
using FracturingFog.Abstractions.Animation;

namespace FracturingFog.UI.Avalonia.ViewModels.Animation;

/// <summary>
/// Drives any number of <see cref="IParameterAnimator"/> instances on a
/// single 50 ms <see cref="DispatcherTimer"/> at <see cref="DispatcherPriority.Background"/>.
/// Ticks are gated on render-completion: while a render is in flight, the
/// bus skips its tick entirely so motion tracks render cadence rather than
/// the wall clock. Once a tick fires, every enabled animator is integrated
/// against the same <c>dt</c>, then a single render-trigger callback is
/// invoked.
/// <para>
/// Extracted from the original Julia-only animation loop in
/// <c>FractalParamsViewModel.cs</c> so further animated parameters can be
/// added without each one racing the renderer independently. Behaviour is
/// preserved bit-for-bit when only the Julia animator is registered.
/// </para>
/// </summary>
public sealed class ParameterAnimationBus
{
    private readonly DispatcherTimer _timer;
    // Animators split by lifecycle. Permanent = long-lived (Julia animator
    // owned by the FractalParams dialog VM, lifetime tied to that VM).
    // Dynamic = region-attached animations swapped on every JumpToRegion;
    // ClearDynamic wipes them before each new region's set is installed
    // without touching the permanent set.
    private readonly List<IParameterAnimator> _permanent = new();
    private readonly List<IParameterAnimator> _dynamic = new();
    private readonly Action _fire;
    private DateTime _lastTick;
    private bool _renderInFlight;

    public ParameterAnimationBus(Action fire)
    {
        _fire = fire ?? throw new ArgumentNullException(nameof(fire));
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            OnTick);
    }

    /// <summary>Add a long-lived animator. Caller owns its lifecycle and
    /// must call <see cref="UnregisterPermanent"/> when done.</summary>
    public void Register(IParameterAnimator animator)
    {
        ArgumentNullException.ThrowIfNull(animator);
        _permanent.Add(animator);
    }

    /// <summary>Remove a previously-registered permanent animator. No-op
    /// if not present.</summary>
    public void UnregisterPermanent(IParameterAnimator animator)
    {
        if (animator == null) return;
        _permanent.Remove(animator);
    }

    /// <summary>Add a dynamic (region-scoped) animator. Wiped by
    /// <see cref="ClearDynamic"/> on each region jump.</summary>
    public void RegisterDynamic(IParameterAnimator animator)
    {
        ArgumentNullException.ThrowIfNull(animator);
        _dynamic.Add(animator);
    }

    /// <summary>Drop every dynamic animator. Call before installing a new
    /// region's animation set so the old set doesn't keep ticking against
    /// stale params.</summary>
    public void ClearDynamic() => _dynamic.Clear();

    /// <summary>Host calls this after each render frame completes. Releases
    /// the gate so the next bus tick can fire.</summary>
    public void NotifyRenderCompleted() => _renderInFlight = false;

    /// <summary>Re-evaluate whether the timer should be running based on any
    /// registered animator being enabled. Idempotent — safe to call after
    /// toggling animators on/off.</summary>
    public void Refresh()
    {
        bool anyEnabled = false;
        foreach (var a in _permanent)
        {
            if (a.IsEnabled) { anyEnabled = true; break; }
        }
        if (!anyEnabled)
        {
            foreach (var a in _dynamic)
            {
                if (a.IsEnabled) { anyEnabled = true; break; }
            }
        }

        if (anyEnabled && !_timer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _renderInFlight = false;
            _timer.Start();
        }
        else if (!anyEnabled && _timer.IsEnabled)
        {
            _timer.Stop();
            _renderInFlight = false;
        }
    }

    /// <summary>Stop the bus unconditionally. Animator IsEnabled state is
    /// not mutated — caller is responsible for that.</summary>
    public void Stop()
    {
        _timer.Stop();
        _renderInFlight = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_renderInFlight) return;

        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0) return;
        if (dt > 0.1) dt = 0.1;

        bool anyTicked = false;
        foreach (var a in _permanent)
        {
            if (!a.IsEnabled) continue;
            a.Tick(dt);
            anyTicked = true;
        }
        foreach (var a in _dynamic)
        {
            if (!a.IsEnabled) continue;
            a.Tick(dt);
            anyTicked = true;
        }

        if (!anyTicked) return;

        _renderInFlight = true;
        _fire();
    }
}
