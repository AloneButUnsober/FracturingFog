// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/RegisteredFractalCatalog.cs
//
// Aggregates promoted equations from SandboxEquationStore and UserEquationStore
// into a single ordered list of "first-class" fractal entries. The fractal-type
// dropdown appends these below the hard-coded FractalType enum entries so a
// saved equation can be selected directly without opening the equation dialog.
//
// Selection of a registered entry switches the active FractalType to the
// appropriate engine (Sandbox or UserEquation) and loads the entry's source
// into FractalParameters.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Which calculator engine evaluates a registered equation. Determines
    /// which FractalParameters source slot (SandboxSource / UserEquationSource)
    /// the entry feeds, and which FractalType the renderer dispatches to.
    /// </summary>
    public enum EquationEngine
    {
        Sandbox,
        UserEquation,
        UserBulb,
    }

    /// <summary>
    /// One promoted equation surfaced as a top-level fractal type. Wraps the
    /// underlying store entry without duplicating its source — the catalog is
    /// a thin projection.
    /// </summary>
    public sealed class RegisteredFractal
    {
        public string Name { get; init; } = string.Empty;
        public EquationEngine Engine { get; init; }
        public string Source { get; init; } = string.Empty;

        public FractalType Type => Engine switch
        {
            EquationEngine.Sandbox      => FractalType.Sandbox,
            EquationEngine.UserEquation => FractalType.UserEquation,
            EquationEngine.UserBulb     => FractalType.UserBulb,
            _ => FractalType.Sandbox,
        };
    }

    public static class RegisteredFractalCatalog
    {
        /// <summary>
        /// Enumerates all promoted entries from both stores. Sandbox entries
        /// come first (cheaper, safer engine), then UserEquation entries.
        /// Within each engine, entries appear in insertion order.
        /// </summary>
        public static IEnumerable<RegisteredFractal> All
        {
            get
            {
                foreach (var e in SandboxEquationStore.Instance.Equations)
                    if (e.Promoted)
                        yield return new RegisteredFractal
                        {
                            Name = e.Name,
                            Engine = EquationEngine.Sandbox,
                            Source = e.Source,
                        };

                foreach (var e in UserEquationStore.Instance.Equations)
                    if (e.Promoted)
                        yield return new RegisteredFractal
                        {
                            Name = e.Name,
                            Engine = EquationEngine.UserEquation,
                            Source = e.Source,
                        };

                foreach (var e in UserBulbStore.Instance.Equations)
                    if (e.Promoted)
                        yield return new RegisteredFractal
                        {
                            Name = e.Name,
                            Engine = EquationEngine.UserBulb,
                            Source = e.Source,
                        };
            }
        }

        /// <summary>Materialised snapshot. Stable index ordering for UI use.</summary>
        public static List<RegisteredFractal> Snapshot()
        {
            var list = new List<RegisteredFractal>();
            foreach (var r in All) list.Add(r);
            return list;
        }

        /// <summary>
        /// Finds a registered entry by case-insensitive name. Returns null
        /// when no promoted entry matches.
        /// </summary>
        public static RegisteredFractal? GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var r in All)
                if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }

        /// <summary>
        /// True when any promoted entry exists in either store. Cheap; lets
        /// the UI skip rebuilding the fractal dropdown when nothing changed.
        /// </summary>
        public static bool HasAny()
        {
            foreach (var _ in All) return true;
            return false;
        }
    }
}
