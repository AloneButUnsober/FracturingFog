// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #361 / #362 (slices of #64) — CLI Command Builder tests. Asserts the emitted
// `--batch` string is well-formed, pairs flags with values, ROUND-TRIPS through
// the real BatchOptions parser (now shared in Abstractions, #362), and reports
// fidelity gaps for live fx the 2D batch path cannot reproduce.

using System.Globalization;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests
{
    public class BatchCommandBuilderTests
    {
        // Split a command line the way a shell would, honouring double quotes.
        private static string[] Tokenize(string cmd)
        {
            var list = new System.Collections.Generic.List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == ' ' && !inQuote)
                {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list.ToArray();
        }

        [Fact]
        public void Build_LeadsWithExeAndBatch()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.StartsWith("FracturingFog --batch", cmd);
        }

        [Fact]
        public void Build_FullExePathWithSpacesIsQuoted()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ExecutableName = @"C:\Program Files\FracturingFog\FracturingFog.exe",
            });
            Assert.StartsWith("\"C:\\Program Files\\FracturingFog\\FracturingFog.exe\" --batch", cmd);
        }

        [Fact]
        public void Build_OmitsDefaultThemeAndQuality()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "HSV",
                QualityName = "Standard",
            });
            Assert.DoesNotContain("--theme", cmd);
            Assert.DoesNotContain("--quality", cmd);
        }

        [Fact]
        public void Build_EmitsNonDefaultThemeAndQuality()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "Fire",
                QualityName = "Ultra",
            });
            Assert.Contains("--theme Fire", cmd);
            Assert.Contains("--quality Ultra", cmd);
        }

        [Fact]
        public void Build_QuotesThemeNameWithSpaces()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ThemeName = "Chromostereopsis Ember",
            });
            Assert.Contains("--theme \"Chromostereopsis Ember\"", cmd);
        }

        [Fact]
        public void Build_OmitsNeutralPostFx()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.DoesNotContain("--brightness", cmd);
            Assert.DoesNotContain("--contrast", cmd);
            Assert.DoesNotContain("--adaptive", cmd);
        }

        [Fact]
        public void Build_EmitsNonNeutralPostFx()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Brightness = 12,
                Contrast = -8,
                HistogramEq = 40,
            });
            Assert.Contains("--brightness 12", cmd);
            Assert.Contains("--contrast -8", cmd);
            Assert.Contains("--adaptive 40", cmd);
        }

        [Fact]
        public void Build_EmitsFractalSpecificParamsForMatchingType()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Flame,
                Parameters = new FractalParameters
                {
                    FlamePresetName = "Swirl Gasket",
                    FlameIterations = 5_000_000,
                    FlameGamma = 2.4,
                    FlameVibrancy = 0.7,
                },
            });
            Assert.Contains("--flame-preset \"Swirl Gasket\"", cmd);
            Assert.Contains("--flame-iter 5000000", cmd);
            Assert.Contains("--flame-gamma 2.4", cmd);
            Assert.Contains("--flame-vibrancy 0.7", cmd);
        }

        [Fact]
        public void Build_DoesNotEmitParamsForUnrelatedType()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                Parameters = new FractalParameters { FlameGamma = 9.9, BulbPower = 3.0 },
            });
            Assert.DoesNotContain("--flame", cmd);
            Assert.DoesNotContain("--bulb-power", cmd);
        }

        [Fact]
        public void Build_EmitsOutputPlaceholder()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot());
            Assert.Contains("--out", cmd);
            Assert.Contains("<OUTPUT.png>", cmd);
        }

        // The fidelity contract (#362): the emitted command parses back to the
        // same values through the REAL BatchOptions parser. This is possible now
        // that both parser and builder share Batch/BatchFlags + live in
        // Abstractions.
        [Fact]
        public void Build_RoundTripsThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.BurningShip,
                CenterX = -1.7549,
                CenterY = -0.0104,
                Zoom = 12345.5,
                Iterations = 2048,
                ThemeName = "Fire",
                QualityName = "High",
                Width = 3840,
                Height = 2160,
                Brightness = 5,
                Contrast = -3,
                HistogramEq = 25,
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));

            // argv[0] is the exe name; the parser starts at the flag after it.
            Assert.Equal("--batch", argv[1]);

            // The builder emits an <OUTPUT.png> placeholder; substitute a real
            // path so the parser's --out requirement is satisfied.
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            bool ok = BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err);
            Assert.True(ok, err);

            Assert.Equal(FractalType.BurningShip, opts.FractalType);
            Assert.Equal(snap.CenterX, opts.CenterX!.Value, 12);
            Assert.Equal(snap.CenterY, opts.CenterY!.Value, 12);
            Assert.Equal(snap.Zoom, opts.Zoom!.Value, 6);
            Assert.Equal(2048, opts.Iterations);
            Assert.Equal("Fire", opts.ThemeName);
            Assert.Equal("High", opts.QualityName);
            Assert.Equal(3840, opts.Width);
            Assert.Equal(2160, opts.Height);
            Assert.Equal(5, opts.Brightness);
            Assert.Equal(-3, opts.Contrast);
            Assert.Equal(25, opts.Adaptive);
        }

        [Fact]
        public void Build_FlameParams_RoundTripThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Flame,
                CenterX = 0, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters
                {
                    FlamePresetName = "Swirl Gasket",
                    FlameIterations = 5_000_000,
                    FlameGamma = 2.4,
                    FlameVibrancy = 0.7,
                },
            };

            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal("Swirl Gasket", opts.FlamePresetName);
            Assert.Equal(5_000_000, opts.FlameIterations);
            Assert.Equal(2.4, opts.FlameGamma!.Value, 6);
            Assert.Equal(0.7, opts.FlameVibrancy!.Value, 6);
        }

        // ── Fidelity gap detection (#362) ─────────────────────────────────────

        [Fact]
        public void DetectGaps_EmptyWhenNoUnrepresentedFx()
        {
            var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot());
            Assert.Empty(gaps);
        }

        [Fact]
        public void DetectGaps_FlagsStereoOnly()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                StereoActive = true,
            });

            Assert.True(report.HasGaps);
            Assert.Single(report.Gaps);
            Assert.Contains(report.Gaps, g => g.Contains("SBS"));
        }

        // #363 — core relief is emitted, not a blanket gap.
        [Fact]
        public void Relief_CoreEmittedAndNotAGap()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                ReliefEnabled = true,
                ReliefHeight = 2.5,
                ReliefLightAzimuth = 90.0,
                ReliefShadow = 0.8,
            });
            Assert.Contains("--relief", report.Command);
            Assert.Contains("--relief-height 2.5", report.Command);
            Assert.Contains("--relief-light-azimuth 90", report.Command);
            Assert.Contains("--relief-shadow 0.8", report.Command);
            Assert.Empty(report.Gaps);
        }

        [Fact]
        public void Relief_DefaultsOmitKnobsButEmitMaster()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { ReliefEnabled = true });
            Assert.Contains("--relief", cmd);
            Assert.DoesNotContain("--relief-height", cmd);
            Assert.DoesNotContain("--relief-strength", cmd);
        }

        // #363 follow-up — camera + isolate now emitted; no relief gap remains.
        [Fact]
        public void Relief_CameraAndIsolate_NoGap()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                ReliefEnabled = true,
                ReliefRaymarch = true,
                ReliefCameraAzimuth = 30.0,
                ReliefCameraOrtho = true,
                ReliefIsolate = true,
            });
            Assert.Contains("--relief-camera-azimuth 30", report.Command);
            Assert.Contains("--relief-camera-ortho", report.Command);
            Assert.Contains("--relief-isolate", report.Command);
            Assert.Empty(report.Gaps);
        }

        [Fact]
        public void Relief_CameraOmittedWithoutRaymarch()
        {
            // Camera knobs are only meaningful on the raymarch path.
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                ReliefEnabled = true,
                ReliefCameraAzimuth = 30.0,   // emboss path — not emitted
            });
            Assert.DoesNotContain("--relief-camera-azimuth", cmd);
        }

        [Fact]
        public void Relief_CameraAndIsolate_RoundTripThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true,
                ReliefRaymarch = true,
                ReliefAbsolute = true,
                ReliefCameraAzimuth = 30.0,
                ReliefCameraElevation = 60.0,
                ReliefCameraFov = 40.0,
                ReliefCameraZoom = 1.5,
                ReliefCameraOrtho = true,
                ReliefIsolate = true,
                ReliefIsolateByDetail = false,
                ReliefIsolateThreshold = 0.3,
                ReliefIsolateByColor = true,
                ReliefIsolateColors = "#000000,#ffffff",
                ReliefIsolateTolerance = 0.2,
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.True(opts.ReliefAbsolute);
            Assert.Equal(30.0, opts.ReliefCameraAzimuth!.Value, 6);
            Assert.Equal(60.0, opts.ReliefCameraElevation!.Value, 6);
            Assert.Equal(40.0, opts.ReliefCameraFov!.Value, 6);
            Assert.Equal(1.5, opts.ReliefCameraZoom!.Value, 6);
            Assert.True(opts.ReliefCameraOrtho);
            Assert.True(opts.ReliefIsolate);
            Assert.True(opts.ReliefIsolateNoDetail);
            Assert.Equal(0.3, opts.ReliefIsolateThreshold!.Value, 6);
            Assert.True(opts.ReliefIsolateByColor);
            Assert.Equal("#000000,#ffffff", opts.ReliefIsolateColors);
            Assert.Equal(0.2, opts.ReliefIsolateTolerance!.Value, 6);
        }

        [Fact]
        public void Relief_RoundTripsThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                ReliefEnabled = true,
                ReliefRaymarch = true,
                ReliefHeight = 3.0,
                ReliefStrength = 0.75,
                ReliefLightAzimuth = 210.0,
                ReliefLightElevation = 55.0,
                ReliefShadow = 0.4,
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);
            Assert.Equal(3.0, opts.ReliefHeight!.Value, 6);
            Assert.Equal(0.75, opts.ReliefStrength!.Value, 6);
            Assert.Equal(210.0, opts.ReliefLightAzimuth!.Value, 6);
            Assert.Equal(55.0, opts.ReliefLightElevation!.Value, 6);
            Assert.Equal(0.4, opts.ReliefShadow!.Value, 6);
        }

        [Fact]
        public void Relief_StrengthOutOfRangeRejected()
        {
            // Builder won't emit this, but the parser must guard it directly.
            string[] argv = { "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1", "--relief-strength", "2.5",
                "--out", "out.png" };
            Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
            Assert.Contains("relief-strength", err);
        }

        // #363 — domain-warp is now a flag, not a gap.
        [Fact]
        public void DomainWarp_EmittedAndNotAGap()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                DomainWarpActive = true,
                DomainWarpStrength = 0.4,
                DomainWarpFrequency = 2.0,
            });
            Assert.Contains("--domain-warp", report.Command);
            Assert.Contains("--domain-warp-strength 0.4", report.Command);
            Assert.Contains("--domain-warp-frequency 2", report.Command);
            Assert.DoesNotContain(report.Gaps, g => g.Contains("warp", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DomainWarp_OffOmitsAllDomainFlags()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { DomainWarpActive = false });
            Assert.DoesNotContain("--domain-warp", cmd);
        }

        [Fact]
        public void AcidWarp_StaticParams_RoundTripThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.AcidWarp,
                CenterX = 0, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters
                {
                    AcidWarpPattern = 7,
                    AcidWarpFrequency = 1.5,
                    AcidWarpWarpStrength = 0.3,
                    AcidWarpSeed = 999,
                },
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(7, opts.AcidPattern);
            Assert.Equal(1.5, opts.AcidFrequency!.Value, 6);
            Assert.Equal(0.3, opts.AcidWarpStrength!.Value, 6);
            Assert.Equal(999, opts.AcidSeed);
        }

        [Fact]
        public void DomainWarp_RoundTripsThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                DomainWarpActive = true,
                DomainWarpStrength = 0.25,
                DomainWarpFrequency = 3.0,
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.True(opts.DomainWarp);
            Assert.Equal(0.25, opts.DomainWarpStrength!.Value, 6);
            Assert.Equal(3.0, opts.DomainWarpFrequency!.Value, 6);
        }

        // #363 — interior alpha is now a real flag, not a gap.
        [Fact]
        public void InteriorAlpha_EmittedAndNotAGap()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                InteriorAlpha = 128,
            });
            Assert.Contains("--interior-alpha 128", report.Command);
            Assert.DoesNotContain(report.Gaps, g => g.Contains("nterior"));
        }

        [Fact]
        public void InteriorAlpha_OpaqueDefaultIsOmitted()
        {
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { InteriorAlpha = 255 });
            Assert.DoesNotContain("--interior-alpha", cmd);
        }

        [Fact]
        public void InteriorAlpha_RoundTripsThroughBatchOptions()
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                InteriorAlpha = 64,
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(64, opts.InteriorAlpha);
        }

        [Fact]
        public void UnsavedTheme_IsAGapAndSuppressesThemeFlag()
        {
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                ThemeName = "My Custom Edit",
                ThemeIsUnsaved = true,
            });

            // No --theme emitted (nothing to reference by name)…
            Assert.DoesNotContain("--theme", report.Command);
            // …and the user is warned.
            Assert.Contains(report.Gaps, g => g.Contains("unsaved", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Stereo_GapUsesRealLightingFxDataDefaultAsBaseline()
        {
            // A default LightingFxData has StereoMode.Off — no gap.
            var off = LightingFxData.CreateDefault();
            Assert.Equal(StereoMode.Off, off.StereoMode);
        }

        // ── S8 (#404) per-light point / spot flags ────────────────────────

        [Fact]
        public void Lights_DirectionalDefaultEmitsNoLightFlags()
        {
            // An all-directional scene (the default) must not emit any --lightN-*.
            var fx = LightingFxData.CreateDefault();
            var cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters { Lighting = fx },
            });
            Assert.DoesNotContain("--light1-", cmd);
            Assert.DoesNotContain("--light2-", cmd);
            Assert.DoesNotContain("--light3-", cmd);
        }

        [Fact]
        public void Lights_PointAndSpot_RoundTripThroughBatchOptions()
        {
            var fx = LightingFxData.CreateDefault();
            fx.Light1.Type = LightType.Point;
            fx.Light1.Intensity = 1.5;
            fx.Light1.Theta = 0.7; fx.Light1.Phi = 1.2;
            fx.Light1.PosX = 0.3; fx.Light1.PosY = 1.1; fx.Light1.PosZ = -0.4;
            fx.Light1.Range = 4.0;
            fx.Light2.Type = LightType.Spot;
            fx.Light2.Intensity = 0.8;
            fx.Light2.Theta = -0.3; fx.Light2.Phi = 0.9;
            fx.Light2.PosX = -0.5; fx.Light2.PosY = 1.0; fx.Light2.PosZ = 0.2;
            fx.Light2.Range = 6.0;
            fx.Light2.SpotInnerDeg = 20.0; fx.Light2.SpotOuterDeg = 45.0;

            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters { Lighting = fx },
            };
            var argv = Tokenize(BatchCommandBuilder.Build(snap));
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            // Any positional light implies relief + raymarch.
            Assert.True(opts.Relief);
            Assert.True(opts.ReliefRaymarch);

            var l1 = opts.Lights[0];
            Assert.Equal(LightType.Point, l1.Type);
            Assert.Equal(1.5, l1.Intensity!.Value, 6);
            Assert.Equal(0.7, l1.Theta!.Value, 6);
            Assert.Equal(1.2, l1.Phi!.Value, 6);
            Assert.Equal(0.3, l1.PosX!.Value, 6);
            Assert.Equal(1.1, l1.PosY!.Value, 6);
            Assert.Equal(-0.4, l1.PosZ!.Value, 6);
            Assert.Equal(4.0, l1.Range!.Value, 6);

            var l2 = opts.Lights[1];
            Assert.Equal(LightType.Spot, l2.Type);
            Assert.Equal(0.8, l2.Intensity!.Value, 6);
            Assert.Equal(20.0, l2.SpotInnerDeg!.Value, 6);
            Assert.Equal(45.0, l2.SpotOuterDeg!.Value, 6);

            // Light 3 stayed directional → no override captured.
            Assert.False(opts.Lights[2].HasAny);
        }

        [Fact]
        public void Lights_AppliedOntoFractalParametersLighting()
        {
            // BatchRenderer's apply path (mirrored here): overrides land on the light.
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light1-type", "spot", "--light1-intensity", "2",
                "--light1-pos", "0.1,2,0.3", "--light1-range", "5",
                "--light1-cone", "15,35",
                "--out", "out.png",
            };
            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            var o = opts.Lights[0];
            Assert.Equal(LightType.Spot, o.Type);
            Assert.Equal(2.0, o.Intensity!.Value, 6);
            Assert.Equal(0.1, o.PosX!.Value, 6);
            Assert.Equal(2.0, o.PosY!.Value, 6);
            Assert.Equal(0.3, o.PosZ!.Value, 6);
            Assert.Equal(5.0, o.Range!.Value, 6);
            Assert.Equal(15.0, o.SpotInnerDeg!.Value, 6);
            Assert.Equal(35.0, o.SpotOuterDeg!.Value, 6);
        }

        [Fact]
        public void Lights_BadTypeRejected()
        {
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light1-type", "banana", "--out", "out.png",
            };
            Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
            Assert.Contains("light1-type", err);
        }

        [Fact]
        public void Lights_BadPosCsvRejected()
        {
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light2-pos", "1,2", "--out", "out.png",   // needs x,y,z
            };
            Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
            Assert.Contains("light2-pos", err);
        }

        [Fact]
        public void Lights_IntensityOutOfRangeRejected()
        {
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light3-intensity", "9", "--out", "out.png",
            };
            Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
            Assert.Contains("light3-intensity", err);
        }

        // ── S8 (#404) per-light colour flag ───────────────────────────────

        [Theory]
        [InlineData("#FF8800", 0xFFFF8800u)]   // 6-digit → opaque
        [InlineData("FF8800", 0xFFFF8800u)]    // '#' optional
        [InlineData("0xFF8800", 0xFFFF8800u)]  // 0x prefix
        [InlineData("#8012AB34", 0x8012AB34u)] // 8-digit → explicit alpha
        public void Lights_Color_ParsesHexForms(string hex, uint expected)
        {
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light1-color", hex, "--out", "out.png",
            };
            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(expected, opts.Lights[0].Color!.Value);
        }

        [Fact]
        public void Lights_Color_BadRejected()
        {
            string[] argv =
            {
                "FracturingFog", "--batch", "--fractal", "Mandelbrot",
                "--x", "-0.5", "--y", "0", "--zoom", "1",
                "--light1-color", "reddish", "--out", "out.png",
            };
            Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
            Assert.Contains("light1-color", err);
        }

        [Fact]
        public void Lights_PointLight_CustomColor_RoundTrips()
        {
            var fx = LightingFxData.CreateDefault();
            fx.Light1.Type = LightType.Point;
            fx.Light1.Intensity = 1.5;
            fx.Light1.PosX = 0.3; fx.Light1.PosY = 1.1; fx.Light1.PosZ = -0.4;
            fx.Light1.Color = 0xFF3366CCu;      // non-default (slot 1 default is white)

            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters { Lighting = fx },
            };
            string cmd = BatchCommandBuilder.Build(snap);
            Assert.Contains("--light1-color", cmd);

            var argv = Tokenize(cmd);
            for (int i = 0; i < argv.Length; i++)
                if (argv[i] == "<OUTPUT.png>") argv[i] = "out.png";

            Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
            Assert.Equal(0xFF3366CCu, opts.Lights[0].Color!.Value);
        }

        [Fact]
        public void Lights_DefaultColor_NotEmitted()
        {
            // A positional light left at its slot-default colour must not clutter
            // the command with a --lightN-color flag (parser keeps the default).
            var fx = LightingFxData.CreateDefault();
            fx.Light1.Type = LightType.Point;   // positional so the light IS emitted
            fx.Light1.Intensity = 1.0;
            // Light1.Color stays the slot-1 default (white).

            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot,
                CenterX = -0.5, CenterY = 0, Zoom = 1,
                Parameters = new FractalParameters { Lighting = fx },
            };
            string cmd = BatchCommandBuilder.Build(snap);
            Assert.Contains("--light1-type", cmd);          // light emitted
            Assert.DoesNotContain("--light1-color", cmd);   // but colour omitted (default)
        }
    }
}
