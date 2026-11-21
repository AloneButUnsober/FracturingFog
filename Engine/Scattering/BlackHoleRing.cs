// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleRing.cs (#1947, gravitational-scattering set §2.6)
//
// Free test-particle motion around a Schwarzschild black hole encircled by a thin Bach-Weyl ring: an
// exact static axisymmetric (Weyl) superposition, after Semerák & Suková, "Free motion around black
// holes with discs or rings: between integrability and chaos - I" (MNRAS 404, 545, 2010;
// arXiv:1211.4106). Every formula below is theirs (equation numbers from that paper), in their units
// c = G = 1, black hole mass M = 1, Weyl coordinates (t, ρ, z, φ):
//
//   ds² = −e^{2ν}dt² + ρ²e^{−2ν}dφ² + e^{2λ−2ν}(dρ² + dz²)                                      (1)
//   ν = ν_Schw + ν_BW (potentials superpose linearly)
//   ν_Schw = ½ ln[(d1 + d2 − 2M)/(d1 + d2 + 2M)],  d_{1,2} = √(ρ² + (z ∓ M)²)                (3)
//   ν_BW = −2ℳ K(k) / (π l2),  k² = 4ρb / l2²,  l_{1,2} = √((ρ ∓ b)² + z²)                   (6)
//   λ_,ρ = ρ(ν_,ρ² − ν_,z²),  λ_,z = 2ρ ν_,ρ ν_,z,  λ = 0 on the axis outside the horizon      (2)
//   E = e^{2ν}u^t,  ℓ = ρ²e^{−2ν}u^φ;  geodesic equations for u^ρ, u^z                         (16)-(17)
//   normalisation −1 = −e^{−2ν}E² + e^{2ν}ℓ²/ρ² + e^{2λ−2ν}[(u^ρ)² + (u^z)²]
//
// The paper notes that for the superposition λ has no closed form and must be found by integrating
// its gradient (2). FF carries λ as a state variable instead, dλ/dτ = λ_,ρ u^ρ + λ_,z u^z (its
// gradient is local in ν), so the line integral from the axis is needed only once per launch: the
// paper's closed form for Schwarzschild alone (4) plus the integral of the rest of the gradient (the
// ring and the cross term). The ring's closed form (7), as transcribed from the paper's ar5iv
// rendering, did not reproduce its own gradient (2) away from the ring (≈ 2e-6 against 2e-7 at
// ρ = 5, z = 0, ring 0.5 at r 20) while it did near the ring, so FF does not use it. The tests check
// the carried λ against a fresh line integral, and (4) against the integral of its gradient.
//
// The complete elliptic integrals K, E use Gauss's arithmetic-geometric mean (NIST DLMF §19.8,
// eqs. 19.8.1, 19.8.2, 19.8.5, 19.8.6) and dK/dk = (E − k′²K)/(k k′²) (DLMF eq. 19.4.1).
//
// FF's own choices (not in the paper): the ring's Schwarzschild-like radius r_ring maps to the Weyl
// radius b = √(r_ring(r_ring − 2M)) (the equatorial relation ρ = √(r(r − 2M)) the paper uses for
// Schwarzschild); captured below r = 2.05M (the paper: integration is inaccurate close to the horizon);
// a hit on the ring within 0.01M stops the orbit.

using System;
using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1947 — complete elliptic integrals by the AGM (DLMF 19.8.1-19.8.6).</summary>
public static class EllipticAgm
{
    /// <summary>K(m) and E(m), m = k² in [0, 1).</summary>
    public static (double K, double E) KE(double m)
    {
        if (!(m < 1.0)) return (double.PositiveInfinity, 1.0);
        double a = 1.0, g = Math.Sqrt(1.0 - m);
        double c2Sum = 0.5 * m;   // 2^(n−1) c_n² at n = 0: c0² = a0² − g0² = m
        double pow = 0.5;
        for (int n = 0; n < 40; n++)
        {
            double an = 0.5 * (a + g), gn = Math.Sqrt(a * g);
            double cn = 0.5 * (a - g);   // c_{n+1} (DLMF 19.8.3)
            pow *= 2.0;
            c2Sum += pow * cn * cn;
            a = an; g = gn;
            if (Math.Abs(cn) < 1e-17 * a) break;
        }
        double k = Math.PI / (2.0 * a);   // K = π / (2 M(1, k′))   (19.8.5)
        double e = k * (1.0 - c2Sum);     // E = K (a0² − Σ 2^(n−1) c_n²) with a0 = 1   (19.8.6)
        return (k, e);
    }

    /// <summary>dK/dm (m = k²) from DLMF 19.4.1: dK/dk = (E − k′²K)/(k k′²), dK/dm = dK/dk / (2k).</summary>
    public static double DKdm(double m, double kk, double ee)
    {
        if (m < 1e-12) return Math.PI / 8.0;   // the m → 0 limit of (E − (1 − m)K) / (2m(1 − m))
        return (ee - (1.0 - m) * kk) / (2.0 * m * (1.0 - m));
    }
}

/// <summary>#1947 — the Weyl potentials of Schwarzschild + Bach-Weyl ring and their gradients.</summary>
public readonly struct WeylBhRing
{
    public readonly double M;      // black hole mass (1)
    public readonly double Mring;  // ring mass ℳ
    public readonly double B;      // ring Weyl radius b

    public WeylBhRing(double ringMass, double ringRadiusSchw)
    {
        M = 1.0;
        Mring = Math.Max(0.0, ringMass);
        double rr = Math.Max(2.0 + 1e-6, ringRadiusSchw);
        B = Math.Sqrt(rr * (rr - 2.0));
    }

    /// <summary>ν and its gradient (ν_,ρ, ν_,z) at (ρ, z); ring part included when ℳ &gt; 0.</summary>
    public void Nu(double rho, double z, out double nu, out double nr, out double nz)
    {
        // Schwarzschild (3): ν = ½ ln[(s − 2M)/(s + 2M)], s = d1 + d2 → ∇ν = ∇s · 2M/(s² − 4M²).
        double m = M;
        double d1 = Math.Sqrt(rho * rho + (z - m) * (z - m)), d2 = Math.Sqrt(rho * rho + (z + m) * (z + m));
        double s = d1 + d2;
        nu = 0.5 * Math.Log((s - 2 * m) / (s + 2 * m));
        double f = 2 * m / (s * s - 4 * m * m);
        double sr = rho / d1 + rho / d2, sz = (z - m) / d1 + (z + m) / d2;
        nr = sr * f; nz = sz * f;
        if (Mring > 0) AddRing(rho, z, ref nu, ref nr, ref nz, Mring, B);
    }

    /// <summary>Bach-Weyl ring (6): ν = −(2ℳ/π) K(m)/l2, m = 4ρb/l2², l2² = (ρ + b)² + z².</summary>
    public static void AddRing(double rho, double z, ref double nu, ref double nr, ref double nz, double mass, double b)
    {
        double l22 = (rho + b) * (rho + b) + z * z, l2 = Math.Sqrt(l22);
        double mm = 4 * rho * b / l22;
        var (kk, ee) = EllipticAgm.KE(mm);
        double km = EllipticAgm.DKdm(mm, kk, ee);
        double c = -2 * mass / Math.PI;
        // ∂m/∂ρ = (4b/l2²)(1 − 2ρ(ρ + b)/l2²), ∂m/∂z = −8ρbz/l2⁴; ∂l2/∂ρ = (ρ + b)/l2, ∂l2/∂z = z/l2.
        double mr = 4 * b / l22 * (1 - 2 * rho * (rho + b) / l22), mz = -8 * rho * b * z / (l22 * l22);
        nu += c * kk / l2;
        nr += c * (km * mr / l2 - kk * (rho + b) / (l2 * l22));
        nz += c * (km * mz / l2 - kk * z / (l2 * l22));
    }

    /// <summary>The paper's closed form for Schwarzschild alone (4): λ = ½ ln[((d1 + d2)² − 4M²)/(4 d1 d2)].</summary>
    public static double LambdaSchwarzschild(double rho, double z)
    {
        double d1 = Math.Sqrt(rho * rho + (z - 1) * (z - 1)), d2 = Math.Sqrt(rho * rho + (z + 1) * (z + 1));
        return 0.5 * Math.Log(((d1 + d2) * (d1 + d2) - 4) / (4 * d1 * d2));
    }

    /// <summary>λ at (ρ, z) by integrating its gradient (2) from the axis (λ = 0 there): along z = Z
    /// from ρ = 0 to ρ, then along ρ = const from Z to z. Z is far above the horizon and the ring.</summary>
    public double LambdaLineIntegral(double rho, double z)
    {
        double zTop = Math.Max(Math.Abs(z), B) + 50.0;
        double sum = 0;
        var self = this;
        // Horizontal leg: dλ = λ_,ρ dρ at z = zTop. Vertical leg: dλ = λ_,z dz at fixed ρ.
        sum += Gauss(0, rho, x => { self.Nu(x, zTop, out _, out double a, out double c); return x * (a * a - c * c); });
        sum += Gauss(zTop, z, y => { self.Nu(rho, y, out _, out double a, out double c); return 2 * rho * a * c; });
        return sum;
    }

    /// <summary>λ at (ρ, z): the paper's closed form for Schwarzschild alone (4) plus the line integral
    /// from the axis (λ = 0 there) of the rest of the gradient (2): with ν = ν_S + ν_R,
    /// λ_,ρ − λ_S,ρ = ρ(2ν_S,ρ ν_R,ρ + ν_R,ρ² − 2ν_S,z ν_R,z − ν_R,z²),
    /// λ_,z − λ_S,z = 2ρ(ν_S,ρ ν_R,z + ν_S,z ν_R,ρ + ν_R,ρ ν_R,z).
    /// Path: along z = Z (far above everything) from the axis, then down ρ = const to z, with segments
    /// halving towards the end so a nearby ring is resolved.</summary>
    public double Lambda(double rho, double z)
    {
        double lam = LambdaSchwarzschild(rho, z);
        if (Mring <= 0) return lam;
        double zTop = Math.Max(Math.Abs(z), B) + 50.0;
        double mr = Mring, b = B;
        double Rest(double x, double y, bool horizontal)
        {
            SchwGrad(x, y, out double sr, out double sz);
            double nu = 0, rr = 0, rz = 0;
            AddRing(x, y, ref nu, ref rr, ref rz, mr, b);
            return horizontal ? x * (2 * sr * rr + rr * rr - 2 * sz * rz - rz * rz)
                              : 2 * x * (sr * rz + sz * rr + rr * rz);
        }
        lam += Gauss(0, rho, x => Rest(x, zTop, true), 120);
        lam += GaussRefined(zTop, z, y => Rest(rho, y, false));
        return lam;
    }

    // From a to b with panels halving in length towards b (resolves a steep integrand near b).
    private static double GaussRefined(double a, double b, Func<double, double> f)
    {
        double sum = 0, start = a, d = b - a;
        for (int j = 1; j <= 40; j++)
        {
            double end = b - d * Math.Pow(0.5, j);
            sum += Gauss(start, end, f, 6);
            start = end;
        }
        return sum + Gauss(start, b, f, 6);
    }

    private static void SchwGrad(double rho, double z, out double nr, out double nz)
    {
        double d1 = Math.Sqrt(rho * rho + (z - 1) * (z - 1)), d2 = Math.Sqrt(rho * rho + (z + 1) * (z + 1));
        double s = d1 + d2, f = 2 / (s * s - 4);
        nr = (rho / d1 + rho / d2) * f; nz = ((z - 1) / d1 + (z + 1) / d2) * f;
    }

    // Composite 8-point Gauss-Legendre (smooth integrands away from the ring and horizon).
    private static readonly double[] Gx = { -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498,
                                             0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363 };
    private static readonly double[] Gw = { 0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620,
                                             0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763 };
    private static double Gauss(double a, double b, Func<double, double> f, int panels = 400)
    {
        double h = (b - a) / panels, sum = 0;
        for (int p = 0; p < panels; p++)
        {
            double mid = a + (p + 0.5) * h, half = 0.5 * h;
            for (int i = 0; i < 8; i++) sum += Gw[i] * f(mid + half * Gx[i]) * half;
        }
        return sum;
    }
}

/// <summary>#1947 — geodesic motion in the meridional plane (eqs. 16-17), state (ρ, z, u^ρ, u^z, λ).</summary>
public struct BhRingSystem : IScatteringSystem
{
    public const int CapturedGate = 0, RingGate = 1, EscapedGate = 2, GateCount = 3;
    /// <summary>Captured below this Schwarzschild-like radius r = (d1 + d2)/2 + M.</summary>
    public const double CaptureR = 2.05;
    public const double RingHitDistance = 0.01;
    public const double EscapeR = 1000.0;

    public WeylBhRing Field;
    public double E, L;
    public double TimeCap;
    public double Time;
    public int Gate;
    /// <summary>Optional: section crossings (r, u^r) are appended here (equatorial plane, both directions).</summary>
    public List<(double R, double Ur)>? Crossings;
    private double _pz, _pr, _pur;
    private bool _havePrev;

    public readonly int Dimension => 5;

    /// <summary>Schwarzschild-like r and u^r from Weyl (ρ, z, u^ρ, u^z): r = (d1 + d2)/2 + M.</summary>
    public static (double R, double Ur) Radial(double rho, double z, double ur, double uz)
    {
        double d1 = Math.Sqrt(rho * rho + (z - 1) * (z - 1)), d2 = Math.Sqrt(rho * rho + (z + 1) * (z + 1));
        double sr = rho / d1 + rho / d2, sz = (z - 1) / d1 + (z + 1) / d2;
        return (0.5 * (d1 + d2) + 1, 0.5 * (sr * ur + sz * uz));
    }

    public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy)
    {
        double rho = y[0], z = y[1], ur = y[2], uz = y[3], lam = y[4];
        Field.Nu(rho, z, out double nu, out double nr, out double nz);
        double lr = rho * (nr * nr - nz * nz), lz = 2 * rho * nr * nz;   // (2)
        double e2l = Math.Exp(-2 * lam), e4n = Math.Exp(4 * nu);
        double q = ur * ur - uz * uz;
        dy[0] = ur;
        dy[1] = uz;
        // (16)
        dy[2] = -E * E * nr * e2l + L * L * e4n / (rho * rho * rho) * e2l * (1 - rho * nr)
              + (nr - lr) * q + 2 * (nz - lz) * ur * uz;
        // (17)
        dy[3] = -E * E * nz * e2l - L * L * e4n * nz / (rho * rho) * e2l
              - (nz - lz) * q + 2 * (nr - lr) * ur * uz;
        dy[4] = lr * ur + lz * uz;
    }

    public ScatterFate Classify(ReadOnlySpan<double> y, out int body)
    {
        body = -1;
        double rho = y[0], z = y[1];
        var (r, _) = Radial(rho, z, 0, 0);
        if (r < CaptureR) { Gate = CapturedGate; return ScatterFate.Captured; }
        double b = Field.B;
        if (Field.Mring > 0 && (rho - b) * (rho - b) + z * z < RingHitDistance * RingHitDistance) { Gate = RingGate; return ScatterFate.Captured; }
        if (r > EscapeR) { Gate = EscapedGate; return ScatterFate.Escaped; }
        if (Time >= TimeCap) { Gate = -1; return ScatterFate.Undecided; }
        return ScatterFate.Running;
    }

    /// <summary>Step cap: a fraction of the distance to the horizon and to the ring, and of r.</summary>
    public readonly double MaxStep(ReadOnlySpan<double> y)
    {
        var (r, _) = Radial(y[0], y[1], 0, 0);
        double d = Math.Max(1e-6, r - 2.0);
        if (Field.Mring > 0)
            d = Math.Min(d, Math.Sqrt((y[0] - Field.B) * (y[0] - Field.B) + y[1] * y[1]));
        return Math.Clamp(0.1 * d, 1e-6, 0.5 * r);
    }

    public void Observe(ReadOnlySpan<double> y, double h)
    {
        Time += h;
        if (Crossings != null)
        {
            var (r, urr) = Radial(y[0], y[1], y[2], y[3]);
            if (_havePrev && (y[1] == 0 || (y[1] > 0) != (_pz > 0)) && _pz != y[1])
            {
                double f = _pz / (_pz - y[1]);
                Crossings.Add((_pr + f * (r - _pr), _pur + f * (urr - _pur)));
            }
            _pz = y[1]; _pr = r; _pur = urr; _havePrev = true;
        }
    }

    /// <summary>The normalisation −1 = −e^{−2ν}E² + e^{2ν}ℓ²/ρ² + e^{2λ−2ν}[(u^ρ)² + (u^z)²], as its
    /// right-hand side + 1 (0 on a timelike geodesic).</summary>
    public static double NormalisationError(in WeylBhRing f, double e, double l, ReadOnlySpan<double> y)
    {
        f.Nu(y[0], y[1], out double nu, out _, out _);
        return -Math.Exp(-2 * nu) * e * e + Math.Exp(2 * nu) * l * l / (y[0] * y[0])
             + Math.Exp(2 * y[4] - 2 * nu) * (y[2] * y[2] + y[3] * y[3]) + 1.0;
    }
}

/// <summary>#1947 — one black hole + ring scene built from <see cref="FractalParameters"/>.</summary>
public sealed class BhRingScene
{
    public WeylBhRing Field { get; }
    public double E { get; }
    public double L { get; }
    public double VelocityScale { get; }
    public double FateTime { get; }
    public double OrbitTime { get; }
    public int Orbits { get; }
    public ScatterSettings Settings { get; }

    public BhRingScene(double ringMass, double ringRadius, double energy, double angularMomentum, double velocityScale,
                       double fateTime, double orbitTime, int orbits, double tolerance)
    {
        Field = new WeylBhRing(double.IsFinite(ringMass) ? Math.Clamp(ringMass, 0.0, 10.0) : 0.5,
                               double.IsFinite(ringRadius) ? Math.Clamp(ringRadius, 3.0, 1000.0) : 20.0);
        E = double.IsFinite(energy) ? Math.Clamp(energy, 0.0, 10.0) : 0.977;
        L = double.IsFinite(angularMomentum) ? Math.Clamp(angularMomentum, -100.0, 100.0) : 3.75;
        VelocityScale = double.IsFinite(velocityScale) && velocityScale > 0 ? velocityScale : 60.0;
        FateTime = double.IsFinite(fateTime) ? Math.Clamp(fateTime, 10.0, 1e7) : 5000.0;
        OrbitTime = double.IsFinite(orbitTime) ? Math.Clamp(orbitTime, 100.0, 1e8) : 200000.0;
        Orbits = Math.Clamp(orbits, 1, 10000);
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-4) : 1e-11;
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = 50_000_000, InitialStep = 0.01 };
    }

    public static BhRingScene Build(FractalParameters p)
        => new(p.BhRingMass, p.BhRingRadius, p.BhRingEnergy, p.BhRingAngularMomentum, p.BhRingVelocityScale,
               p.BhRingFateTime, p.BhRingOrbitTime, p.BhRingOrbits, p.BhRingTolerance);

    /// <summary>Launch from the equatorial section point (r, u^r) moving up (u^z ≥ 0): ρ = √(r(r − 2M)),
    /// u^ρ = (r − M)/ρ · u^r, λ from the line integral, u^z from the normalisation. False outside the
    /// accessible region.</summary>
    public bool LaunchState(double r, double urad, Span<double> s, double? lambda = null)
    {
        if (!(r > BhRingSystem.CaptureR)) return false;
        double rho = Math.Sqrt(r * (r - 2.0));
        double b = Field.B;
        if (Field.Mring > 0 && Math.Abs(rho - b) < BhRingSystem.RingHitDistance) return false;
        double urho = (r - 1.0) / rho * urad;
        Field.Nu(rho, 0, out double nu, out _, out _);
        double lam = lambda ?? EquatorLambda(r);
        double rest = -1 + Math.Exp(-2 * nu) * E * E - Math.Exp(2 * nu) * L * L / (rho * rho);
        double uz2 = rest * Math.Exp(2 * nu - 2 * lam) - urho * urho;
        if (!(uz2 >= 0)) return false;
        s[0] = rho; s[1] = 0; s[2] = urho; s[3] = Math.Sqrt(uz2); s[4] = lam;
        return true;
    }

    /// <summary>λ on the equatorial plane at Schwarzschild-like radius r (launches sit there, so one
    /// value serves a whole pixel column).</summary>
    public double EquatorLambda(double r)
    {
        if (!(r > 2.0)) return double.NaN;
        return Field.Lambda(Math.Sqrt(r * (r - 2.0)), 0);
    }

    public BhRingSystem System(double timeCap, List<(double R, double Ur)>? crossings = null)
        => new() { Field = Field, E = E, L = L, TimeCap = timeCap, Gate = -1, Crossings = crossings };
}
