// Models/AlgorithmicLighting3DBase.cs
//
// Shared base classes for 3D Phong and PBR variants of *algorithmic* colour
// schemes (those that compute colour directly from smooth/distance rather
// than sampling a gradient stop list).
//
// AlgorithmicPhong3DBase  — Blinn-Phong with KeyLight/FillLight, ambient,
//                           specular scales.  Mirrors GradientPhong3DBase but
//                           takes albedo from a subclass-supplied function
//                           instead of the GradientColorMap.MapNormalized() pipeline.
//
// AlgorithmicPbr3DBase    — Cook-Torrance GGX PBR.  Mirrors PbrGradient3DBase
//                           but, again, sources albedo from the subclass.
//
// Subclass contract:
//   • Override ComputeAlbedo(smooth, distance, maxIter, out r,g,b).  This is
//     the original algorithmic colour formula; the base then layers lighting
//     on top.  Returning [0,1]-clamped channels keeps tone-mapping behaved.
//   • Assign KeyLight / FillLight in the constructor.
//   • Optionally override Steepness, Ambient, KeySpecScale, FillSpecScale,
//     FillDiffScale (Phong) or LightingMode, BuildMaterial, GlowBoost (PBR)
//     to tune the surface character.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    // =========================================================================
    // Phong 3D base for algorithmic colour maps
    // =========================================================================

    public abstract class AlgorithmicPhong3DBase : IColorMap
    {
        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;
        public int MaxIterations { get; set; } = 1000;

        protected LightSource KeyLight;
        protected LightSource FillLight;

        protected virtual float Steepness => 1.5f;
        protected virtual float Ambient => 0.12f;
        protected virtual float KeySpecScale => 0.85f;
        protected virtual float FillSpecScale => 0.25f;
        protected virtual float FillDiffScale => 0.35f;

        public LightSource ExportKeyLight => KeyLight;
        public LightSource ExportFillLight => FillLight;
        public float ExportSteepness => Steepness;
        public float ExportAmbient => Ambient;
        public float ExportKeySpecScale => KeySpecScale;
        public float ExportFillSpecScale => FillSpecScale;
        public float ExportFillDiffScale => FillDiffScale;

        /// <summary>
        /// Subclass supplies the unlit base colour as a [0,1] RGB triple.
        /// This is the same formula the flat algorithmic theme uses, so the
        /// 3D variant inherits the exact colour personality and just adds
        /// lighting on top.
        /// </summary>
        protected abstract void ComputeAlbedo(
            float smooth, float distance, int maxIter,
            out float aR, out float aG, out float aB);

        public int Map(float smooth, float distance, int iterations)
            => LitMap(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
            => LitMap(smooth, distance, iterations, nx, ny);

        private int LitMap(float smooth, float distance, int maxIterations,
                           float nx, float ny)
        {
            if (smooth >= maxIterations)
                return unchecked((int)0xFF000000);

            ComputeAlbedo(smooth, distance, maxIterations, out float aR, out float aG, out float aB);
            aR = Math.Clamp(aR, 0f, 1f);
            aG = Math.Clamp(aG, 0f, 1f);
            aB = Math.Clamp(aB, 0f, 1f);

            // 3D normal (ny negated for screen-space convention).
            float ry = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else { Nx = 0f; Ny = 0f; Nz = 1f; }

            // Ambient.
            float r = aR * Ambient;
            float g = aG * Ambient;
            float b = aB * Ambient;

            // Key light diffuse.
            float dk = MathF.Max(0f, Nx * KeyLight.Lx + Ny * KeyLight.Ly + Nz * KeyLight.Lz);
            r += dk * KeyLight.DiffR * aR;
            g += dk * KeyLight.DiffG * aG;
            b += dk * KeyLight.DiffB * aB;

            // Key specular (Blinn-Phong).
            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1f;
            float hkl = MathF.Sqrt(hkx * hkx + hky * hky + hkz * hkz);
            if (hkl > 1e-8f)
            {
                hkx /= hkl; hky /= hkl; hkz /= hkl;
                float sk = MathF.Pow(MathF.Max(0f, Nx * hkx + Ny * hky + Nz * hkz),
                                     KeyLight.Shininess) * KeySpecScale;
                r += sk * KeyLight.SpecR;
                g += sk * KeyLight.SpecG;
                b += sk * KeyLight.SpecB;
            }

            // Fill diffuse.
            float df = MathF.Max(0f, Nx * FillLight.Lx + Ny * FillLight.Ly + Nz * FillLight.Lz);
            r += df * FillLight.DiffR * aR * FillDiffScale;
            g += df * FillLight.DiffG * aG * FillDiffScale;
            b += df * FillLight.DiffB * aB * FillDiffScale;

            // Fill specular.
            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1f;
            float hfl = MathF.Sqrt(hfx * hfx + hfy * hfy + hfz * hfz);
            if (hfl > 1e-8f)
            {
                hfx /= hfl; hfy /= hfl; hfz /= hfl;
                float sf = MathF.Pow(MathF.Max(0f, Nx * hfx + Ny * hfy + Nz * hfz),
                                     FillLight.Shininess) * FillSpecScale;
                r += sf * FillLight.SpecR;
                g += sf * FillLight.SpecG;
                b += sf * FillLight.SpecB;
            }

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }
    }

    // =========================================================================
    // PBR 3D base for algorithmic colour maps  (Cook-Torrance GGX)
    // =========================================================================

    public abstract class AlgorithmicPbr3DBase : IColorMap
    {
        public ColorPaletteType Type { get; } = ColorPaletteType.Relief3D;
        public int MaxIterations { get; set; } = 1000;

        protected virtual PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;
        protected LightSource KeyLight;
        protected LightSource FillLight;
        protected virtual float Steepness => 1.6f;
        protected virtual float Ambient => 0.05f;

        /// <summary>
        /// Optional emissive boost added after tone-map.  Receives the raw
        /// fractal sample data so the subclass can drive glow from smooth or
        /// distance just like the 2D algorithmic original.
        /// </summary>
        protected virtual float GlowBoost(float smooth, float distance, int maxIter) => 0f;

        protected abstract void ComputeAlbedo(
            float smooth, float distance, int maxIter,
            out float aR, out float aG, out float aB);

        protected virtual PbrMaterial BuildMaterial(
            float smooth, float distance, int maxIter,
            float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.7f);

        public LightSource ExportKeyLight => KeyLight;
        public LightSource ExportFillLight => FillLight;
        public float ExportSteepness => Steepness;
        public float ExportAmbient => Ambient;
        public PbrLightingMode ExportLightingMode => LightingMode;

        private (float key, float fill, float ambient, float toneMapBias) GetLightingMultipliers()
            => LightingMode switch
            {
                PbrLightingMode.PBRBright => (3.0f, 2.0f, 1.8f, 0.2f),
                _ => (1.0f, 1.0f, 1.4f, 0.5f),
            };

        public int Map(float smooth, float distance, int iterations)
            => LitMapPbr(smooth, distance, iterations, 0f, 0f);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
            => LitMapPbr(smooth, distance, iterations, nx, ny);

        private int LitMapPbr(float smooth, float distance, int maxIterations,
                              float nx, float ny)
        {
            if (smooth >= maxIterations)
                return unchecked((int)0xFF000000);

            var (keyMul, fillMul, ambMul, toneBias) = GetLightingMultipliers();

            ComputeAlbedo(smooth, distance, maxIterations, out float aR, out float aG, out float aB);
            aR = Math.Clamp(aR, 0f, 1f);
            aG = Math.Clamp(aG, 0f, 1f);
            aB = Math.Clamp(aB, 0f, 1f);

            PbrMaterial mat = BuildMaterial(smooth, distance, maxIterations, aR, aG, aB);
            mat.ComputeF0(out float F0R, out float F0G, out float F0B);

            float ry = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else { Nx = 0f; Ny = 0f; Nz = 1f; }

            float Vx = 0f, Vy = 0f, Vz = 1f;
            float NdotV = MathF.Max(0f, Nx * Vx + Ny * Vy + Nz * Vz);

            float r = 0f, g = 0f, b = 0f;
            AddLight(KeyLight, keyMul, ref r, ref g, ref b);
            AddLight(FillLight, fillMul, ref r, ref g, ref b);

            float amb = Ambient * ambMul;
            r += mat.BaseR * amb;
            g += mat.BaseG * amb;
            b += mat.BaseB * amb;

            float glow = GlowBoost(smooth, distance, maxIterations);
            r += glow;
            g += glow * 0.9f;
            b += glow * 1.2f;

            r = r / (toneBias + r);
            g = g / (toneBias + g);
            b = b / (toneBias + b);

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);

            void AddLight(LightSource src, float mul,
                          ref float outR, ref float outG, ref float outB)
            {
                float Lx = src.Lx, Ly = src.Ly, Lz = src.Lz;
                float Ll = MathF.Sqrt(Lx * Lx + Ly * Ly + Lz * Lz);
                if (Ll < 1e-6f) return;
                Lx /= Ll; Ly /= Ll; Lz /= Ll;

                float NdotL = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);
                if (NdotL <= 0f) return;

                float Hx = Vx + Lx;
                float Hy = Vy + Ly;
                float Hz = Vz + Lz;
                float Hl = MathF.Sqrt(Hx * Hx + Hy * Hy + Hz * Hz);
                if (Hl < 1e-6f) return;
                Hx /= Hl; Hy /= Hl; Hz /= Hl;

                float NdotH = MathF.Max(0f, Nx * Hx + Ny * Hy + Nz * Hz);
                float VdotH = MathF.Max(0f, Vx * Hx + Vy * Hy + Vz * Hz);

                float D = PbrMath.DistributionGGX(NdotH, mat.Roughness);
                float Gs = PbrMath.GeometrySmith(NdotV, NdotL, mat.Roughness);

                float Fr = PbrMath.FresnelSchlick(VdotH, F0R);
                float Fg = PbrMath.FresnelSchlick(VdotH, F0G);
                float Fb = PbrMath.FresnelSchlick(VdotH, F0B);

                float denom = 4f * MathF.Max(NdotV, 1e-4f) * MathF.Max(NdotL, 1e-4f);
                float specScale = (D * Gs) / denom;

                float specR = Fr * specScale;
                float specG = Fg * specScale;
                float specB = Fb * specScale;

                float kd = 1f - mat.Metalness;
                float diffR = kd * mat.BaseR / MathF.PI;
                float diffG = kd * mat.BaseG / MathF.PI;
                float diffB = kd * mat.BaseB / MathF.PI;

                float Lr = src.DiffR * mul;
                float Lg = src.DiffG * mul;
                float Lb = src.DiffB * mul;

                outR += (diffR + specR) * Lr * NdotL;
                outG += (diffG + specG) * Lg * NdotL;
                outB += (diffB + specB) * Lb * NdotL;
            }
        }
    }
}
