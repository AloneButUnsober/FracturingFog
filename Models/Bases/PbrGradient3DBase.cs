////using FracturingFog.Interefaces;
////using System;

////namespace FracturingFog.Models
////{
////    /// <summary>
////    /// Pbr light source definition for 3D Phong shading.
////    /// </summary>
////    public struct PbrLight
////    {
////        public float Lx, Ly, Lz;      // direction (normalized)
////        public float R, G, B;        // light color (radiance)
////    }

////    /// <summary>
////    /// Abstract base class for gradient-based 3D Phong colour maps.
////    /// Subclasses set gradient stops, KeyLight, FillLight, and optionally
////    /// CycleSpeed and Steepness; the base handles all lighting maths.
////    /// </summary>
////    public abstract class PbrGradient3DBase : GradientColorMap, IColorMap
////    {
////        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

////        // ── Subclass-configurable properties ──────────────────────────────────

////        /// <summary>
////        /// Primary (key) light source — the dominant light that defines
////        /// where highlights fall.  Set in the subclass constructor.
////        /// </summary>
////        protected LightSource KeyLight;

////        /// <summary>
////        /// Secondary (fill) light — softer, from the opposite side.
////        /// Colours shadowed faces so they're not flat black.
////        /// Set in the subclass constructor.
////        /// </summary>
////        protected LightSource FillLight;

////        /// <summary>
////        /// Controls how many gradient cycles appear per 1/CycleSpeed smooth-units.
////        /// Match the value used by the flat counterpart (default 0.02 = cycle
////        /// every 50 smooth-units).
////        /// </summary>
////        protected virtual float CycleSpeed => 0.02f;

////        /// <summary>
////        /// Controls 3D depth drama.
////        /// 0.9  = deep carving / dramatic shadows
////        /// 1.6  = balanced (default)
////        /// 2.5  = gentle emboss / subtle relief
////        /// </summary>
////        protected virtual float Steepness => 1.6f;

////        /// <summary>
////        /// Ambient light scale [0..1].  Determines the minimum brightness
////        /// of fully-shadowed pixels.  0.12 keeps shadows dark; raise to
////        /// 0.25 to lift shadows and reveal more detail in dark areas.
////        /// </summary>
////        protected virtual float Ambient => 0.12f;

////        /// <summary>
////        /// Scale factor applied to the key specular highlight [0..1].
////        /// 0.85 gives a strong but not overblown specular (default).
////        /// </summary>
////        protected virtual float KeySpecScale => 0.85f;

////        /// <summary>
////        /// Scale factor applied to the fill specular highlight [0..1].
////        /// 0.25 is a very subtle back-fill specular (default).
////        /// </summary>
////        protected virtual float FillSpecScale => 0.25f;

////        /// <summary>
////        /// Scale factor applied to the fill diffuse contribution [0..1].
////        /// 0.35 keeps the fill from washing out the key (default).
////        /// </summary>
////        protected virtual float FillDiffScale => 0.35f;

////        // ── Interface routing — declared ONCE for all subclasses ──────────────
////        //
////        // This explicit interface implementation ensures that when the calculator
////        // calls colorMap.Map(s,d,i,nx,ny) through an IColorMap reference, it
////        // routes directly to our LitMap — NOT to the default interface method.
////        // Every subclass inherits this routing automatically.

////        /// <inheritdoc cref="IColorMap.Map(float,float,int)"/>
////        public sealed override int Map(float smooth, float distance, int maxIterations)
////            => LitMap(smooth, distance, maxIterations, 0f, 0f);

////        int IColorMap.Map(float smooth, float distance, int maxIterations,
////                          float nx, float ny)
////            => LitMap(smooth, distance, maxIterations, nx, ny);

////        // ── Core Phong implementation ─────────────────────────────────────────

////        private int LitMap(float smooth, float distance, int maxIterations,
////                            float nx, float ny)
////        {
////            if (smooth >= maxIterations)
////                return unchecked((int)0xFF000000);

////            // Sample gradient at cycling t.
////            float t = (smooth * CycleSpeed) % 1.0f;
////            int albedoI = MapNormalized(t, distance);
////            float aR = ((albedoI >> 16) & 0xFF) / 255f;
////            float aG = ((albedoI >> 8) & 0xFF) / 255f;
////            float aB = (albedoI & 0xFF) / 255f;

////            // Build 3D surface normal (ny negated for screen-space convention).
////            float ry = -ny;
////            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
////            float Nx, Ny, Nz;
////            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
////            else { Nx = 0f; Ny = 0f; Nz = 1f; }

////            // Ambient.
////            float r = aR * Ambient;
////            float g = aG * Ambient;
////            float b = aB * Ambient;

////            // Key light diffuse.
////            float dk = MathF.Max(0f, Nx * KeyLight.Lx + Ny * KeyLight.Ly + Nz * KeyLight.Lz);
////            r += dk * KeyLight.DiffR * aR;
////            g += dk * KeyLight.DiffG * aG;
////            b += dk * KeyLight.DiffB * aB;

////            // Key specular (Blinn-Phong half-vector H = normalize(L + V), V=(0,0,1)).
////            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1f;
////            float hkl = MathF.Sqrt(hkx * hkx + hky * hky + hkz * hkz);
////            if (hkl > 1e-8f)
////            {
////                hkx /= hkl; hky /= hkl; hkz /= hkl;
////                float sk = MathF.Pow(MathF.Max(0f, Nx * hkx + Ny * hky + Nz * hkz), KeyLight.Shininess) * KeySpecScale;
////                r += sk * KeyLight.SpecR;
////                g += sk * KeyLight.SpecG;
////                b += sk * KeyLight.SpecB;
////            }

////            // Fill light diffuse (scaled down to avoid washing out key).
////            float df = MathF.Max(0f, Nx * FillLight.Lx + Ny * FillLight.Ly + Nz * FillLight.Lz);
////            r += df * FillLight.DiffR * aR * FillDiffScale;
////            g += df * FillLight.DiffG * aG * FillDiffScale;
////            b += df * FillLight.DiffB * aB * FillDiffScale;

////            // Fill specular (subtle).
////            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1f;
////            float hfl = MathF.Sqrt(hfx * hfx + hfy * hfy + hfz * hfz);
////            if (hfl > 1e-8f)
////            {
////                hfx /= hfl; hfy /= hfl; hfz /= hfl;
////                float sf = MathF.Pow(MathF.Max(0f, Nx * hfx + Ny * hfy + Nz * hfz), FillLight.Shininess) * FillSpecScale;
////                r += sf * FillLight.SpecR;
////                g += sf * FillLight.SpecG;
////                b += sf * FillLight.SpecB;
////            }

////            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
////            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
////            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
////            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
////        }

////        /// <summary>
////        /// Dot Saturaturation
////        /// </summary>
////        /// <param name="x"></param>
////        /// <param name="y"></param>
////        /// <param name="z"></param>
////        /// <param name="x2"></param>
////        /// <param name="y2"></param>
////        /// <param name="z2"></param>
////        /// <returns></returns>
////        public static float DotSaturate(float x, float y, float z, float x2, float y2, float z2)
////        {
////            float d = x * x2 + y * y2 + z * z2;
////            return MathF.Max(d, 0f);
////        }

////        /// <summary>
////        /// Fresnel Schlick approximation for reflectance.
////        /// </summary>
////        /// <param name="cosTheta"></param>
////        /// <param name="F0"></param>
////        /// <returns></returns>
////        public static float FresnelSchlick(float cosTheta, float F0)
////        {
////            // Schlick approximation
////            return F0 + (1f - F0) * MathF.Pow(1f - cosTheta, 5f);
////        }

////        /// <summary>
////        /// Distribution function for microfacet normals using the GGX/Trowbridge-Reitz model.
////        /// </summary>
////        /// <param name="NdotH"></param>
////        /// <param name="roughness"></param>
////        /// <returns></returns>
////        public static float DistributionGGX(float NdotH, float roughness)
////        {
////            float a = roughness * roughness;
////            float a2 = a * a;
////            float d = (NdotH * NdotH) * (a2 - 1f) + 1f;
////            return a2 / (MathF.PI * d * d + 1e-6f);
////        }

////        /// <summary>
////        /// Geometry function using the Schlick-GGX approximation for both view and light directions.
////        /// </summary>
////        /// <param name="NdotV"></param>
////        /// <param name="NdotL"></param>
////        /// <param name="roughness"></param>
////        /// <returns></returns>
////        public static float GeometrySmith(float NdotV, float NdotL, float roughness)
////        {
////            float r = roughness + 1f;
////            float k = (r * r) / 8f; // Schlick-GGX

////            float g1V = NdotV / (NdotV * (1f - k) + k);
////            float g1L = NdotL / (NdotL * (1f - k) + k);
////            return g1V * g1L;
////        }

////    }
////}

using FracturingFog.Interefaces;

using System;

namespace FracturingFog.Models
{
    public enum PbrLightingMode
    {
        PBRRealistic,   // physically accurate, subtle
        PBRBright       // HDR boosted, glow-friendly
    }

    public abstract class PbrGradient3DBase : GradientColorMap, IColorMap
    {
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Subclasses choose the lighting profile
        protected virtual PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

        protected LightSource KeyLight;
        protected LightSource FillLight;
        protected LightSource RimLight;
        protected bool UseRimLight;

        protected virtual float CycleSpeed => 0.02f;
        protected virtual float Steepness => 1.6f;

        // Ambient is still used, but Bright mode will scale it
        protected virtual float Ambient => 0.05f;

        // Optional glow boost (Cesium uses this)
        protected virtual float GlowBoost(float t) => 0f;

        // Per-mode tuning of the new linear-HDR + ACES pipeline.
        //   exposure   — overall pre-tone-map gain on direct lights
        //   rimBoost   — strength of the Fresnel rim term (PBR-only edge glow)
        //   glowMult   — multiplies subclass GlowBoost() before tone-map
        //   specBoost  — visual amplifier on the Cook-Torrance spec lobe
        //                (compensates for point-light vs. env-map integration)
        private (float exposure, float rimBoost, float glowMult, float specBoost)
            GetLightingParams()
        {
            return LightingMode switch
            {
                PbrLightingMode.PBRBright =>
                    (exposure: 1.6f, rimBoost: 1.2f, glowMult: 1.5f, specBoost: 2.4f),

                _ => // PBRRealistic
                    (exposure: 1.0f, rimBoost: 0.7f, glowMult: 1.0f, specBoost: 1.8f)
            };
        }

        // Subclasses override this to define metal/roughness per gradient band
        protected virtual PbrMaterial BuildMaterial(float t, float r, float g, float b)
            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.7f);

        // ── Export accessors (used by JSON serialisation) ─────────────────────

        public LightSource ExportKeyLight => KeyLight;
        public LightSource ExportFillLight => FillLight;
        public LightSource ExportRimLight => RimLight;
        public bool ExportUseRimLight => UseRimLight;
        public float ExportCycleSpeed => CycleSpeed;
        public float ExportSteepness => Steepness;
        public float ExportAmbient => Ambient;
        public PbrLightingMode ExportLightingMode => LightingMode;

        /// <summary>Sample the glow boost at <paramref name="t"/> for export.</summary>
        public float ExportGlowBoost(float t) => GlowBoost(t);

        /// <summary>Sample the metal/roughness function at <paramref name="t"/> for export.</summary>
        public PbrMaterial ExportMaterial(float t)
            => BuildMaterial(t, 1f, 1f, 1f);

        // Interface routing
        public sealed override int Map(float smooth, float distance, int maxIterations)
            => LitMapPbr(smooth, distance, maxIterations, 0f, 0f);

        int IColorMap.Map(float smooth, float distance, int maxIterations,
                          float nx, float ny)
            => LitMapPbr(smooth, distance, maxIterations, nx, ny);

        // ───────────────────────────────────────────────────────────────
        // Core PBR implementation
        // ───────────────────────────────────────────────────────────────
        // Pipeline:
        //   1. Sample gradient (sRGB)            → linear albedo
        //   2. Direct lighting (Cook-Torrance)   in linear HDR
        //      kD  = (1 - F) · (1 - metalness)   — energy-conserving
        //      kS  = F · D · G / (4 N·V N·L)     — boosted by specBoost
        //      Lambert diffuse without /π        — π folded into light scale
        //   3. Fresnel rim term                  — (1 - N·V)^5 · F0 · rimBoost
        //                                          gives PBR-only edge glow
        //                                          metals get colored rim
        //   4. Ambient split:
        //        diffuse   = (1 - metal) · albedo · A
        //        spec      = F0 · A · 0.5         (env-map approximation —
        //                                          metals reflect a tinted
        //                                          ambient even in shadow)
        //   5. GlowBoost                         — albedo-tinted, pre-tone-map
        //   6. ACES filmic tone-map              — preserves shadow blacks,
        //                                          rolls highlights smoothly
        //   7. Linear → sRGB encode for output.
        //
        // What makes this distinctly PBR (vs. Phong):
        //   - Linear-space math + ACES gives cinematic highlight rolloff.
        //   - Metalness ramps in BuildMaterial are visible everywhere
        //     (ambient+rim are F0-tinted), not just on highlights.
        //   - Fresnel rim adds an edge glow Phong can't naturally produce.
        //   - Roughness controls highlight tightness via GGX, so the same
        //     gradient can range from chalky to mirror-finish.
        private int LitMapPbr(float smooth, float distance, int maxIterations,
                              float nx, float ny)
        {
            if (smooth >= maxIterations)
                return unchecked((int)0xFF000000);

            var (exposure, rimBoost, glowMult, specBoost) = GetLightingParams();

            // 1. Sample gradient (sRGB), convert to linear for PBR math.
            float t = (smooth * CycleSpeed) % 1f;
            int albedoI = MapNormalized(t, distance);

            float aR = PbrMath.SrgbToLinear(((albedoI >> 16) & 0xFF) / 255f);
            float aG = PbrMath.SrgbToLinear(((albedoI >> 8) & 0xFF) / 255f);
            float aB = PbrMath.SrgbToLinear((albedoI & 0xFF) / 255f);

            // 2. Material
            PbrMaterial mat = BuildMaterial(t, aR, aG, aB);
            mat.ComputeF0(out float F0R, out float F0G, out float F0B);

            // 3. Surface normal (screen-space convention: invert ny)
            float ry = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
            else { Nx = 0f; Ny = 0f; Nz = 1f; }

            float Vx = 0f, Vy = 0f, Vz = 1f;
            float NdotV = MathF.Max(1e-4f, Nx * Vx + Ny * Vy + Nz * Vz);

            float r = 0f, g = 0f, b = 0f;

            // 4. Direct lights (key + fill + optional rim)
            AddLight(KeyLight, exposure, ref r, ref g, ref b);
            AddLight(FillLight, exposure, ref r, ref g, ref b);
            if (UseRimLight) AddLight(RimLight, exposure, ref r, ref g, ref b);

            // 5. Ambient — diffuse for non-metals, F0-tinted spec for metals.
            float kdAmb = 1f - mat.Metalness;
            float ambSpec = Ambient * 0.5f;
            r += mat.BaseR * kdAmb * Ambient + F0R * ambSpec;
            g += mat.BaseG * kdAmb * Ambient + F0G * ambSpec;
            b += mat.BaseB * kdAmb * Ambient + F0B * ambSpec;

            // 6. Fresnel rim — distinct PBR edge glow tinted by F0.
            float rim = MathF.Pow(1f - NdotV, 5f) * rimBoost;
            r += F0R * rim;
            g += F0G * rim;
            b += F0B * rim;

            // 7. Glow boost (pre-tone-map, albedo-tinted so it follows palette).
            float glow = GlowBoost(t) * glowMult;
            r += glow * mat.BaseR;
            g += glow * mat.BaseG;
            b += glow * mat.BaseB;

            // 8. ACES filmic tone-map (linear HDR → linear LDR [0,1])
            r = PbrMath.AcesFilmic(r);
            g = PbrMath.AcesFilmic(g);
            b = PbrMath.AcesFilmic(b);

            // 9. Linear → sRGB encode for display.
            byte R = (byte)(Math.Clamp(PbrMath.LinearToSrgb(r), 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(PbrMath.LinearToSrgb(g), 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(PbrMath.LinearToSrgb(b), 0f, 1f) * 255f);

            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);

            // ── Local: Cook-Torrance contribution from one light ────────────
            void AddLight(LightSource src, float exp,
                          ref float outR, ref float outG, ref float outB)
            {
                float Lx = src.Lx, Ly = src.Ly, Lz = src.Lz;
                float Ll = MathF.Sqrt(Lx * Lx + Ly * Ly + Lz * Lz);
                if (Ll < 1e-6f) return;
                Lx /= Ll; Ly /= Ll; Lz /= Ll;

                float NdotL = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);
                if (NdotL <= 0f) return;

                float Hx = Vx + Lx, Hy = Vy + Ly, Hz = Vz + Lz;
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

                float denom = 4f * NdotV * MathF.Max(NdotL, 1e-4f);
                float specScale = (D * Gs) / denom * specBoost;

                float specR = Fr * specScale;
                float specG = Fg * specScale;
                float specB = Fb * specScale;

                // Energy-conserving kD: subtract Fresnel reflectance, zero for metals.
                float oneMinusMetal = 1f - mat.Metalness;
                float kdR = (1f - Fr) * oneMinusMetal;
                float kdG = (1f - Fg) * oneMinusMetal;
                float kdB = (1f - Fb) * oneMinusMetal;

                // Lambert diffuse — no /π (folded into light intensity scale).
                float diffR = kdR * mat.BaseR;
                float diffG = kdG * mat.BaseG;
                float diffB = kdB * mat.BaseB;

                float Lr = src.DiffR * exp;
                float Lg = src.DiffG * exp;
                float Lb = src.DiffB * exp;

                outR += (diffR + specR) * Lr * NdotL;
                outG += (diffG + specG) * Lg * NdotL;
                outB += (diffB + specB) * Lb * NdotL;
            }
        }
    }


    // ─────────────────────────────────────────────────────────────────────────
    // Material struct
    // ─────────────────────────────────────────────────────────────────────────

    public readonly struct PbrMaterial
    {
        public readonly float BaseR, BaseG, BaseB;
        public readonly float Metalness;
        public readonly float Roughness;

        public PbrMaterial(float r, float g, float b, float metalness, float roughness)
        {
            BaseR = r; BaseG = g; BaseB = b;
            Metalness = Math.Clamp(metalness, 0f, 1f);
            Roughness = Math.Clamp(roughness, 0.04f, 1f);
        }

        public void ComputeF0(out float F0R, out float F0G, out float F0B)
        {
            const float dielectric = 0.04f;
            float oneMinusMetal = 1f - Metalness;

            F0R = dielectric * oneMinusMetal + BaseR * Metalness;
            F0G = dielectric * oneMinusMetal + BaseG * Metalness;
            F0B = dielectric * oneMinusMetal + BaseB * Metalness;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PBR math helpers
    // ─────────────────────────────────────────────────────────────────────────

    public static class PbrMath
    {
        public static float FresnelSchlick(float cosTheta, float F0)
        {
            return F0 + (1f - F0) * MathF.Pow(1f - cosTheta, 5f);
        }

        public static float DistributionGGX(float NdotH, float roughness)
        {
            float a = roughness * roughness;
            float a2 = a * a;
            float d = (NdotH * NdotH) * (a2 - 1f) + 1f;
            return a2 / (MathF.PI * d * d + 1e-6f);
        }

        public static float GeometrySmith(float NdotV, float NdotL, float roughness)
        {
            float r = roughness + 1f;
            float k = (r * r) / 8f;

            float g1V = NdotV / (NdotV * (1f - k) + k);
            float g1L = NdotL / (NdotL * (1f - k) + k);
            return g1V * g1L;
        }

        /// <summary>
        /// Smooth-step interpolation between two values using Hermite blending.
        /// Used to remove hard edges from material breakpoints in BuildMaterial().
        /// <paramref name="edge0"/> and <paramref name="edge1"/> define the ramp range;
        /// the return value is in [a, b].
        /// </summary>
        public static float SmoothLerp(float t, float edge0, float edge1, float a, float b)
        {
            // Clamp and smooth-step t into [0, 1] over [edge0, edge1].
            float x = Math.Clamp((t - edge0) / (edge1 - edge0 + 1e-6f), 0f, 1f);
            float s = x * x * (3f - 2f * x);   // Hermite cubic
            return a + s * (b - a);
        }

        // sRGB ↔ linear conversion (IEC 61966-2-1).  Required for correct PBR:
        // gradient stops are authored in sRGB, but the lighting math is linear.
        public static float SrgbToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            if (c <= 0f) return 0f;
            if (c <= 0.0031308f) return c * 12.92f;
            return 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <summary>
        /// ACES filmic tone-map (Narkowicz fitted curve).  Maps linear HDR
        /// radiance into [0, 1] with cinematic shoulder + toe — preserves
        /// shadow blacks (f(0)=0) and rolls highlights smoothly.
        /// </summary>
        public static float AcesFilmic(float x)
        {
            if (x <= 0f) return 0f;
            float a = x * (2.51f * x + 0.03f);
            float b = x * (2.43f * x + 0.59f) + 0.14f;
            return Math.Clamp(a / b, 0f, 1f);
        }
    }
}
//////using FracturingFog.Interefaces;
//////using System;

//////namespace FracturingFog.Models
//////{
//////    /// <summary>
//////    /// Pbr light source definition for 3D Phong shading.
//////    /// </summary>
//////    public struct PbrLight
//////    {
//////        public float Lx, Ly, Lz;      // direction (normalized)
//////        public float R, G, B;        // light color (radiance)
//////    }

//////    /// <summary>
//////    /// Abstract base class for gradient-based 3D Phong colour maps.
//////    /// Subclasses set gradient stops, KeyLight, FillLight, and optionally
//////    /// CycleSpeed and Steepness; the base handles all lighting maths.
//////    /// </summary>
//////    public abstract class PbrGradient3DBase : GradientColorMap, IColorMap
//////    {
//////        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

//////        // ── Subclass-configurable properties ──────────────────────────────────

//////        /// <summary>
//////        /// Primary (key) light source — the dominant light that defines
//////        /// where highlights fall.  Set in the subclass constructor.
//////        /// </summary>
//////        protected LightSource KeyLight;

//////        /// <summary>
//////        /// Secondary (fill) light — softer, from the opposite side.
//////        /// Colours shadowed faces so they're not flat black.
//////        /// Set in the subclass constructor.
//////        /// </summary>
//////        protected LightSource FillLight;

//////        /// <summary>
//////        /// Controls how many gradient cycles appear per 1/CycleSpeed smooth-units.
//////        /// Match the value used by the flat counterpart (default 0.02 = cycle
//////        /// every 50 smooth-units).
//////        /// </summary>
//////        protected virtual float CycleSpeed => 0.02f;

//////        /// <summary>
//////        /// Controls 3D depth drama.
//////        /// 0.9  = deep carving / dramatic shadows
//////        /// 1.6  = balanced (default)
//////        /// 2.5  = gentle emboss / subtle relief
//////        /// </summary>
//////        protected virtual float Steepness => 1.6f;

//////        /// <summary>
//////        /// Ambient light scale [0..1].  Determines the minimum brightness
//////        /// of fully-shadowed pixels.  0.12 keeps shadows dark; raise to
//////        /// 0.25 to lift shadows and reveal more detail in dark areas.
//////        /// </summary>
//////        protected virtual float Ambient => 0.12f;

//////        /// <summary>
//////        /// Scale factor applied to the key specular highlight [0..1].
//////        /// 0.85 gives a strong but not overblown specular (default).
//////        /// </summary>
//////        protected virtual float KeySpecScale => 0.85f;

//////        /// <summary>
//////        /// Scale factor applied to the fill specular highlight [0..1].
//////        /// 0.25 is a very subtle back-fill specular (default).
//////        /// </summary>
//////        protected virtual float FillSpecScale => 0.25f;

//////        /// <summary>
//////        /// Scale factor applied to the fill diffuse contribution [0..1].
//////        /// 0.35 keeps the fill from washing out the key (default).
//////        /// </summary>
//////        protected virtual float FillDiffScale => 0.35f;

//////        // ── Interface routing — declared ONCE for all subclasses ──────────────
//////        //
//////        // This explicit interface implementation ensures that when the calculator
//////        // calls colorMap.Map(s,d,i,nx,ny) through an IColorMap reference, it
//////        // routes directly to our LitMap — NOT to the default interface method.
//////        // Every subclass inherits this routing automatically.

//////        /// <inheritdoc cref="IColorMap.Map(float,float,int)"/>
//////        public sealed override int Map(float smooth, float distance, int maxIterations)
//////            => LitMap(smooth, distance, maxIterations, 0f, 0f);

//////        int IColorMap.Map(float smooth, float distance, int maxIterations,
//////                          float nx, float ny)
//////            => LitMap(smooth, distance, maxIterations, nx, ny);

//////        // ── Core Phong implementation ─────────────────────────────────────────

//////        private int LitMap(float smooth, float distance, int maxIterations,
//////                            float nx, float ny)
//////        {
//////            if (smooth >= maxIterations)
//////                return unchecked((int)0xFF000000);

//////            // Sample gradient at cycling t.
//////            float t = (smooth * CycleSpeed) % 1.0f;
//////            int albedoI = MapNormalized(t, distance);
//////            float aR = ((albedoI >> 16) & 0xFF) / 255f;
//////            float aG = ((albedoI >> 8) & 0xFF) / 255f;
//////            float aB = (albedoI & 0xFF) / 255f;

//////            // Build 3D surface normal (ny negated for screen-space convention).
//////            float ry = -ny;
//////            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
//////            float Nx, Ny, Nz;
//////            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
//////            else { Nx = 0f; Ny = 0f; Nz = 1f; }

//////            // Ambient.
//////            float r = aR * Ambient;
//////            float g = aG * Ambient;
//////            float b = aB * Ambient;

//////            // Key light diffuse.
//////            float dk = MathF.Max(0f, Nx * KeyLight.Lx + Ny * KeyLight.Ly + Nz * KeyLight.Lz);
//////            r += dk * KeyLight.DiffR * aR;
//////            g += dk * KeyLight.DiffG * aG;
//////            b += dk * KeyLight.DiffB * aB;

//////            // Key specular (Blinn-Phong half-vector H = normalize(L + V), V=(0,0,1)).
//////            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1f;
//////            float hkl = MathF.Sqrt(hkx * hkx + hky * hky + hkz * hkz);
//////            if (hkl > 1e-8f)
//////            {
//////                hkx /= hkl; hky /= hkl; hkz /= hkl;
//////                float sk = MathF.Pow(MathF.Max(0f, Nx * hkx + Ny * hky + Nz * hkz), KeyLight.Shininess) * KeySpecScale;
//////                r += sk * KeyLight.SpecR;
//////                g += sk * KeyLight.SpecG;
//////                b += sk * KeyLight.SpecB;
//////            }

//////            // Fill light diffuse (scaled down to avoid washing out key).
//////            float df = MathF.Max(0f, Nx * FillLight.Lx + Ny * FillLight.Ly + Nz * FillLight.Lz);
//////            r += df * FillLight.DiffR * aR * FillDiffScale;
//////            g += df * FillLight.DiffG * aG * FillDiffScale;
//////            b += df * FillLight.DiffB * aB * FillDiffScale;

//////            // Fill specular (subtle).
//////            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1f;
//////            float hfl = MathF.Sqrt(hfx * hfx + hfy * hfy + hfz * hfz);
//////            if (hfl > 1e-8f)
//////            {
//////                hfx /= hfl; hfy /= hfl; hfz /= hfl;
//////                float sf = MathF.Pow(MathF.Max(0f, Nx * hfx + Ny * hfy + Nz * hfz), FillLight.Shininess) * FillSpecScale;
//////                r += sf * FillLight.SpecR;
//////                g += sf * FillLight.SpecG;
//////                b += sf * FillLight.SpecB;
//////            }

//////            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
//////            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
//////            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
//////            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
//////        }

//////        /// <summary>
//////        /// Dot Saturaturation
//////        /// </summary>
//////        /// <param name="x"></param>
//////        /// <param name="y"></param>
//////        /// <param name="z"></param>
//////        /// <param name="x2"></param>
//////        /// <param name="y2"></param>
//////        /// <param name="z2"></param>
//////        /// <returns></returns>
//////        public static float DotSaturate(float x, float y, float z, float x2, float y2, float z2)
//////        {
//////            float d = x * x2 + y * y2 + z * z2;
//////            return MathF.Max(d, 0f);
//////        }

//////        /// <summary>
//////        /// Fresnel Schlick approximation for reflectance.
//////        /// </summary>
//////        /// <param name="cosTheta"></param>
//////        /// <param name="F0"></param>
//////        /// <returns></returns>
//////        public static float FresnelSchlick(float cosTheta, float F0)
//////        {
//////            // Schlick approximation
//////            return F0 + (1f - F0) * MathF.Pow(1f - cosTheta, 5f);
//////        }

//////        /// <summary>
//////        /// Distribution function for microfacet normals using the GGX/Trowbridge-Reitz model.
//////        /// </summary>
//////        /// <param name="NdotH"></param>
//////        /// <param name="roughness"></param>
//////        /// <returns></returns>
//////        public static float DistributionGGX(float NdotH, float roughness)
//////        {
//////            float a = roughness * roughness;
//////            float a2 = a * a;
//////            float d = (NdotH * NdotH) * (a2 - 1f) + 1f;
//////            return a2 / (MathF.PI * d * d + 1e-6f);
//////        }

//////        /// <summary>
//////        /// Geometry function using the Schlick-GGX approximation for both view and light directions.
//////        /// </summary>
//////        /// <param name="NdotV"></param>
//////        /// <param name="NdotL"></param>
//////        /// <param name="roughness"></param>
//////        /// <returns></returns>
//////        public static float GeometrySmith(float NdotV, float NdotL, float roughness)
//////        {
//////            float r = roughness + 1f;
//////            float k = (r * r) / 8f; // Schlick-GGX

//////            float g1V = NdotV / (NdotV * (1f - k) + k);
//////            float g1L = NdotL / (NdotL * (1f - k) + k);
//////            return g1V * g1L;
//////        }

//////    }
//////}

//using FracturingFog.Interefaces;

//using System;

//namespace FracturingFog.Models
//{
//    public enum PbrLightingMode
//    {
//        PBRRealistic,   // physically accurate, subtle
//        PBRBright       // HDR boosted, glow-friendly
//    }

//    public abstract class PbrGradient3DBase : GradientColorMap, IColorMap
//    {
//        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

//        // Subclasses choose the lighting profile
//        protected virtual PbrLightingMode LightingMode => PbrLightingMode.PBRRealistic;

//        protected LightSource KeyLight;
//        protected LightSource FillLight;

//        protected virtual float CycleSpeed => 0.02f;
//        protected virtual float Steepness => 1.6f;

//        // Ambient is still used, but Bright mode will scale it
//        protected virtual float Ambient => 0.05f;

//        // Optional glow boost (Cesium uses this)
//        protected virtual float GlowBoost(float t) => 0f;

//        // Light multipliers based on mode
//        private (float key, float fill, float ambient, float toneMapBias) GetLightingMultipliers()
//        {
//            return LightingMode switch
//            {
//                PbrLightingMode.PBRBright =>
//                    (key: 3.0f, fill: 2.0f, ambient: 1.8f, toneMapBias: 0.2f),

//                _ => // PBRRealistic
//                    (key: 1.0f, fill: 1.0f, ambient: 1.4f, toneMapBias: 0.5f)
//            };
//        }

//        // Subclasses override this to define metal/roughness per gradient band
//        protected virtual PbrMaterial BuildMaterial(float t, float r, float g, float b)
//            => new PbrMaterial(r, g, b, metalness: 0.0f, roughness: 0.7f);

//        // ── Export accessors (used by JSON serialisation) ─────────────────────

//        public LightSource ExportKeyLight => KeyLight;
//        public LightSource ExportFillLight => FillLight;
//        public float ExportCycleSpeed => CycleSpeed;
//        public float ExportSteepness => Steepness;
//        public float ExportAmbient => Ambient;
//        public PbrLightingMode ExportLightingMode => LightingMode;

//        /// <summary>Sample the glow boost at <paramref name="t"/> for export.</summary>
//        public float ExportGlowBoost(float t) => GlowBoost(t);

//        /// <summary>Sample the metal/roughness function at <paramref name="t"/> for export.</summary>
//        public PbrMaterial ExportMaterial(float t)
//            => BuildMaterial(t, 1f, 1f, 1f);

//        // Interface routing
//        public sealed override int Map(float smooth, float distance, int maxIterations)
//            => LitMapPbr(smooth, distance, maxIterations, 0f, 0f);

//        int IColorMap.Map(float smooth, float distance, int maxIterations,
//                          float nx, float ny)
//            => LitMapPbr(smooth, distance, maxIterations, nx, ny);

//        // ───────────────────────────────────────────────────────────────
//        // Core PBR implementation (Cook–Torrance GGX)
//        // ───────────────────────────────────────────────────────────────
//        private int LitMapPbr(float smooth, float distance, int maxIterations,
//                              float nx, float ny)
//        {
//            if (smooth >= maxIterations)
//                return unchecked((int)0xFF000000);

//            var (keyMul, fillMul, ambMul, toneBias) = GetLightingMultipliers();

//            // 1. Sample gradient
//            float t = (smooth * CycleSpeed) % 1f;
//            int albedoI = MapNormalized(t, distance);

//            float aR = ((albedoI >> 16) & 0xFF) / 255f;
//            float aG = ((albedoI >> 8) & 0xFF) / 255f;
//            float aB = (albedoI & 0xFF) / 255f;

//            // 2. Material
//            float smoothStep = smooth / maxIterations;
//            PbrMaterial mat = BuildMaterial(t, aR, aG, aB);
//            //PbrMaterial mat = BuildMaterial(t, aR * smoothStep, aG * smoothStep, aB * smoothStep);
//            mat.ComputeF0(out float F0R, out float F0G, out float F0B);

//            // 3. Normal
//            float ry = -ny;
//            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);

//            float Nx, Ny, Nz;
//            if (len > 1e-8f) { Nx = nx / len; Ny = ry / len; Nz = Steepness / len; }
//            else { Nx = 0f; Ny = 0f; Nz = 1f; }

//            float Vx = 0f, Vy = 0f, Vz = 1f;
//            float NdotV = MathF.Max(0f, Nx * Vx + Ny * Vy + Nz * Vz);

//            float r = 0f, g = 0f, b = 0f;

//            // 4. Lights
//            AddLight(KeyLight, keyMul, ref r, ref g, ref b);
//            AddLight(FillLight, fillMul, ref r, ref g, ref b);

//            // 5. Ambient
//            float amb = Ambient * ambMul;
//            r += mat.BaseR * amb;
//            g += mat.BaseG * amb;
//            b += mat.BaseB * amb;

//            // 6. Optional glow boost (Cesium uses this)
//            float glow = GlowBoost(t);
//            r += glow;
//            g += glow * 0.9f;
//            b += glow * 1.2f;

//            // 7. Tone mapping (Reinhard2)
//            r = r / (toneBias + r);
//            g = g / (toneBias + g);
//            b = b / (toneBias + b);

//            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
//            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
//            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);

//            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);

//            // Local function
//            void AddLight(LightSource src, float mul, ref float outR, ref float outG, ref float outB)
//            {
//                float Lx = src.Lx, Ly = src.Ly, Lz = src.Lz;
//                float Ll = MathF.Sqrt(Lx * Lx + Ly * Ly + Lz * Lz);
//                if (Ll < 1e-6f) return;
//                Lx /= Ll; Ly /= Ll; Lz /= Ll;

//                float NdotL = MathF.Max(0f, Nx * Lx + Ny * Ly + Nz * Lz);
//                if (NdotL <= 0f) return;

//                float Hx = Vx + Lx;
//                float Hy = Vy + Ly;
//                float Hz = Vz + Lz;
//                float Hl = MathF.Sqrt(Hx * Hx + Hy * Hy + Hz * Hz);
//                if (Hl < 1e-6f) return;
//                Hx /= Hl; Hy /= Hl; Hz /= Hl;

//                float NdotH = MathF.Max(0f, Nx * Hx + Ny * Hy + Nz * Hz);
//                float VdotH = MathF.Max(0f, Vx * Hx + Vy * Hy + Vz * Hz);

//                float D = PbrMath.DistributionGGX(NdotH, mat.Roughness);
//                float Gs = PbrMath.GeometrySmith(NdotV, NdotL, mat.Roughness);

//                float Fr = PbrMath.FresnelSchlick(VdotH, F0R);
//                float Fg = PbrMath.FresnelSchlick(VdotH, F0G);
//                float Fb = PbrMath.FresnelSchlick(VdotH, F0B);

//                float denom = 4f * MathF.Max(NdotV, 1e-4f) * MathF.Max(NdotL, 1e-4f);
//                float specScale = (D * Gs) / denom;

//                float specR = Fr * specScale;
//                float specG = Fg * specScale;
//                float specB = Fb * specScale;

//                float kd = 1f - mat.Metalness;
//                float diffR = kd * mat.BaseR / MathF.PI;
//                float diffG = kd * mat.BaseG / MathF.PI;
//                float diffB = kd * mat.BaseB / MathF.PI;

//                float Lr = src.DiffR * mul;
//                float Lg = src.DiffG * mul;
//                float Lb = src.DiffB * mul;

//                outR += (diffR + specR) * Lr * NdotL;
//                outG += (diffG + specG) * Lg * NdotL;
//                outB += (diffB + specB) * Lb * NdotL;
//            }
//        }
//    }


//    // ─────────────────────────────────────────────────────────────────────────
//    // Material struct
//    // ─────────────────────────────────────────────────────────────────────────

//    public readonly struct PbrMaterial
//    {
//        public readonly float BaseR, BaseG, BaseB;
//        public readonly float Metalness;
//        public readonly float Roughness;

//        public PbrMaterial(float r, float g, float b, float metalness, float roughness)
//        {
//            BaseR = r; BaseG = g; BaseB = b;
//            Metalness = Math.Clamp(metalness, 0f, 1f);
//            Roughness = Math.Clamp(roughness, 0.04f, 1f);
//        }

//        public void ComputeF0(out float F0R, out float F0G, out float F0B)
//        {
//            const float dielectric = 0.04f;
//            float oneMinusMetal = 1f - Metalness;

//            F0R = dielectric * oneMinusMetal + BaseR * Metalness;
//            F0G = dielectric * oneMinusMetal + BaseG * Metalness;
//            F0B = dielectric * oneMinusMetal + BaseB * Metalness;
//        }
//    }

//    // ─────────────────────────────────────────────────────────────────────────
//    // PBR math helpers
//    // ─────────────────────────────────────────────────────────────────────────

//    public static class PbrMath
//    {
//        public static float FresnelSchlick(float cosTheta, float F0)
//        {
//            return F0 + (1f - F0) * MathF.Pow(1f - cosTheta, 5f);
//        }

//        public static float DistributionGGX(float NdotH, float roughness)
//        {
//            float a = roughness * roughness;
//            float a2 = a * a;
//            float d = (NdotH * NdotH) * (a2 - 1f) + 1f;
//            return a2 / (MathF.PI * d * d + 1e-6f);
//        }

//        public static float GeometrySmith(float NdotV, float NdotL, float roughness)
//        {
//            float r = roughness + 1f;
//            float k = (r * r) / 8f;

//            float g1V = NdotV / (NdotV * (1f - k) + k);
//            float g1L = NdotL / (NdotL * (1f - k) + k);
//            return g1V * g1L;
//        }

//        /// <summary>
//        /// Smooth-step interpolation between two values using Hermite blending.
//        /// Used to remove hard edges from material breakpoints in BuildMaterial().
//        /// <paramref name="edge0"/> and <paramref name="edge1"/> define the ramp range;
//        /// the return value is in [a, b].
//        /// </summary>
//        public static float SmoothLerp(float t, float edge0, float edge1, float a, float b)
//        {
//            // Clamp and smooth-step t into [0, 1] over [edge0, edge1].
//            float x = Math.Clamp((t - edge0) / (edge1 - edge0 + 1e-6f), 0f, 1f);
//            float s = x * x * (3f - 2f * x);   // Hermite cubic
//            return a + s * (b - a);
//        }
//    }
//}