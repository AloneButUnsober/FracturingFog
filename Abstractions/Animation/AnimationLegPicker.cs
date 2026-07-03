using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog;

namespace FracturingFog.Abstractions.Animation;

/// <summary>
/// Pure animation-selection logic for an Animation-type slideshow leg. Lives
/// in Abstractions (no Avalonia / render dependency) so it can be unit-tested
/// head-lessly. The slideshow engine wraps it with live library / region
/// lookups from <c>IColorThemeService</c>.
/// <para>
/// Selection rule (Animation Roadmap Phase 4):
/// </para>
/// <list type="number">
///   <item>If the region carries an attached animation and
///     <c>randomizeByType</c> is false, that animation wins (assumed authored
///     for the region) — provided it survives the include/tag filters.</item>
///   <item>Otherwise pick a random animation from the library that is
///     compatible with the region's fractal type
///     (<c>TargetFractalTypes</c> empty = unconstrained, else must contain the
///     type), and survives the include whitelist + tag filter.</item>
///   <item>If nothing qualifies, return null — the engine falls back to a
///     static (non-animated) leg.</item>
/// </list>
/// </summary>
public static class AnimationLegPicker
{
    /// <summary>A library animation reduced to just the fields the picker
    /// needs. The engine materialises these from
    /// <c>IColorThemeService.GetAnimation</c>.</summary>
    public readonly record struct Candidate(
        string Name,
        IReadOnlyList<FractalType> TargetTypes,
        IReadOnlyList<string> Tags);

    /// <summary>Choose the animation for a leg, or null for a static leg.</summary>
    /// <param name="library">Every animation in the user's library.</param>
    /// <param name="regionFractalTypeName">Serialized enum name of the region's
    /// fractal type (e.g. "Julia"). Empty/unparseable disables the compat
    /// filter (every animation is treated as compatible).</param>
    /// <param name="regionAttachedAnimation">Name of the animation the region
    /// carries, or null/empty when it has none.</param>
    /// <param name="randomizeByType">When true, ignore the region's attached
    /// animation and always draw a random compatible one.</param>
    /// <param name="includedAnimations">Whitelist of animation names;
    /// null/empty = all eligible.</param>
    /// <param name="filterTags">Tag filter; null/empty = no tag filter.
    /// An animation survives when it shares at least one tag.</param>
    /// <param name="nextRandom">Bounded RNG: given a positive count, returns an
    /// index in [0, count). Injected so tests are deterministic.</param>
    public static string? Pick(
        IReadOnlyList<Candidate> library,
        string? regionFractalTypeName,
        string? regionAttachedAnimation,
        bool randomizeByType,
        IReadOnlyList<string>? includedAnimations,
        IReadOnlyList<string>? filterTags,
        Func<int, int> nextRandom)
    {
        ArgumentNullException.ThrowIfNull(nextRandom);
        if (library == null || library.Count == 0) return null;

        FractalType? regionType = null;
        if (!string.IsNullOrWhiteSpace(regionFractalTypeName)
            && Enum.TryParse<FractalType>(regionFractalTypeName, out var parsed))
        {
            regionType = parsed;
        }

        var incSet = (includedAnimations != null && includedAnimations.Count > 0)
            ? new HashSet<string>(includedAnimations, StringComparer.OrdinalIgnoreCase)
            : null;
        var tagSet = (filterTags != null && filterTags.Count > 0)
            ? new HashSet<string>(filterTags, StringComparer.OrdinalIgnoreCase)
            : null;

        // Attached-animation fast path: only when not randomizing and the
        // region names an animation that exists and passes the filters.
        if (!randomizeByType && !string.IsNullOrWhiteSpace(regionAttachedAnimation))
        {
            foreach (var c in library)
            {
                if (!string.Equals(c.Name, regionAttachedAnimation, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Survives(c, regionType, incSet, tagSet))
                    return c.Name;
                break; // named but filtered out → fall through to random
            }
        }

        // Random-compatible path: collect survivors, pick one.
        List<string>? survivors = null;
        foreach (var c in library)
        {
            if (!Survives(c, regionType, incSet, tagSet)) continue;
            (survivors ??= new List<string>()).Add(c.Name);
        }

        if (survivors == null || survivors.Count == 0) return null;
        int i = nextRandom(survivors.Count);
        if (i < 0 || i >= survivors.Count) i = 0;
        return survivors[i];
    }

    private static bool Survives(
        Candidate c,
        FractalType? regionType,
        HashSet<string>? incSet,
        HashSet<string>? tagSet)
    {
        if (incSet != null && !incSet.Contains(c.Name)) return false;

        if (regionType.HasValue && c.TargetTypes != null && c.TargetTypes.Count > 0
            && !c.TargetTypes.Contains(regionType.Value))
            return false;

        if (tagSet != null)
        {
            bool anyTag = false;
            if (c.Tags != null)
            {
                foreach (var t in c.Tags)
                    if (tagSet.Contains(t)) { anyTag = true; break; }
            }
            if (!anyTag) return false;
        }

        return true;
    }
}
