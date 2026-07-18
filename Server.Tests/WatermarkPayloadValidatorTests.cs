// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Server.Guard;
using System.Text;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class WatermarkPayloadValidatorTests
{
    [Fact]
    public void Validate_AcceptsMinimalDef()
    {
        const string json = """
            {
              "Name": "Studio",
              "Text": "Hello",
              "TextColor": { "R": 255, "G": 255, "B": 255 },
              "Placement": "Bottom",
              "Justify": "Right"
            }
            """;
        WatermarkPayloadValidator.Validate(json);
    }

    [Fact]
    public void Validate_AcceptsAllFourPlacementsAndThreeJustifies()
    {
        foreach (var p in new[] { "Left", "Top", "Right", "Bottom" })
        foreach (var j in new[] { "Left", "Center", "Right" })
        {
            string json = $"{{\"Placement\":\"{p}\",\"Justify\":\"{j}\"}}";
            WatermarkPayloadValidator.Validate(json);
        }
    }

    [Fact]
    public void Validate_RejectsEmpty()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate(""));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsMalformedJson()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("{ not valid }"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsArrayRoot()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("[]"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsUnknownPlacement()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("{\"Placement\":\"Diagonal\"}"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsUnknownJustify()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("{\"Justify\":\"Top\"}"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsOversizeText()
    {
        var huge = new string('A', WatermarkPayloadValidator.MaxTextLength + 1);
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate($"{{\"Text\":\"{huge}\"}}"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsOversizePayload()
    {
        var padding = new string('x', WatermarkPayloadValidator.MaxBytes);
        string json = "{\"Name\":\"" + padding + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(json) > WatermarkPayloadValidator.MaxBytes);
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate(json));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsNonStringName()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("{\"Name\":123}"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_RejectsNonStringText()
    {
        var ex = Assert.Throws<ServerProtocolException>(
            () => WatermarkPayloadValidator.Validate("{\"Text\":42}"));
        Assert.Equal("bad-watermark-payload", ex.Code);
    }

    [Fact]
    public void Validate_AcceptsIntegerEnumValues()
    {
        // 0 = Left placement, 1 = Center justify — backward-compat with
        // System.Text.Json integer enum mode.
        WatermarkPayloadValidator.Validate("{\"Placement\":0,\"Justify\":1}");
    }
}
