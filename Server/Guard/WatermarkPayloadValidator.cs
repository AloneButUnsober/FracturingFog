// Server/Guard/WatermarkPayloadValidator.cs
// Defensive shape-check on a client-supplied WatermarkDef JSON blob before
// the engine deserializes it. Mirrors RegionPayloadValidator's idiom: parse
// through JsonDocument, bound size, refuse pathological values.
//
// What this guards against:
//   • Oversize blobs (a real WatermarkDef serializes to under 512 B —
//     2 KB closes any reasonable headroom).
//   • Out-of-range placement / justify enum names (anything other than the
//     four placements / three justifies).
//   • Non-string Name / Text fields (would crash the legacy JsonSerializer
//     downstream; we want a clean protocol error).
//   • An over-long Text field (UI-realistic top-line is short; capping at
//     256 chars stops accidental "embed the whole manifest" payloads).

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace FracturingFog.Server.Guard;

public static class WatermarkPayloadValidator
{
    /// <summary>Maximum bytes a watermark JSON blob may occupy on the wire.</summary>
    public const int MaxBytes = 2 * 1024;

    /// <summary>Maximum characters the top-line <c>Text</c> may carry. Real
    /// watermarks are short ("Studio Brand" etc.); 256 caps accidents while
    /// allowing reasonable taglines.</summary>
    public const int MaxTextLength = 256;

    private static readonly HashSet<string> ValidPlacements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Left", "Top", "Right", "Bottom",
    };

    private static readonly HashSet<string> ValidJustifies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Left", "Center", "Right",
    };

    /// <summary>Throws <see cref="ServerProtocolException"/> with code
    /// "bad-watermark-payload" when refused.</summary>
    public static void Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ServerProtocolException("bad-watermark-payload", "clientWatermarkJson is empty");

        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxBytes)
            throw new ServerProtocolException("bad-watermark-payload",
                $"clientWatermarkJson is {byteCount} bytes (limit {MaxBytes})");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ServerProtocolException("bad-watermark-payload", $"malformed JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ServerProtocolException("bad-watermark-payload",
                    "watermark must be a JSON object");

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "Name":
                    case "name":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            throw new ServerProtocolException("bad-watermark-payload",
                                "Name must be a string");
                        break;
                    case "Text":
                    case "text":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            throw new ServerProtocolException("bad-watermark-payload",
                                "Text must be a string");
                        if (prop.Value.GetString()?.Length > MaxTextLength)
                            throw new ServerProtocolException("bad-watermark-payload",
                                $"Text exceeds {MaxTextLength} chars");
                        break;
                    case "Placement":
                    case "placement":
                        ValidateEnum(prop.Value, ValidPlacements, "Placement");
                        break;
                    case "Justify":
                    case "justify":
                        ValidateEnum(prop.Value, ValidJustifies, "Justify");
                        break;
                }
            }
        }
    }

    private static void ValidateEnum(JsonElement el, HashSet<string> valid, string field)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return; // integer enum values are tolerated; deserializer maps them.
        if (el.ValueKind != JsonValueKind.String)
            throw new ServerProtocolException("bad-watermark-payload",
                $"{field} must be a string or integer");
        string? name = el.GetString();
        if (name == null || !valid.Contains(name))
            throw new ServerProtocolException("bad-watermark-payload",
                $"{field} value '{name}' is not recognised");
    }
}
