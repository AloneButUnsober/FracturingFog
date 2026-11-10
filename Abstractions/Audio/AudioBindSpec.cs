// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Audio/AudioBindSpec.cs (#1432)
//
// The command-line form of one audio→param binding (`--audio-bind`):
//
//     PARAM=SIGNAL[,key=value]...
//
// e.g. DualOrbitWhichPath=BeatPulse,min=0,max=1,release=0.15. Keys (any order,
// case-insensitive): min, max, gain, bias, curve (linear|exp|log|smoothstep),
// invert (true|false), mode (position|rate), edge (clamp|wrap|pingpong), attack,
// release — every field of AudioModulationBinding, so a live binding round-trips.
// Format writes min and max always and the other keys only when they differ from
// the binding defaults.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FracturingFog.Audio
{
    public static class AudioBindSpec
    {
        public static bool TryParse(string? spec, out AudioParamBinding binding, out string? error)
        {
            binding = new AudioParamBinding();
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) { error = "empty audio binding"; return false; }
            int eq = spec.IndexOf('=');
            if (eq <= 0) { error = $"expects PARAM=SIGNAL[,key=value…], got '{spec}'"; return false; }
            string param = spec[..eq].Trim();
            string[] parts = spec[(eq + 1)..].Split(',');
            if (!Enum.TryParse(parts[0].Trim(), true, out AudioSignalKind source) || !Enum.IsDefined(source))
            { error = $"unknown audio signal '{parts[0].Trim()}' (one of {string.Join(", ", Enum.GetNames<AudioSignalKind>())})"; return false; }
            var b = new AudioModulationBinding { Source = source };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 1; k < parts.Length; k++)
            {
                string kv = parts[k].Trim();
                int e = kv.IndexOf('=');
                if (e <= 0) { error = $"'{kv}' is not key=value"; return false; }
                string key = kv[..e].Trim(), val = kv[(e + 1)..].Trim();
                if (!seen.Add(key)) { error = $"'{key}' given twice"; return false; }
                switch (key.ToLowerInvariant())
                {
                    case "min": if (!Num(val, out double mn, ref error, key)) return false; b.OutMin = mn; break;
                    case "max": if (!Num(val, out double mx, ref error, key)) return false; b.OutMax = mx; break;
                    case "gain": if (!Num(val, out double g, ref error, key)) return false; b.Gain = g; break;
                    case "bias": if (!Num(val, out double bi, ref error, key)) return false; b.Bias = bi; break;
                    case "attack": if (!Num(val, out double at, ref error, key) || !NonNeg(at, ref error, key)) return false; b.AttackSeconds = at; break;
                    case "release": if (!Num(val, out double re, ref error, key) || !NonNeg(re, ref error, key)) return false; b.ReleaseSeconds = re; break;
                    case "curve": if (!Choice(val, out AudioResponseCurve c, ref error, key)) return false; b.Curve = c; break;
                    case "mode": if (!Choice(val, out AudioBindingMode m, ref error, key)) return false; b.Mode = m; break;
                    case "edge": if (!Choice(val, out AudioRateEdge ed, ref error, key)) return false; b.Edge = ed; break;
                    case "invert":
                        if (!bool.TryParse(val, out bool inv)) { error = $"invert expects true or false, got '{val}'"; return false; }
                        b.Invert = inv; break;
                    default:
                        error = $"unknown key '{key}' (min, max, gain, bias, curve, invert, mode, edge, attack, release)";
                        return false;
                }
            }
            if (param.Length == 0) { error = "missing PARAM"; return false; }
            binding = new AudioParamBinding { ParamName = param, Enabled = true, Binding = b };
            return true;
        }

        public static string Format(AudioParamBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            var b = binding.Binding ?? new AudioModulationBinding();
            var d = new AudioModulationBinding();
            var sb = new StringBuilder();
            sb.Append(binding.ParamName).Append('=').Append(b.Source);
            sb.Append(",min=").Append(R(b.OutMin)).Append(",max=").Append(R(b.OutMax));
            if (b.Gain != d.Gain) sb.Append(",gain=").Append(R(b.Gain));
            if (b.Bias != d.Bias) sb.Append(",bias=").Append(R(b.Bias));
            if (b.Curve != d.Curve) sb.Append(",curve=").Append(b.Curve.ToString().ToLowerInvariant());
            if (b.Invert) sb.Append(",invert=true");
            if (b.Mode != d.Mode) sb.Append(",mode=").Append(b.Mode.ToString().ToLowerInvariant());
            if (b.Edge != d.Edge) sb.Append(",edge=").Append(b.Edge.ToString().ToLowerInvariant());
            if (b.AttackSeconds != d.AttackSeconds) sb.Append(",attack=").Append(R(b.AttackSeconds));
            if (b.ReleaseSeconds != d.ReleaseSeconds) sb.Append(",release=").Append(R(b.ReleaseSeconds));
            return sb.ToString();
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static bool Num(string s, out double v, ref string? error, string key)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v)) return true;
            error = $"{key} expects a number, got '{s}'";
            return false;
        }

        private static bool NonNeg(double v, ref string? error, string key)
        {
            if (v >= 0) return true;
            error = $"{key} must be 0 or more seconds";
            return false;
        }

        private static bool Choice<T>(string s, out T v, ref string? error, string key) where T : struct, Enum
        {
            if (Enum.TryParse(s, true, out v) && Enum.IsDefined(v) && !int.TryParse(s, out _)) return true;
            error = $"{key} expects one of {string.Join(", ", Enum.GetNames<T>()).ToLowerInvariant()}, got '{s}'";
            return false;
        }
    }
}
