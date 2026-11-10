// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AudioRecipeLibrary.cs (#1434)
//
// One-click audio recipe presets: a named set of audio→param bindings (plus any
// side setting a recipe needs to show, e.g. Acid Warp Morph for AcidWarpFlow),
// applied to the binding matrix in one go. The built-ins are the machine form of
// the Fractured Audio cookbook recipes (#1433) and are read-only; user presets
// live in %APPDATA%\FracturingFog\audio-recipes.json, read element by element
// (TolerantJsonList, #964/#966) so one entry this build can't read never hides
// the rest. The batch resolves a name here for `--audio-preset`.
//
// A recipe is type-agnostic: it applies to a fractal type when at least one of its
// bindings targets a parameter that type can animate (FractalAnimatableParamsMap);
// the bindings the type can't animate are skipped, so one "spin" recipe covers
// every type with an angle knob.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;

namespace FracturingFog.Audio
{
    /// <summary>A side setting a recipe needs (a <see cref="FractalParameters"/>
    /// property name and its value as invariant text, e.g. <c>AcidWarpMorph = true</c>).</summary>
    public sealed class AudioRecipeSetting
    {
        public string ParamName { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One audio recipe preset (#1434).</summary>
    public sealed class AudioRecipePreset
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<AudioParamBinding> Bindings { get; set; } = new();
        public List<AudioRecipeSetting> Settings { get; set; } = new();

        /// <summary>True for the shipped, read-only recipes.</summary>
        [JsonIgnore] public bool IsBuiltIn { get; init; }

        /// <summary>The bindings <paramref name="type"/> can animate (scalar kinds),
        /// cloned, in recipe order.</summary>
        public List<AudioParamBinding> BindingsFor(FractalType type)
        {
            var names = AnimatableNames(type);
            return Bindings.Where(b => b != null && names.Contains(b.ParamName))
                           .Select(AudioRecipeLibrary.Clone).ToList();
        }

        /// <summary>True when at least one binding targets a parameter
        /// <paramref name="type"/> can animate.</summary>
        public bool AppliesTo(FractalType type)
        {
            var names = AnimatableNames(type);
            return Bindings.Any(b => b != null && names.Contains(b.ParamName));
        }

        private static HashSet<string> AnimatableNames(FractalType type)
            => FractalAnimatableParamsMap.For(type)
                .Where(d => d.Kind != AnimatableParamKind.Complex)
                .Select(d => d.ParamName)
                .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Built-in + user audio recipe presets (#1434).</summary>
    public static class AudioRecipeLibrary
    {
        public const string FileName = "audio-recipes.json";
        private static string FilePath => AppDataPaths.Combine(FileName);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // ── built-ins ────────────────────────────────────────────────────────
        // Recipes from the #1433 cookbook sketch. Ranges sit inside each target's
        // FractalAnimatableParamsMap range; Rate-mode ranges are units per second.

        private static AudioParamBinding B(string param, AudioSignalKind src, double min, double max,
            AudioBindingMode mode = AudioBindingMode.Position, AudioRateEdge edge = AudioRateEdge.Clamp,
            AudioResponseCurve curve = AudioResponseCurve.Linear, double attack = 0, double release = 0,
            bool invert = false) => new()
        {
            ParamName = param,
            Enabled = true,
            Binding = new AudioModulationBinding
            {
                Source = src, OutMin = min, OutMax = max, Mode = mode, Edge = edge, Curve = curve,
                AttackSeconds = attack, ReleaseSeconds = release, Invert = invert,
            },
        };

        private static readonly AudioRecipeSetting MorphOn = new() { ParamName = "AcidWarpMorph", Value = "true" };
        private static readonly AudioRecipeSetting DomainWarpOn = new() { ParamName = "DomainWarpEnabled", Value = "true" };

        public static IReadOnlyList<AudioRecipePreset> BuiltIns { get; } = new List<AudioRecipePreset>
        {
            new()
            {
                Name = "Acid Fog - Endless flow",
                Description = "The music pushes the pattern flow along: louder is faster, quiet "
                            + "passages still drift. Rate + Wrap on AcidWarpFlow, overall loudness, "
                            + "0.05 to 1.5 patterns/s, 0.8 s release. Turns Morph on.",
                Bindings = { B("AcidWarpFlow", AudioSignalKind.Rms, 0.05, 1.5, AudioBindingMode.Rate,
                               AudioRateEdge.Wrap, release: 0.8) },
                Settings = { MorphOn },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Acid Fog - Beat nudge",
                Description = "Each beat nudges the flow forward, then it rests. Rate + Wrap on "
                            + "AcidWarpFlow, beat pulse, 0 to 3 patterns/s. Turns Morph on.",
                Bindings = { B("AcidWarpFlow", AudioSignalKind.BeatPulse, 0.0, 3.0, AudioBindingMode.Rate,
                               AudioRateEdge.Wrap) },
                Settings = { MorphOn },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Acid Fog - Breathe between patterns",
                Description = "Breathes between the first two patterns in time with the tempo. "
                            + "Position on AcidWarpFlow, tempo sine, 0 to 1. Turns Morph on.",
                Bindings = { B("AcidWarpFlow", AudioSignalKind.BpmPhaseSine, 0.0, 1.0) },
                Settings = { MorphOn },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Bass-swelled domain warp",
                Description = "The bass swells the domain warp and lets it settle. Position on "
                            + "DomainWarpStrength, bass, 0 to 0.4, smoothstep, attack 0.05 s / "
                            + "release 0.4 s. Turns Domain warp on (shallow zoom only).",
                Bindings = { B("DomainWarpStrength", AudioSignalKind.Bass, 0.0, 0.4,
                               curve: AudioResponseCurve.Smoothstep, attack: 0.05, release: 0.4) },
                Settings = { DomainWarpOn },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Spin with the music",
                Description = "Turns the angle knobs at a speed the music sets: louder spins "
                            + "faster, never jumps back. Rate + Wrap, overall loudness, 5 to 120 "
                            + "degrees/s, 0.5 s release. Covers the user-equation rotation, the "
                            + "dual-orbit trap angle and the billiard beam angle.",
                Bindings =
                {
                    B("UserEquationRotationDegrees", AudioSignalKind.Rms, 5, 120, AudioBindingMode.Rate,
                      AudioRateEdge.Wrap, release: 0.5),
                    B("DualOrbitTrapAngle", AudioSignalKind.Rms, 5, 120, AudioBindingMode.Rate,
                      AudioRateEdge.Wrap, release: 0.5),
                    B("BilliardRaysAngle", AudioSignalKind.Rms, 5, 120, AudioBindingMode.Rate,
                      AudioRateEdge.Wrap, release: 0.5),
                },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Fringes collapse on the beat",
                Description = "Every beat switches the which-path detector on: the double-slit "
                            + "fringes flash flat on the beat and return between beats. Position, "
                            + "beat pulse, 0 to 1, 0.15 s release.",
                Bindings =
                {
                    B("DualOrbitWhichPath", AudioSignalKind.BeatPulse, 0.0, 1.0, release: 0.15),
                    B("SlitCascadeWhichPath", AudioSignalKind.BeatPulse, 0.0, 1.0, release: 0.15),
                },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Calm passages hold still",
                Description = "Holds the interference fringes steady between beats and ducks them "
                            + "on each beat (Invert: no beat = the top of the range). Position on "
                            + "DualOrbitInterferenceK, inverted beat pulse, 10 to 30, 0.3 s release.",
                Bindings = { B("DualOrbitInterferenceK", AudioSignalKind.BeatPulse, 10.0, 30.0,
                               release: 0.3, invert: true) },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Bulb breathes with the bass",
                Description = "The bass swells the Mandelbulb power and lets it fall back. Position "
                            + "on BulbPower, bass, 7 to 9, smoothstep, attack 0.05 s / release 0.5 s.",
                Bindings = { B("BulbPower", AudioSignalKind.Bass, 7.0, 9.0,
                               curve: AudioResponseCurve.Smoothstep, attack: 0.05, release: 0.5) },
                IsBuiltIn = true,
            },
            new()
            {
                Name = "Downbeat slice",
                Description = "Each bar's downbeat sweeps the 4D slice and lets it glide back. "
                            + "Position on the slice-W knobs, downbeat pulse, -0.3 to 0.3, "
                            + "0.6 s release.",
                Bindings =
                {
                    B("QJuliaSliceW", AudioSignalKind.DownbeatPulse, -0.3, 0.3, release: 0.6),
                    B("QMandelSliceW", AudioSignalKind.DownbeatPulse, -0.3, 0.3, release: 0.6),
                    B("BicomplexSliceW", AudioSignalKind.DownbeatPulse, -0.3, 0.3, release: 0.6),
                    B("CoquaternionSliceW", AudioSignalKind.DownbeatPulse, -0.3, 0.3, release: 0.6),
                },
                IsBuiltIn = true,
            },
        };

        // ── user presets ─────────────────────────────────────────────────────

        private static readonly TolerantJsonList<AudioRecipePreset> s_persisted = new();
        private static readonly object s_lock = new();

        /// <summary>The saved user presets (never null; unreadable entries are kept on
        /// disk and written back on the next save).</summary>
        public static List<AudioRecipePreset> LoadUser()
        {
            lock (s_lock)
            {
                var list = s_persisted.LoadFile(FilePath, JsonOpts);
                list.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name) || IsBuiltInName(p.Name));
                foreach (var p in list) { p.Bindings ??= new(); p.Settings ??= new(); p.Description ??= string.Empty; }
                return list;
            }
        }

        private static void SaveUser(List<AudioRecipePreset> list)
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(AppDataPaths.Root);
                AtomicFile.WriteAllText(FilePath, s_persisted.Serialize(list, JsonOpts, x => x.Name));
            }
        }

        /// <summary>Built-ins first, then the user's presets.</summary>
        public static IReadOnlyList<AudioRecipePreset> All() => BuiltIns.Concat(LoadUser()).ToList();

        /// <summary>The presets that apply to <paramref name="type"/>, built-ins first.</summary>
        public static IReadOnlyList<AudioRecipePreset> ForType(FractalType type)
            => All().Where(p => p.AppliesTo(type)).ToList();

        /// <summary>Look up a preset by name (case-insensitive), built-ins first.</summary>
        public static AudioRecipePreset? Find(string? name)
            => string.IsNullOrWhiteSpace(name) ? null
             : All().FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        public static bool IsBuiltInName(string? name)
            => !string.IsNullOrWhiteSpace(name)
            && BuiltIns.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Save (or replace) a user preset. Refuses a blank name, a built-in's
        /// name, or a preset with no enabled binding.</summary>
        public static bool TrySaveUser(AudioRecipePreset preset, out string? error)
        {
            error = null;
            if (preset == null || string.IsNullOrWhiteSpace(preset.Name)) { error = "Name the preset first."; return false; }
            string name = preset.Name.Trim();
            if (IsBuiltInName(name)) { error = $"'{name}' is a built-in recipe; choose another name."; return false; }
            if (preset.Bindings == null || !preset.Bindings.Any(b => b != null && b.Enabled && !string.IsNullOrEmpty(b.ParamName)))
            { error = "No audio binding is enabled, so there is nothing to save."; return false; }

            var copy = new AudioRecipePreset
            {
                Name = name,
                Description = preset.Description ?? string.Empty,
                Bindings = preset.Bindings.Where(b => b != null && b.Enabled && !string.IsNullOrEmpty(b.ParamName))
                                          .Select(Clone).ToList(),
                Settings = (preset.Settings ?? new()).Select(s => new AudioRecipeSetting { ParamName = s.ParamName, Value = s.Value }).ToList(),
            };
            var list = LoadUser();
            int i = list.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) list[i] = copy; else list.Add(copy);
            try { SaveUser(list); }
            catch (Exception ex) { error = ex.Message; return false; }
            return true;
        }

        /// <summary>Delete a user preset. Built-ins can't be deleted: they are never in
        /// the user list (<see cref="LoadUser"/> drops a stored built-in name).</summary>
        public static bool DeleteUser(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var list = LoadUser();
            int removed = list.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            try { SaveUser(list); } catch { return false; }
            return true;
        }

        // ── side settings ────────────────────────────────────────────────────

        /// <summary>The side settings a set of bindings needs to show: Acid Warp
        /// Morph for <c>AcidWarpFlow</c>, the Domain-warp toggle for
        /// <c>DomainWarpStrength</c>. Saved with a user preset.</summary>
        public static List<AudioRecipeSetting> ImpliedSettings(IEnumerable<AudioParamBinding>? bindings)
        {
            var list = new List<AudioRecipeSetting>();
            var names = (bindings ?? Array.Empty<AudioParamBinding>())
                .Where(b => b != null && b.Enabled).Select(b => b.ParamName).ToHashSet(StringComparer.Ordinal);
            if (names.Contains("AcidWarpFlow")) list.Add(new AudioRecipeSetting { ParamName = MorphOn.ParamName, Value = MorphOn.Value });
            if (names.Contains("DomainWarpStrength")) list.Add(new AudioRecipeSetting { ParamName = DomainWarpOn.ParamName, Value = DomainWarpOn.Value });
            return list;
        }

        /// <summary>Apply a preset's side settings onto <paramref name="p"/> (bool,
        /// integer, double or enum properties, invariant text). Returns the names it
        /// set; a setting it can't apply is reported in <paramref name="skipped"/>.</summary>
        public static IReadOnlyList<string> ApplySettings(AudioRecipePreset preset, FractalParameters p, List<string>? skipped = null)
        {
            ArgumentNullException.ThrowIfNull(preset);
            ArgumentNullException.ThrowIfNull(p);
            var set = new List<string>();
            foreach (var s in preset.Settings ?? new())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.ParamName)) continue;
                var prop = typeof(FractalParameters).GetProperty(s.ParamName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite || !TryConvert(s.Value, prop.PropertyType, out object? v))
                {
                    skipped?.Add($"{s.ParamName}={s.Value}");
                    continue;
                }
                prop.SetValue(p, v);
                set.Add(s.ParamName);
            }
            return set;
        }

        private static bool TryConvert(string? text, Type t, out object? value)
        {
            value = null;
            text = text?.Trim() ?? string.Empty;
            if (t == typeof(bool) && bool.TryParse(text, out bool b)) { value = b; return true; }
            if (t == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) { value = i; return true; }
            if (t == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && double.IsFinite(d)) { value = d; return true; }
            if (t.IsEnum && Enum.TryParse(t, text, ignoreCase: true, out object? e) && Enum.IsDefined(t, e!)) { value = e; return true; }
            return false;
        }

        internal static AudioParamBinding Clone(AudioParamBinding b) => new()
        {
            ParamName = b.ParamName,
            Enabled = b.Enabled,
            Binding = b.Binding == null ? new AudioModulationBinding() : new AudioModulationBinding
            {
                Source = b.Binding.Source, Gain = b.Binding.Gain, Bias = b.Binding.Bias, Curve = b.Binding.Curve,
                Invert = b.Binding.Invert, OutMin = b.Binding.OutMin, OutMax = b.Binding.OutMax, Mode = b.Binding.Mode,
                Edge = b.Binding.Edge, AttackSeconds = b.Binding.AttackSeconds, ReleaseSeconds = b.Binding.ReleaseSeconds,
            },
        };
    }
}
