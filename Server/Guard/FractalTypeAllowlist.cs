// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Guard/FractalTypeAllowlist.cs
// Refuses fractal types that execute user-authored code or step functions.
// UserEquation runs C#-script-compiled expressions, Sandbox executes a
// user step function, UserBulb mixes ILGPU kernels with user parameters.
// All three would be RCE risks if accepted over the network.

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
