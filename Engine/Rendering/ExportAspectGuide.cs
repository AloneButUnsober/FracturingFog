// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Rendering;

/// <summary>#511 (C) — geometry for the export-aspect frame guide: the largest
/// rectangle of a target aspect (export width / height) that fits, centred, inside
/// the window. The drawing lives in the (internal) overlay compositor; this pure
/// helper is factored out so the fit math is testable on its own.</summary>
public static class ExportAspectGuide
{
    /// <summary>Largest centred rect of <paramref name="aspect"/> (w/h) fitting a
    /// <paramref name="winW"/>×<paramref name="winH"/> window. Returns pixel
    /// (X, Y, W, H). A wider-than-window aspect letterboxes (bars top/bottom); a
    /// taller one pillarboxes (bars left/right); an equal aspect fills exactly.
    /// Degenerate inputs return the full window.</summary>
    public static (double X, double Y, double W, double H) Fit(int winW, int winH, double aspect)
    {
        if (winW <= 0 || winH <= 0 || aspect <= 0.0)
            return (0, 0, System.Math.Max(0, winW), System.Math.Max(0, winH));

        double winAspect = (double)winW / winH;
        double gw, gh;
        if (aspect >= winAspect) { gw = winW; gh = winW / aspect; }   // export wider → fit width
        else                     { gh = winH; gw = winH * aspect; }   // export taller → fit height
        double gx = (winW - gw) * 0.5, gy = (winH - gh) * 0.5;
        return (gx, gy, gw, gh);
    }
}
