// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Text.Json;
using FracturingFog.Audio;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #268 — per-region persistence of audio->param bindings. The region record now
// carries an optional AudioBindings list; these cover JSON round-trip (the same
// List<FractalRegion> serialization FractalRegionLibrary uses) and back-compat
// for regions saved before the field existed. Pure serialization — no data-root
// touch (see the store-singleton isolation gotcha).
public sealed class RegionAudioBindingPersistenceTests
{
    private const int P = 9;

    // Mirrors FractalRegionLibrary.Save() options (default STJ + indented).
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    [Fact]
    public void Region_AudioBindings_RoundTrip_Json()
    {
        var region = new FractalRegion
        {
            Name = "Pulse Region",
            FractalType = FractalType.Mandelbrot,
            AudioBindings = new List<AudioParamBinding>
            {
                new()
                {
                    ParamName = "EscapeRadius",
                    Enabled = true,
                    Binding = new AudioModulationBinding
                    {
                        Source = AudioSignalKind.Bass,
                        Curve = AudioResponseCurve.Smoothstep,
                        Gain = 1.25,
                        Bias = 0.1,
                        Invert = true,
                        OutMin = 2.0,
                        OutMax = 12.0,
                    },
                },
                new()
                {
                    ParamName = "ColorScale",
                    Enabled = false,
                    Binding = new AudioModulationBinding { Source = AudioSignalKind.Rms },
                },
            },
        };

        string json = JsonSerializer.Serialize(new List<FractalRegion> { region }, Opts);
        var back = JsonSerializer.Deserialize<List<FractalRegion>>(json, Opts);

        Assert.NotNull(back);
        var r = Assert.Single(back!);
        Assert.NotNull(r.AudioBindings);
        Assert.Equal(2, r.AudioBindings!.Count);

        var a = r.AudioBindings[0];
        Assert.Equal("EscapeRadius", a.ParamName);
        Assert.True(a.Enabled);
        Assert.Equal(AudioSignalKind.Bass, a.Binding.Source);
        Assert.Equal(AudioResponseCurve.Smoothstep, a.Binding.Curve);
        Assert.Equal(1.25, a.Binding.Gain, precision: P);
        Assert.Equal(0.1, a.Binding.Bias, precision: P);
        Assert.True(a.Binding.Invert);
        Assert.Equal(2.0, a.Binding.OutMin, precision: P);
        Assert.Equal(12.0, a.Binding.OutMax, precision: P);

        var b = r.AudioBindings[1];
        Assert.Equal("ColorScale", b.ParamName);
        Assert.False(b.Enabled);
        Assert.Equal(AudioSignalKind.Rms, b.Binding.Source);
    }

    [Fact]
    public void Region_Without_AudioBindings_Is_Omitted_And_Loads_Null()
    {
        var region = new FractalRegion { Name = "Plain", FractalType = FractalType.Mandelbrot };
        string json = JsonSerializer.Serialize(new List<FractalRegion> { region }, Opts);

        // Null list is omitted from JSON (WhenWritingNull) — legacy files stay clean.
        Assert.DoesNotContain("AudioBindings", json);

        var back = JsonSerializer.Deserialize<List<FractalRegion>>(json, Opts);
        Assert.Null(Assert.Single(back!).AudioBindings);
    }

    [Fact]
    public void Legacy_Region_Json_Without_Field_Loads_As_No_Drive()
    {
        // A region serialized before the field existed has no AudioBindings key.
        string legacy = "[{\"Name\":\"Legacy\",\"FractalType\":\"Mandelbrot\",\"Zoom\":1.0}]";
        var back = JsonSerializer.Deserialize<List<FractalRegion>>(legacy, Opts);
        Assert.Null(Assert.Single(back!).AudioBindings);
    }
}
