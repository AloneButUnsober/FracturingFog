// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AutostereoSequence.cs
//
// #1013 (S6 of the autostereogram epic #1014) — temporal coherence for animated
// autostereograms (video, slideshow, batch video, and live interaction).
//
// A stateless autostereogram re-derives everything from each frame, so in a
// sequence:
//   * the FractalTexture tile and ThemeDots palette are re-cut from every new
//     frame — the whole pattern changes frame to frame (heavy flicker);
//   * per-frame depth noise shimmers the surface.
// While a real sequence runs (temporal: a video, slideshow or batch video) it
// holds the pattern source (tile / palette) from the first frame and blends the
// depth over time with StereoAutoTemporal (an exponential moving average, before
// the depth levels / cross-eyed steps). It starts over when the frame size or a
// setting that shapes the pattern changes. Outside a sequence (live stills,
// interaction) every frame stands alone, so a settled view always shows its own
// fractal texture rather than one held from before a pan.

using System;

namespace FracturingFog.Rendering.Lighting;

public sealed class AutostereoSequence
{
    private AutostereoTexture? _tile;
    private uint[]? _palette;
    private float[]? _depth;       // blended continuous depth (before levels / cross-eyed)
    private Key _key;
    private int _frames;

    /// <summary>Frames rendered since the last reset (for tests / diagnostics).</summary>
    public int FramesSinceReset => _frames;

    private readonly record struct Key(int W, int H, AutostereoPattern Pattern, int EyeSepPx, double Mu);

    /// <summary>Forget the held pattern and depth history.</summary>
    public void Reset()
    {
        _tile = null; _palette = null; _depth = null; _frames = 0;
    }

    /// <summary>The next frame: the autostereogram of <paramref name="mono"/> +
    /// <paramref name="rayDistance"/>. When <paramref name="temporal"/> (a video /
    /// slideshow is running) the pattern source is held from the sequence's first
    /// frame and the depth blended with the previous frames by
    /// <see cref="LightingFxData.StereoAutoTemporal"/>; otherwise the frame stands
    /// alone (and the sequence starts over).</summary>
    public uint[] Frame(uint[] mono, float[] rayDistance, int w, int h, in LightingFxData fx, bool temporal)
    {
        var key = new Key(w, h, fx.StereoAutoPattern, Autostereogram.EyeSeparationPx(w, in fx), Autostereogram.DepthOfField(in fx));
        if (key != _key || !temporal) { Reset(); _key = key; }

        var (options, depthOpts) = Autostereogram.OptionsFromLighting(mono, rayDistance, w, h, in fx, _tile, _palette);
        _tile ??= options.Texture;
        _palette ??= options.DotColors;

        var z = Autostereogram.PrepareDepthContinuous(rayDistance, w, h, depthOpts);
        double a = Math.Clamp(fx.StereoAutoTemporal, 0.0, 0.95);
        if (a > 0.0 && _depth != null && _depth.Length == z.Length)
        {
            float keep = (float)a, take = 1f - (float)a;
            for (int i = 0; i < z.Length; i++) z[i] = keep * _depth[i] + take * z[i];
        }
        _depth = (float[])z.Clone();
        _frames++;

        Autostereogram.FinishDepth(z, depthOpts);
        return Autostereogram.Render(z, w, h, options);
    }
}
