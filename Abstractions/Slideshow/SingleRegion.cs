// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Slideshow/SingleRegion.cs
//
// #436 — "is this slideshow showing a single region?" When it is, leaving the
// region would only re-enter the same one: the engine would re-apply the view,
// restart the region's animation and cross-fade the region into itself. The
// engine keeps cycling themes in place instead.

using System;
using System.Collections.Generic;

namespace FracturingFog.Slideshow;

public static class SingleRegion
{
    /// <summary>True when <paramref name="region"/> is the only region the
    /// slideshow can show next: the region is locked, a travel course visits only
    /// it, or (no course) the filtered pool holds just that region.</summary>
    public static bool StaysOn(string region, bool locked,
        IReadOnlyList<string>? travelCourse, IReadOnlyList<string>? pool)
    {
        if (locked) return true;
        if (travelCourse != null)
        {
            if (travelCourse.Count == 0) return false;
            foreach (var stop in travelCourse)
                if (!string.Equals(stop, region, StringComparison.Ordinal)) return false;
            return true;
        }
        return pool != null && pool.Count == 1 && string.Equals(pool[0], region, StringComparison.Ordinal);
    }
}
