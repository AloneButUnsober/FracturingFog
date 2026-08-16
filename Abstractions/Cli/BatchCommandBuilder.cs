// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Cli/BatchCommandBuilder.cs
// #361 (slice of #64) — CLI Command Builder, MVP.
//
// Turns a snapshot of the live 2D configuration into a copy/paste-ready
// `--batch` command line. This is the *reverse* of Batch/BatchOptions.TryParse:
// the flag names + default values here MUST stay in step with that parser.
// #362 replaces this hand-kept pairing with a single shared flag-metadata
// table consumed by both sides; until then, treat the two files as a couple.
//
// MVP scope (image / 2D poster path only): fractal, coordinates, iterations,
// theme, quality, size, post-FX (brightness / contrast / adaptive) and the
// fractal-specific parameters that already have batch flags. Fx families with
// no batch flag (lighting / relief / volumetric / interior-alpha / acid / SBS)
// are NOT represented here — #362 adds the gap-detection banner that warns the
// user when the live config uses one of them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FracturingFog.Models;

namespace FracturingFog.Cli
{
    /// <summary>
    /// Immutable snapshot of the live 2D configuration the command builder
    /// serialises. Populated by the UI from the active view-state; kept as a
    /// plain input so the builder stays pure and unit-testable without a shell.
    /// </summary>
    public sealed class BatchCommandSnapshot
    {
        public FractalType Fractal { get; init; } = FractalType.Mandelbrot;

        // Live authoritative coordinates. Emitted verbatim (round-trippable) so
        // the command reproduces exactly what is on screen — even when a region
        // was loaded and then panned/zoomed away from its saved coordinates.
        public double CenterX { get; init; } = FractalViewStateDefaults.CenterX;
        public double CenterY { get; init; } = FractalViewStateDefaults.CenterY;
        public double Zoom { get; init; } = FractalViewStateDefaults.Zoom;

        /// <summary>Effective iteration count in use for the current render.
        /// Emitted as <c>--iter</c> whenever &gt; 0 so the poster does not drift
        /// with the quality preset's iteration formula.</summary>
        public int Iterations { get; init; }

        public string ThemeName { get; init; } = "HSV";
        public string QualityName { get; init; } = "Standard";

        public int Width { get; init; } = 1920;
        public int Height { get; init; } = 1080;

        // Post-FX (parity with the interactive sliders). 0 = neutral / omit.
        public int Brightness { get; init; }
        public int Contrast { get; init; }
        public int HistogramEq { get; init; }

        /// <summary>Fractal-specific parameters. Only the fields with a matching
        /// batch flag are read, and only for the matching fractal type.</summary>
        public FractalParameters? Parameters { get; init; }

        /// <summary>Executable name to lead the command with. The user runs from
        /// wherever the exe lives; a bare name keeps the string portable.</summary>
        public string ExecutableName { get; init; } = "FracturingFog";

        /// <summary>Placeholder token emitted for <c>--out</c>. The builder never
        /// invents a real path — the user substitutes one.</summary>
        public string OutputPlaceholder { get; init; } = "<OUTPUT.png>";
    }

    /// <summary>Default view-state constants mirrored here so the snapshot has
    /// sensible defaults without depending on the ViewState assembly's layout.</summary>
    internal static class FractalViewStateDefaults
    {
        public const double CenterX = -0.5;
        public const double CenterY = 0.0;
        public const double Zoom = 0.13;
    }

    public static class BatchCommandBuilder
    {
        /// <summary>Serialise <paramref name="snap"/> to a single-line
        /// <c>--batch</c> command string suitable for copy/paste.</summary>
        public static string Build(BatchCommandSnapshot snap)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));

            var parts = new List<string>(24)
            {
                Token(snap.ExecutableName),
                "--batch",
            };

            // Fractal type — always explicit so the command is self-describing.
            parts.Add("--fractal");
            parts.Add(Token(snap.Fractal.ToString()));

            // Live coordinates + iterations — always emitted for exact fidelity.
            parts.Add("--x");    parts.Add(Num(snap.CenterX));
            parts.Add("--y");    parts.Add(Num(snap.CenterY));
            parts.Add("--zoom"); parts.Add(Num(snap.Zoom));
            if (snap.Iterations > 0)
            {
                parts.Add("--iter");
                parts.Add(snap.Iterations.ToString(CultureInfo.InvariantCulture));
            }

            // Theme — emit unless it is the batch default (HSV).
            if (!string.IsNullOrWhiteSpace(snap.ThemeName) &&
                !string.Equals(snap.ThemeName, "HSV", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("--theme");
                parts.Add(Token(snap.ThemeName));
            }

            // Quality — emit unless the batch default (Standard).
            if (!string.IsNullOrWhiteSpace(snap.QualityName) &&
                !string.Equals(snap.QualityName, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("--quality");
                parts.Add(Token(snap.QualityName));
            }

            // Output size — always explicit (deterministic poster dimensions).
            parts.Add("--width");  parts.Add(snap.Width.ToString(CultureInfo.InvariantCulture));
            parts.Add("--height"); parts.Add(snap.Height.ToString(CultureInfo.InvariantCulture));

            // Post-FX — only when non-neutral.
            if (snap.Brightness != 0)  { parts.Add("--brightness"); parts.Add(snap.Brightness.ToString(CultureInfo.InvariantCulture)); }
            if (snap.Contrast != 0)    { parts.Add("--contrast");   parts.Add(snap.Contrast.ToString(CultureInfo.InvariantCulture)); }
            if (snap.HistogramEq != 0) { parts.Add("--adaptive");   parts.Add(snap.HistogramEq.ToString(CultureInfo.InvariantCulture)); }

            // Fractal-specific parameters that have batch flags. Emitted only for
            // the matching fractal type so unrelated defaults never clutter.
            AppendFractalParams(parts, snap);

            // Output path placeholder — always last, always a placeholder.
            parts.Add("--out");
            parts.Add(Token(snap.OutputPlaceholder));

            return string.Join(" ", parts);
        }

        private static void AppendFractalParams(List<string> parts, BatchCommandSnapshot snap)
        {
            var p = snap.Parameters;
            if (p == null) return;

            switch (snap.Fractal)
            {
                case FractalType.Multibrot:
                    parts.Add("--multibrot-exp");
                    parts.Add(p.MultibrotExponent.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Mandelbulb:
                    parts.Add("--bulb-power");
                    parts.Add(Num(p.BulbPower));
                    break;

                case FractalType.LSystem:
                    parts.Add("--lsystem-preset");
                    parts.Add(Token(p.LSystemPresetName));
                    parts.Add("--lsystem-depth");
                    parts.Add(p.LSystemDepth.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Plasma:
                    parts.Add("--plasma-roughness");
                    parts.Add(Num(p.PlasmaRoughness));
                    parts.Add("--plasma-seed");
                    parts.Add(p.PlasmaSeed.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Flame:
                    parts.Add("--flame-preset");
                    parts.Add(Token(p.FlamePresetName));
                    parts.Add("--flame-iter");
                    parts.Add(p.FlameIterations.ToString(CultureInfo.InvariantCulture));
                    parts.Add("--flame-gamma");
                    parts.Add(Num(p.FlameGamma));
                    parts.Add("--flame-vibrancy");
                    parts.Add(Num(p.FlameVibrancy));
                    break;
            }
        }

        /// <summary>Round-trippable invariant formatting for a double so the
        /// parsed value reproduces the live one bit-for-bit.</summary>
        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Quote a token when it contains whitespace or characters a
        /// shell would split on; escape embedded double quotes.</summary>
        private static string Token(string value)
        {
            value ??= string.Empty;
            bool needsQuote = value.Length == 0 || value.IndexOfAny(QuoteTriggers) >= 0;
            if (!needsQuote) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static readonly char[] QuoteTriggers = { ' ', '\t', '"', '\'', '<', '>', '&', '|', '(', ')' };
    }
}
