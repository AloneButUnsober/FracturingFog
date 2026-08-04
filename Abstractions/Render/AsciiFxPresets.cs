// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/AsciiFxPresets.cs
//
// Curated named looks for the ASCII FX chain (#229). Each preset configures an
// AsciiFxSettings with a combination of effects that reads well together, so the
// UI can offer a one-click picker instead of 25 individual toggles. Lives in
// Abstractions next to AsciiFxSettings so the shell and the animation recorder
// share one catalogue.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging
{
    /// <summary>A named ASCII-FX look and the mutation that produces it.</summary>
    public sealed class AsciiFxPreset
    {
        public string Name { get; }
        /// <summary>Configure a fresh <see cref="AsciiFxSettings"/> for this look.
        /// Does not touch <see cref="AsciiFxSettings.TimeSeconds"/>.</summary>
        public Action<AsciiFxSettings> Apply { get; }
        public AsciiFxPreset(string name, Action<AsciiFxSettings> apply)
        { Name = name; Apply = apply; }
    }

    /// <summary>Catalogue of one-click ASCII FX looks.</summary>
    public static class AsciiFxPresets
    {
        /// <summary>The "no preset" sentinel name.</summary>
        public const string NoneName = "None";

        /// <summary>All presets (excluding <see cref="NoneName"/>), in menu order.</summary>
        public static IReadOnlyList<AsciiFxPreset> All { get; } = new[]
        {
            new AsciiFxPreset("Matrix", fx =>
            {
                fx.MatrixRain = true; fx.MatrixRainDensity = 0.9; fx.MatrixRainSpeed = 16;
            }),
            new AsciiFxPreset("CRT Monitor", fx =>
            {
                fx.CrtFull = true; fx.CrtBarrel = 0.14;
            }),
            new AsciiFxPreset("Fire", fx =>
            {
                fx.Plasma = true; fx.PlasmaStrength = 0.7; fx.PlasmaSpeed = 1.4;
            }),
            new AsciiFxPreset("Blueprint", fx =>
            {
                fx.Edge = true; fx.EdgeThreshold = 0.2;
                fx.Duotone = true;
                fx.DuotoneLoR = 8; fx.DuotoneLoG = 20; fx.DuotoneLoB = 70;
                fx.DuotoneHiR = 120; fx.DuotoneHiG = 180; fx.DuotoneHiB = 255;
            }),
            new AsciiFxPreset("Film Noir", fx =>
            {
                fx.Monochrome = true; fx.MonochromeR = 235; fx.MonochromeG = 235; fx.MonochromeB = 235;
                fx.Grain = true; fx.GrainAmount = 0.35;
                fx.Vignette = true; fx.VignetteStrength = 0.75;
            }),
            new AsciiFxPreset("Amber Terminal", fx =>
            {
                fx.Monochrome = true; fx.MonochromeR = 255; fx.MonochromeG = 176; fx.MonochromeB = 0;
                fx.Crt = true;
            }),
            new AsciiFxPreset("Rainbow", fx =>
            {
                fx.HueCycle = true; fx.HueCycleDegPerSec = 60;
                fx.Saturate = true; fx.SaturateMid = 1.4;
            }),
            new AsciiFxPreset("Shimmer", fx =>
            {
                fx.RampScroll = true; fx.RampScrollSpeed = 3;
                fx.Breathe = true;
            }),
            new AsciiFxPreset("Ghost Trails", fx =>
            {
                fx.Trails = true; fx.TrailDecay = 0.88;
                fx.HueCycle = true; fx.HueCycleDegPerSec = 30;
            }),
            new AsciiFxPreset("Snow", fx =>
            {
                // Additive white flecks over the user's chosen colour theme — no
                // Duotone recolour (that discarded the palette and looked dark).
                fx.Particles = true; fx.ParticleCount = 90; fx.ParticleGlyph = '*'; fx.ParticleSpeed = 5;
            }),
            new AsciiFxPreset("Glitch", fx =>
            {
                fx.Glitch = true; fx.GlitchIntensity = 0.35;
                fx.ChromaticAberration = true; fx.ChromaticShift = 1;
            }),
            new AsciiFxPreset("Swirl", fx =>
            {
                fx.Twist = true; fx.TwistStrength = 1.4;
                fx.Wave = true; fx.WaveAmplitude = 1.5;
            }),
            new AsciiFxPreset("Retro Poster", fx =>
            {
                fx.Quantize = true; fx.QuantizeLevels = 4;
                fx.Dither = true; fx.DitherLevels = 4;
                fx.Vignette = true; fx.VignetteStrength = 0.5;
            }),
            new AsciiFxPreset("Neon Bloom", fx =>
            {
                fx.Bloom = true; fx.BloomStrength = 0.8; fx.BloomThreshold = 0.45;
                fx.Saturate = true; fx.SaturateMid = 1.5;
                fx.HueCycle = true; fx.HueCycleDegPerSec = 24;
            }),
            new AsciiFxPreset("Typewriter Intro", fx =>
            {
                fx.Typewriter = true; fx.TransitionSeconds = 3;
            }),
            new AsciiFxPreset("Dissolve Intro", fx =>
            {
                fx.Dissolve = true; fx.TransitionSeconds = 3;
            }),
        };

        /// <summary>Menu names: <see cref="NoneName"/> then every preset.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>(All.Count + 1) { NoneName };
                foreach (var p in All) names.Add(p.Name);
                return names;
            }
        }

        /// <summary>Apply the named preset onto <paramref name="fx"/> (no-op for
        /// null / unknown / <see cref="NoneName"/>).</summary>
        public static void ApplyByName(string? name, AsciiFxSettings fx)
        {
            if (fx is null || string.IsNullOrEmpty(name) || name == NoneName) return;
            foreach (var p in All)
                if (p.Name == name) { p.Apply(fx); return; }
        }
    }
}
