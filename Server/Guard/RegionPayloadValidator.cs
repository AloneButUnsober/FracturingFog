// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Guard/RegionPayloadValidator.cs
// Defensive shape-check on a client-supplied FractalRegion JSON blob before
// the engine deserializes it. Stays in Server (no Models reference) by
// parsing through JsonDocument and rejecting unsafe values.
//
// What this guards against:
//   • Oversize blobs (regions are tiny — well under 2 KB — so a 16 KB cap is
//     generous and protects against accidental megabyte-sized payloads).
//   • Regions whose FractalType is on the blocked list (UserEquation,
//     Sandbox, UserBulb) — those run user-authored code and must never reach
//     the engine, region-wrapped or otherwise.
//   • Regions that smuggle user-authored code fields even when the declared
//     FractalType looks safe: UserBulbSource / UserEquationName /
//     SandboxName / UserBulbName must all be absent or null/empty. A region
//     tagged Mandelbrot that carries a non-empty UserBulbSource is still
//     refused — the field has no business on a non-3D region anyway, and
//     accepting it would let an attacker shadow the FractalType check by
//     swapping engines downstream.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FracturingFog.Server.Guard;

public static class RegionPayloadValidator
{
    /// <summary>Maximum bytes a region JSON blob may occupy. Real regions
    /// serialize to well under 2 KB even with quad-precision limbs;
    /// 16 KB closes any reasonable headroom argument.</summary>
    public const int MaxBytes = 16 * 1024;

    private static readonly HashSet<string> BlockedFractalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserEquation", "Sandbox", "UserBulb",
    };

    /// <summary>Fields whose presence on the wire indicates a user-authored
    /// code path. Refused regardless of declared fractal type so an attacker
    /// cannot smuggle them past the FractalType allowlist by labelling the
    /// region "Mandelbrot".</summary>
    private static readonly string[] ForbiddenFieldsCi =
    {
        "userBulbSource", "userEquationName", "sandboxName", "userBulbName",
    };

    /// <summary>Throws <see cref="ServerProtocolException"/> with code
    /// "bad-region-payload" or "forbidden-fractal" when refused.</summary>
    public static void Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ServerProtocolException("bad-region-payload", "regionJson is empty");

        int byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxBytes)
            throw new ServerProtocolException("bad-region-payload",
                $"regionJson is {byteCount} bytes (limit {MaxBytes})");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ServerProtocolException("bad-region-payload",
                $"regionJson is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ServerProtocolException("bad-region-payload",
                    "regionJson root must be a JSON object");

            // FractalType (case-insensitive) must be present and not on the
            // blocked list. Missing → bad-request (the engine needs a type).
            string? ftype = null;
            if (TryGetCi(doc.RootElement, "FractalType", out var ftEl) &&
                ftEl.ValueKind == JsonValueKind.String)
            {
                ftype = ftEl.GetString();
            }
            if (string.IsNullOrWhiteSpace(ftype))
                throw new ServerProtocolException("bad-region-payload",
                    "regionJson is missing FractalType");
            if (BlockedFractalTypes.Contains(ftype))
                throw new ServerProtocolException("forbidden-fractal",
                    $"regionJson FractalType '{ftype}' is not permitted for remote rendering");

            // Refuse any non-empty user-authored-code field, no matter what
            // FractalType the region claims.
            foreach (string forbidden in ForbiddenFieldsCi)
            {
                if (TryGetCi(doc.RootElement, forbidden, out var fEl))
                {
                    if (fEl.ValueKind == JsonValueKind.String)
                    {
                        string? v = fEl.GetString();
                        if (!string.IsNullOrEmpty(v))
                            throw new ServerProtocolException("forbidden-fractal",
                                $"regionJson carries '{forbidden}' which is reserved for user-authored code");
                    }
                    else if (fEl.ValueKind != JsonValueKind.Null)
                    {
                        throw new ServerProtocolException("bad-region-payload",
                            $"regionJson '{forbidden}' must be a string or null");
                    }
                }
            }
        }
    }

    private static bool TryGetCi(JsonElement obj, string name, out JsonElement el)
    {
        if (obj.TryGetProperty(name, out el)) return true;
        // System.Text.Json default policy is camelCase on the wire; check the
        // pascal-case form too so regions serialized via either convention
        // validate uniformly.
        string alt = char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : char.ToUpperInvariant(name[0]) + name[1..];
        return obj.TryGetProperty(alt, out el);
    }
}
