// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/IWaveColorMap.cs (#1593)
//
// Wave colouring contract for the double-slit renderers (Julibrot Pair path
// interference, Slit Cascade, Talbot Carpet). When the active ColorMap
// implements it, those calculators colour each theme-coloured pixel through
// MapWave with the pixel's wave quantities instead of the 1-D smooth value.
// Same split as IBilliardColorMap (#633): a ColorGen program referencing any
// `wave*` input becomes an InterpretedWaveColorMap, CPU-only (no HLSL), and C#
// export refuses it.
//
// The views that paint their own colour are left alone: Domain (phase wheel),
// white light and Detections. A wave theme on any other fractal is a plain
// theme whose wave inputs read 0.

using System;

namespace FracturingFog.Interefaces
{
    /// <summary>#1593 — which wave inputs a theme reads, so a calculator only
    /// computes those (the Slit Cascade far field and aperture address are the
    /// expensive ones).</summary>
    [Flags]
    public enum WaveInputs
    {
        None = 0,
        /// <summary>waveIntensity / waveLevel / wavePhase.</summary>
        Field = 1,
        /// <summary>Julibrot Pair: waveDeltaG / waveDeltaTheta / waveWhichPath / waveAngularMomentum / wavePaths.</summary>
        Paths = 2,
        /// <summary>Slit Cascade: waveAlpha / waveFAlpha / waveAlphaMin / waveAlphaMax / waveOnSet /
        /// waveFirstChild / waveLastChild / waveChildren.</summary>
        Address = 4,
        /// <summary>Talbot Carpet: waveXi / waveZeta.</summary>
        Coords = 8,
    }

    /// <summary>#1593 — one pixel's wave quantities. Fields a renderer does not have
    /// stay 0 (see Docs/User/ColorGen-UserGuide.md §2.3 for each input).</summary>
    public struct WaveSample
    {
        /// <summary>Intensity as a fraction of the largest possible, [0, 1].</summary>
        public double Intensity;
        /// <summary>What the renderer's Intensity view shows, [0, 1] (log-scaled by the
        /// dynamic range on Slit Cascade / Talbot, linear on the Pair).</summary>
        public double Level;
        /// <summary>Phase of the summed amplitude, turns [0, 1).</summary>
        public double Phase;
        // Julibrot Pair (path interference).
        public double DeltaG, DeltaTheta, WhichPath, AngularMomentum, Paths;
        // Slit Cascade (aperture address).
        public double Alpha, FAlpha, AlphaMin, AlphaMax, OnSet, FirstChild, LastChild, Children;
        // Talbot Carpet (plane coordinates).
        public double Xi, Zeta;
    }

    public interface IWaveColorMap : IColorMap
    {
        /// <summary>The wave inputs this theme reads.</summary>
        WaveInputs Needs { get; }
        int MapWave(in WaveSample s);
    }
}
