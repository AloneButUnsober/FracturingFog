// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// PhongFxBridge.cs
//
// Phase 8 — bridge between the shared LightingFxData (used by every 3D
// raymarcher) and the 2D fractal Phong theme stack (PhongHelper +
// LightSource). Theme authors who want a theme that responds to the
// "Lighting & FX" params dialog call PhongFxBridge.BuildLightSources(...)
// instead of hard-coding a LightSource[]; the existing 30+ themes keep
// their hand-tuned hard-coded rigs untouched.
//
// Bridge layout
//   LightingFxData.Light{1,2,3}  →  LightSource{0,1,2}
//   spherical (theta, phi)       →  unit (Lx, Ly, Lz) world dir
//   intensity × color (BGRA)     →  diffuse RGB (0..1)
//   per-theme shininess          →  spec exponent (caller-supplied)
//
// Notes
//   • Lights with Intensity = 0 are returned as zero-contribution slots so
//     iteration in PhongColor stays branchless.
//   • Specular colour defaults to the diffuse colour scaled by intensity so
//     metallic-looking 2D surfaces stay tinted; theme authors who want
//     achromatic highlights can override after the call.
//   • The complex-plane normal sign convention is handled by
//     PhongHelper.NormalFromRaw, NOT here. Bridge speaks world-space directly.

using System;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Models;

public static class PhongFxBridge
{
    /// <summary>
    /// Build a 3-slot light rig from the shared LightingFxData. Lights with
    /// Intensity = 0 are returned as zero-contribution slots; AccumulateLight
    /// short-circuits on diff &lt;= 0 so they cost nothing per pixel.
    /// </summary>
    /// <param name="fx">Active shared lighting parameters.</param>
    /// <param name="shininess">Blinn-Phong specular exponent. Higher = tighter
    /// highlight. Typical theme range: 16 (matte) to 256 (mirror).</param>
    public static LightSource[] BuildLightSources(in LightingFxData fx, float shininess = 64f)
    {
        return new[]
        {
            BuildOne(fx.Light1, shininess),
            BuildOne(fx.Light2, shininess),
            BuildOne(fx.Light3, shininess),
        };
    }

    /// <summary>
    /// Ambient strength derived from the shared rig. 2D Phong themes that
    /// honour <see cref="LightingFxData.IblStrength"/> sample env separately;
    /// scalar callers can use this and multiply by base colour as usual.
    /// </summary>
    public static float AmbientFromFx(in LightingFxData fx) => (float)fx.AmbientStrength;

    /// <summary>
    /// Resolve (theta, phi) spherical light to a unit world-space direction.
    /// Mirrors <see cref="ShadingPipeline.LightDir"/> so 2D and 3D paths read
    /// the same orientation from a single source of truth.
    /// </summary>
    public static (float X, float Y, float Z) DirectionOf(in DirectionalLight d)
    {
        double sinPhi = Math.Sin(d.Phi);
        return (
            (float)(sinPhi * Math.Cos(d.Theta)),
            (float)Math.Cos(d.Phi),
            (float)(sinPhi * Math.Sin(d.Theta)));
    }

    private static LightSource BuildOne(DirectionalLight d, float shininess)
    {
        if (d.Intensity <= 0)
        {
            // Zero-contribution slot. Direction is irrelevant but must be
            // non-zero so the LightSource constructor's normalisation doesn't
            // divide by zero.
            return new LightSource(
                0f, 1f, 0f,
                0f, 0f, 0f,
                0f, 0f, 0f,
                shininess);
        }
        var dir = DirectionOf(d);
        float scale = (float)d.Intensity;
        float r = ((d.Color >> 16) & 0xFF) / 255f * scale;
        float g = ((d.Color >>  8) & 0xFF) / 255f * scale;
        float b = ( d.Color        & 0xFF) / 255f * scale;
        return new LightSource(
            dir.X, dir.Y, dir.Z,
            r, g, b,        // diffuse tinted by light colour × intensity
            r, g, b,        // spec matches diffuse so metallic surfaces stay tinted
            shininess);
    }
}
