// Models/ColorThemeData.cs
//
// JSON-serializable data transfer objects describing a colour theme.
//
// A colour theme normally lives as a concrete C# class (e.g., CesiumSpectrumPhong3D)
// so its Map() method can run hot in the inner render loop.  These DTOs capture
// just the *parameters* of a theme — gradient stops, cycle rate, Phong/PBR
// lighting — so a theme can be exported to JSON, hand-edited, shared, and
// re-imported as a "data driven" theme that renders identically (or close to it).
//
// JSON layout is hand-tuned for human editing:
//   • Colours stored as plain byte R/G/B (not hex strings or System.Drawing.Color),
//     so users can tweak values in any text editor.
//   • Lighting and PBR fields are nullable — only present in the JSON when the
//     theme kind needs them.

using System.Collections.Generic;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Discriminator selecting which data-driven runtime class to instantiate.
    /// </summary>
    public enum ColorThemeKind
    {
        /// <summary>Linear gradient stretched once across the iteration range.</summary>
        Gradient,
        /// <summary>Gradient that repeats every 1/CycleSpeed smooth-units.</summary>
        Cycling,
        /// <summary>Cycling gradient with Blinn-Phong 3D lighting.</summary>
        Phong3D,
        /// <summary>Cycling gradient with Cook-Torrance PBR lighting.</summary>
        Pbr3D
    }

    /// <summary>
    /// Single gradient stop, JSON-friendly (avoids serializing System.Drawing.Color).
    /// </summary>
    public sealed class ColorStopData
    {
        public float Position { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public ColorStopData() { }

        public ColorStopData(ColorStop stop)
        {
            Position = stop.Position;
            R = stop.Color.R;
            G = stop.Color.G;
            B = stop.Color.B;
        }

        public ColorStop ToColorStop()
            => new ColorStop(Position, Color.FromArgb(R, G, B));
    }

    /// <summary>
    /// Directional light source parameters used by Phong/PBR themes.
    /// </summary>
    public sealed class LightSourceData
    {
        public float Lx { get; set; }
        public float Ly { get; set; }
        public float Lz { get; set; }
        public float DiffR { get; set; }
        public float DiffG { get; set; }
        public float DiffB { get; set; }
        public float SpecR { get; set; }
        public float SpecG { get; set; }
        public float SpecB { get; set; }
        public float Shininess { get; set; } = 32f;

        public LightSourceData() { }

        public LightSourceData(LightSource src)
        {
            Lx = src.Lx; Ly = src.Ly; Lz = src.Lz;
            DiffR = src.DiffR; DiffG = src.DiffG; DiffB = src.DiffB;
            SpecR = src.SpecR; SpecG = src.SpecG; SpecB = src.SpecB;
            Shininess = src.Shininess;
        }

        public LightSource ToLightSource()
            => new LightSource(Lx, Ly, Lz,
                               DiffR, DiffG, DiffB,
                               SpecR, SpecG, SpecB,
                               Shininess);
    }

    /// <summary>
    /// One band of the PBR metal/roughness piecewise function.
    /// Bands are evaluated in list order; the first band whose
    /// <see cref="UpperT"/> exceeds <c>t</c> wins.  The final band acts as the
    /// fallback for any <c>t</c> past all earlier thresholds (set its
    /// <see cref="UpperT"/> to <c>1.0</c> or higher).
    /// </summary>
    public sealed class PbrMaterialBandData
    {
        public float UpperT { get; set; } = 1.0f;
        public float Metal { get; set; } = 0.0f;
        public float Roughness { get; set; } = 0.7f;
    }

    /// <summary>
    /// Optional override colour for in-set (interior) pixels.  When present on a
    /// theme, the calculator paints unescaped pixels with this colour instead of
    /// the default opaque black (0xFF000000).  Themes that omit this field keep
    /// the historical black interior.
    /// </summary>
    public sealed class InSetColorData
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public InSetColorData() { }

        public InSetColorData(byte r, byte g, byte b)
        {
            R = r; G = g; B = b;
        }

        /// <summary>Packs the colour as opaque 0xFFRRGGBB.</summary>
        public uint ToPackedArgb()
            => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>
    /// Full data definition of a colour theme.  Persisted to JSON; consumed by
    /// the data-driven runtime classes in <see cref="DataDrivenColorThemes"/>.
    /// </summary>
    public sealed class ColorThemeData
    {
        // ── Identity / display ────────────────────────────────────────────────

        public string Name { get; set; } = "Unnamed Theme";
        public string Category { get; set; } = "User";
        public string Description { get; set; } = "";

        public ColorThemeKind Kind { get; set; } = ColorThemeKind.Gradient;

        // ── Gradient (all kinds) ──────────────────────────────────────────────

        public List<ColorStopData> Stops { get; set; } = new();

        // ── Cycling / 3D ──────────────────────────────────────────────────────

        public float CycleSpeed { get; set; } = 0.02f;

        // ── 3D shared (Phong + PBR) ───────────────────────────────────────────

        public float Steepness { get; set; } = 1.6f;
        public float Ambient { get; set; } = 0.12f;
        public LightSourceData? KeyLight { get; set; }
        public LightSourceData? FillLight { get; set; }

        // ── Phong3D extras ────────────────────────────────────────────────────

        public float KeySpecScale { get; set; } = 0.85f;
        public float FillSpecScale { get; set; } = 0.25f;
        public float FillDiffScale { get; set; } = 0.35f;

        // ── PBR extras ────────────────────────────────────────────────────────

        public PbrLightingMode PbrLightingMode { get; set; } = PbrLightingMode.PBRRealistic;

        /// <summary>
        /// Glow boost is computed as <c>scale * pow(t, exponent)</c>.
        /// Set <see cref="GlowBoostScale"/> to 0 to disable.
        /// </summary>
        public float GlowBoostExponent { get; set; } = 8f;
        public float GlowBoostScale { get; set; } = 0f;

        /// <summary>
        /// Piecewise metal/roughness bands for PBR.  Empty list = single
        /// fallback band (metal 0, roughness 0.7).
        /// </summary>
        public List<PbrMaterialBandData> MaterialBands { get; set; } = new();

        // ── In-set override (all kinds) ───────────────────────────────────────

        /// <summary>
        /// Optional alternative colour for in-set pixels.  Null = default black.
        /// </summary>
        public InSetColorData? InSetColor { get; set; }
    }
}