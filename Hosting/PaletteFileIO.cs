// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/Editors/PaletteFileIO.cs
//
// Import/export helpers for ColorStopListControl. Supports four palette
// file formats:
//   • PaletteBuilder JSON  (.json)  — reads "stops" array (position + r/g/b
//                                     or hex), falls back to "swatches".
//   • GIMP palette          (.gpl)  — text, "R G B name" per line.
//   • CSS variables          (.css) — extracts any #RRGGBB / rgb() in file.
//   • Hex list               (.hex / .txt) — one #RRGGBB per line.
//
// Writers emit the same shape PaletteBuilder + downstream tools accept.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using FracturingFog.Models;

namespace FracturingFog.Views.Editors
{
    public static class PaletteFileIO
    {
        public const string ImportFilter =
            "All palettes (*.json;*.gpl;*.css;*.hex;*.txt)|*.json;*.gpl;*.css;*.hex;*.txt|" +
            "PaletteBuilder JSON (*.json)|*.json|" +
            "GIMP palette (*.gpl)|*.gpl|" +
            "CSS variables (*.css)|*.css|" +
            "Hex list (*.hex;*.txt)|*.hex;*.txt|" +
            "All files (*.*)|*.*";

        public const string ExportFilter =
            "PaletteBuilder JSON (*.json)|*.json|" +
            "GIMP palette (*.gpl)|*.gpl|" +
            "CSS variables (*.css)|*.css|" +
            "Hex list (*.hex)|*.hex";

        public readonly record struct Rgb(byte R, byte G, byte B);

        /// <summary>
        /// Parse a palette file. Returns the ordered color list (positions
        /// in source files are ignored — caller decides whether to keep or
        /// regenerate positions on Add vs Replace).
        /// </summary>
        public static List<Rgb> Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".json" => LoadJson(path),
                ".gpl" => LoadGpl(path),
                ".css" or ".scss" => LoadCss(path),
                ".hex" or ".txt" => LoadHex(path),
                _ => TryAnyFormat(path),
            };
        }

        /// <summary>
        /// Write the current stops in the format selected by the file's
        /// extension. Stops are written ordered by Position.
        /// </summary>
        public static void Save(string path, IEnumerable<ColorStopData> stops, string paletteName)
        {
            var ordered = stops.OrderBy(s => s.Position).ToList();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".json": SaveJson(path, ordered, paletteName); break;
                case ".gpl": SaveGpl(path, ordered, paletteName); break;
                case ".css": SaveCss(path, ordered, paletteName); break;
                case ".hex":
                case ".txt": SaveHex(path, ordered); break;
                default: SaveJson(path, ordered, paletteName); break;
            }
        }

        // ── JSON (PaletteBuilder shape) ─────────────────────────────────────

        private static List<Rgb> LoadJson(string path)
        {
            var result = new List<Rgb>();
            string raw = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            // PaletteBuilder shape: { stops: [...], swatches: [...] }
            // Some exports may be just an array at root.
            JsonElement arr = default;
            bool gotArr = false;

            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root; gotArr = true;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("stops", out var s) && s.ValueKind == JsonValueKind.Array)
                { arr = s; gotArr = true; }
                else if (root.TryGetProperty("swatches", out var w) && w.ValueKind == JsonValueKind.Array)
                { arr = w; gotArr = true; }
                else if (root.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Array)
                { arr = c; gotArr = true; }
            }

            if (!gotArr) return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    if (TryParseHex(item.GetString(), out var rgb)) result.Add(rgb);
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("hex", out var h) && h.ValueKind == JsonValueKind.String
                    && TryParseHex(h.GetString(), out var fromHex))
                {
                    result.Add(fromHex);
                    continue;
                }

                byte? r = ReadByte(item, "r") ?? ReadByte(item, "red");
                byte? g = ReadByte(item, "g") ?? ReadByte(item, "green");
                byte? b = ReadByte(item, "b") ?? ReadByte(item, "blue");
                if (r.HasValue && g.HasValue && b.HasValue)
                    result.Add(new Rgb(r.Value, g.Value, b.Value));
            }
            return result;
        }

        private static byte? ReadByte(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var p)) return null;
            try
            {
                if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n))
                    return (byte)Math.Clamp(n, 0, 255);
                if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int n2))
                    return (byte)Math.Clamp(n2, 0, 255);
            }
            catch { }
            return null;
        }

        private static void SaveJson(string path, List<ColorStopData> stops, string name)
        {
            var doc = new
            {
                name,
                source = (string?)null,
                method = "ColorThemeEditor",
                swatches = stops.Select(s => new
                {
                    hex = $"#{s.R:X2}{s.G:X2}{s.B:X2}",
                    r = s.R, g = s.G, b = s.B,
                }).ToList(),
                stops = stops.Select(s => new
                {
                    position = s.Position,
                    hex = $"#{s.R:X2}{s.G:X2}{s.B:X2}",
                    r = s.R, g = s.G, b = s.B,
                }).ToList(),
            };
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, opts));
        }

        // ── GIMP .gpl ───────────────────────────────────────────────────────

        private static readonly Regex GplRow = new(
            @"^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})(?:\s+.*)?$",
            RegexOptions.Compiled);

        private static List<Rgb> LoadGpl(string path)
        {
            var result = new List<Rgb>();
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith('#')) continue;
                if (trimmed.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("Columns:", StringComparison.OrdinalIgnoreCase)) continue;

                var m = GplRow.Match(line);
                if (!m.Success) continue;
                byte r = (byte)Math.Clamp(int.Parse(m.Groups[1].Value), 0, 255);
                byte g = (byte)Math.Clamp(int.Parse(m.Groups[2].Value), 0, 255);
                byte b = (byte)Math.Clamp(int.Parse(m.Groups[3].Value), 0, 255);
                result.Add(new Rgb(r, g, b));
            }
            return result;
        }

        private static void SaveGpl(string path, List<ColorStopData> stops, string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("GIMP Palette");
            sb.AppendLine("Name: " + (string.IsNullOrWhiteSpace(name) ? "Palette" : name));
            sb.AppendLine("Columns: 0");
            sb.AppendLine("#");
            foreach (var s in stops)
                sb.AppendLine($"{s.R,3} {s.G,3} {s.B,3}  #{s.R:X2}{s.G:X2}{s.B:X2}");
            File.WriteAllText(path, sb.ToString());
        }

        // ── CSS variables ───────────────────────────────────────────────────

        private static readonly Regex CssHex = new(
            @"#([0-9a-fA-F]{6})\b",
            RegexOptions.Compiled);
        private static readonly Regex CssRgb = new(
            @"rgb\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)",
            RegexOptions.Compiled);

        private static List<Rgb> LoadCss(string path)
        {
            var result = new List<Rgb>();
            string text = File.ReadAllText(path);

            int hexIdx = 0;
            foreach (Match m in CssHex.Matches(text))
            {
                if (TryParseHex("#" + m.Groups[1].Value, out var rgb))
                {
                    result.Add(rgb);
                    hexIdx = m.Index + m.Length;
                }
            }
            if (result.Count > 0) return result;

            foreach (Match m in CssRgb.Matches(text))
            {
                byte r = (byte)Math.Clamp(int.Parse(m.Groups[1].Value), 0, 255);
                byte g = (byte)Math.Clamp(int.Parse(m.Groups[2].Value), 0, 255);
                byte b = (byte)Math.Clamp(int.Parse(m.Groups[3].Value), 0, 255);
                result.Add(new Rgb(r, g, b));
            }
            _ = hexIdx;
            return result;
        }

        private static void SaveCss(string path, List<ColorStopData> stops, string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/* " + (string.IsNullOrWhiteSpace(name) ? "Palette" : name)
                          + " — generated by Color Theme Editor */");
            sb.AppendLine(":root {");
            for (int i = 0; i < stops.Count; i++)
            {
                var s = stops[i];
                sb.Append($"  --palette-{(i + 1):00}: #{s.R:X2}{s.G:X2}{s.B:X2};");
                sb.AppendLine($"   /* rgb({s.R}, {s.G}, {s.B}) */");
            }
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
        }

        // ── Hex list ────────────────────────────────────────────────────────

        private static readonly Regex AnyHex = new(
            @"#?([0-9a-fA-F]{6})",
            RegexOptions.Compiled);

        private static List<Rgb> LoadHex(string path)
        {
            var result = new List<Rgb>();
            foreach (var line in File.ReadAllLines(path))
            {
                var m = AnyHex.Match(line);
                if (!m.Success) continue;
                if (TryParseHex(m.Groups[1].Value, out var rgb))
                    result.Add(rgb);
            }
            return result;
        }

        private static void SaveHex(string path, List<ColorStopData> stops)
        {
            var sb = new StringBuilder();
            foreach (var s in stops)
                sb.AppendLine($"#{s.R:X2}{s.G:X2}{s.B:X2}");
            File.WriteAllText(path, sb.ToString());
        }

        // ── Fallback / shared ───────────────────────────────────────────────

        private static List<Rgb> TryAnyFormat(string path)
        {
            // Best-effort: peek the first non-empty line for a JSON brace,
            // a GIMP header, or a hex token.
            try
            {
                string head = File.ReadAllText(path);
                string t = head.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("[")) return LoadJson(path);
                if (t.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase)) return LoadGpl(path);
                if (t.Contains(":root", StringComparison.OrdinalIgnoreCase)) return LoadCss(path);
                return LoadHex(path);
            }
            catch { return new List<Rgb>(); }
        }

        private static bool TryParseHex(string? s, out Rgb rgb)
        {
            rgb = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string h = s.Trim();
            if (h.StartsWith('#')) h = h.Substring(1);
            if (h.Length == 3)
            {
                h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            }
            if (h.Length != 6) return false;
            if (!byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)) return false;
            if (!byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)) return false;
            if (!byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return false;
            rgb = new Rgb(r, g, b);
            return true;
        }
    }
}
