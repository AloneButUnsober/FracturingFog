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
        /// <summary>#1409 — 3D look: dark modules are raised blocks on a light
        /// floor, with bevelled edges and cast shadows lit by the active 3D
        /// theme's key / fill lights (or an explicit light). Module centres are
        /// flat and stay in their luma band, so the code reads like Steer.</summary>
        Bevel = 3,
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
        public const double MinBevelHeight = 0.05, MaxBevelHeight = 2.0, DefaultBevelHeight = 0.35;
        public const double MinBevelWidth = 0.05, MaxBevelWidth = 0.45, DefaultBevelWidth = 0.2;
        public const double DefaultBevelSpecular = 0.5, DefaultBevelShadow = 0.4;
        public const double DefaultBevelAzimuthDeg = 135, DefaultBevelElevationDeg = 30;
        public const double MinReliefHeight = 0.05, MaxReliefHeight = 3.0, DefaultReliefHeight = 0.5;
        public const double DefaultReliefTerrain = 0.15;
        public const double MinReliefElevation = 5, DefaultReliefMinElevation = 60;
        public const double MinMeshSizeMm = 20, MaxMeshSizeMm = 400, DefaultMeshSizeMm = 60;
        public const double MinMeshBaseMm = 0.4, MaxMeshBaseMm = 20, DefaultMeshBaseMm = 2;
        public const double MinMeshHeightMm = 0.2, MaxMeshHeightMm = 10, DefaultMeshHeightMm = 1;
        public const double MaxMeshTerrainMm = 20;

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

        /// <summary>#1402 — shape the code toward the image (QArt): the data bits
        /// after the terminator, which a decoder never reads, are chosen so the
        /// code's own modules follow the fractal's light and dark areas. Lower
        /// error correction and a larger <see cref="MinVersion"/> leave more bits
        /// free, so more of the code can follow the image.</summary>
        public bool Shape { get; init; }

        /// <summary>Smallest QR version to use (1..40). Above what the payload
        /// needs it buys free bits for <see cref="Shape"/> and smaller modules.</summary>
        public int MinVersion { get; init; } = 1;

        // ── #1409 Bevel style ────────────────────────────────────────────
        /// <summary>Block height as a fraction of the module side. Taller blocks
        /// cast longer shadows and steeper bevels shade harder.</summary>
        public double BevelHeight { get; init; } = DefaultBevelHeight;

        /// <summary>Width of the sloped edge as a fraction of the module side.</summary>
        public double BevelWidth { get; init; } = DefaultBevelWidth;

        /// <summary>Light the blocks with the active theme's 3D lights (Phong / PBR
        /// key + fill). When false, or when the theme has no 3D lights, a single
        /// light at <see cref="BevelAzimuthDeg"/> / <see cref="BevelElevationDeg"/> is used.</summary>
        public bool BevelLightFromTheme { get; init; } = true;

        /// <summary>Light direction in the saved image: degrees counter-clockwise
        /// from +x (east), so 135 = from the upper left.</summary>
        public double BevelAzimuthDeg { get; init; } = DefaultBevelAzimuthDeg;

        /// <summary>Light elevation above the image plane, 1..89 degrees.</summary>
        public double BevelElevationDeg { get; init; } = DefaultBevelElevationDeg;

        /// <summary>Specular highlight strength on the bevels, 0..1.</summary>
        public double BevelSpecular { get; init; } = DefaultBevelSpecular;

        /// <summary>Darkness of the shadow a block casts on the floor, 0..1.</summary>
        public double BevelShadow { get; init; } = DefaultBevelShadow;

        // ── #1410 Extrude in Relief 3D ───────────────────────────────────
        /// <summary>Build the code into the Relief 3D raymarch as real blocks (needs
        /// the relief raymarch on; otherwise the flat style is used). Camera tilt,
        /// cast shadows, AO and the lights then act on the code itself.</summary>
        public bool Relief { get; init; }

        /// <summary>Block height in modules (world height = this x module size).</summary>
        public double ReliefHeight { get; init; } = DefaultReliefHeight;

        /// <summary>How much of the fractal's own relief is kept under the code,
        /// 0 (flat plinth) .. 1 (full terrain). Tall terrain casts shadows across
        /// modules and can hide them from the camera.</summary>
        public double ReliefTerrain { get; init; } = DefaultReliefTerrain;

        /// <summary>The camera elevation is raised to at least this (degrees) while
        /// the code is extruded: a low camera lets a block hide the module behind it.</summary>
        public double ReliefMinElevationDeg { get; init; } = DefaultReliefMinElevation;

        // ── #1411 printable tile (QR mesh export) ─────────────────────────
        /// <summary>Tile side in millimetres (the code plus its quiet zone).</summary>
        public double MeshSizeMm { get; init; } = DefaultMeshSizeMm;
        /// <summary>Base plate thickness in millimetres.</summary>
        public double MeshBaseMm { get; init; } = DefaultMeshBaseMm;
        /// <summary>How far dark modules stand above the base (millimetres).</summary>
        public double MeshHeightMm { get; init; } = DefaultMeshHeightMm;
        /// <summary>Terrain from the fractal's brightness under the code, in
        /// millimetres (0 = a flat plate; a two-material print needs 0).</summary>
        public double MeshTerrainMm { get; init; }

        /// <summary>The CLI / UI spelling of a style.</summary>
        public static string StyleName(QrStyle s) => s switch
        {
            QrStyle.Dots     => "dots",
            QrStyle.Halftone => "halftone",
            QrStyle.Bevel    => "bevel",
            _                => "steer",
        };

        public static bool TryParseStyle(string? s, out QrStyle style)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "dots":     style = QrStyle.Dots;     return true;
                case "steer":    style = QrStyle.Steer;    return true;
                case "halftone": style = QrStyle.Halftone; return true;
                case "bevel":    style = QrStyle.Bevel;    return true;
                default:         style = QrStyle.Steer;    return false;
            }
        }

        /// <summary>The CLI spelling of the bevel light: <c>theme</c> or <c>AZ,EL</c>.</summary>
        public string BevelLightSpec => BevelLightFromTheme
            ? "theme"
            : FormattableString.Invariant($"{BevelAzimuthDeg:0.###},{BevelElevationDeg:0.###}");

        /// <summary>Parse <c>theme</c> or <c>AZ,EL</c> (degrees; elevation 1..89).</summary>
        public static bool TryParseBevelLight(string? s, out bool fromTheme, out double azimuthDeg, out double elevationDeg)
        {
            fromTheme = true; azimuthDeg = DefaultBevelAzimuthDeg; elevationDeg = DefaultBevelElevationDeg;
            var t = (s ?? "").Trim();
            if (t.Equals("theme", StringComparison.OrdinalIgnoreCase)) return true;
            var parts = t.Split(',');
            if (parts.Length != 2) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, inv, out var az)
                || !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, inv, out var el)
                || !double.IsFinite(az) || el < 1 || el > 89)
                return false;
            fromTheme = false;
            azimuthDeg = ((az % 360) + 360) % 360;
            elevationDeg = el;
            return true;
        }

        public static bool TryParseErrorCorrection(string? s, out QrErrorCorrection ecc)
            => Enum.TryParse((s ?? "").Trim(), ignoreCase: true, out ecc)
               && Enum.IsDefined(ecc)
               && (s ?? "").Trim().Length == 1;
    }
}
