// UserBulbTemporalCache.cs
//
// Frame-to-frame reuse for UserBulbCalculator. Two reuse modes:
//
//   1. Identity blit: scene key unchanged + camera unchanged → return cached
//      buffer verbatim. Zero render cost.
//
//   2. Forward reprojection: scene key unchanged + camera within small delta
//      → project each cached pixel's world hit point into the new camera and
//      splat the cached color. Holes (where no prior pixel projects) are
//      marked dirty for raymarch.
//
// Anything bigger than the reprojection budget → invalidate, full render,
// rebuild cache.

using System;

namespace FracturingFog.Calculators;

public sealed class UserBulbTemporalCache
{
    public uint[]? Buffer;
    public double[]? HitX, HitY, HitZ;
    public bool[]? Hit;
    public int Width, Height;

    public string SceneKey = string.Empty;
    public double CamX, CamY, CamZ;
    public double TargetX, TargetY, TargetZ;
    public double FwdX, FwdY, FwdZ;
    public double RightX, RightY, RightZ;
    public double UpX, UpY, UpZ;
    public double FovScale, Aspect;

    public bool HasCache => Buffer != null;

    /// <summary>Reuse decision based on camera/scene deltas. Returns flags.</summary>
    public ReuseDecision Decide(
        string sceneKey, int w, int h,
        double camX, double camY, double camZ,
        double fwdX, double fwdY, double fwdZ)
    {
        if (!HasCache || Width != w || Height != h || SceneKey != sceneKey)
            return ReuseDecision.None;

        double dPos = Math.Sqrt(
            (camX - CamX) * (camX - CamX) +
            (camY - CamY) * (camY - CamY) +
            (camZ - CamZ) * (camZ - CamZ));

        double dFwd = Math.Acos(Math.Clamp(
            FwdX * fwdX + FwdY * fwdY + FwdZ * fwdZ, -1.0, 1.0));

        // Identity: nothing moved measurably.
        if (dPos < 1e-9 && dFwd < 1e-9) return ReuseDecision.Identity;

        // Reproject budget: rotation < 5°, position delta < 5% of camera range.
        double camDist = Math.Sqrt(CamX * CamX + CamY * CamY + CamZ * CamZ);
        if (dFwd < 5.0 * Math.PI / 180.0 && dPos < camDist * 0.05)
            return ReuseDecision.Reproject;

        return ReuseDecision.None;
    }

    public void Save(uint[] buffer, double[] hx, double[] hy, double[] hz, bool[] hit,
        int w, int h, string sceneKey,
        double camX, double camY, double camZ,
        double tgtX, double tgtY, double tgtZ,
        double fwdX, double fwdY, double fwdZ,
        double rightX, double rightY, double rightZ,
        double upX, double upY, double upZ,
        double fovScale, double aspect)
    {
        if (Buffer == null || Buffer.Length != buffer.Length) Buffer = new uint[buffer.Length];
        Array.Copy(buffer, Buffer, buffer.Length);
        HitX = (double[])hx.Clone();
        HitY = (double[])hy.Clone();
        HitZ = (double[])hz.Clone();
        Hit = (bool[])hit.Clone();
        Width = w; Height = h; SceneKey = sceneKey;
        CamX = camX; CamY = camY; CamZ = camZ;
        TargetX = tgtX; TargetY = tgtY; TargetZ = tgtZ;
        FwdX = fwdX; FwdY = fwdY; FwdZ = fwdZ;
        RightX = rightX; RightY = rightY; RightZ = rightZ;
        UpX = upX; UpY = upY; UpZ = upZ;
        FovScale = fovScale; Aspect = aspect;
    }

    public void Invalidate() { Buffer = null; HitX = HitY = HitZ = null; Hit = null; }

    /// <summary>Forward-reproject cached pixels into dst. Marks reprojected
    /// pixels in covered[]. Caller raymarches the uncovered ones.</summary>
    public void Reproject(
        uint[] dst, bool[] covered,
        int dstW, int dstH,
        double newCamX, double newCamY, double newCamZ,
        double newFwdX, double newFwdY, double newFwdZ,
        double newRightX, double newRightY, double newRightZ,
        double newUpX, double newUpY, double newUpZ,
        double newFovScale, double newAspect)
    {
        if (Buffer == null || HitX == null || HitY == null || HitZ == null || Hit == null) return;

        for (int y = 0; y < Height; y++)
        {
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int srcIdx = rowBase + x;
                if (!Hit[srcIdx]) continue;

                // World hit point → new camera space.
                double wx = HitX[srcIdx] - newCamX;
                double wy = HitY[srcIdx] - newCamY;
                double wz = HitZ[srcIdx] - newCamZ;

                double zCam = wx * newFwdX + wy * newFwdY + wz * newFwdZ;
                if (zCam <= 0.001) continue; // behind camera

                double xCam = wx * newRightX + wy * newRightY + wz * newRightZ;
                double yCam = wx * newUpX + wy * newUpY + wz * newUpZ;

                // Perspective project: u = xCam / (zCam * fovScale * aspect); etc.
                double u = xCam / (zCam * newFovScale * newAspect);
                double v = yCam / (zCam * newFovScale);
                if (u < -1 || u > 1 || v < -1 || v > 1) continue;

                int nx = (int)((u + 1.0) * 0.5 * dstW);
                int ny = (int)((1.0 - (v + 1.0) * 0.5) * dstH);
                if (nx < 0 || nx >= dstW || ny < 0 || ny >= dstH) continue;

                int dstIdx = ny * dstW + nx;
                dst[dstIdx] = Buffer[srcIdx];
                covered[dstIdx] = true;
            }
        }
    }
}

public enum ReuseDecision
{
    None,       // full render
    Identity,   // blit cache
    Reproject,  // splat cached pixels, raymarch holes
}
