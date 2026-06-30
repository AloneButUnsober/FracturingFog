using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Factory helpers that turn a saved <see cref="AnimationData"/>
/// into runtime <see cref="IParameterAnimator"/> instances bound to a live
/// <see cref="FracturingFog.Models.FractalParameters"/> target via
/// reflection. Tracks whose <see cref="AnimationTrack.ParamName"/> doesn't
/// resolve to a public read/write property of the expected CLR type are
/// silently skipped — the bus tolerates missing params so an animation
/// authored on Julia can play on Phoenix (or vice versa) without
/// throwing, just animating only the params both types share.</summary>
public static class AnimationDataExtensions
{
    /// <summary>Build one animator per track. Skips tracks whose param
    /// doesn't exist on the target type or whose CLR type isn't supported
    /// (only <c>double</c>, <c>int</c>, <c>Complex</c> today).</summary>
    public static IEnumerable<IParameterAnimator> ToAnimators(
        this AnimationData data,
        object target)
    {
        if (data == null || target == null) yield break;

        var t = target.GetType();
        foreach (var track in data.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.ParamName)) continue;

            var prop = t.GetProperty(
                track.ParamName,
                BindingFlags.Public | BindingFlags.Instance);

            if (prop == null || !prop.CanRead || !prop.CanWrite) continue;

            if (prop.PropertyType == typeof(double))
            {
                yield return new DoubleProceduralAnimator(
                    track,
                    v => prop.SetValue(target, v));
            }
            else if (prop.PropertyType == typeof(int))
            {
                yield return new IntProceduralAnimator(
                    track,
                    v => prop.SetValue(target, v));
            }
            else if (prop.PropertyType == typeof(Complex))
            {
                yield return new ComplexProceduralAnimator(
                    track,
                    c => prop.SetValue(target, c));
            }
            // Unsupported CLR type → silently skip. Adding a new Kind
            // (Vec3, Color, …) means adding a concrete ProceduralAnimator
            // subclass and a branch here.
        }
    }
}
