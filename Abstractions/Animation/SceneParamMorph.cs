// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneParamMorph.cs
//
// Scene Engine Roadmap — Phase S8: the ParamMorph transition's numeric core.
//
// A ParamMorph shot doesn't composite two rendered frames (like Crossfade /
// LightSweep); it renders the incoming shot with its fractal params
// interpolated from the outgoing shot's, so the *shape itself* morphs across
// the window. This is only meaningful when both shots are the same fractal type
// (the offline renderer guards that and falls back to a crossfade otherwise) —
// two types share no comparable param space.
//
// The interpolation is a plain component-wise lerp over every public read/write
// double property of FractalParameters: those are the continuous shape knobs
// (BulbPower, MandelboxScale, PlasmaRoughness, the per-type camera scalars, …).
// Non-double state (enums, source strings, collections, the fractal type) is
// taken from the incoming shot unchanged — it can't be meaningfully blended, so
// the morph carries the outgoing shot's continuous knobs into the incoming
// shot's discrete configuration. Pure + deterministic + unit-tested.

using System.Linq;
using System.Reflection;

using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation;

public static class SceneParamMorph
{
    // Public instance double properties, resolved once. These are the params a
    // morph can continuously blend.
    private static readonly PropertyInfo[] DoubleProps = typeof(FractalParameters)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(double) && p.CanRead && p.CanWrite)
        .ToArray();

    /// <summary>Build a params record whose continuous (double) knobs are lerped
    /// from <paramref name="from"/> to <paramref name="to"/> by <paramref name="t"/>
    /// (0 = fully <paramref name="from"/>, 1 = fully <paramref name="to"/>), on
    /// top of a clone of <paramref name="to"/> for all non-blendable state.
    /// <paramref name="t"/> is not clamped — callers pass a normalised progress.</summary>
    public static FractalParameters Lerp(FractalParameters from, FractalParameters to, double t)
    {
        var result = to.Clone();
        if (from == null) return result;

        foreach (var p in DoubleProps)
        {
            double a = (double)p.GetValue(from)!;
            double b = (double)p.GetValue(to)!;
            if (a == b) continue; // nothing to blend — leave the clone's value
            p.SetValue(result, a + (b - a) * t);
        }
        return result;
    }
}
