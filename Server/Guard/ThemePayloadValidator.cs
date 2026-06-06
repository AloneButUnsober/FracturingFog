// Server/Guard/ThemePayloadValidator.cs
// Defensive shape-check on a client-supplied ColorThemeData JSON blob before
// the engine deserializes it. The full ColorThemeData type lives in the main
// exe (System.Drawing dependency) and is not referenced by Server; this
// validator works on the raw JSON via JsonDocument so the Server assembly
// stays decoupled. Green-lights the blob for the engine to fully parse.
//
// What this guards against:
//   • Oversize blobs (DoS — server allocates a fully-formed theme graph per
//     request, with light/PBR sub-objects and a stops list).
//   • Unknown discriminator values (Kind must be one of the four documented
//     ColorThemeKind names).
//   • Pathological structures: non-object root, gigantic Stops list, etc.
//
// Themes are otherwise data-driven and contain no executable surface, so a
// successful Validate() implies the JSON is safe to hand to the engine.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FracturingFog.Server.Guard;

public static class ThemePayloadValidator
{
    /// <summary>Maximum bytes a theme JSON blob may occupy. Real themes
    /// emitted by the user library cap out around 4-6 KB; 64 KB leaves
    /// plenty of headroom for PBR multi-band themes while keeping the
    /// allocation budget bounded.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Maximum number of gradient stops accepted on the wire.
    /// In-product themes peak around 16 stops; 256 covers any sane editor
    /// output and rejects pathological "ship a million stops" payloads.</summary>
    public const int MaxStops = 256;

    /// <summary>Maximum number of PBR material bands accepted on the wire.</summary>
    public const int MaxPbrBands = 64;

    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gradient", "Cycling", "Phong3D", "Pbr3D",
    };

    /// <summary>Throws <see cref="ServerProtocolException"/> with code
    /// "bad-theme-payload" if the blob is not safe to hand to the engine.</summary>
    public static void Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ServerProtocolException("bad-theme-payload", "themeJson is empty");

        int byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxBytes)
            throw new ServerProtocolException("bad-theme-payload",
                $"themeJson is {byteCount} bytes (limit {MaxBytes})");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ServerProtocolException("bad-theme-payload",
                $"themeJson is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ServerProtocolException("bad-theme-payload",
                    "themeJson root must be a JSON object");

            // Kind discriminator must be one of the four enum values.
            // Missing Kind defaults to Gradient (matches ColorThemeData default).
            if (doc.RootElement.TryGetProperty("Kind", out var kindEl) ||
                doc.RootElement.TryGetProperty("kind", out kindEl))
            {
                if (kindEl.ValueKind == JsonValueKind.String)
                {
                    string? k = kindEl.GetString();
                    if (k != null && !AllowedKinds.Contains(k))
                        throw new ServerProtocolException("bad-theme-payload",
                            $"themeJson Kind='{k}' is not one of Gradient/Cycling/Phong3D/Pbr3D");
                }
            }

            if (doc.RootElement.TryGetProperty("Stops", out var stopsEl) ||
                doc.RootElement.TryGetProperty("stops", out stopsEl))
            {
                if (stopsEl.ValueKind == JsonValueKind.Array && stopsEl.GetArrayLength() > MaxStops)
                    throw new ServerProtocolException("bad-theme-payload",
                        $"themeJson Stops has {stopsEl.GetArrayLength()} entries (limit {MaxStops})");
            }

            if (doc.RootElement.TryGetProperty("MaterialBands", out var bandsEl) ||
                doc.RootElement.TryGetProperty("materialBands", out bandsEl))
            {
                if (bandsEl.ValueKind == JsonValueKind.Array && bandsEl.GetArrayLength() > MaxPbrBands)
                    throw new ServerProtocolException("bad-theme-payload",
                        $"themeJson MaterialBands has {bandsEl.GetArrayLength()} entries (limit {MaxPbrBands})");
            }
        }
    }
}
