// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/WatermarkResolver.cs
//
// Single resolver consulted by every render surface (image save, poster,
// slideshow, video, client/server, live Avalonia overlay) to decide which
// top-line text + colours + edge placement to draw, given the user's
// configured precedence chain:
//
//   1. FloatingMenu "Override region watermark" + active custom → use active custom
//   2. The current region has an EmbeddedWatermark → use embedded
//   3. "Use Custom Watermark" toggle + active custom → use active custom
//   4. Default → today's RegionName [- ThemeName] with auto-contrast colour
//
// Plus the mandatory program/version sub-line that the user can never hide or
// edit (per spec).
//
// Pure / stateless. No System.Drawing or Avalonia dependency: returns a small
// WatermarkRender DTO that draw paths translate into their own colour types.

using System;
using System.Reflection;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Resolved watermark render payload — the per-frame answer to
    /// "what do I draw?". Construct via <see cref="WatermarkResolver"/>; never
    /// directly by callers.</summary>
    public sealed class WatermarkRender
    {
        public string TopText { get; init; } = string.Empty;
        public string SubText { get; init; } = string.Empty;

        /// <summary>Glyph fill — for the default path this is the auto-contrast
        /// colour the existing ComputeContrastColor pipeline produces. For a
        /// custom watermark it is the user's TextColor.</summary>
        public RgbDef TextColor { get; init; } = new RgbDef(255, 255, 255);

        public RgbaDef? HighlightColor { get; init; }
        public RgbaDef? BackgroundColor { get; init; }

        public WatermarkPlacement Placement { get; init; } = WatermarkPlacement.Bottom;
        public WatermarkJustify Justify { get; init; } = WatermarkJustify.Right;

        /// <summary>True when the active watermark is a user-defined override.
        /// Callers that already auto-sample the lower-right region for contrast
        /// (e.g. ImageExport.ComputeContrastColor) skip that work when this is
        /// true — the user's TextColor is authoritative.</summary>
        public bool IsCustom { get; init; }
    }

    public static class WatermarkResolver
    {
        /// <summary>Resolve the precedence chain into a single render payload.
        /// Pass the user's current active custom watermark (or null), any
        /// embedded watermark on the current region (or null), the FloatingMenu
        /// override flag, and the master "Use Custom Watermark" flag. The
        /// caller still owns auto-contrast colour resolution for the default
        /// path — pass it in via <paramref name="defaultTextColor"/>.</summary>
        public static WatermarkRender Resolve(
            WatermarkDef? activeCustom,
            WatermarkDef? regionEmbedded,
            bool overrideRegionWatermark,
            bool useCustomWatermark,
            string regionName,
            string themeName,
            string programName,
            string programVersion,
            RgbDef defaultTextColor)
        {
            string sub = BuildSubText(programName, programVersion);

            WatermarkDef? chosen =
                (overrideRegionWatermark && activeCustom != null) ? activeCustom :
                regionEmbedded != null                            ? regionEmbedded :
                (useCustomWatermark && activeCustom != null)      ? activeCustom :
                                                                    null;

            if (chosen != null)
            {
                return new WatermarkRender
                {
                    TopText = chosen.Text ?? string.Empty,
                    SubText = sub,
                    TextColor = chosen.TextColor ?? new RgbDef(255, 255, 255),
                    HighlightColor = chosen.HighlightColor,
                    BackgroundColor = chosen.BackgroundColor,
                    Placement = chosen.Placement,
                    Justify = chosen.Justify,
                    IsCustom = true,
                };
            }

            return new WatermarkRender
            {
                TopText = ComposeDefaultTopText(regionName, themeName),
                SubText = sub,
                TextColor = defaultTextColor,
                HighlightColor = null,
                BackgroundColor = null,
                Placement = WatermarkPlacement.Bottom,
                Justify = WatermarkJustify.Right,
                IsCustom = false,
            };
        }

        /// <summary>The default (non-custom) top-line: "Region - Theme",
        /// degrading to whichever half is present. Public so surfaces that
        /// pre-compose the top-line before reaching Resolve (poster/wallpaper
        /// requests, batch) format it the same way rather than each spelling
        /// out the separator.</summary>
        public static string ComposeDefaultTopText(string? regionName, string? themeName)
        {
            string main = string.IsNullOrEmpty(regionName) ? string.Empty : regionName!;
            if (!string.IsNullOrEmpty(themeName))
                main = string.IsNullOrEmpty(main) ? themeName! : main + " - " + themeName;
            return main;
        }

        /// <summary>The mandatory program/version line. Centralised so every
        /// surface formats it identically.</summary>
        public static string BuildSubText(string programName, string programVersion)
            => $"{programName} v{(string.IsNullOrEmpty(programVersion) ? "?" : programVersion)} {DateTime.Now.Year}";

        /// <summary>Product name for the sub-line. Headless surfaces (batch,
        /// server) have no shell to hand them one.</summary>
        public const string DefaultProgramName = "Fracturing Fog";

        /// <summary>Version for the sub-line, read off the entry assembly with
        /// the git-hash suffix stripped — the same rule the shell's help
        /// provider applies, so headless output carries the version the UI
        /// shows rather than a surface-specific label.</summary>
        public static string DetectProgramVersion()
        {
            string v = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? string.Empty;
            int plus = v.IndexOf('+');
            return plus >= 0 ? v.Substring(0, plus) : v;
        }

        /// <summary>The mandatory sub-line for a surface with no shell to ask.
        /// Equivalent to what the interactive paths produce.</summary>
        public static string BuildDefaultSubText()
            => BuildSubText(DefaultProgramName, DetectProgramVersion());

        /// <summary>Geometry helper used by callers that need to allocate
        /// scratch surfaces (slideshow overlay bitmap, video frame compositor).
        /// Returns the on-image pixel rectangle the resolved watermark will
        /// occupy. Inputs in pixels; <paramref name="topW"/>/<paramref name="topH"/>
        /// + <paramref name="subW"/>/<paramref name="subH"/> are the measured
        /// glyph metrics the caller obtained from its own text-measurement API.
        /// Caller pads for outline stroke / AA fringe.</summary>
        public static (int X, int Y, int W, int H) ComputeBlockBounds(
            WatermarkRender wm,
            int imgW, int imgH,
            int topW, int topH,
            int subW, int subH,
            int edgePad)
        {
            // Combined block width / height as a single rectangle. Top/Bottom
            // stack vertically; Left/Right also stack vertically (subtext under
            // top-line in reading order).
            int blockW = Math.Max(topW, subW);
            int blockH = topH + subH;

            int x, y;
            switch (wm.Placement)
            {
                case WatermarkPlacement.Top:
                    y = edgePad;
                    x = wm.Justify switch
                    {
                        WatermarkJustify.Left => edgePad,
                        WatermarkJustify.Center => (imgW - blockW) / 2,
                        _ => imgW - blockW - edgePad,
                    };
                    break;

                case WatermarkPlacement.Left:
                    x = edgePad;
                    y = wm.Justify switch
                    {
                        WatermarkJustify.Left => edgePad,
                        WatermarkJustify.Center => (imgH - blockH) / 2,
                        _ => imgH - blockH - edgePad,
                    };
                    break;

                case WatermarkPlacement.Right:
                    x = imgW - blockW - edgePad;
                    y = wm.Justify switch
                    {
                        WatermarkJustify.Left => edgePad,
                        WatermarkJustify.Center => (imgH - blockH) / 2,
                        _ => imgH - blockH - edgePad,
                    };
                    break;

                case WatermarkPlacement.Bottom:
                default:
                    y = imgH - blockH - edgePad;
                    x = wm.Justify switch
                    {
                        WatermarkJustify.Left => edgePad,
                        WatermarkJustify.Center => (imgW - blockW) / 2,
                        _ => imgW - blockW - edgePad,
                    };
                    break;
            }

            return (Math.Max(0, x), Math.Max(0, y), Math.Min(blockW, imgW), Math.Min(blockH, imgH));
        }

        /// <summary>Per-line horizontal placement within the block rectangle.
        /// Top/Bottom + Justify=Left/Center/Right shifts to match. Left/Right
        /// also justify the lines within the block.</summary>
        public static int AlignLineX(int blockX, int blockW, int lineW, WatermarkJustify justify)
            => justify switch
            {
                WatermarkJustify.Left => blockX,
                WatermarkJustify.Center => blockX + (blockW - lineW) / 2,
                _ => blockX + (blockW - lineW),
            };
    }
}
