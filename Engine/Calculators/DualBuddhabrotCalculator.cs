// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualBuddhabrotCalculator.cs (#1124, epic #1114 S10)
//
// Dual Buddhabrot — the Buddhabrot of the dual-orbit construction
// (Docs/Technical/DualOrbit-Coloring-RnD.md §3.E). Sample the parameter s
// exactly as the classic Buddhabrot does, iterate TWO orbits under u → u² + s —
// the critical z-orbit (seed 0) and the c-orbit (fixed, animatable seed c) — and
// deposit the trajectories into JOINT-OUTCOME channels, held in the base's three
// hit buffers:
//
//   R = Z  — z-orbit, z escaped                    (= classic Buddhabrot; control)
//   G = CB — c-orbit, c escaped AND z bounded      (s ∈ M \ M_c — the new structure)
//   B = CE — c-orbit, both escaped
//
// "z escaped, c bounded" is (to round-off) impossible because M_c ⊂ M; it is
// counted, never deposited. A minimum escape count (DualBuddhaMinIter, default
// 12) drops the fast-escaping orbits that otherwise wash the |s| ≤ 2 disc.
//
// Reuse: sampling domain, bailout (|u|² > 4), orbit recording, splatting, MH
// mutation mix, progressive batches, zoom compensation and seeding all come from
// BuddhaFamilyCalculator. The z-orbit is iterated with the classic code and its
// splats draw from the classic RNG stream; the c-orbit splats draw from a second
// stream — so with uniform sampling and DualBuddhaMinIter = 0 the Z channel is
// the classic Buddhabrot hit total, hit for hit.
//
// HD real-axis mirror: valid for the z-orbit always (the Mandelbrot family is
// conjugation-symmetric) but for the c channels only when c is real — conj(s)
// carries the orbit of conj(c), not of c. Forced off for c channels otherwise.
//
// Variants (#1125, S11) — all transform the recorded orbits in place, so MH
// scoring / acceptance / splatting stay generic:
//   DualBuddhaDeposit.Midpoint       c channels get m_k = (z_k + c_k)/2, k ≥ 1 (the
//                                    split-complex real part); the z-orbit is
//                                    recorded up to the c-orbit's escape even in
//                                    the bulbs.
//   DualBuddhaDeposit.PairChord      c channels get one point per step at a
//                                    uniform random t on the chord z_k → c_k
//                                    (string-art density; t from the c stream).
//   DualBuddhaDeposit.EscapeLocation each kept orbit deposits only its first point
//                                    past the bailout (|u| > 2) — escape space.
//   DualBuddhaNebula Z / CB / CE     one outcome only, split by its escape count
//                                    into the Low / Mid / High iteration bands
//                                    (R / G / B) — an outcome Nebulabrot.
//   DualBuddhaAnti                   bounded orbits: R = z-orbit for s ∈ M \ M_c,
//                                    G = c-orbit for s ∈ M_c, B = z-orbit for
//                                    s ∈ M_c (no bulb skip, no periodicity exit).
//
// Colour: DualBuddhaComposite.Channels (each channel log-normalised on its own,
// tinted by its colour × gain, added — the sparse CB channel stays visible) or
// Theme (gain-weighted total through the active colour map, like Buddhabrot).
// Colour / gain / composite edits re-composite the cached hits (no re-sample).

using System;
using System.Threading;

using FracturingFog.Models;

namespace FracturingFog;

public sealed class DualBuddhabrotCalculator : BuddhaFamilyCalculator
{
    protected override bool IsInSet => false;

    public DualBuddhabrotCalculator(int width, int height) : base(width, height) { }

    // ── Diagnostics ─────────────────────────────────────────────────────────

    /// <summary>Deposit counts of the last full sample pass per hit buffer:
    /// [0] R (Z), [1] G (CB), [2] B (CE) in the default mode, and [3] "z escaped,
    /// c bounded" samples (should be ~0: M_c ⊂ M).</summary>
    public long[] OutcomeCounts { get; } = new long[4];

    // ── Sample reuse (colour-only edits) ────────────────────────────────────

    private readonly record struct SampleKey(
        int Width, int Height, double CenterX, double CenterY, double Zoom,
        int Samples, int IterLow, int IterMid, int IterHigh,
        BuddhaQualityMode Quality, bool Metropolis, bool Progressive, int Seed, bool ZoomComp,
        int? BatchOverride, double CX, double CY, int MinIter, int Threads,
        DualBuddhaDeposit Deposit, DualBuddhaNebula Nebula, bool Anti);

    private SampleKey? _lastKey;

    private SampleKey CurrentKey()
    {
        var p = FractalParameters;
        return new SampleKey(Width, Height, CenterX, CenterY, Zoom,
            p.BuddhaSamples, p.BuddhaIterLow, p.BuddhaIterMid, p.BuddhaIterHigh,
            p.BuddhaQualityMode, p.BuddhaMetropolis, p.BuddhaProgressive, p.BuddhaSeed, p.BuddhaZoomCompensation,
            ProgressiveBatchesOverride, p.DualBuddhaCSeedX, p.DualBuddhaCSeedY, Math.Max(0, p.DualBuddhaMinIter),
            Environment.ProcessorCount,
            p.DualBuddhaDeposit, p.DualBuddhaNebula, p.DualBuddhaAnti);
    }

    // The video accumulation leg relies on the per-batch callbacks of a real
    // progressive run, so never short-circuit when one is attached.
    /// <summary>#838 — the two-orbit samplers have no GPU kernel.</summary>
    protected override bool SupportsGpuSampling => false;

    protected override bool CanReuseSamples()
        => OnBatchComposited == null && _lastKey is SampleKey k && k.Equals(CurrentKey());

    protected override void OnSamplingFinished(bool completed) => _lastKey = completed ? CurrentKey() : null;

    // ── Sampling ────────────────────────────────────────────────────────────

    private sealed class DualMh
    {
        public double Sx, Sy;
        public double[] ZR = Array.Empty<double>(), ZI = Array.Empty<double>();
        public double[] CR = Array.Empty<double>(), CI = Array.Empty<double>();
        public int ZLen, CLen;      // 0 = that set not deposited
        public int ZTarget, CTarget; // hit buffer index (0 = R, 1 = G, 2 = B)
        public int Score;
        public bool HasSeed;
    }

    private DualMh[] _mh = Array.Empty<DualMh>();
    private double _cx, _cy;
    private int _minIter;
    private bool _mirrorC;
    private DualBuddhaDeposit _deposit;
    private DualBuddhaNebula _nebula;
    private bool _anti;
    private int _low, _mid;

    protected override void PrepareSampling(int threads, int maxOrbit, bool mh)
    {
        var p = FractalParameters;
        _cx = p.DualBuddhaCSeedX;
        _cy = p.DualBuddhaCSeedY;
        _minIter = Math.Max(0, p.DualBuddhaMinIter);
        _mirrorC = _cy == 0.0;
        _deposit = p.DualBuddhaDeposit;
        _nebula = p.DualBuddhaNebula;
        _anti = p.DualBuddhaAnti;
        _low = p.BuddhaIterLow; _mid = p.BuddhaIterMid;
        Array.Clear(OutcomeCounts);
        _mh = new DualMh[mh ? threads : 0];
        for (int t = 0; t < _mh.Length; t++)
            _mh[t] = new DualMh
            {
                ZR = new double[maxOrbit], ZI = new double[maxOrbit],
                CR = new double[maxOrbit], CI = new double[maxOrbit],
            };
    }

    // One sample's two orbits, classified and transformed into the two deposit
    // sets: the z set (zr/zi, zLen, target zTarget) and the c set (cr/ci, cLen,
    // target cTarget). Length 0 = not deposited. Targets index the hit buffers
    // (0 = R / Z, 1 = G / CB, 2 = B / CE).
    private void IteratePair(double sx, double sy, double[] zr, double[] zi, double[] cr, double[] ci,
        int maxOrbit, Random rngC, out int zLen, out int zTarget, out int cLen, out int cTarget)
    {
        zLen = cLen = 0; zTarget = 0; cTarget = 1;
        // c-orbit first. Bounded c-orbits stop on periodicity (never deposited) —
        // except in anti mode, which deposits them.
        int cN = Record(_cx, _cy, sx, sy, cr, ci, maxOrbit, out bool cEsc, periodicity: !_anti);

        // z-orbit: the classic recording (orbit[n] stored before the escape test).
        // Main cardioid / period-2 bulb: z provably bounded — skip it (classic
        // early-reject) unless anti mode needs the bounded orbit, or a pair
        // deposit needs z up to the c-orbit's escape.
        bool pair = _deposit is DualBuddhaDeposit.Midpoint or DualBuddhaDeposit.PairChord;
        bool zEsc = false; int zN = maxOrbit;
        if (!InCardioidOrBulb(sx, sy) || _anti)
            zN = Record(0.0, 0.0, sx, sy, zr, zi, maxOrbit, out zEsc);
        else if (pair && cEsc && _nebula == DualBuddhaNebula.Off)
            Record(0.0, 0.0, sx, sy, zr, zi, Math.Min(maxOrbit, cN + 1), out _);   // bounded: recorded to cN
        if (zEsc && !cEsc) Interlocked.Increment(ref OutcomeCounts[3]);

        if (_anti)
        {
            if (!zEsc) { zLen = maxOrbit; zTarget = cEsc ? 0 : 2; }   // z for s ∈ M \ M_c → R, s ∈ M_c → B
            if (!cEsc) { cLen = maxOrbit; cTarget = 1; }              // c for s ∈ M_c → G
            return;
        }

        bool zKeep = zEsc && zN >= _minIter, cKeep = cEsc && cN >= _minIter;
        if (_nebula != DualBuddhaNebula.Off)
        {
            // Outcome Nebulabrot: one outcome, banded by its escape count.
            if (_nebula == DualBuddhaNebula.Z) { if (zKeep) { zLen = zN; zTarget = Band(zN); } }
            else if (cKeep && (_nebula == DualBuddhaNebula.CE) == zEsc) { cLen = cN; cTarget = Band(cN); }
            return;
        }

        zLen = zKeep ? zN : 0;
        cLen = cKeep ? cN : 0;
        cTarget = zEsc ? 2 : 1;
        switch (_deposit)
        {
            case DualBuddhaDeposit.Midpoint:
            case DualBuddhaDeposit.PairChord:
                if (cLen > 0)
                {
                    // From step 1: the seed step's pair (0, c) is the same for every
                    // sample, so it would pile one fixed point / segment into the
                    // image (smoke render: a bright straight chord 0 → c).
                    int L = zEsc ? Math.Min(zN, cN) : cN;
                    bool chord = _deposit == DualBuddhaDeposit.PairChord;
                    for (int k = 1; k < L; k++)
                    {
                        double t = chord ? rngC.NextDouble() : 0.5;
                        cr[k - 1] = zr[k] + t * (cr[k] - zr[k]);
                        ci[k - 1] = zi[k] + t * (ci[k] - zi[k]);
                    }
                    cLen = Math.Max(0, L - 1);
                }
                break;
            case DualBuddhaDeposit.EscapeLocation:
                // Record stores the escaping point at index N (before its test).
                if (zLen > 0) { zr[0] = zr[zN]; zi[0] = zi[zN]; zLen = 1; }
                if (cLen > 0) { cr[0] = cr[cN]; ci[0] = ci[cN]; cLen = 1; }
                break;
        }

        int Band(int n) => n < _low ? 0 : n < _mid ? 1 : 2;
    }

    // Iterate u² + s from u0, storing u_n before the |u|² > 4 test (the classic
    // Buddhabrot loop). Returns the escape index (= recorded length) or maxOrbit.
    // `periodicity` (c-orbit only): Brent cycle check — an orbit that returns to
    // within 1e-13 of a saved point is bounded and stops early. Bounded c-orbits
    // are never deposited, so this only saves time (no analytic cardioid test
    // exists for "c ∈ K_s"); the z-orbit keeps the exact classic loop.
    private static int Record(double u0x, double u0y, double sx, double sy,
        double[] or, double[] oi, int maxOrbit, out bool escaped, bool periodicity = false)
    {
        double zr = u0x, zi = u0y;
        double px = zr, py = zi; int power = 1, lam = 0;
        int iter;
        for (iter = 0; iter < maxOrbit; iter++)
        {
            or[iter] = zr;
            oi[iter] = zi;
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 > 4.0) break;
            double newZr = zr2 - zi2 + sx;
            zi = 2.0 * zr * zi + sy;
            zr = newZr;
            if (periodicity)
            {
                if (Math.Abs(zr - px) < 1e-13 && Math.Abs(zi - py) < 1e-13) { escaped = false; return maxOrbit; }
                if (++lam == power) { px = zr; py = zi; power <<= 1; lam = 0; }
            }
        }
        escaped = iter < maxOrbit;
        return iter;
    }

    private static int Score(double[] or, double[] oi, int len,
        double scale, double midX, double midY, int width, int height)
    {
        int s = 0;
        double invScale = 1.0 / scale;
        double xOff = width * 0.5 - midX * invScale;
        double yOff = height * 0.5 - midY * invScale;
        for (int k = 0; k < len; k++)
        {
            int ix = (int)(or[k] * invScale + xOff);
            int iy = (int)(oi[k] * invScale + yOff);
            if ((uint)ix < (uint)width && (uint)iy < (uint)height) s++;
        }
        return s;
    }

    private void Deposit(uint[] tR, uint[] tG, uint[] tB,
        double[] zr, double[] zi, int zLen, int zTarget, double[] cr, double[] ci, int cLen, int cTarget,
        bool hd, Random rng, Random rngC,
        double scale, double midX, double midY, int width, int height)
    {
        if (zLen > 0)
        {
            uint[] tz = zTarget == 0 ? tR : zTarget == 1 ? tG : tB;
            if (hd) SplatOrbitHD(tz, zr, zi, zLen, scale, midX, midY, width, height, rng);
            else SplatOrbitStd(tz, zr, zi, zLen, scale, midX, midY, width, height);
        }
        if (cLen > 0)
        {
            uint[] tc = cTarget == 0 ? tR : cTarget == 1 ? tG : tB;
            if (hd) SplatOrbitHD(tc, cr, ci, cLen, scale, midX, midY, width, height, rngC, _mirrorC);
            else SplatOrbitStd(tc, cr, ci, cLen, scale, midX, midY, width, height);
        }
    }

    protected override void SampleBatch(
        int threadIndex, int batch, MhState? mhState, Random rng, int sampleCount,
        uint[] tR, uint[] tG, uint[] tB,
        int maxOrbit, bool inSet, bool skipBulbs, bool hd,
        double scale, double midX, double midY, int width, int height,
        int low, int mid)
    {
        // Second, independent stream for the c-channel splats (keeps the Z
        // channel's RNG sequence identical to the classic Buddhabrot).
        var rngC = new Random(unchecked(FractalParameters.BuddhaSeed * 40503 + threadIndex * 2654435 + batch * 97 + 1124));
        if (mhState != null) RunDualMh(_mh[threadIndex], rng, rngC, sampleCount, tR, tG, tB, maxOrbit, hd, scale, midX, midY, width, height);
        else RunDualUniform(rng, rngC, sampleCount, tR, tG, tB, maxOrbit, hd, scale, midX, midY, width, height);
    }

    private void RunDualUniform(Random rng, Random rngC, int sampleCount, uint[] tZ, uint[] tCB, uint[] tCE,
        int maxOrbit, bool hd, double scale, double midX, double midY, int width, int height)
    {
        var zr = new double[maxOrbit]; var zi = new double[maxOrbit];
        var cr = new double[maxOrbit]; var ci = new double[maxOrbit];
        long nz = 0, ncb = 0, nce = 0;
        for (int s = 0; s < sampleCount; s++)
        {
            double sx = -2.5 + rng.NextDouble() * 4.0;    // classic sampling domain
            double sy = -1.5 + rng.NextDouble() * 3.0;
            IteratePair(sx, sy, zr, zi, cr, ci, maxOrbit, rngC, out int zLen, out int zT, out int cLen, out int cT);
            Count(zLen, zT, cLen, cT, ref nz, ref ncb, ref nce);
            Deposit(tZ, tCB, tCE, zr, zi, zLen, zT, cr, ci, cLen, cT, hd, rng, rngC, scale, midX, midY, width, height);
        }
        AddCounts(nz, ncb, nce);
    }

    // Metropolis-Hastings over s (Galloway mix, as the classic sampler): the
    // score is the viewport hits of every deposited orbit of the sample.
    private void RunDualMh(DualMh st, Random rng, Random rngC, int sampleCount, uint[] tZ, uint[] tCB, uint[] tCE,
        int maxOrbit, bool hd, double scale, double midX, double midY, int width, int height)
    {
        var pzr = new double[maxOrbit]; var pzi = new double[maxOrbit];
        var pcr = new double[maxOrbit]; var pci = new double[maxOrbit];

        if (!st.HasSeed)
        {
            for (int w = 0; w < 4096 && !st.HasSeed; w++)
            {
                double sx = -2.5 + rng.NextDouble() * 4.0, sy = -1.5 + rng.NextDouble() * 3.0;
                IteratePair(sx, sy, pzr, pzi, pcr, pci, maxOrbit, rngC, out int zLen, out int zT, out int cLen, out int cT);
                int score = Score(pzr, pzi, zLen, scale, midX, midY, width, height)
                          + Score(pcr, pci, cLen, scale, midX, midY, width, height);
                if (score == 0) continue;
                Accept(st, sx, sy, pzr, pzi, zLen, zT, pcr, pci, cLen, cT, score);
                st.HasSeed = true;
            }
            if (!st.HasSeed)
            {
                RunDualUniform(rng, rngC, sampleCount, tZ, tCB, tCE, maxOrbit, hd, scale, midX, midY, width, height);
                return;
            }
        }

        long nz = 0, ncb = 0, nce = 0;
        for (int s = 0; s < sampleCount; s++)
        {
            double nsx, nsy;
            if (rng.NextDouble() < 0.5)
            {
                double sigma = 0.0001 + rng.NextDouble() * 0.001;
                nsx = st.Sx + Gaussian(rng) * sigma;
                nsy = st.Sy + Gaussian(rng) * sigma;
            }
            else
            {
                nsx = -2.5 + rng.NextDouble() * 4.0;
                nsy = -1.5 + rng.NextDouble() * 3.0;
            }
            IteratePair(nsx, nsy, pzr, pzi, pcr, pci, maxOrbit, rngC, out int zLen, out int zT, out int cLen, out int cT);
            int score = Score(pzr, pzi, zLen, scale, midX, midY, width, height)
                      + Score(pcr, pci, cLen, scale, midX, midY, width, height);
            if (score > 0 && (st.Score == 0 || rng.NextDouble() < (double)score / st.Score))
                Accept(st, nsx, nsy, pzr, pzi, zLen, zT, pcr, pci, cLen, cT, score);

            Count(st.ZLen, st.ZTarget, st.CLen, st.CTarget, ref nz, ref ncb, ref nce);
            Deposit(tZ, tCB, tCE, st.ZR, st.ZI, st.ZLen, st.ZTarget, st.CR, st.CI, st.CLen, st.CTarget,
                    hd, rng, rngC, scale, midX, midY, width, height);
        }
        AddCounts(nz, ncb, nce);
    }

    private static void Accept(DualMh st, double sx, double sy,
        double[] zr, double[] zi, int zLen, int zT, double[] cr, double[] ci, int cLen, int cT, int score)
    {
        Array.Copy(zr, st.ZR, zLen); Array.Copy(zi, st.ZI, zLen);
        Array.Copy(cr, st.CR, cLen); Array.Copy(ci, st.CI, cLen);
        st.Sx = sx; st.Sy = sy; st.ZLen = zLen; st.CLen = cLen; st.ZTarget = zT; st.CTarget = cT; st.Score = score;
    }

    // Per-buffer deposit counts (OutcomeCounts[0..2] = R / G / B; Z / CB / CE in
    // the default mode).
    private static void Count(int zLen, int zT, int cLen, int cT, ref long r, ref long g, ref long b)
    {
        if (zLen > 0) { if (zT == 0) r++; else if (zT == 1) g++; else b++; }
        if (cLen > 0) { if (cT == 0) r++; else if (cT == 1) g++; else b++; }
    }

    private void AddCounts(long nz, long ncb, long nce)
    {
        Interlocked.Add(ref OutcomeCounts[0], nz);
        Interlocked.Add(ref OutcomeCounts[1], ncb);
        Interlocked.Add(ref OutcomeCounts[2], nce);
    }

    // ── Composite ───────────────────────────────────────────────────────────

    protected override void Composite()
    {
        var p = FractalParameters;
        if (p.DualBuddhaComposite == DualBuddhaComposite.Theme) CompositeTheme(p);
        else CompositeChannels(p);
        UpdateHeightField();
    }

    // Each channel log-normalised against its own max (so the sparse CB channel
    // is not drowned by Z), tinted, gain-weighted and added. HD drops the
    // low-hit speckle floor exactly as the classic band composite does.
    private void CompositeChannels(FractalParameters p)
    {
        var ch = new[] { HitsR, HitsG, HitsB };
        uint[] col = { p.DualBuddhaColorZ, p.DualBuddhaColorCB, p.DualBuddhaColorCE };
        double[] gain = { Math.Max(0, p.DualBuddhaGainZ), Math.Max(0, p.DualBuddhaGainCB), Math.Max(0, p.DualBuddhaGainCE) };
        bool hd = p.BuddhaQualityMode == BuddhaQualityMode.HighDefinition;
        uint floor = hd ? (uint)Math.Max(2, p.BuddhaSamples / 2_000_000) : 0u;

        var inv = new double[3];
        for (int c = 0; c < 3; c++)
        {
            uint max = 0;
            foreach (uint h in ch[c]) if (h > max) max = h;
            inv[c] = max > floor ? 1.0 / Math.Log(max + 1.0) : 0.0;
        }

        int n = ColorBuffer.Length;
        for (int i = 0; i < n; i++)
        {
            double r = 0, g = 0, b = 0;
            for (int c = 0; c < 3; c++)
            {
                uint h = ch[c][i];
                if (h <= floor || inv[c] == 0) continue;
                double w = Math.Log(h + 1.0) * inv[c] * gain[c];
                r += w * ((col[c] >> 16) & 0xFF);
                g += w * ((col[c] >> 8) & 0xFF);
                b += w * (col[c] & 0xFF);
            }
            ColorBuffer[i] = 0xFF000000u
                | ((uint)Math.Clamp((int)(r + 0.5), 0, 255) << 16)
                | ((uint)Math.Clamp((int)(g + 0.5), 0, 255) << 8)
                | (uint)Math.Clamp((int)(b + 0.5), 0, 255);
        }
    }

    // Gain-weighted density through the active theme (the classic ColorMap
    // composite: hot → outer-escape end, density² alpha toward InSetColor).
    private void CompositeTheme(FractalParameters p)
    {
        uint[] hz = HitsR, hcb = HitsG, hce = HitsB;
        double gz = Math.Max(0, p.DualBuddhaGainZ), gcb = Math.Max(0, p.DualBuddhaGainCB), gce = Math.Max(0, p.DualBuddhaGainCE);
        int n = ColorBuffer.Length;
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            double v = gz * hz[i] + gcb * hcb[i] + gce * hce[i];
            if (v > max) max = v;
        }
        var cm = ColorMap;
        int iters = MaxIterations;
        cm.MaxIterations = iters;
        uint bg = cm.InSetColor;
        if (max <= 0) { Array.Fill(ColorBuffer, bg); return; }
        double inv = 1.0 / Math.Log(max + 1.0);
        for (int i = 0; i < n; i++)
        {
            double v = gz * hz[i] + gcb * hcb[i] + gce * hce[i];
            if (v <= 0) { ColorBuffer[i] = bg; continue; }
            double norm = Math.Log(v + 1.0) * inv;
            uint argb = unchecked((uint)cm.Map((float)((1.0 - norm) * iters), 0f, iters));
            double a = norm * norm, oneMa = 1.0 - a;
            uint R = (uint)(((argb >> 16) & 0xFF) * a + ((bg >> 16) & 0xFF) * oneMa);
            uint G = (uint)(((argb >> 8) & 0xFF) * a + ((bg >> 8) & 0xFF) * oneMa);
            uint B = (uint)((argb & 0xFF) * a + (bg & 0xFF) * oneMa);
            ColorBuffer[i] = 0xFF000000u | (R << 16) | (G << 8) | B;
        }
    }
}
