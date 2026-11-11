// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneShotScript.cs
//
// #1700 (scripted scenes SS6, #1557) — a Scene Engine shot on a Math Plane region
// plays the region's script on shot-local time: MathPlaneScriptTime = the shot's
// local time, in the offline export (SceneVideoRenderer) and the live player
// (ShellViewModel's scene tick) alike. SceneShot.PlayScript turns it off. The
// shot's default length is the script's duration. Design:
// Docs/Technical/MathPlane-ScriptedScenes-Plan.md §3.7.

using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation;

public static class SceneShotScript
{
    /// <summary>The scene text has a script (one or more valid step / with lines).</summary>
    public static bool HasScript(string? sceneText)
        => MathPlaneScene.TryParse(sceneText, out var scene, out _) && scene.Steps.Count > 0;

    /// <summary>The script's duration in seconds; 0 without a (valid) script.</summary>
    public static double ScriptSeconds(string? sceneText)
        => MathPlaneScene.TryParse(sceneText, out var scene, out _) ? MathPlaneScript.Duration(scene) : 0;

    /// <summary>Whether <paramref name="shot"/>, rendering <paramref name="renderType"/> with
    /// <paramref name="sceneText"/>, plays the scene's script.</summary>
    public static bool Plays(SceneShot shot, FractalType renderType, string? sceneText)
        => shot.PlayScript && renderType == FractalType.MathPlane && HasScript(sceneText);

    /// <summary>The script time at shot-local <paramref name="localTime"/> (clamped to ≥ 0),
    /// or null when the shot doesn't play a script.</summary>
    public static double? TimeFor(SceneShot shot, FractalType renderType, string? sceneText, double localTime)
        => Plays(shot, renderType, sceneText) ? System.Math.Max(0, localTime) : null;
}
