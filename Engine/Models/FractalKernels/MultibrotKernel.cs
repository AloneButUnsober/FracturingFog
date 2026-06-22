using System;
using System.Numerics;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Multibrot: z_{n+1} = z^d + c, integer exponent d ≥ 2.
    /// d ∈ {3,4,5}: unrolled direct complex multiplication (scalar + SIMD).
    /// d ≥ 6: polar fallback (scalar only). Caller dispatches accordingly.
    /// </summary>
    public readonly struct MultibrotKernel : ISimdFractalKernel
    {
        private readonly int _d;

        public MultibrotKernel(int d) { _d = d < 2 ? 2 : d; }

        public int Exponent { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _d; }

        /// <summary>True when this kernel's d is in the SIMD-unrolled range.</summary>
        public bool SimdSupported { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _d >= 3 && _d <= 5; }

        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 512.0 * 512.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = 0; zi = 0; dr = 1; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            switch (_d)
            {
                case 3: Step3(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                case 4: Step4(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                case 5: Step5(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                default: StepPolar(ref zr, ref zi, ref dr, ref di, cx, cy); return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step3(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            // z³ = zr(zr² - 3 zi²) + i zi(3 zr² - zi²)
            double newZr = zr * (zr2 - 3.0 * zi2) + cx;
            double newZi = zi * (3.0 * zr2 - zi2) + cy;
            // dz/dc' = 3 z²·dz/dc + 1 ;  3 z² = 3((zr²-zi²) + 2 zr zi i)
            double pr = 3.0 * (zr2 - zi2);
            double pi = 6.0 * zr * zi;
            double newDr = pr * dr - pi * di + 1.0;
            double newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step4(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            // z² = u + i v
            double u = zr * zr - zi * zi;
            double v = 2.0 * zr * zi;
            // z⁴ = (u+iv)² = (u²-v²) + 2 u v i
            double newZr = u * u - v * v + cx;
            double newZi = 2.0 * u * v + cy;
            // 4 z³ = 4 z · z² = 4 (zr+i zi)(u+iv)
            double pr = 4.0 * (zr * u - zi * v);
            double pi = 4.0 * (zr * v + zi * u);
            double newDr = pr * dr - pi * di + 1.0;
            double newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step5(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double u = zr * zr - zi * zi;       // Re(z²)
            double v = 2.0 * zr * zi;            // Im(z²)
            double U = u * u - v * v;            // Re(z⁴)
            double V = 2.0 * u * v;              // Im(z⁴)
            // z⁵ = z · z⁴
            double newZr = zr * U - zi * V + cx;
            double newZi = zr * V + zi * U + cy;
            // 5 z⁴
            double pr = 5.0 * U;
            double pi = 5.0 * V;
            double newDr = pr * dr - pi * di + 1.0;
            double newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StepPolar(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double r2 = zr * zr + zi * zi;
            if (r2 == 0.0)
            {
                zr = cx; zi = cy;
                return;
            }
            double r = Math.Sqrt(r2);
            double theta = Math.Atan2(zi, zr);
            double rd = Math.Pow(r, _d);
            double td = _d * theta;
            double newZr = rd * Math.Cos(td) + cx;
            double newZi = rd * Math.Sin(td) + cy;

            double rdm1 = Math.Pow(r, _d - 1);
            double tdm1 = (_d - 1) * theta;
            double pr = _d * rdm1 * Math.Cos(tdm1);
            double pi = _d * rdm1 * Math.Sin(tdm1);
            double newDr = pr * dr - pi * di + 1.0;
            double newDi = pr * di + pi * dr;

            zr = newZr; zi = newZi;
            dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitStateSimd(
            Vector<double> cx, Vector<double> cy,
            out Vector<double> zr, out Vector<double> zi,
            out Vector<double> dr, out Vector<double> di)
        {
            zr = Vector<double>.Zero;
            zi = Vector<double>.Zero;
            dr = Vector<double>.One;
            di = Vector<double>.Zero;
        }

        /// <summary>
        /// SIMD step for d ∈ {3,4,5}. d ≥ 6 must dispatch through the scalar
        /// path — calling StepSimd at d ≥ 6 falls back to passing the input
        /// through unchanged (no-op). Caller checks <see cref="SimdSupported"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StepSimd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy)
        {
            switch (_d)
            {
                case 3: Step3Simd(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                case 4: Step4Simd(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                case 5: Step5Simd(ref zr, ref zi, ref dr, ref di, cx, cy); return;
                default: return;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step3Simd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy)
        {
            var three = new Vector<double>(3.0);
            var six = new Vector<double>(6.0);
            var one = Vector<double>.One;
            var zr2 = zr * zr;
            var zi2 = zi * zi;
            var newZr = zr * (zr2 - three * zi2) + cx;
            var newZi = zi * (three * zr2 - zi2) + cy;
            var pr = three * (zr2 - zi2);
            var pi = six * zr * zi;
            var newDr = pr * dr - pi * di + one;
            var newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step4Simd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy)
        {
            var two = new Vector<double>(2.0);
            var four = new Vector<double>(4.0);
            var one = Vector<double>.One;
            var u = zr * zr - zi * zi;
            var v = two * zr * zi;
            var newZr = u * u - v * v + cx;
            var newZi = two * u * v + cy;
            var pr = four * (zr * u - zi * v);
            var pi = four * (zr * v + zi * u);
            var newDr = pr * dr - pi * di + one;
            var newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Step5Simd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy)
        {
            var two = new Vector<double>(2.0);
            var five = new Vector<double>(5.0);
            var one = Vector<double>.One;
            var u = zr * zr - zi * zi;
            var v = two * zr * zi;
            var U = u * u - v * v;
            var V = two * u * v;
            var newZr = zr * U - zi * V + cx;
            var newZi = zr * V + zi * U + cy;
            var pr = five * U;
            var pi = five * V;
            var newDr = pr * dr - pi * di + one;
            var newDi = pr * di + pi * dr;
            zr = newZr; zi = newZi; dr = newDr; di = newDi;
        }
    }
}
