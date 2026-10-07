// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using FracturingFog.Abstractions;
using FracturingFog.Abstractions.Assets;

using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1212 — imported JSON is decoded and sanitized before any importer sees it.
/// Each test checks the repaired value against the known original text (not
/// against the helper's own output), and that clean input passes through untouched.
/// </summary>
public sealed class ImportedJsonTests
{
    private const string Doc = "{\"Name\":\"Café ’quote’ 日本 🎨\",\"Source\":\"z^2 + c\\nline2\",\"X\":0.1000000000000000055511151231257827}";

    private static string NameOf(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.GetProperty("Name").GetString()!;
    }

    private static string Prop(string json, string name)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.GetProperty(name).GetString()!;
    }

    // ── encoding detection ──────────────────────────────────────────────

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16lebom")]
    [InlineData("utf16be")]
    [InlineData("utf16bebom")]
    [InlineData("utf32le")]
    [InlineData("utf32lebom")]
    [InlineData("utf32be")]
    public void DecodeBytes_RecoversOriginalText_ForEveryUnicodeEncoding(string form)
    {
        byte[] bytes = form switch
        {
            "utf8"       => new UTF8Encoding(false).GetBytes(Doc),
            "utf8bom"    => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Doc)).ToArray(),
            "utf16le"    => Encoding.Unicode.GetBytes(Doc),
            "utf16lebom" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Doc)).ToArray(),
            "utf16be"    => Encoding.BigEndianUnicode.GetBytes(Doc),
            "utf16bebom" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Doc)).ToArray(),
            "utf32le"    => new UTF32Encoding(false, false).GetBytes(Doc),
            "utf32lebom" => new UTF32Encoding(false, true).GetPreamble().Concat(new UTF32Encoding(false, false).GetBytes(Doc)).ToArray(),
            "utf32be"    => new UTF32Encoding(true, false).GetBytes(Doc),
            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };

        Assert.Equal(Doc, ImportedJson.DecodeBytes(bytes));
    }

    [Fact]
    public void DecodeBytes_AnsiFile_FallsBackToWindows1252()
    {
        // "Café – ‘x’" saved as Windows-1252: é=E9, en dash=96, quotes=91/92.
        byte[] ansi = { (byte)'{', (byte)'"', (byte)'N', (byte)'a', (byte)'m', (byte)'e', (byte)'"', (byte)':', (byte)'"',
                        (byte)'C', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x96, (byte)' ', 0x91, (byte)'x', 0x92,
                        (byte)'"', (byte)'}' };
        var report = new ImportTextReport();

        string text = ImportedJson.DecodeBytes(ansi, report);

        Assert.Equal("Café – ‘x’", NameOf(text));
        Assert.Contains(report.Notes, n => n.Contains("Windows-1252"));
    }

    // ── pass-through ────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_CleanDocument_ReturnsSameInstance()
    {
        var report = new ImportTextReport();
        string pretty = "[\n  " + Doc + ",\n  {\"Name\": \"naïve façade\"}\n]";

        Assert.Same(pretty, ImportedJson.Sanitize(pretty, report));
        Assert.False(report.HasFixes);
    }

    [Fact]
    public void Sanitize_NotJson_ReturnsTextForCallerToReject()
    {
        Assert.Equal("{ this is not json", ImportedJson.Sanitize("{ this is not json"));
        Assert.Empty(AssetJsonFile.SplitEntries("{ this is not json"));
    }

    // ── value repairs ───────────────────────────────────────────────────

    [Fact]
    public void Sanitize_NormalizesLineEndingsInValues()
    {
        string json = "{\"Name\":\"n\",\"Source\":\"a\\r\\nb\\rc\\nd\"}";

        Assert.Equal("a\nb\nc\nd", Prop(ImportedJson.Sanitize(json), "Source"));
    }

    [Theory]
    [InlineData("CafÃ©", "Café")]
    [InlineData("Itâ€™s", "It’s")]
    [InlineData("Ã¦Ã¸Ã¥ â€” Ã‰cole", "æøå — École")]
    [InlineData("CafÃƒÂ©", "Café")] // double-encoded
    public void Sanitize_RepairsUtf8ReadAsAnsiMojibake(string mangled, string expected)
    {
        string json = JsonSerializer.Serialize(new { Name = mangled });

        Assert.Equal(expected, NameOf(ImportedJson.Sanitize(json)));
    }

    [Theory]
    [InlineData("Café")]
    [InlineData("naïve façade — ½ × ±")]
    [InlineData("Ångström 日本")]
    public void Sanitize_LeavesGenuineAccentedTextAlone(string name)
    {
        string json = JsonSerializer.Serialize(new { Name = name });

        Assert.Equal(name, NameOf(ImportedJson.Sanitize(json)));
    }

    [Fact]
    public void Sanitize_StripsControlAndZeroWidthCharacters_KeepsTabs()
    {
        string json = "{\"Name\":\"a\\u0000b\\u200Bc\\uFEFFd\\u0007e\\tf\\u0085g\"}";

        Assert.Equal("abcde\tfg", NameOf(ImportedJson.Sanitize(json)));
    }

    [Fact]
    public void Sanitize_ReplacesLoneSurrogates()
    {
        string json = "{\"Name\":\"a\\uD800b\"}";

        string name = NameOf(ImportedJson.Sanitize(json));
        Assert.Equal("a\uFFFDb", name);
    }

    [Fact]
    public void Sanitize_LoneLowSurrogate_Replaced_ValidPairAndEscapedBackslashKept()
    {
        // valid pair (U+1F3A8), unpaired low, and a literal backslash-u text run
        string json = "{\"Name\":\"\\uD83C\\uDFA8 x\\uDC00 \\\\uD800\"}";

        Assert.Equal("\U0001F3A8 x\uFFFD \\uD800", NameOf(ImportedJson.Sanitize(json)));
    }

    [Fact]
    public void Sanitize_ComposesDecomposedAccentsToNfc()
    {
        string nfd = "Cafe\u0301";
        string json = JsonSerializer.Serialize(new { Name = nfd });

        Assert.Equal("Café", NameOf(ImportedJson.Sanitize(json)));
    }

    // ── structure repairs ───────────────────────────────────────────────

    [Fact]
    public void Sanitize_DoubledAndEmbeddedBoms_Removed()
    {
        string json = "\uFEFF\uFEFF[{\"Name\":\"a\"},\uFEFF{\"Name\":\"b\"}]";

        var entries = AssetJsonFile.SplitEntries(json);
        Assert.Equal(new[] { "a", "b" }, entries.Select(NameOf));
    }

    [Fact]
    public void Sanitize_NonBreakingSpacesInStructure_Normalized_KeptInValues()
    {
        string json = "{\u00A0\"Name\":\u00A0\"a\u00A0b\"\u3000}";

        Assert.Equal("a\u00A0b", NameOf(ImportedJson.Sanitize(json)));
    }

    [Fact]
    public void Sanitize_WordProcessorQuotes_Repaired()
    {
        string json = "{\u201CName\u201D: \u201CSpiral\u201D, \u201CSource\u201D: \u201Cz^2 + c\u201D}";

        string fixedJson = ImportedJson.Sanitize(json);
        Assert.Equal("Spiral", NameOf(fixedJson));
        Assert.Equal("z^2 + c", Prop(fixedJson, "Source"));
    }

    [Fact]
    public void Sanitize_CurlyQuotesInsideValidJson_AreContent()
    {
        string json = "{\"Name\":\"\u201Chello\u201D\"}";

        Assert.Same(json, ImportedJson.Sanitize(json));
    }

    [Fact]
    public void Sanitize_CommentsAndTrailingCommas_Accepted()
    {
        string json = "// hand edited\n[ { \"Name\": \"a\", /* note */ }, ]";

        Assert.Equal(new[] { "a" }, AssetJsonFile.SplitEntries(json).Select(NameOf));
    }

    [Fact]
    public void Sanitize_RepairedDocument_KeepsNumbersExact()
    {
        string json = "{\"Name\":\"CafÃ©\",\"X\":0.1000000000000000055511151231257827,\"N\":12345678901234567890}";

        string fixedJson = ImportedJson.Sanitize(json);
        using var d = JsonDocument.Parse(fixedJson);
        Assert.Equal("0.1000000000000000055511151231257827", d.RootElement.GetProperty("X").GetRawText());
        Assert.Equal("12345678901234567890", d.RootElement.GetProperty("N").GetRawText());
    }

    [Fact]
    public void Sanitize_JunkInPropertyName_Removed()
    {
        string json = "{\"\\uFEFFName\":\"a\"}";

        Assert.Equal("a", NameOf(ImportedJson.Sanitize(json)));
    }

    // ── end-to-end through the readers ──────────────────────────────────

    [Fact]
    public void ReadStream_Utf16ZipEntryWithCrlf_SplitsIntoCleanEntries()
    {
        string original = "[\r\n {\"Name\":\"Café\",\"Source\":\"a\\r\\nb\"},\r\n {\"Name\":\"b\"}\r\n]";
        using var ms = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(original)).ToArray());
        var report = new ImportTextReport();

        var entries = AssetJsonFile.SplitEntries(ImportedJson.ReadStream(ms, report), report);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Café", NameOf(entries[0]));
        Assert.Equal("a\nb", Prop(entries[0], "Source"));
        Assert.Contains("decoded as UTF-16 LE", report.Notes);
    }

    [Fact]
    public void TolerantJsonList_LoadsUtf16StoreFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff1212-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "[{\"Name\":\"Café\"},{\"Name\":\"b\"}]", Encoding.Unicode);

            var list = new TolerantJsonList<NamedEntry>().LoadFile(path, (JsonSerializerOptions?)null);

            Assert.Equal(new[] { "Café", "b" }, list.Select(e => e.Name));
        }
        finally { File.Delete(path); }
    }

    public sealed class NamedEntry
    {
        public string Name { get; set; } = string.Empty;
    }
}
