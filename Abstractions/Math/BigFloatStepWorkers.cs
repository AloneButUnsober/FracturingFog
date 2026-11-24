// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2033 — the three multiplies of one Mandelbrot step (zr², zi², zr·zi) on three threads.
//
// Past 1e100 the BigFloat reference orbit (MandelbrotCalculator.ComputeReferenceOrbitAP)
// spends 70-95 % of a step in BigFloat.Mul, on one thread (--bigfloatopsprobe: 33k bits,
// a multiply 192 µs, a square 128 µs, the step 504 µs). The three products are independent,
// so the calling thread computes zr² while two helpers compute zi² and zr·zi: the same
// operations on the same operands, so the results are bit-identical to the serial step.
// A step lasts tens to hundreds of µs, far below a thread-pool wake-up, so the helpers spin
// on a generation counter (falling back to an event after a millisecond idle) and are
// created per orbit build (Dispose ends them).

using System;
using System.Diagnostics;
using System.Threading;

namespace FracturingFog.FFMath
{
    public sealed class BigFloatStepWorkers : IDisposable
    {
        /// <summary>Precision (bits) from which the orbit uses the workers: the parallel step
        /// beat the serial one at every size measured (--bigfloatopsprobe, i5-13420H: x1.26 at
        /// 1.1k bits up to x2 at 33k), and the AP orbit starts near 1e110 (~400 bits), where
        /// the handoff is not measured.</summary>
        public const int MinBits = 1024;

        private readonly Helper _sq, _mul;

        public BigFloatStepWorkers()
        {
            _sq = new Helper("BigFloat zi²");
            _mul = new Helper("BigFloat zr·zi");
        }

        /// <summary>zr², zi² and zr·zi at <paramref name="prec"/>, bit-identical to three
        /// <see cref="BigFloat.Mul"/> calls.</summary>
        public void Products(BigFloat zr, BigFloat zi, int prec, out BigFloat zr2, out BigFloat zi2, out BigFloat zri)
        {
            _sq.Start(zi, zi, prec, null);
            _mul.Start(zr, zi, prec, null);
            zr2 = BigFloat.Mul(zr, zr, prec);
            zi2 = _sq.Join(out _);
            zri = _mul.Join(out _);
        }

        /// <summary><see cref="Products"/> and the OD splits of zr and zi
        /// (<see cref="BigFloat.ToOD"/>, the reference orbit's stored form) together: zi² and
        /// zr's split on one helper, zr·zi on the other, zr² and zi's split here.</summary>
        public void Step(BigFloat zr, BigFloat zi, int prec, out BigFloat zr2, out BigFloat zi2, out BigFloat zri,
            out OD zrOd, out OD ziOd)
        {
            _sq.Start(zi, zi, prec, zr);
            _mul.Start(zr, zi, prec, null);
            zr2 = BigFloat.Mul(zr, zr, prec);
            ziOd = zi.ToOD();
            zi2 = _sq.Join(out zrOd);
            zri = _mul.Join(out _);
        }

        public void Dispose()
        {
            _sq.Dispose();
            _mul.Dispose();
        }

        private sealed class Helper : IDisposable
        {
            private readonly Thread _thread;
            private readonly ManualResetEventSlim _wake = new(false, 0);
            private BigFloat _a, _b, _r, _odOf;
            private bool _wantOd;
            private OD _od;
            private int _prec;
            private Exception? _error;
            private volatile int _gen, _doneGen;
            private volatile bool _stop;

            public Helper(string name)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = name };
                _thread.Start();
            }

            public void Start(BigFloat a, BigFloat b, int prec, BigFloat? odOf)
            {
                _a = a; _b = b; _prec = prec; _error = null;
                _wantOd = odOf.HasValue; _odOf = odOf.GetValueOrDefault();
                _gen = _gen + 1;   // volatile write: the operands are visible before it
                _wake.Set();
            }

            public BigFloat Join(out OD od)
            {
                var spin = new SpinWait();
                while (_doneGen != _gen) spin.SpinOnce(-1);   // -1: never Sleep(1) (15.6 ms on Windows)
                if (_error is { } e) throw new AggregateException(e);
                od = _od;
                return _r;
            }

            private void Loop()
            {
                int seen = 0;
                while (true)
                {
                    long idleFrom = Stopwatch.GetTimestamp();
                    var spin = new SpinWait();
                    while (_gen == seen && !_stop)
                    {
                        if (Stopwatch.GetElapsedTime(idleFrom).TotalMilliseconds < 1) spin.SpinOnce(-1);
                        else { _wake.Wait(1); _wake.Reset(); }
                    }
                    if (_stop) return;
                    seen = _gen;
                    try
                    {
                        _r = BigFloat.Mul(_a, _b, _prec);
                        if (_wantOd) _od = _odOf.ToOD();
                    }
                    catch (Exception e) { _error = e; }
                    _doneGen = seen;   // volatile write: _r / _error are visible before it
                }
            }

            public void Dispose()
            {
                _stop = true;
                _wake.Set();
                _thread.Join();
                _wake.Dispose();
            }
        }
    }
}
