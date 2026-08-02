// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Security/UserCodeSecurityPolicy.cs
//
// #27 Phase 0 — trust-boundary gate for user-authored code that reaches a
// Roslyn compile (UserEquation / UserBulb raw-C# step functions). These
// surfaces string-interpolate user text into a class template and compile it
// with full BCL references, so a hostile body is arbitrary in-process code
// execution. The DSL interpreters (SandboxExpression / SandboxBulbExpression /
// ColorGen) are unaffected — they never reach raw Roslyn — and are always
// allowed.
//
// The gate distinguishes WHERE the source came from (UserCodeOrigin) rather
// than blanket-refusing, because:
//   • Interactive editing (the user typed it and clicked Compile) is trusted.
//   • Built-in presets ship in the binary; all current UserBulbStore examples
//     are Roslyn C#, so they must keep compiling.
//   • Source that arrived by loading a region / scene / preset / theme file, or
//     the startup persisted-.cs scan, is UNTRUSTED — that is the file-borne RCE
//     vector this gate closes.
//
// Default mode `TrustedOnly` denies only ExternalFile, so interactive use and
// shipped presets are unchanged (no regression) while opening a hostile file no
// longer runs its code. Override globally via env FF_ROSLYN_USERCODE:
//   allow-all | trusted-only (default) | deny-all

using System;

namespace FracturingFog.Security;

/// <summary>Provenance of a user-code source string, used by
/// <see cref="UserCodeSecurityPolicy"/> to decide whether a raw-C# Roslyn
/// compile may proceed.</summary>
public enum UserCodeOrigin
{
    /// <summary>User typed it in the editor and asked to compile. Trusted.</summary>
    Interactive,
    /// <summary>App-shipped built-in preset (UserBulbStore, built-in themes).
    /// Trusted — ships in the binary.</summary>
    BuiltIn,
    /// <summary>Arrived by loading a region / scene / preset / theme file, or
    /// the startup persisted-calculator scan. Untrusted.</summary>
    ExternalFile,
}

/// <summary>Global policy for raw-C# user-code compilation.</summary>
public enum RoslynUserCodeMode
{
    /// <summary>Every origin may compile raw C#. Legacy behaviour; opt-in only.</summary>
    AllowAll,
    /// <summary>Interactive + BuiltIn may compile; ExternalFile is denied.
    /// Default.</summary>
    TrustedOnly,
    /// <summary>No raw-C# compile from any origin (DSL-only lockdown).</summary>
    DenyAll,
}

/// <summary>Outcome of a gate check. <see cref="Allowed"/> false carries a
/// user-facing <see cref="DenyReason"/> the caller surfaces via LastError.</summary>
public readonly record struct UserCodeGateResult(bool Allowed, string? DenyReason)
{
    public static UserCodeGateResult Allow() => new(true, null);
    public static UserCodeGateResult Deny(string reason) => new(false, reason);
}

public static class UserCodeSecurityPolicy
{
    private static RoslynUserCodeMode? s_override;

    /// <summary>Active policy. Falls back to the env-derived value
    /// (<c>FF_ROSLYN_USERCODE</c>) when not explicitly set. Settable so a host
    /// or test can pin it; assigning <c>null</c> via <see cref="ResetToEnv"/>
    /// restores env resolution.</summary>
    public static RoslynUserCodeMode Mode
    {
        get => s_override ?? ResolveFromEnv();
        set => s_override = value;
    }

    /// <summary>Drop any explicit override and resolve from the environment on
    /// the next read. Primarily for tests.</summary>
    public static void ResetToEnv() => s_override = null;

    private static RoslynUserCodeMode ResolveFromEnv()
    {
        string? raw = Environment.GetEnvironmentVariable("FF_ROSLYN_USERCODE");
        if (string.IsNullOrWhiteSpace(raw)) return RoslynUserCodeMode.TrustedOnly;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "allow-all":
            case "allowall":
            case "allow":
                return RoslynUserCodeMode.AllowAll;
            case "deny-all":
            case "denyall":
            case "deny":
                return RoslynUserCodeMode.DenyAll;
            case "trusted-only":
            case "trustedonly":
            case "trusted":
                return RoslynUserCodeMode.TrustedOnly;
            default:
                // Unknown value → fail safe to the default (do not silently
                // widen to AllowAll on a typo).
                return RoslynUserCodeMode.TrustedOnly;
        }
    }

    /// <summary>True when a raw-C# Roslyn compile from <paramref name="origin"/>
    /// is permitted under the active <see cref="Mode"/>.</summary>
    public static bool IsRoslynAllowed(UserCodeOrigin origin) => Mode switch
    {
        RoslynUserCodeMode.AllowAll => true,
        RoslynUserCodeMode.DenyAll => false,
        RoslynUserCodeMode.TrustedOnly => origin != UserCodeOrigin.ExternalFile,
        _ => origin != UserCodeOrigin.ExternalFile,
    };
}

/// <summary>Single chokepoint every raw-C# user-code compile site calls before
/// invoking Roslyn.</summary>
public static class UserCodeGate
{
    /// <summary>Check whether a raw-C# compile from <paramref name="origin"/>
    /// may proceed. On denial returns a user-facing reason pointing at the safe
    /// DSL path.</summary>
    public static UserCodeGateResult EnsureRoslynAllowed(UserCodeOrigin origin)
    {
        if (UserCodeSecurityPolicy.IsRoslynAllowed(origin))
            return UserCodeGateResult.Allow();

        string reason = origin == UserCodeOrigin.ExternalFile
            ? "Blocked: this equation arrived from an external file and uses raw C#, "
              + "which can run arbitrary code. Rewrite it in the safe expression DSL, "
              + "or set FF_ROSLYN_USERCODE=allow-all only if you trust the source."
            : "Blocked: raw-C# user code is disabled (FF_ROSLYN_USERCODE=deny-all). "
              + "Use the safe expression DSL instead.";
        return UserCodeGateResult.Deny(reason);
    }
}
