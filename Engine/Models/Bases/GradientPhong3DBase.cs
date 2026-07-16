// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/GradientPhong3DBase.cs
//
// Shared base class for all gradient-based 3D Phong colour themes.
//
// REPLACES the duplicated LitMap / normal-building / specular boilerplate
// that was copy-pasted identically into every *3D.cs file.
//
// How to create a new 3D gradient theme using this base:
//
//   1.  Inherit from GradientPhong3DBase (not GradientColorMap directly).
//   2.  In the constructor, add gradient stops to Stops[] and assign the
//       two lights by setting KeyLight and FillLight.
//   3.  Override CycleSpeed if the flat counterpart uses a different value.
//   4.  Override Steepness if you want more/less dramatic 3D carving.
//   5.  That's it — the base handles all Phong maths and interface routing.
//
// The explicit interface implementation of IColorMap.Map(5-param) is
// declared here once so every subclass automatically routes correctly.
// Subclasses do NOT need to redeclare it.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Abstract base class for gradient-based 3D Phong colour maps.
    /// Subclasses set gradient stops, KeyLight, FillLight, and optionally
    /// CycleSpeed and Steepness; the base handles all lighting maths.
    /// </summary>
    public abstract class GradientPhong3DBase : GradientColorMap, IColorMap
    {
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // ── Subclass-configurable properties ──────────────────────────────────

        /// <summary>
        /// Primary (key) light source — the dominant light that defines
        /// where highlights fall.  Set in the subclass constructor.
        /// </summary>
        protected LightSource KeyLight;

        /// <summary>
        /// Secondary (fill) light — softer, from the opposite side.
        /// Colours shadowed faces so they're not flat black.
        /// Set in the subclass constructor.
        /// </summary>
        protected LightSource FillLight;

        /// <summary>
        /// Optional tertiary (rim) light — typically behind/side of the subject
        /// with high shininess + low diffuse for an accent edge highlight.
        /// When <see cref="UseRimLight"/> is false the rim block is skipped at
        /// zero cost so existing themes pay no perf penalty.
        /// </summary>
        protected LightSource RimLight;

        /// <summary>True if subclass populated <see cref="RimLight"/>.</summary>
        protected bool UseRimLight;

        /// <summary>
        /// Controls how many gradient cycles appear per 1/CycleSpeed smooth-units.
        /// Match the value used by the flat counterpart (default 0.02 = cycle
        /// every 50 smooth-units).
        /// </summary>
        protected virtual float CycleSpeed => 0.02f;

        /// <summary>
        /// Controls 3D depth drama.
        /// 0.9  = deep carving / dramatic shadows
        /// 1.6  = balanced (default)
        /// 2.5  = gentle emboss / subtle relief
        /// </summary>
        protected virtual float Steepness => 1.6f;

        /// <summary>
        /// Ambient light scale [0..1].  Determines the minimum brightness
        /// of fully-shadowed pixels.  0.12 keeps shadows dark; raise to
        /// 0.25 to lift shadows and reveal more detail in dark areas.
        /// </summary>
        protected virtual float Ambient => 0.12f;

        /// <summary>
        /// Scale factor applied to the key specular highlight [0..1].
        /// 0.85 gives a strong but not overblown specular (default).
        /// </summary>
        protected virtual float KeySpecScale => 0.85f;

        /// <summary>
        /// Scale factor applied to the fill specular highlight [0..1].
        /// 0.25 is a very subtle back-fill specular (default).
        /// </summary>
        protected virtual float FillSpecScale => 0.25f;

        /// <summary>
        /// Scale factor applied to the fill diffuse contribution [0..1].
        /// 0.35 keeps the fill from washing out the key (default).
        /// </summary>
        protected virtual float FillDiffScale => 0.35f;

        /// <summary>Scale factor for the rim specular highlight (default 1.0).</summary>
        protected virtual float RimSpecScale => 1.0f;

        /// <summary>Scale factor for the rim diffuse contribution (default 0.20).</summary>
        protected virtual float RimDiffScale => 0.20f;

        // ── Export accessors (used by JSON serialisation) ─────────────────────

        public LightSource ExportKeyLight => KeyLight;
        public LightSource ExportFillLight => FillLight;
        public LightSource ExportRimLight => RimLight;
        public bool ExportUseRimLight => UseRimLight;
        public float ExportCycleSpeed => CycleSpeed;
        public float ExportSteepness => Steepness;
        public float ExportAmbient => Ambient;
        public float ExportKeySpecScale => KeySpecScale;
        public float ExportFillSpecScale => FillSpecScale;
        public float ExportFillDiffScale => FillDiffScale;
        public float ExportRimSpecScale => RimSpecScale;
        public float ExportRimDiffScale => RimDiffScale;

        // ── Interface routing — declared ONCE for all subclasses ──────────────
        //
        // This explicit interface implementation ensures that when the calculator
        // calls colorMap.Map(s,d,i,nx,ny) through an IColorMap reference, it
        // routes directly to our LitMap — NOT to the default interface method.
        // Every subclass inherits this routing automatically.

        /// <inheritdoc cref="IColorMap.Map(float,float,int)"/>
        public sealed override int Map(float smooth, float distance, int maxIterations)
            => LitMap(smooth, distance, maxIterations, 0f, 0f);

        int IColorMap.Map(float smooth, float distance, int maxIterations,
                          float nx, float ny)
            => LitMap(smooth, distance, maxIterations, nx, ny);

        // ── Core Phong implementation ─────────────────────────────────────────

        private int LitMap(float smooth, float distance, int maxIterations,
                            float nx, float ny)
        {
            if (smooth >= maxIterations)
                return unchecked((int)0xFF000000);

            // Sample gradient at cycling t.
            float t       = (smooth * CycleSpeed) % 1.0f;
            int   albedoI = MapNormalized(t, distance);
            float aR      = ((albedoI >> 16) & 0xFF) / 255f;
            float aG      = ((albedoI >>  8) & 0xFF) / 255f;
            float aB      = ( albedoI        & 0xFF) / 255f;

            // Build 3D surface normal (ny negated for screen-space convention).
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx/len;  Ny = ry/len;  Nz = Steepness/len; }
            else             { Nx = 0f;       Ny = 0f;       Nz = 1f; }

            // Ambient.
            float r = aR * Ambient;
            float g = aG * Ambient;
            float b = aB * Ambient;

            // Key light diffuse.
            float dk = MathF.Max(0f, Nx*KeyLight.Lx + Ny*KeyLight.Ly + Nz*KeyLight.Lz);
            r += dk * KeyLight.DiffR * aR;
            g += dk * KeyLight.DiffG * aG;
            b += dk * KeyLight.DiffB * aB;

            // Key specular (Blinn-Phong half-vector H = normalize(L + V), V=(0,0,1)).
            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1f;
            float hkl = MathF.Sqrt(hkx*hkx + hky*hky + hkz*hkz);
            if (hkl > 1e-8f)
            {
                hkx/=hkl; hky/=hkl; hkz/=hkl;
                float sk = MathF.Pow(MathF.Max(0f, Nx*hkx+Ny*hky+Nz*hkz), KeyLight.Shininess) * KeySpecScale;
                r += sk * KeyLight.SpecR;
                g += sk * KeyLight.SpecG;
                b += sk * KeyLight.SpecB;
            }

            // Fill light diffuse (scaled down to avoid washing out key).
            float df = MathF.Max(0f, Nx*FillLight.Lx + Ny*FillLight.Ly + Nz*FillLight.Lz);
            r += df * FillLight.DiffR * aR * FillDiffScale;
            g += df * FillLight.DiffG * aG * FillDiffScale;
            b += df * FillLight.DiffB * aB * FillDiffScale;

            // Fill specular (subtle).
            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1f;
            float hfl = MathF.Sqrt(hfx*hfx + hfy*hfy + hfz*hfz);
            if (hfl > 1e-8f)
            {
                hfx/=hfl; hfy/=hfl; hfz/=hfl;
                float sf = MathF.Pow(MathF.Max(0f, Nx*hfx+Ny*hfy+Nz*hfz), FillLight.Shininess) * FillSpecScale;
                r += sf * FillLight.SpecR;
                g += sf * FillLight.SpecG;
                b += sf * FillLight.SpecB;
            }

            // Rim light (optional — typically back/side accent).
            if (UseRimLight)
            {
                float dr = MathF.Max(0f, Nx*RimLight.Lx + Ny*RimLight.Ly + Nz*RimLight.Lz);
                r += dr * RimLight.DiffR * aR * RimDiffScale;
                g += dr * RimLight.DiffG * aG * RimDiffScale;
                b += dr * RimLight.DiffB * aB * RimDiffScale;

                float hrx = RimLight.Lx, hry = RimLight.Ly, hrz = RimLight.Lz + 1f;
                float hrl = MathF.Sqrt(hrx*hrx + hry*hry + hrz*hrz);
                if (hrl > 1e-8f)
                {
                    hrx/=hrl; hry/=hrl; hrz/=hrl;
                    float sr = MathF.Pow(MathF.Max(0f, Nx*hrx+Ny*hry+Nz*hrz), RimLight.Shininess) * RimSpecScale;
                    r += sr * RimLight.SpecR;
                    g += sr * RimLight.SpecG;
                    b += sr * RimLight.SpecB;
                }
            }

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }
}
