// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog.Abstractions.Animation;

namespace FracturingFog.UI.Avalonia.ViewModels.Animation;

/// <summary>
/// Sweeps the Julia c constant in a circular orbit around the origin of the
/// complex plane at the current |c| radius. Forward = CCW (positive angular
/// velocity), Reverse = CW. Speed is radians per second so 6.28 ≈ one full
/// orbit per second; default 0.2 gives a calm sweep visible in real time.
/// <para>
/// Lifted out of the inline <c>OnJuliaTick</c> body in
/// <c>FractalParamsViewModel.cs</c>. Holds no state of its own — reads
/// current JuliaR/JuliaI from the ViewModel and writes the new pair back
/// through the ViewModel's silent setter. The render-pacing gate and the
/// dispatcher tick are owned by <see cref="ParameterAnimationBus"/>.
/// </para>
/// </summary>
internal sealed class JuliaCAnimator : IParameterAnimator
{
    private readonly FractalParamsViewModel _vm;

    public JuliaCAnimator(FractalParamsViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
    }

    public string Name => "Julia C orbit";
    public bool IsEnabled { get; set; }

    public void Tick(double dt)
    {
        double r = Math.Sqrt(_vm.JuliaR * _vm.JuliaR + _vm.JuliaI * _vm.JuliaI);
        if (r < 1e-6) r = 0.5;

        double theta = Math.Atan2(_vm.JuliaI, _vm.JuliaR);
        double dir = _vm.JuliaAnimateForward ? 1.0 : -1.0;
        theta += dir * _vm.JuliaAnimateSpeed * dt;

        double nr = r * Math.Cos(theta);
        double ni = r * Math.Sin(theta);

        _vm.SetJuliaSilent(nr, ni);
    }
}
