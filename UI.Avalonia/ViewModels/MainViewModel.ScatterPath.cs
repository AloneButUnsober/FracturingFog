// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MainViewModel.ScatterPath.cs
//
// #2000 — "send this pixel's path to Math Plane": on a Hénon–Heiles, three-body or
// magnetised-black-hole view, the J key takes the launch under the pointer (the same
// mapping the family's calculator uses, MathPlaneTrajectory.PlaneAt) and the shell
// hands it to the Math Plane section (ShellViewModel.SendPathToMathPlane). The pointer
// is tracked through the input controller's hover hook, like the math overlay (#1554).

using System.Linq;
using FracturingFog.Input;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MainViewModel
{
    // Last hover: client position and size, and the type it was over.
    private (double X, double Y, double W, double H, FractalType Type)? _scatterHover;

    private void InitScatterPath() => _input.HoverObserver += OnScatterHover;

    /// <summary>Families whose launch under the pointer can be sent to Math Plane (2D, #2000) or
    /// Math Plane 3D (#1996).</summary>
    public static bool IsPathFamily(FractalType t)
        => MathPlaneTrajectory.BackdropOf(t) != MathPlaneBackdrop.None || MathPlane3DScene.PathTypes.Contains(t);

    private void OnScatterHover(PointerInput e)
    {
        if (!IsPathFamily(SelectedFractalType)) { _scatterHover = null; return; }
        _scatterHover = (e.X, e.Y, e.ClientWidth, e.ClientHeight, SelectedFractalType);
    }

    /// <summary>#2000 — the launch (a, b) under the pointer on a scattering view, or false when the
    /// view is not one of the trajectory families or the pointer has not been over it.</summary>
    public bool TryScatterLaunchAtPointer(out FractalType type, out double a, out double b)
    {
        type = SelectedFractalType; a = b = 0;
        if (!IsPathFamily(type)) return false;
        if (_scatterHover is not { } h || h.Type != type || h.W <= 0 || h.H <= 0) return false;
        (a, b) = MathPlaneTrajectory.PlaneAt(h.X, h.Y, h.W, h.H, ViewState.CenterX, ViewState.CenterY, ViewState.Zoom);
        return true;
    }
}
