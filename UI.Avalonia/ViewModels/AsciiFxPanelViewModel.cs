// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/AsciiFxPanelViewModel.cs
//
// View-model for the full ASCII FX panel (#229) — a reactive property per
// effect toggle plus its primary tunable(s), grouped by family in the view.
// Snapshot() materialises an Engine-consumable AsciiFxSettings; LoadFrom() pulls
// a preset back into the controls. Any edit raises Changed so the shell can
// repaint the live ASCII view and re-evaluate the animation timer.

using System;
using System.Reactive;

using ReactiveUI;

using FracturingFog.Imaging;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Full per-effect control surface for the ASCII FX chain.</summary>
public sealed class AsciiFxPanelViewModel : ReactiveObject
{
    /// <summary>Raised whenever any effect value changes (toggle or slider).</summary>
    public event EventHandler? Changed;

    public AsciiFxPanelViewModel()
    {
        ClearCommand = ReactiveCommand.Create(Clear);
    }

    /// <summary>Turn every effect off (the "All off" button).</summary>
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    private bool _suppress; // set while LoadFrom bulk-applies, to fire Changed once

    private T Set<T>(ref T field, T value)
    {
        this.RaiseAndSetIfChanged(ref field, value);
        if (!_suppress) Changed?.Invoke(this, EventArgs.Empty);
        return value;
    }

    // ── Glyph-space ───────────────────────────────────────────────────────
    private bool _breathe; public bool Breathe { get => _breathe; set => Set(ref _breathe, value); }
    private bool _rampScroll; public bool RampScroll { get => _rampScroll; set => Set(ref _rampScroll, value); }
    private double _rampScrollSpeed = 4; public double RampScrollSpeed { get => _rampScrollSpeed; set => Set(ref _rampScrollSpeed, value); }
    private bool _grain; public bool Grain { get => _grain; set => Set(ref _grain, value); }
    private double _grainAmount = 0.4; public double GrainAmount { get => _grainAmount; set => Set(ref _grainAmount, value); }
    private bool _charsetSwap; public bool CharsetSwap { get => _charsetSwap; set => Set(ref _charsetSwap, value); }
    private string _swapRamp = "░▒▓█"; public string SwapRamp { get => _swapRamp; set => Set(ref _swapRamp, value ?? " "); }

    // ── Colour-space ──────────────────────────────────────────────────────
    private bool _hueCycle; public bool HueCycle { get => _hueCycle; set => Set(ref _hueCycle, value); }
    private double _hueSpeed = 40; public double HueSpeed { get => _hueSpeed; set => Set(ref _hueSpeed, value); }
    private bool _saturate; public bool Saturate { get => _saturate; set => Set(ref _saturate, value); }
    private double _saturateMid = 1.0; public double SaturateMid { get => _saturateMid; set => Set(ref _saturateMid, value); }
    private bool _monochrome; public bool Monochrome { get => _monochrome; set => Set(ref _monochrome, value); }
    private bool _invert; public bool Invert { get => _invert; set => Set(ref _invert, value); }
    private bool _solarize; public bool Solarize { get => _solarize; set => Set(ref _solarize, value); }
    private bool _quantize; public bool Quantize { get => _quantize; set => Set(ref _quantize, value); }
    private double _quantizeLevels = 4; public double QuantizeLevels { get => _quantizeLevels; set => Set(ref _quantizeLevels, value); }
    private bool _quantizeTerminal16; public bool QuantizeTerminal16 { get => _quantizeTerminal16; set => Set(ref _quantizeTerminal16, value); }
    private bool _dither; public bool Dither { get => _dither; set => Set(ref _dither, value); }
    private bool _duotone; public bool Duotone { get => _duotone; set => Set(ref _duotone, value); }
    private bool _plasma; public bool Plasma { get => _plasma; set => Set(ref _plasma, value); }
    private double _plasmaStrength = 0.55; public double PlasmaStrength { get => _plasmaStrength; set => Set(ref _plasmaStrength, value); }

    // ── Spatial ───────────────────────────────────────────────────────────
    private bool _chromatic; public bool ChromaticAberration { get => _chromatic; set => Set(ref _chromatic, value); }
    private bool _wave; public bool Wave { get => _wave; set => Set(ref _wave, value); }
    private double _waveAmplitude = 2; public double WaveAmplitude { get => _waveAmplitude; set => Set(ref _waveAmplitude, value); }
    private bool _drift; public bool Drift { get => _drift; set => Set(ref _drift, value); }
    private bool _twist; public bool Twist { get => _twist; set => Set(ref _twist, value); }
    private double _twistStrength = 1.5; public double TwistStrength { get => _twistStrength; set => Set(ref _twistStrength, value); }
    private bool _glitch; public bool Glitch { get => _glitch; set => Set(ref _glitch, value); }
    private double _glitchIntensity = 0.3; public double GlitchIntensity { get => _glitchIntensity; set => Set(ref _glitchIntensity, value); }
    private bool _bloom; public bool Bloom { get => _bloom; set => Set(ref _bloom, value); }
    private double _bloomStrength = 0.6; public double BloomStrength { get => _bloomStrength; set => Set(ref _bloomStrength, value); }
    private bool _edge; public bool Edge { get => _edge; set => Set(ref _edge, value); }

    // ── Structural ────────────────────────────────────────────────────────
    private bool _matrixRain; public bool MatrixRain { get => _matrixRain; set => Set(ref _matrixRain, value); }
    private double _matrixRainDensity = 0.85; public double MatrixRainDensity { get => _matrixRainDensity; set => Set(ref _matrixRainDensity, value); }
    private bool _particles; public bool Particles { get => _particles; set => Set(ref _particles, value); }
    private double _particleCount = 60; public double ParticleCount { get => _particleCount; set => Set(ref _particleCount, value); }
    private bool _vignette; public bool Vignette { get => _vignette; set => Set(ref _vignette, value); }
    private double _vignetteStrength = 0.7; public double VignetteStrength { get => _vignetteStrength; set => Set(ref _vignetteStrength, value); }
    private bool _crtFull; public bool CrtFull { get => _crtFull; set => Set(ref _crtFull, value); }
    private bool _crt; public bool Crt { get => _crt; set => Set(ref _crt, value); }

    // ── Transitions ───────────────────────────────────────────────────────
    private bool _typewriter; public bool Typewriter { get => _typewriter; set => Set(ref _typewriter, value); }
    private bool _dissolve; public bool Dissolve { get => _dissolve; set => Set(ref _dissolve, value); }
    private bool _trails; public bool Trails { get => _trails; set => Set(ref _trails, value); }
    private double _trailDecay = 0.85; public double TrailDecay { get => _trailDecay; set => Set(ref _trailDecay, value); }
    private double _transitionSeconds = 2; public double TransitionSeconds { get => _transitionSeconds; set => Set(ref _transitionSeconds, value); }

    /// <summary>Materialise the current panel state as an Engine FX settings.
    /// <paramref name="timeSeconds"/> stamps the animation clock.</summary>
    public AsciiFxSettings Snapshot(double timeSeconds = 0.0) => new()
    {
        TimeSeconds = timeSeconds,
        Breathe = Breathe,
        RampScroll = RampScroll, RampScrollSpeed = RampScrollSpeed,
        Grain = Grain, GrainAmount = GrainAmount,
        CharsetSwap = CharsetSwap, SwapRamp = SwapRamp,
        HueCycle = HueCycle, HueCycleDegPerSec = HueSpeed,
        Saturate = Saturate, SaturateMid = SaturateMid,
        Monochrome = Monochrome, Invert = Invert, Solarize = Solarize,
        Quantize = Quantize, QuantizeLevels = (int)Math.Round(QuantizeLevels), QuantizeTerminal16 = QuantizeTerminal16,
        Dither = Dither, Duotone = Duotone,
        Plasma = Plasma, PlasmaStrength = PlasmaStrength,
        ChromaticAberration = ChromaticAberration,
        Wave = Wave, WaveAmplitude = WaveAmplitude,
        Drift = Drift,
        Twist = Twist, TwistStrength = TwistStrength,
        Glitch = Glitch, GlitchIntensity = GlitchIntensity,
        Bloom = Bloom, BloomStrength = BloomStrength,
        Edge = Edge,
        MatrixRain = MatrixRain, MatrixRainDensity = MatrixRainDensity,
        Particles = Particles, ParticleCount = (int)Math.Round(ParticleCount),
        Vignette = Vignette, VignetteStrength = VignetteStrength,
        CrtFull = CrtFull, Crt = Crt,
        Typewriter = Typewriter, Dissolve = Dissolve,
        Trails = Trails, TrailDecay = TrailDecay,
        TransitionSeconds = TransitionSeconds,
    };

    /// <summary>Overwrite the panel from an FX settings (e.g. a chosen preset).
    /// Raises <see cref="Changed"/> once for the whole bulk apply.</summary>
    public void LoadFrom(AsciiFxSettings fx)
    {
        if (fx is null) return;
        _suppress = true;
        try
        {
            Breathe = fx.Breathe;
            RampScroll = fx.RampScroll; RampScrollSpeed = fx.RampScrollSpeed;
            Grain = fx.Grain; GrainAmount = fx.GrainAmount;
            CharsetSwap = fx.CharsetSwap; SwapRamp = fx.SwapRamp;
            HueCycle = fx.HueCycle; HueSpeed = fx.HueCycleDegPerSec;
            Saturate = fx.Saturate; SaturateMid = fx.SaturateMid;
            Monochrome = fx.Monochrome; Invert = fx.Invert; Solarize = fx.Solarize;
            Quantize = fx.Quantize; QuantizeLevels = fx.QuantizeLevels; QuantizeTerminal16 = fx.QuantizeTerminal16;
            Dither = fx.Dither; Duotone = fx.Duotone;
            Plasma = fx.Plasma; PlasmaStrength = fx.PlasmaStrength;
            ChromaticAberration = fx.ChromaticAberration;
            Wave = fx.Wave; WaveAmplitude = fx.WaveAmplitude;
            Drift = fx.Drift;
            Twist = fx.Twist; TwistStrength = fx.TwistStrength;
            Glitch = fx.Glitch; GlitchIntensity = fx.GlitchIntensity;
            Bloom = fx.Bloom; BloomStrength = fx.BloomStrength;
            Edge = fx.Edge;
            MatrixRain = fx.MatrixRain; MatrixRainDensity = fx.MatrixRainDensity;
            Particles = fx.Particles; ParticleCount = fx.ParticleCount;
            Vignette = fx.Vignette; VignetteStrength = fx.VignetteStrength;
            CrtFull = fx.CrtFull; Crt = fx.Crt;
            Typewriter = fx.Typewriter; Dissolve = fx.Dissolve;
            Trails = fx.Trails; TrailDecay = fx.TrailDecay;
            TransitionSeconds = fx.TransitionSeconds;
        }
        finally { _suppress = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turn every effect off.</summary>
    public void Clear() => LoadFrom(new AsciiFxSettings());
}
