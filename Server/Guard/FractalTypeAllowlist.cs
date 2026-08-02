// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Guard/FractalTypeAllowlist.cs
// Refuses fractal types that execute user-authored step functions over the
// network. #27 Phase 3 moved UserEquation / Sandbox / UserBulb onto safe DSL
// interpreters (no BCL surface, no Roslyn), so the raw-C# RCE primitive is
// gone — but these types still run arbitrary user-supplied step math and
// unbounded iteration budgets, so they stay blocked as defense-in-depth
// (network content is untrusted; no reason to accept a user step function
// from a remote peer).

using System;
using System.Collections.Generic;

namespace FracturingFog.Server.Guard;

public static class FractalTypeAllowlist
{
    private static readonly HashSet<FractalType> Blocked = new()
    {
        FractalType.UserEquation,
        FractalType.Sandbox,
        FractalType.UserBulb,
    };

    public static bool IsAllowed(FractalType t) => !Blocked.Contains(t);

    public static bool IsAllowed(string name, out FractalType parsed)
    {
        if (!Enum.TryParse(name, ignoreCase: true, out parsed))
            return false;
        return IsAllowed(parsed);
    }

    public static IReadOnlyCollection<FractalType> BlockedTypes => Blocked;
}
