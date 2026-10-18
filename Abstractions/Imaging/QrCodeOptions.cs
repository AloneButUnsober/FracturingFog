// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/QrCodeOptions.cs
//
// #1305 (fractal QR codes) - the export-time option set that turns a rendered
// fractal into a scannable QR code. Lives in Abstractions so PosterRequest
// (Engine), BatchOptions and the Control Center (UI.Avalonia, which does not
// reference the Engine) all carry the same type. The encoder / compositor /
// verifier are Engine-side (FracturingFog.Imaging.Qr).

using System;

namespace FracturingFog.Imaging
{
    /// <summary>QR Reed-Solomon error-correction level: roughly the share of
    /// codewords that may be damaged and still decode (L 7%, M 15%, Q 25%,
    /// H 30%). Higher levels leave more room for the fractal but need a larger
    /// code (smaller modules) for the same payload.</summary>
    public enum QrErrorCorrection
    {
        L = 0,
        M = 1,
        Q = 2,
        H = 3,
    }

    /// <summary>How the fractal and the code are combined.</summary>
    public enum QrStyle
    {
        /// <summary>Only a dot at each module's centre is forced to the module's
        /// shade (hue kept); the rest of the module keeps the fractal, with just
        /// its far-end lightness compressed.</summary>
        Dots = 0,
        /// <summary>Every module's lightness is steered to the dark or light band
        /// (hue and chroma kept, internal contrast compressed), the centre more
        /// strongly than the edges. The whole code reads as the fractal recoloured.</summary>
        Steer = 1,
        /// <summary>Halftone QR (after Chu et al., SIGGRAPH Asia 2013): each module
        /// is split 3x3; the centre cell carries the code, the eight outer cells
        /// are the fractal's own two-tone halftone within the module's half of the
        /// lightness range.</summary>
        Halftone = 2,
    }

    /// <summary>Fractal QR export options. Attached to a poster / batch render;
    /// <c>null</c> on the request = no QR (byte-identical to before #1305).</summary>
    public sealed record QrCodeOptions
    {
        public const int MaxPayloadBytes = 2953;   // version 40-L, byte mode
        public const double MinDotFraction = 0.2;
        public const double MaxDotFraction = 0.6;
        public const double DefaultDotFraction = 0.4;
        public const int MaxModulePixels = 256;

        /// <summary>Text to encode (UTF-8, byte mode). Required, non-empty.</summary>
        public string Payload { get; init; } = "";

        public QrErrorCorrection ErrorCorrection { get; init; } = QrErrorCorrection.H;

        public QrStyle Style { get; init; } = QrStyle.Steer;

        /// <summary>Pixels per module side. 0 = auto: the largest whole size at
        /// which the code plus its 4-module quiet zone fits the output's short side.</summary>
        public int ModulePixels { get; init; }

        /// <summary>Light modules on a dark field. Many but not all readers accept
        /// inverted codes, so it is opt-in.</summary>
        public bool Invert { get; init; }

        /// <summary>Side of the forced centre square as a fraction of the module
        /// (Dots style; also the strongly steered core in Steer style).</summary>
        public double DotFraction { get; init; } = DefaultDotFraction;

        /// <summary>Decode the saved image with an independent reader after the
        /// write and report whether it scans.</summary>
        public bool Verify { get; init; } = true;

        /// <summary>Preferred mask pattern 0..7, or -1 = auto. #1401 — the view
        /// search picks the mask whose pattern best matches the fractal; it is
        /// tried first, and only replaced if that composite would not scan.</summary>
        public int Mask { get; init; } = -1;

        /// <summary>The CLI / UI spelling of a style.</summary>
        public static string StyleName(QrStyle s) => s switch
        {
            QrStyle.Dots     => "dots",
            QrStyle.Halftone => "halftone",
            _                => "steer",
        };

        public static bool TryParseStyle(string? s, out QrStyle style)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "dots":     style = QrStyle.Dots;     return true;
                case "steer":    style = QrStyle.Steer;    return true;
                case "halftone": style = QrStyle.Halftone; return true;
                default:         style = QrStyle.Steer;    return false;
            }
        }

        public static bool TryParseErrorCorrection(string? s, out QrErrorCorrection ecc)
            => Enum.TryParse((s ?? "").Trim(), ignoreCase: true, out ecc)
               && Enum.IsDefined(ecc)
               && (s ?? "").Trim().Length == 1;
    }
}
