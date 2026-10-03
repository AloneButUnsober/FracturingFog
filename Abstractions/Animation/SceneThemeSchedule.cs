// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneThemeSchedule.cs
//
// #1052 — which colour theme a scene shot shows at a given moment. One pure
// rule shared by the offline exporter (SceneVideoRenderer) and live scene
// playback (via IColorThemeService.ResolveSceneShotTheme), so a shot looks the
// same in the Scene Editor's Play / Preview as in its export.
//
// Precedence (unchanged from the export path it was lifted from):
//   shot ThemeName → the region's first valid curated theme → the fallback.
// Rotation (opt-in, SceneShot.RotateThemes): when the shot's region curates two
// or more valid themes, the shot steps through them every
// SceneShot.ThemeRotateSeconds of shot-local time — a deterministic schedule
// (not random, unlike the slideshow), so live and export agree frame for frame.
// A shot ThemeName that is in the rotation starts it; one that isn't is put
// first.
//
// Theme names are checked through a caller-supplied canonicaliser: name →
// current library name (aliases resolved), or null when no such theme exists.
// Deleted / unknown curated names are skipped rather than applied.

using System;
using System.Collections.Generic;

namespace FracturingFog.Abstractions.Animation;

public static class SceneThemeSchedule
{
    /// <summary>Default seconds per theme when a shot rotates themes.</summary>
    public const double DefaultRotateSeconds = 3.0;

    /// <summary>Shortest allowed rotation step; keeps a typo (0.01 s) from
    /// strobing.</summary>
    public const double MinRotateSeconds = 0.25;

    /// <summary>The ordered theme list a rotating shot cycles through, or an
    /// empty list when the shot doesn't rotate (flag off, or fewer than two
    /// valid themes).</summary>
    public static IReadOnlyList<string> Rotation(
        SceneShot shot, IReadOnlyList<string>? regionCurated, Func<string, string?> canonical)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(canonical);
        if (!shot.RotateThemes) return Array.Empty<string>();

        var list = ValidCurated(regionCurated, canonical);
        string? start = Canon(shot.ThemeName, canonical);
        if (start != null)
        {
            int at = IndexOf(list, start);
            if (at < 0) list.Insert(0, start);
            else if (at > 0) list = Rotate(list, at);
        }
        return list.Count >= 2 ? list : Array.Empty<string>();
    }

    /// <summary>The theme shown at <paramref name="localTime"/> seconds into the
    /// shot. Never null: falls back to <paramref name="fallback"/> when neither
    /// the shot nor its region names a valid theme.</summary>
    public static string ThemeAt(
        SceneShot shot, IReadOnlyList<string>? regionCurated, Func<string, string?> canonical,
        string fallback, double localTime)
    {
        var rotation = Rotation(shot, regionCurated, canonical);
        if (rotation.Count > 0)
            return rotation[StepIndex(localTime, shot.ThemeRotateSeconds, rotation.Count)];

        return Canon(shot.ThemeName, canonical)
            ?? FirstValid(regionCurated, canonical)
            ?? fallback;
    }

    /// <summary>Index into a rotation of <paramref name="count"/> themes at
    /// <paramref name="localTime"/>, stepping every <paramref name="seconds"/>.</summary>
    public static int StepIndex(double localTime, double seconds, int count)
    {
        if (count <= 1) return 0;
        double step = System.Math.Max(MinRotateSeconds, double.IsFinite(seconds) ? seconds : DefaultRotateSeconds);
        double t = double.IsFinite(localTime) && localTime > 0 ? localTime : 0;
        long n = (long)System.Math.Floor(t / step);
        return (int)(n % count);
    }

    private static List<string> ValidCurated(IReadOnlyList<string>? curated, Func<string, string?> canonical)
    {
        var list = new List<string>();
        if (curated == null) return list;
        foreach (var raw in curated)
        {
            var c = Canon(raw, canonical);
            if (c != null && IndexOf(list, c) < 0) list.Add(c);
        }
        return list;
    }

    private static string? FirstValid(IReadOnlyList<string>? curated, Func<string, string?> canonical)
    {
        if (curated == null) return null;
        foreach (var raw in curated)
        {
            var c = Canon(raw, canonical);
            if (c != null) return c;
        }
        return null;
    }

    private static string? Canon(string? name, Func<string, string?> canonical)
        => string.IsNullOrWhiteSpace(name) ? null : canonical(name);

    private static int IndexOf(List<string> list, string name)
        => list.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static List<string> Rotate(List<string> list, int start)
    {
        var r = new List<string>(list.Count);
        for (int i = 0; i < list.Count; i++) r.Add(list[(start + i) % list.Count]);
        return r;
    }
}

/// <summary>#1052 — a shot's resolved theme schedule: one name (static) or the
/// rotation, plus the step length. Resolved once per shot (library lookups),
/// then sampled cheaply per frame / tick with <see cref="At"/>.</summary>
public sealed record SceneThemePlan(IReadOnlyList<string> Names, double RotateSeconds)
{
    /// <summary>True when the shot cycles through more than one theme.</summary>
    public bool Rotates => Names.Count > 1;

    /// <summary>The theme name at <paramref name="localTime"/> seconds into the shot.</summary>
    public string At(double localTime)
        => Names[SceneThemeSchedule.StepIndex(localTime, RotateSeconds, Names.Count)];

    /// <summary>Build the plan for <paramref name="shot"/> (see
    /// <see cref="SceneThemeSchedule"/> for the precedence and rotation rules).</summary>
    public static SceneThemePlan For(
        SceneShot shot, IReadOnlyList<string>? regionCurated, Func<string, string?> canonical, string fallback)
    {
        var rotation = SceneThemeSchedule.Rotation(shot, regionCurated, canonical);
        return rotation.Count > 0
            ? new SceneThemePlan(rotation, shot.ThemeRotateSeconds)
            : new SceneThemePlan(new[] { SceneThemeSchedule.ThemeAt(shot, regionCurated, canonical, fallback, 0) },
                shot.ThemeRotateSeconds);
    }
}
