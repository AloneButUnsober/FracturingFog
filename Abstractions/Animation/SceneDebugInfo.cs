// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneDebugInfo.cs
//
// #1051 — the text of the live scene debug overlay: scene clock, the current
// shot (index / name / local time / transition), what the shot pulls in
// (region / theme / animation / lighting), and the camera — which key segment
// is playing, its timing / ease, and the live pose. Pure, so it is unit-tested
// without a UI; the shell feeds it on every scene tick and a tethered HUD window
// shows it.

using System;
using System.Globalization;
using System.Text;

using FracturingFog.Render;

namespace FracturingFog.Abstractions.Animation;

public static class SceneDebugInfo
{
    /// <summary>The camera key segment playing at <paramref name="time"/> in a
    /// track (looped over its duration, as playback does).</summary>
    /// <returns>(from-key index, to-key index, segment start, segment end,
    /// looped time); from == to when the track has a single key.</returns>
    public static (int From, int To, double Start, double End, double Time) Segment(CameraTrack track, double time)
    {
        ArgumentNullException.ThrowIfNull(track);
        var keys = track.Keys;
        if (keys.Count == 0) return (-1, -1, 0, 0, 0);
        double dur = track.Duration;
        double t = time;
        if (dur > 0) t -= System.Math.Floor(t / dur) * dur;
        if (keys.Count == 1 || t <= keys[0].Time) return (0, keys.Count == 1 ? 0 : 1, keys[0].Time, keys.Count == 1 ? keys[0].Time : keys[1].Time, t);
        for (int i = 0; i < keys.Count - 1; i++)
            if (t < keys[i + 1].Time) return (i, i + 1, keys[i].Time, keys[i + 1].Time, t);
        int last = keys.Count - 1;
        return (last - 1, last, keys[last - 1].Time, keys[last].Time, t);
    }

    /// <param name="scene">The playing scene (or a one-shot wrapper for Preview).</param>
    /// <param name="sample">Timeline sample at <paramref name="clock"/>.</param>
    /// <param name="clock">Global scene time in seconds.</param>
    /// <param name="totalSeconds">Timeline length (0 = unknown, e.g. Preview).</param>
    /// <param name="liveCamera">The live camera pose, when the shot drives one.</param>
    /// <param name="reliefCamera">The pose is the Relief 3D oblique camera.</param>
    /// <param name="theme">The theme the live view is showing (null = unknown).</param>
    /// <param name="frameMs">Last frame's render time, if known.</param>
    public static string Format(SceneData scene, SceneSample sample, double clock, double totalSeconds,
        CameraState? liveCamera = null, bool reliefCamera = false, string? theme = null, double? frameMs = null,
        bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.Append(preview ? "Preview  " : "Scene  ").Append(Q(scene.Name));
        if (!preview)
        {
            sb.Append(ic, $"   t {clock:0.00}");
            if (totalSeconds > 0) sb.Append(ic, $" / {totalSeconds:0.00} s");
        }
        if (frameMs is double ms) sb.Append(ic, $"   frame {ms:0} ms");
        sb.AppendLine();

        int idx = sample.OriginalIndex;
        if (idx < 0 || idx >= scene.Shots.Count)
        {
            sb.Append("(no shot)");
            return sb.ToString();
        }
        var shot = scene.Shots[idx];

        sb.Append(ic, $"Shot {idx + 1}/{scene.Shots.Count}  {Q(shot.Name)}");
        sb.Append(ic, $"   local {sample.LocalTime:0.00} / {shot.DurationSeconds:0.00} s");
        if (sample.OutgoingEntry >= 0 && sample.Blend > 0 && sample.Blend < 1)
            sb.Append(ic, $"   {shot.Transition} {sample.Blend * 100:0}%");
        else if (idx > 0 && shot.Transition != SceneTransitionKind.Cut)
            sb.Append(ic, $"   in: {shot.Transition} {shot.TransitionSeconds:0.##} s");
        sb.AppendLine();

        sb.Append("Region ").Append(Or(shot.RegionName, "(default " + shot.FractalType + ")"));
        sb.Append("   Theme ").Append(theme ?? Or(shot.ThemeName, "(region)"));
        if (shot.RotateThemes) sb.Append(ic, $" (rotating /{shot.ThemeRotateSeconds:0.##} s)");
        sb.AppendLine();
        sb.Append("Anim ").Append(Or(shot.AnimationName, "(region)"));
        sb.Append("   Light ").Append(
            !string.IsNullOrWhiteSpace(shot.LightingPresetName)
                ? (shot.LightingPresetIsBuiltIn ? "built-in " : "preset ") + Q(shot.LightingPresetName)
                : !string.IsNullOrWhiteSpace(shot.LightingRegionName) ? "region " + Q(shot.LightingRegionName) : "(region)");
        if (shot.ToneMap is { } tm) sb.Append("   Tone ").Append(tm);
        sb.AppendLine();

        var cam = shot.Camera;
        if (cam is { Keys.Count: > 0 })
        {
            var (from, to, start, end, t) = Segment(cam, sample.LocalTime);
            sb.Append(ic, $"Cam {(reliefCamera ? "Relief 3D " : "")}key {from + 1}->{to + 1} of {cam.Keys.Count}");
            sb.Append(ic, $"   {start:0.00}-{end:0.00} s ({end - start:0.00} s)   at {t:0.00}");
            sb.Append("   ").Append(cam.Interpolation).Append(", ease ").Append(cam.Keys[from].Ease);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Cam (no keys)");
        }

        if (liveCamera is { } c)
        {
            if (reliefCamera)
                sb.Append(ic, $"Pose zoom {1.0 / System.Math.Max(1e-9, c.Distance):0.000}   az {Deg(c.Theta):0.0}°   el {Deg(c.Phi):0.0}°");
            else
                sb.Append(ic, $"Pose dist {c.Distance:0.000}   az {Deg(c.Theta):0.0}°   el {Deg(c.Phi):0.0}°");
        }
        return sb.ToString().TrimEnd();
    }

    private static double Deg(double rad) => rad * 180.0 / System.Math.PI;
    private static string Q(string? s) => string.IsNullOrWhiteSpace(s) ? "(unnamed)" : "\"" + s + "\"";
    private static string Or(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : Q(s);
}
