// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Guard against "orphaned" colour maps: concrete IColorMap themes that exist in
// the Engine assembly but were never added to ColorPalette.BuiltIns (a hardcoded
// `new X()` list with no reflection auto-discovery), so they never appear in the
// Color Theme Editor / slideshow.
//
// A type is considered a *catalog* theme (and therefore must be registered) when
// it is a concrete IColorMap with a public parameterless constructor and does
// NOT implement INamedColorMap.  INamedColorMap is the marker for dynamic /
// data-driven themes (one runtime type carrying many named themes: user JSON
// themes, DataDriven*, BlendedColorMap) which are added at runtime, not baked
// into BuiltIns.  A small explicit allow-list covers any remaining infra type
// that is intentionally never a standalone catalog entry.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FracturingFog.Interefaces;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests
{
    public class ColorMapRegistrationGuardTests
    {
        // Infra / dynamic IColorMap types that are intentionally NOT standalone
        // BuiltIns catalog entries.  Add here (with a reason) only when a type is
        // genuinely not meant to be user-selectable on its own.
        private static readonly HashSet<string> IntentionallyUnregistered = new()
        {
            // Deliberately commented out in ColorPalette.BuiltIns (see the
            // "//new DistanceGlowMap()," line). Theme "Distance Enhanced" was
            // disabled by hand — kept out here rather than silently resurrected;
            // the registered DistanceFieldGlowMap covers the distance-glow slot.
            // Remove from this set (and uncomment in BuiltIns) to re-enable.
            "DistanceGlowMap",
        };

        [Fact]
        public void EveryCatalogColorMap_IsRegisteredInBuiltIns()
        {
            var registeredTypes = ColorPalette.BuiltIns.Select(m => m.GetType()).ToHashSet();

            var catalogTypes = typeof(ColorPalette).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => typeof(IColorMap).IsAssignableFrom(t))
                // Dynamic / data-driven themes register at runtime, not in BuiltIns.
                .Where(t => !typeof(INamedColorMap).IsAssignableFrom(t))
                // Must be constructable as `new X()` to live in the BuiltIns list.
                .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
                .Where(t => !IntentionallyUnregistered.Contains(t.Name))
                .ToList();

            var orphans = catalogTypes
                .Where(t => !registeredTypes.Contains(t))
                .Select(t => t.Name)
                .OrderBy(n => n)
                .ToList();

            Assert.True(orphans.Count == 0,
                "Colour map(s) defined but not registered in ColorPalette.BuiltIns " +
                "(they will never appear in the UI). Add them to BuiltIns, or add " +
                "to IntentionallyUnregistered with a reason:\n  " +
                string.Join("\n  ", orphans));
        }
    }
}
