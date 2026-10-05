// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Height.cs (#1123, epic #1114 S9)
//
// Relief split (Docs/Technical/DualOrbit-Coloring-RnD.md §3.C): Relief-3D, SSAO
// and every other height consumer read SmoothBuffer, and by default that is the
// same scalar the theme colours. With DualOrbitSplitHeight the height comes from
// a different channel, DualOrbitHeightField — e.g. height = secant Lyapunov,
// colour = ExternalAngleDelta or the per-orbit layer composite.
//
// The height field is computed by a private height-only twin of this calculator
// (Field mode, the height field selected, no colour pass). It keeps its own
// #981 orbit cache, so the colour orbits never re-iterate for a height change
// (the height params are colour-only for the outer cache) and a recolour reuses
// both caches. The colour scalar itself lives in _scalar; split off,
// SmoothBuffer is _scalar exactly as before (byte-identical).

using System.Threading;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    private DualOrbitEscapeCalculator? _heightCalc;
    private float[]? _heightBuf;
    // The twin: iterate + publish SmoothBuffer only.
    private bool _heightOnly;

    /// <summary>#1123 — true when the last Calculate published a split height
    /// (SmoothBuffer = DualOrbitHeightField, not the colour scalar).</summary>
    public bool HeightIsSplit => _heightBuf != null;

    /// <summary>#1123 — the scalar the theme coloured on the last Calculate (equal
    /// to SmoothBuffer unless the height is split).</summary>
    public float[] ColorScalarBuffer => _scalar;

    private void UpdateHeight(CancellationToken ct)
    {
        var fp = FractalParameters;
        if (_heightOnly || !fp.DualOrbitSplitHeight) { _heightBuf = null; return; }

        var hp = fp.Clone();
        hp.DualOrbitSplitHeight = false;
        hp.DualOrbitColorMode = DualOrbitColorMode.Field;
        hp.DualOrbitField = fp.DualOrbitHeightField;
        var h = _heightCalc ??= new DualOrbitEscapeCalculator(Width, Height) { _heightOnly = true };
        if (h.Width != Width || h.Height != Height) h.Resize(Width, Height);
        h.CenterX = CenterX; h.CenterY = CenterY; h.Zoom = Zoom;
        h.MaxIterations = MaxIterations; h.Quality = Quality;
        h.ColorMap = ColorMap;   // an orbit-theme height field samples with it
        h.FractalParameters = hp;
        h.Calculate(ct);
        _heightBuf = ct.IsCancellationRequested ? null : h.SmoothBuffer;
    }
}
