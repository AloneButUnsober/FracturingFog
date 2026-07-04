// Abstractions/Animation/SceneData.cs
//
// Scene Engine Roadmap — Phase S4: the Scene asset.
//
// A Scene is an ordered sequence of shots — the cinematic layer above single
// regions / animations. Each shot names the assets it renders (region, theme,
// param-animation) by string, exactly the way AnimationTrack names a param by
// string: loose coupling, so a Scene serialises without embedding a copy of
// every asset it points at, and a renamed / missing asset degrades to a
// resolve-time fallback rather than a load-time crash. On top of that a shot
// carries the genuinely new authored thing from S3 — an optional CameraTrack —
// plus its own duration and the transition that brings it in from the shot
// before it.
//
// Pure DTO. Persistence is SceneLibrary (scenes.json); playback is S6.

using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Render;

namespace FracturingFog.Abstractions.Animation;

/// <summary>How a shot enters from the shot before it. The concrete blend is
/// S6 (it extends the existing <c>SlideshowEngine</c> cross-fade); on disk in
/// S4 this is authored intent.</summary>
public enum SceneTransitionKind
{
    /// <summary>Hard cut — the previous shot ends, this one begins on the next
    /// frame. <see cref="SceneShot.TransitionSeconds"/> is ignored.</summary>
    Cut,
    /// <summary>Alpha cross-fade over <see cref="SceneShot.TransitionSeconds"/>,
    /// reusing the slideshow cross-fade machinery.</summary>
    Crossfade,
    /// <summary>A directional light-sweep wipe — a lighting-aware transition for
    /// the 3D shots. Falls back to a cross-fade where lighting is off.</summary>
    LightSweep,
    /// <summary>Morph the fractal params of the outgoing shot into the incoming
    /// shot's over <see cref="SceneShot.TransitionSeconds"/> (only meaningful
    /// when both shots share a fractal type). Falls back to a cross-fade.</summary>
    ParamMorph,
}

/// <summary>One shot in a <see cref="SceneData"/>: what to render, for how long,
/// and how it arrives. Assets are referenced by name (resolved at play time
/// against the region / theme / animation libraries), so a Scene is a thin
/// cinematic script, not a bundle of copied assets.</summary>
public sealed class SceneShot
{
    /// <summary>Optional shot label shown in the editor timeline. Not a library
    /// key — shots are ordered, not named-addressed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the region asset that supplies the base params
    /// (position, iterations, fractal type, lighting, theme). Empty = render
    /// the current default params for <see cref="FractalType"/> — used by the
    /// self-contained built-in camera demos that need no library lookup.</summary>
    public string RegionName { get; set; } = string.Empty;

    /// <summary>Optional colour-theme override. Null / empty = the region's own
    /// theme.</summary>
    public string? ThemeName { get; set; }

    /// <summary>Optional param-animation override (an <see cref="AnimationData"/>
    /// name). Null / empty = the region's own <c>AnimationName</c>, or none.</summary>
    public string? AnimationName { get; set; }

    /// <summary>The fractal type this shot renders. Normally mirrors the named
    /// region's type; kept explicit so a <see cref="Camera"/> track can be
    /// validated against <see cref="Render.CameraParamBinding"/> without first
    /// resolving the region.</summary>
    public FractalType FractalType { get; set; } = FractalType.Mandelbrot;

    /// <summary>Optional keyframed orbit camera (S3). Only meaningful for the 3D
    /// raymarch types (<see cref="Render.CameraParamBinding.Supports"/>); null
    /// for 2D shots, which have no orbit camera to drive.</summary>
    public CameraTrack? Camera { get; set; }

    /// <summary>Shot length in seconds (the camera track and param animation
    /// play across this window). Must be &gt; 0 to contribute to the scene.</summary>
    public double DurationSeconds { get; set; } = 5.0;

    /// <summary>How this shot enters from the previous one. The first shot's
    /// transition is ignored (nothing precedes it).</summary>
    public SceneTransitionKind Transition { get; set; } = SceneTransitionKind.Crossfade;

    /// <summary>Transition length in seconds. Overlaps into the tail of the
    /// previous shot (S6); ignored for <see cref="SceneTransitionKind.Cut"/>.</summary>
    public double TransitionSeconds { get; set; } = 1.0;
}

/// <summary>Top-level Scene asset — an ordered list of <see cref="SceneShot"/>.
/// Persisted to <c>%APPDATA%/FracturingFog/scenes.json</c> via
/// <c>SceneLibrary</c>; edited by the S5 <c>SceneEditorView</c>; played by S6.
/// Mirrors <see cref="AnimationData"/>'s shape (name key + category + tags) so
/// it slots into the Asset Manager the same way.</summary>
public sealed class SceneData
{
    /// <summary>User-visible name. Library key (case-insensitive).</summary>
    public string Name { get; set; } = "Unnamed Scene";

    /// <summary>Optional human-readable description shown in the editor.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>"User" by default; built-in demo scenes ship with "Built-in".
    /// Display-only.</summary>
    public string Category { get; set; } = "User";

    /// <summary>The shots, in play order.</summary>
    public List<SceneShot> Shots { get; set; } = new();

    /// <summary>Scene-wide keyframed post/look scalars (S8 "global tracks"),
    /// sampled at GLOBAL scene time and applied on top of every shot — an
    /// exposure ramp, a bloom swell, a closing vignette across the whole scene.
    /// Empty = no scene-wide override, so the scene renders exactly as its shots
    /// dictate. See <see cref="SceneGlobalTrack"/>.</summary>
    public List<SceneGlobalTrack> GlobalTracks { get; set; } = new();

    /// <summary>Free-form tags for the Asset Manager / slideshow filter UI
    /// ("demo", "3D", "calm", …). Case-sensitive.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Total length in seconds — the sum of the shots'
    /// <see cref="SceneShot.DurationSeconds"/>. Transition overlaps are an S6
    /// playback concern; the authored total is the plain sum. Not serialised
    /// (computed).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double TotalDurationSeconds
    {
        get
        {
            double total = 0.0;
            foreach (var s in Shots)
                if (s.DurationSeconds > 0) total += s.DurationSeconds;
            return total;
        }
    }
}
