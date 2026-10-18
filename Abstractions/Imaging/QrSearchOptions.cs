// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/QrSearchOptions.cs
//
// #1305 / S6 (#1401) - settings for the fractal-native QR view search: look
// around the start view (centre + zoom) and over the 8 mask patterns for the
// framing whose own light/dark pattern already agrees best with the code, so
// the compositor has to flip as few modules as possible. Deterministic for a
// seed. The batch spells it --qr-search "seed=1,tries=24,levels=4,...".

using System;
using System.Globalization;

namespace FracturingFog.Imaging
{
    public sealed record QrSearchOptions
    {
        public int Seed { get; init; } = 1;
        /// <summary>Candidate views tried per level.</summary>
        public int Tries { get; init; } = 24;
        /// <summary>Refinement levels; each narrows the search around the best views.</summary>
        public int Levels { get; init; } = 4;
        /// <summary>Best views kept between levels.</summary>
        public int Keep { get; init; } = 3;
        /// <summary>Zoom range relative to the start view, as factors.</summary>
        public double MinZoomFactor { get; init; } = 0.5;
        public double MaxZoomFactor { get; init; } = 8;
        /// <summary>Probe pixels per module (the probe renders small).</summary>
        public int ProbeModulePixels { get; init; } = 3;
        /// <summary>A candidate must keep at least this share of the start view's
        /// detail (edge energy inside the code area). Without it the search
        /// drifts to flat, featureless framings - they are the cheapest to steer
        /// but show no fractal.</summary>
        public double MinDetail { get; init; } = 0.8;

        public static QrSearchOptions Default { get; } = new();

        /// <summary>Null when every field is in range, else why not.</summary>
        public string? Validate()
        {
            if (Tries is < 1 or > 1000) return "tries must be 1..1000";
            if (Levels is < 1 or > 20) return "levels must be 1..20";
            if (Keep is < 1 or > 50) return "keep must be 1..50";
            if (!(MinZoomFactor > 0) || MinZoomFactor > 1) return "zmin must be in (0, 1]";
            if (MaxZoomFactor < 1 || MaxZoomFactor > 1e6) return "zmax must be 1..1e6";
            if (ProbeModulePixels is < 1 or > 16) return "probe must be 1..16 px/module";
            if (MinDetail is < 0 or > 2) return "detail must be 0..2 (share of the start view's detail)";
            return null;
        }

        /// <summary>Parse comma-separated key=value pairs: seed, tries, levels,
        /// keep, zmin, zmax, probe, detail. An empty spec is the defaults.</summary>
        public static bool TryParseSpec(string? spec, out QrSearchOptions options, out string? error)
        {
            options = Default;
            error = null;
            var o = Default;
            foreach (var raw in (spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) { error = $"--qr-search: '{raw}' is not key=value"; return false; }
                string k = raw[..eq].Trim().ToLowerInvariant(), v = raw[(eq + 1)..].Trim();
                bool okI = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv);
                bool okD = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv);
                switch (k)
                {
                    case "seed"   when okI: o = o with { Seed = iv }; break;
                    case "tries"  when okI: o = o with { Tries = iv }; break;
                    case "levels" when okI: o = o with { Levels = iv }; break;
                    case "keep"   when okI: o = o with { Keep = iv }; break;
                    case "zmin"   when okD: o = o with { MinZoomFactor = dv }; break;
                    case "zmax"   when okD: o = o with { MaxZoomFactor = dv }; break;
                    case "probe"  when okI: o = o with { ProbeModulePixels = iv }; break;
                    case "detail" when okD: o = o with { MinDetail = dv }; break;
                    default:
                        error = $"--qr-search: unknown key or bad value '{raw}' (keys: seed, tries, levels, keep, zmin, zmax, probe, detail)";
                        return false;
                }
            }
            if (o.Validate() is { } bad) { error = "--qr-search: " + bad; return false; }
            options = o;
            return true;
        }

        public static string FormatSpec(QrSearchOptions o) => string.Create(CultureInfo.InvariantCulture,
            $"seed={o.Seed},tries={o.Tries},levels={o.Levels},keep={o.Keep},zmin={o.MinZoomFactor:R},zmax={o.MaxZoomFactor:R},probe={o.ProbeModulePixels},detail={o.MinDetail:R}");
    }
}
