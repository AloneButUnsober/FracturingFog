// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SkiaSharpVersion.cs
//
// SkiaSharp dropped the static SKVersion descriptor class somewhere in the
// 2.x line and never restored it through 3.x. This helper resolves the
// loaded SkiaSharp assembly's informational version at runtime so the
// renderer description still names the binary that actually shipped —
// useful when triaging native-library mismatches across publish RIDs.

using System.Reflection;
using SkiaSharp;

namespace FracturingFog.Rendering.Skia;

internal static class SkiaSharpVersion
{
    private static string? _cached;

    public static string Describe()
    {
        if (_cached != null) return _cached;
        try
        {
            var asm = typeof(SKBitmap).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            _cached = info?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "?";
        }
        catch
        {
            _cached = "?";
        }
        return _cached;
    }
}
