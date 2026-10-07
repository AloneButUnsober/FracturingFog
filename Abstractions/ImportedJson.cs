// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ImportedJson.cs
//
// #1212 — safeguard layer for JSON that comes into Fracturing Fog from outside:
// per-editor Import buttons, asset bundles (zip), .fbulb files, palette files and
// the user stores themselves (a data folder copied between machines). Files that
// travel through mail, chat, cloud drives, editors on another OS or a "save as"
// in the wrong encoding arrive in shapes System.Text.Json either rejects outright
// or accepts with damaged text:
//
//   - encodings: UTF-16/32 (with or without BOM), legacy ANSI / Windows-1252
//     instead of UTF-8, doubled or embedded BOMs;
//   - line endings: CRLF / lone CR inside multi-line values (equation sources,
//     notes) depending on which OS last touched the file;
//   - mangled characters: UTF-8 decoded as Windows-1252 and re-saved ("Ã©" for
//     "é", "â€™" for "’"), NULs, zero-width / non-breaking spaces, stray control
//     characters, lone surrogates, non-NFC accents;
//   - word-processor damage: typographic quotes and non-breaking spaces in the
//     JSON structure itself, comments and trailing commas from hand edits.
//
// The importers stay unchanged: they take the text this helper returns. A
// document that needs no repair comes back as the exact input string, so clean
// files are byte-for-byte unaffected; a repaired one is re-serialized from the
// cleaned tree. Text that still is not JSON after repair is returned (pre-cleaned)
// so each caller reports the failure the way it always has.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FracturingFog.Abstractions
{
    /// <summary>What <see cref="ImportedJson"/> had to fix in one imported document,
    /// as short human-readable notes (distinct, in the order found).</summary>
    public sealed class ImportTextReport
    {
        private readonly List<string> _notes = new();

        /// <summary>Distinct repair notes, e.g. "decoded as UTF-16 LE".</summary>
        public IReadOnlyList<string> Notes => _notes;

        /// <summary>True when anything was repaired.</summary>
        public bool HasFixes => _notes.Count > 0;

        internal void Add(string note)
        {
            if (!_notes.Contains(note)) _notes.Add(note);
        }

        /// <summary>Fold another report's notes into this one.</summary>
        public void Merge(ImportTextReport? other)
        {
            if (other == null) return;
            foreach (var n in other._notes) Add(n);
        }
    }

    /// <summary>Decode + sanitize JSON from outside the app (#1212). See file header.</summary>
    public static class ImportedJson
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly JsonDocumentOptions LenientDoc = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // ── entry points ─────────────────────────────────────────────────────

        /// <summary>Read <paramref name="path"/>, detect its encoding and sanitize the
        /// JSON. Throws on IO errors exactly as <see cref="File.ReadAllText(string)"/> does.</summary>
        public static string ReadFile(string path, ImportTextReport? report = null)
            => Sanitize(DecodeBytes(File.ReadAllBytes(path), report), report);

        /// <summary>Read <paramref name="path"/> as text with encoding detection only — for
        /// non-JSON import formats (palette CSS / GPL / hex lists).</summary>
        public static string ReadTextFile(string path, ImportTextReport? report = null)
            => DecodeBytes(File.ReadAllBytes(path), report);

        /// <summary><see cref="ReadFile"/> for a stream (e.g. a zip entry).</summary>
        public static string ReadStream(Stream stream, ImportTextReport? report = null)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Sanitize(DecodeBytes(ms.GetBuffer().AsSpan(0, (int)ms.Length), report), report);
        }

        // ── encoding ─────────────────────────────────────────────────────────

        /// <summary>Decode raw file bytes to text. Honours UTF-8/16/32 BOMs, sniffs
        /// BOM-less UTF-16/32 from the zero-byte pattern of the leading ASCII
        /// character (RFC 4627 §3), otherwise requires valid UTF-8 and falls back to
        /// Windows-1252 (the usual "ANSI" save on Windows) when it is not.</summary>
        public static string DecodeBytes(ReadOnlySpan<byte> b, ImportTextReport? report = null)
        {
            if (b.Length == 0) return string.Empty;

            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
                return Note(report, "decoded as UTF-32 LE", new UTF32Encoding(false, false).GetString(b[4..]));
            if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
                return Note(report, "decoded as UTF-32 BE", new UTF32Encoding(true, false).GetString(b[4..]));
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return DecodeUtf8OrAnsi(b[3..], report);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                return Note(report, "decoded as UTF-16 LE", Encoding.Unicode.GetString(b[2..]));
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return Note(report, "decoded as UTF-16 BE", Encoding.BigEndianUnicode.GetString(b[2..]));

            // No BOM: JSON text starts with an ASCII character, so the position of
            // its zero bytes gives the code-unit width and byte order away.
            if (b.Length >= 4 && b[0] != 0 && b[1] == 0 && b[2] == 0 && b[3] == 0)
                return Note(report, "decoded as UTF-32 LE", new UTF32Encoding(false, false).GetString(b));
            if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] != 0)
                return Note(report, "decoded as UTF-32 BE", new UTF32Encoding(true, false).GetString(b));
            if (b.Length >= 2 && b[0] != 0 && b[1] == 0)
                return Note(report, "decoded as UTF-16 LE", Encoding.Unicode.GetString(b));
            if (b.Length >= 2 && b[0] == 0 && b[1] != 0)
                return Note(report, "decoded as UTF-16 BE", Encoding.BigEndianUnicode.GetString(b));

            return DecodeUtf8OrAnsi(b, report);
        }

        private static string DecodeUtf8OrAnsi(ReadOnlySpan<byte> b, ImportTextReport? report)
        {
            try { return StrictUtf8.GetString(b); }
            catch (DecoderFallbackException)
            {
                report?.Add("not valid UTF-8 — decoded as Windows-1252 (ANSI)");
                return DecodeWindows1252(b);
            }
        }

        private static string Note(ImportTextReport? report, string note, string text)
        {
            report?.Add(note);
            return text;
        }

        // Windows-1252 0x80..0x9F; the five undefined bytes keep their C1 code point.
        private static readonly char[] Cp1252High =
        {
            '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
            '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
            '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
            '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
        };

        private static readonly Dictionary<char, byte> Cp1252Reverse = BuildCp1252Reverse();

        private static Dictionary<char, byte> BuildCp1252Reverse()
        {
            var map = new Dictionary<char, byte>();
            for (int i = 0; i < Cp1252High.Length; i++) map[Cp1252High[i]] = (byte)(0x80 + i);
            return map;
        }

        /// <summary>Decode bytes as Windows-1252 (no CodePages provider needed).</summary>
        public static string DecodeWindows1252(ReadOnlySpan<byte> b)
        {
            var chars = new char[b.Length];
            for (int i = 0; i < b.Length; i++)
            {
                byte x = b[i];
                chars[i] = x is >= 0x80 and <= 0x9F ? Cp1252High[x - 0x80] : (char)x;
            }
            return new string(chars);
        }

        // ── JSON sanitize ────────────────────────────────────────────────────

        /// <summary>Repair decoded JSON text: strip stray BOMs / NULs, fix
        /// word-processor damage in the structure, accept comments and trailing
        /// commas, then clean every string value (see <see cref="CleanString"/>) and
        /// property name. Returns <paramref name="text"/> itself when nothing needed
        /// fixing. Never throws.</summary>
        public static string Sanitize(string? text, ImportTextReport? report = null)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            var local = new ImportTextReport();
            string pre = FixEscapedSurrogates(PreClean(text, local), local);

            JsonNode? root;
            if (!TryParse(pre, strict: true, out root))
            {
                if (TryParse(pre, strict: false, out root))
                    local.Add("comments / trailing commas removed");
                else
                {
                    string fixedQuotes = RepairTypographicQuotes(pre);
                    if (!ReferenceEquals(fixedQuotes, pre) && TryParse(fixedQuotes, strict: false, out root))
                        local.Add("typographic quotes replaced in the JSON structure");
                    else
                    {
                        report?.Merge(local);
                        return pre; // not JSON — the caller reports it as before
                    }
                }
            }

            try
            {
                root = CleanNode(root, local);
                report?.Merge(local);
                if (!local.HasFixes) return text;
                return root?.ToJsonString(WriteOpts) ?? "null";
            }
            catch
            {
                report?.Merge(local);
                return pre;
            }
        }

        private static bool TryParse(string text, bool strict, out JsonNode? root)
        {
            try
            {
                root = strict ? JsonNode.Parse(text) : JsonNode.Parse(text, null, LenientDoc);
                // Materialize the whole tree now so duplicate keys or bad escapes
                // surface here rather than half-way through the clean pass.
                Touch(root);
                return true;
            }
            catch
            {
                root = null;
                return false;
            }
        }

        private static void Touch(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o: foreach (var kv in o) Touch(kv.Value); break;
                case JsonArray a: foreach (var v in a) Touch(v); break;
            }
        }

        // Characters that are invalid anywhere in JSON text yet are routinely
        // injected by transfers: BOMs (doubled, or mid-file after concatenation),
        // NULs (UTF-16 read as bytes), and — outside strings only — Unicode spaces
        // and line separators that editors / web pages substitute for plain ones.
        private static string PreClean(string text, ImportTextReport report)
        {
            StringBuilder? sb = null;
            bool inString = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char? replacement = null;
                bool drop = false;

                if (c is '\uFEFF' or '\u0000')
                {
                    drop = true;
                    report.Add(c == '\uFEFF' ? "stray byte-order marks removed" : "NUL characters removed");
                }
                else if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c is '\u00A0' or '\u202F' or '\u205F' or '\u3000' or (>= '\u2000' and <= '\u200A'))
                {
                    replacement = ' ';
                    report.Add("non-breaking / Unicode spaces normalized");
                }
                else if (c is '\u200B' or '\u2060')
                {
                    drop = true;
                    report.Add("zero-width characters removed");
                }
                else if (c is '\u2028' or '\u2029')
                {
                    replacement = '\n';
                    report.Add("line endings normalized");
                }

                if (drop || replacement.HasValue)
                {
                    sb ??= new StringBuilder(text, 0, i, text.Length);
                    if (replacement.HasValue) sb.Append(replacement.Value);
                }
                else sb?.Append(c);
            }
            return sb?.ToString() ?? text;
        }

        // A JSON escape for half a surrogate pair (truncated text, a bad encoder)
        // parses, but every GetString on it throws — so the importer would fail
        // on an otherwise good file. Rewrite each unpaired escape to U+FFFD.
        private static string FixEscapedSurrogates(string text, ImportTextReport report)
        {
            if (text.IndexOf('\\') < 0) return text;
            StringBuilder? sb = null;
            bool inString = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (!inString)
                {
                    if (c == '"') inString = true;
                    sb?.Append(c);
                    i++;
                    continue;
                }
                if (c == '"') { inString = false; sb?.Append(c); i++; continue; }
                if (c != '\\' || i + 1 >= text.Length) { sb?.Append(c); i++; continue; }

                if (text[i + 1] != 'u' || !TryHex4(text, i + 2, out int unit))
                {
                    sb?.Append(c).Append(text[i + 1]); // any other escape, copied whole
                    i += 2;
                    continue;
                }

                int width = 6;
                bool bad = false;
                if (unit is >= 0xD800 and <= 0xDBFF)
                {
                    if (i + 7 < text.Length && text[i + 6] == '\\' && text[i + 7] == 'u'
                        && TryHex4(text, i + 8, out int low) && low is >= 0xDC00 and <= 0xDFFF)
                        width = 12; // a proper pair
                    else bad = true;
                }
                else if (unit is >= 0xDC00 and <= 0xDFFF) bad = true;

                if (bad)
                {
                    report.Add("invalid characters replaced");
                    sb ??= new StringBuilder(text, 0, i, text.Length);
                    sb.Append('\\').Append('u').Append("FFFD");
                }
                else sb?.Append(text, i, width);
                i += width;
            }
            return sb?.ToString() ?? text;
        }

        private static bool TryHex4(string s, int start, out int value)
        {
            value = 0;
            if (start + 4 > s.Length) return false;
            for (int k = start; k < start + 4; k++)
            {
                int d = s[k] switch
                {
                    >= '0' and <= '9' => s[k] - '0',
                    >= 'a' and <= 'f' => s[k] - 'a' + 10,
                    >= 'A' and <= 'F' => s[k] - 'A' + 10,
                    _ => -1,
                };
                if (d < 0) return false;
                value = value * 16 + d;
            }
            return true;
        }

        // A document pushed through a word processor or chat client has every
        // straight double quote turned typographic, so its structure no longer
        // parses. Only when no straight quote survives is it safe to assume every
        // curly one was a delimiter.
        private static string RepairTypographicQuotes(string text)
        {
            if (text.IndexOf('"') >= 0) return text;
            if (text.IndexOfAny(new[] { '\u201C', '\u201D', '\u201E', '\u201F', '\u2033' }) < 0) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                sb.Append(c is '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' ? '"' : c);
            return sb.ToString();
        }

        private static JsonNode? CleanNode(JsonNode? node, ImportTextReport report)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    var props = new List<KeyValuePair<string, JsonNode?>>(obj.Count);
                    bool renamed = false;
                    foreach (var kv in obj)
                    {
                        string key = CleanString(kv.Key, report, isKey: true);
                        renamed |= !ReferenceEquals(key, kv.Key);
                        props.Add(new(key, kv.Value));
                    }
                    if (renamed)
                    {
                        obj.Clear();
                        foreach (var p in props)
                        {
                            // Two keys that only differed by junk collapse; first wins.
                            if (!obj.ContainsKey(p.Key)) obj[p.Key] = p.Value;
                        }
                    }
                    foreach (var p in props)
                    {
                        if (!obj.TryGetPropertyValue(p.Key, out var child) || child is null) continue;
                        var cleaned = CleanNode(child, report);
                        if (!ReferenceEquals(cleaned, child)) obj[p.Key] = cleaned;
                    }
                    return obj;
                }
                case JsonArray arr:
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is null) continue;
                        var cleaned = CleanNode(child, report);
                        if (!ReferenceEquals(cleaned, child)) arr[i] = cleaned;
                    }
                    return arr;
                case JsonValue val when val.GetValueKind() == JsonValueKind.String:
                {
                    string s = val.GetValue<string>();
                    string c = CleanString(s, report, isKey: false);
                    return ReferenceEquals(c, s) ? val : JsonValue.Create(c);
                }
                default:
                    return node;
            }
        }

        /// <summary>Clean one string value: repair UTF-8-read-as-ANSI mojibake,
        /// normalize CRLF / CR to LF (values only), drop BOMs, zero-width spaces, NULs
        /// and other control characters (tab and LF stay), replace lone surrogates with
        /// U+FFFD and compose to NFC. Returns <paramref name="s"/> itself when clean.</summary>
        public static string CleanString(string s, ImportTextReport? report = null, bool isKey = false)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string original = s;

            string demangled = RepairMojibake(s);
            if (!ReferenceEquals(demangled, s))
            {
                report?.Add("mis-decoded characters repaired (UTF-8 read as ANSI)");
                s = demangled;
            }

            StringBuilder? sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string? note = null;
                char? replacement = null;
                bool drop = false;

                if (c == '\r')
                {
                    if (isKey) { drop = true; note = "control characters removed"; }
                    else
                    {
                        note = "line endings normalized";
                        if (i + 1 < s.Length && s[i + 1] == '\n') drop = true; // CRLF → LF
                        else replacement = '\n';                                // CR → LF
                    }
                }
                else if (c == '\n' && isKey) { drop = true; note = "control characters removed"; }
                else if (c is '\uFEFF' or '\u200B' or '\u2060')
                {
                    drop = true;
                    note = "zero-width characters removed";
                }
                else if ((c < ' ' && c != '\t' && c != '\n') || c is >= '\u007F' and <= '\u009F')
                {
                    drop = true;
                    note = "control characters removed";
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb?.Append(c).Append(s[i + 1]);
                        i++;
                        continue;
                    }
                    replacement = '\uFFFD';
                    note = "invalid characters replaced";
                }
                else if (char.IsLowSurrogate(c))
                {
                    replacement = '\uFFFD';
                    note = "invalid characters replaced";
                }

                if (note != null)
                {
                    report?.Add(note);
                    sb ??= new StringBuilder(s, 0, i, s.Length);
                    if (replacement.HasValue) sb.Append(replacement.Value);
                }
                else sb?.Append(c);
            }
            if (sb != null) s = sb.ToString();

            if (!s.IsNormalized(NormalizationForm.FormC))
            {
                s = s.Normalize(NormalizationForm.FormC);
                report?.Add("accented characters normalized (NFC)");
            }

            return s == original ? original : s;
        }

        /// <summary>Undo UTF-8 text that was decoded as Windows-1252 / Latin-1 and
        /// re-saved ("CafÃ©" → "Café", "â€™" → "’"), up to three layers deep. Only
        /// applies when the whole string maps back to single bytes that form valid,
        /// multi-byte UTF-8 — random accented text essentially never does — so genuine
        /// Latin-1 text is left alone. Returns <paramref name="s"/> itself when unchanged.</summary>
        public static string RepairMojibake(string s)
        {
            string current = s;
            for (int layer = 0; layer < 3; layer++)
            {
                if (!TryEncodeAsAnsi(current, out byte[] bytes)) break;
                string decoded;
                try { decoded = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { break; }
                if (decoded == current) break;
                current = decoded;
            }
            return ReferenceEquals(current, s) ? s : current;
        }

        private static bool TryEncodeAsAnsi(string s, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            bool anyHigh = false;
            var buf = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x80) buf[i] = (byte)c;
                else if (Cp1252Reverse.TryGetValue(c, out byte mapped)) { buf[i] = mapped; anyHigh = true; }
                else if (c <= 0xFF) { buf[i] = (byte)c; anyHigh = true; } // Latin-1 / undefined-1252 C1
                else return false;
            }
            if (!anyHigh) return false;
            bytes = buf;
            return true;
        }
    }
}
