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
    private readonly List<IParameterAnimator> _animators = new();
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

    public void Register(IParameterAnimator animator)
    {
        ArgumentNullException.ThrowIfNull(animator);
        _animators.Add(animator);
    }

    /// <summary>Host calls this after each render frame completes. Releases
    /// the gate so the next bus tick can fire.</summary>
    public void NotifyRenderCompleted() => _renderInFlight = false;

    /// <summary>Re-evaluate whether the timer should be running based on any
    /// registered animator being enabled. Idempotent — safe to call after
    /// toggling animators on/off.</summary>
    public void Refresh()
    {
        bool anyEnabled = false;
        foreach (var a in _animators)
        {
            if (a.IsEnabled) { anyEnabled = true; break; }
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
        foreach (var a in _animators)
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
