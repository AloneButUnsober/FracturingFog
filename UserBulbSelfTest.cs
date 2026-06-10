// UserBulbSelfTest.cs — invoked via `dotnet run -- --ubtest`. Headlessly
// compiles the default UserBulb source and runs Calculate on a small grid,
// then prints diagnostics (compile error, hit %) so we can confirm the
// Roslyn compile path is reachable outside the WinForms harness.

using System;
using FracturingFog.Calculators;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class UserBulbSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("[ubtest] Begin");
            const int w = 80, h = 60;
            var calc = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbSource =
                        "return new Vec3(" +
                        "    z.X*z.X - z.Y*z.Y - z.Z*z.Z," +
                        "    2*z.X*z.Y," +
                        "    2*z.X*z.Z) + c;"
                }
            };

            Console.WriteLine("[ubtest] Compiling…");
            calc.Compile(calc.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] IsCompiled={calc.IsCompiled}");
            if (!string.IsNullOrEmpty(calc.LastError))
                Console.WriteLine($"[ubtest] LastError:\n{calc.LastError}");
            if (!calc.IsCompiled) return 1;

            Console.WriteLine("[ubtest] Calculate…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            calc.Calculate();
            sw.Stop();

            int hits = 0;
            uint bg = calc.ColorMap.InSetColor;
            for (int i = 0; i < calc.ColorBuffer.Length; i++)
                if (calc.ColorBuffer[i] != bg) hits++;

            Console.WriteLine($"[ubtest] Done in {sw.ElapsedMilliseconds} ms, hits={hits}/{calc.ColorBuffer.Length}");

            // Dump as PPM so we can eyeball the buffer outside the WinForms harness.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ubtest.ppm");
            using (var fs = System.IO.File.Create(path))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
                bw.Write(header);
                for (int i = 0; i < calc.ColorBuffer.Length; i++)
                {
                    uint p = calc.ColorBuffer[i];
                    bw.Write((byte)((p >> 16) & 0xFF));
                    bw.Write((byte)((p >> 8) & 0xFF));
                    bw.Write((byte)(p & 0xFF));
                }
            }
            Console.WriteLine($"[ubtest] Wrote {path}");

            // Sandbox-compiler parity check. Same triplex map expressed in DSL.
            Console.WriteLine("[ubtest] Sandbox compile…");
            var calcSbx = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbSource =
                        "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c"
                }
            };
            calcSbx.Compile(calcSbx.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] Sandbox IsCompiled={calcSbx.IsCompiled}");
            if (!string.IsNullOrEmpty(calcSbx.LastError))
                Console.WriteLine($"[ubtest] Sandbox LastError:\n{calcSbx.LastError}");
            if (!calcSbx.IsCompiled) return 3;
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            calcSbx.Calculate();
            sw2.Stop();
            int hits2 = 0;
            for (int i = 0; i < calcSbx.ColorBuffer.Length; i++)
                if (calcSbx.ColorBuffer[i] != bg) hits2++;
            Console.WriteLine($"[ubtest] Sandbox done in {sw2.ElapsedMilliseconds} ms, hits={hits2}/{calcSbx.ColorBuffer.Length}");
            Console.WriteLine($"[ubtest] Sandbox AnalyticPattern={calcSbx.AnalyticPattern.Kind} power={calcSbx.AnalyticPattern.Power}");
            if (calcSbx.AnalyticPattern.Kind != AnalyticDEKind.Square)
            {
                Console.WriteLine($"[ubtest] Sandbox explicit-Square detection FAILED — expected Square, got {calcSbx.AnalyticPattern.Kind}");
                return 6;
            }

            // Sandbox + triplex(z,8) — analytic DE pattern recognition test.
            Console.WriteLine("[ubtest] Sandbox triplex compile…");
            var calcTri = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbSource = "triplex(z, 8) + c",
                }
            };
            calcTri.Compile(calcTri.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] Triplex IsCompiled={calcTri.IsCompiled}");
            if (!string.IsNullOrEmpty(calcTri.LastError))
                Console.WriteLine($"[ubtest] Triplex LastError:\n{calcTri.LastError}");
            if (!calcTri.IsCompiled) return 4;
            var sw3 = System.Diagnostics.Stopwatch.StartNew();
            calcTri.Calculate();
            sw3.Stop();
            int hits3 = 0;
            for (int i = 0; i < calcTri.ColorBuffer.Length; i++)
                if (calcTri.ColorBuffer[i] != bg) hits3++;
            Console.WriteLine($"[ubtest] Triplex done in {sw3.ElapsedMilliseconds} ms, hits={hits3}/{calcTri.ColorBuffer.Length}");

            // Sandbox + chain — two-step Burning-Bulb-ish (abs each axis then triplex).
            Console.WriteLine("[ubtest] Sandbox chain compile…");
            var calcChain = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbChain = new System.Collections.Generic.List<UserBulbChainStep>
                    {
                        new() { OutputName = "folded", Source = "vec(abs(z.x), abs(z.y), abs(z.z))" },
                        new() { OutputName = "out",    Source = "triplex(folded, 8) + c" },
                    }
                }
            };
            calcChain.Compile(string.Empty);
            Console.WriteLine($"[ubtest] Chain IsCompiled={calcChain.IsCompiled}");
            if (!string.IsNullOrEmpty(calcChain.LastError))
                Console.WriteLine($"[ubtest] Chain LastError:\n{calcChain.LastError}");
            if (!calcChain.IsCompiled) return 5;
            var sw4 = System.Diagnostics.Stopwatch.StartNew();
            calcChain.Calculate();
            sw4.Stop();
            int hits4 = 0;
            for (int i = 0; i < calcChain.ColorBuffer.Length; i++)
                if (calcChain.ColorBuffer[i] != bg) hits4++;
            Console.WriteLine($"[ubtest] Chain done in {sw4.ElapsedMilliseconds} ms, hits={hits4}/{calcChain.ColorBuffer.Length}");

            // Sandbox + Quat — qmul-based z² + c quaternion Julia (small slice).
            Console.WriteLine("[ubtest] Sandbox quat compile…");
            var calcQuat = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbAxisMode = UserBulbAxisModeKind.Quat,
                    UserBulbSource = "qmul(z, z) + c",
                }
            };
            calcQuat.Compile(calcQuat.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] Quat IsCompiled={calcQuat.IsCompiled}");
            if (!string.IsNullOrEmpty(calcQuat.LastError))
                Console.WriteLine($"[ubtest] Quat LastError:\n{calcQuat.LastError}");
            if (!calcQuat.IsCompiled) return 7;
            var sw5 = System.Diagnostics.Stopwatch.StartNew();
            calcQuat.Calculate();
            sw5.Stop();
            int hits5 = 0;
            for (int i = 0; i < calcQuat.ColorBuffer.Length; i++)
                if (calcQuat.ColorBuffer[i] != bg) hits5++;
            Console.WriteLine($"[ubtest] Quat done in {sw5.ElapsedMilliseconds} ms, hits={hits5}/{calcQuat.ColorBuffer.Length}");

            // Stage-2 ILGPU emitter: parse known Sandbox shapes, emit C#, verify.
            Console.WriteLine("[ubtest] Emitter smoke…");
            var triExpr = SandboxBulbExpression.Parse("triplex(z, 8) + c", new[] { "t" });
            var triEmit = UserBulbSandboxEmitter.Emit(triExpr.Root, new[] { "t" }, false);
            Console.WriteLine($"[ubtest] Emitter triplex Ok={triEmit.Ok} Kind={triEmit.ResultKind} Body=\n  {triEmit.Body}");
            if (!triEmit.Ok || !(triEmit.Body?.Contains("Vec3.Pow") ?? false))
            { Console.WriteLine("[ubtest] Emitter triplex FAILED"); return 8; }

            var sqExpr = SandboxBulbExpression.Parse(
                "vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c",
                new[] { "t" });
            var sqEmit = UserBulbSandboxEmitter.Emit(sqExpr.Root, new[] { "t" }, false);
            Console.WriteLine($"[ubtest] Emitter square Ok={sqEmit.Ok} Body length={sqEmit.Body?.Length}");
            if (!sqEmit.Ok || !(sqEmit.Body?.Contains("new Vec3(") ?? false))
            { Console.WriteLine("[ubtest] Emitter square FAILED"); return 9; }

            var qExpr = SandboxBulbExpression.Parse("qmul(z, z) + c", new[] { "t" });
            var qEmit = UserBulbSandboxEmitter.Emit(qExpr.Root, new[] { "t" }, true);
            Console.WriteLine($"[ubtest] Emitter quat Ok={qEmit.Ok} Kind={qEmit.ResultKind} Body=\n  {qEmit.Body}");
            if (!qEmit.Ok || qEmit.ResultKind != SbxEmitKind.Quat)
            { Console.WriteLine("[ubtest] Emitter quat FAILED"); return 10; }

            // End-to-end emitter→Roslyn: emit a Sandbox source as C#, feed
            // through the Roslyn pipeline, verify hit count agrees with the
            // Sandbox interpreter rendering of the same source.
            Console.WriteLine("[ubtest] Emitter→Roslyn end-to-end…");
            const string e2eSource = "triplex(z, 8) + c";
            var e2eExpr = SandboxBulbExpression.Parse(e2eSource, Array.Empty<string>());
            var e2eEmit = UserBulbSandboxEmitter.Emit(e2eExpr.Root, Array.Empty<string>(), false);
            if (!e2eEmit.Ok)
            { Console.WriteLine($"[ubtest] E2E emit failed: {e2eEmit.Error}"); return 11; }
            Console.WriteLine($"[ubtest] E2E emitted body: {e2eEmit.Body}");

            var calcE2E = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                    UserBulbSource = e2eEmit.Body!,
                }
            };
            calcE2E.Compile(calcE2E.FractalParameters.UserBulbSource!);
            if (!calcE2E.IsCompiled)
            { Console.WriteLine($"[ubtest] E2E Roslyn compile FAILED: {calcE2E.LastError}"); return 12; }
            calcE2E.Calculate();
            int hitsE2E = 0;
            for (int i = 0; i < calcE2E.ColorBuffer.Length; i++)
                if (calcE2E.ColorBuffer[i] != bg) hitsE2E++;
            Console.WriteLine($"[ubtest] E2E hits={hitsE2E}/{calcE2E.ColorBuffer.Length}  vs Sandbox-triplex hits3={hits3}");

            // Tolerance: same fractal, different DE algorithms (analytic vs
            // numerical). Hit counts should agree within 5%.
            int delta = Math.Abs(hitsE2E - hits3);
            int tol = (int)(hits3 * 0.05);
            if (delta > tol)
            { Console.WriteLine($"[ubtest] E2E parity FAILED delta={delta} tol={tol}"); return 13; }

            // qpow emitter: literal int → unfolded qmul chain; non-literal → Quat.Pow call.
            var qpowExpr = SandboxBulbExpression.Parse("qpow(z, 3) + c", new[] { "t" });
            var qpowEmit = UserBulbSandboxEmitter.Emit(qpowExpr.Root, new[] { "t" }, true);
            Console.WriteLine($"[ubtest] Emitter qpow literal: {qpowEmit.Body}");
            if (!qpowEmit.Ok || !(qpowEmit.Body?.Contains(" * ") ?? false))
            { Console.WriteLine("[ubtest] Emitter qpow literal FAILED — expected unfolded chain"); return 14; }

            var qpowNExpr = SandboxBulbExpression.Parse("qpow(z, n) + c", new[] { "t" });
            var qpowNEmit = UserBulbSandboxEmitter.Emit(qpowNExpr.Root, new[] { "t" }, true);
            Console.WriteLine($"[ubtest] Emitter qpow runtime: {qpowNEmit.Body}");
            if (!qpowNEmit.Ok || !(qpowNEmit.Body?.Contains("Quat.Pow(") ?? false))
            { Console.WriteLine("[ubtest] Emitter qpow runtime FAILED — expected Quat.Pow call"); return 15; }

            // AnalyticDE: `z ^ 8 + c` operator form should detect as MandelbulbN(8).
            Console.WriteLine("[ubtest] Sandbox z^N compile…");
            var calcZN = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbSource = "z ^ 8 + c",
                }
            };
            calcZN.Compile(calcZN.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] z^N IsCompiled={calcZN.IsCompiled} AnalyticPattern={calcZN.AnalyticPattern.Kind} power={calcZN.AnalyticPattern.Power}");
            if (!calcZN.IsCompiled) { Console.WriteLine($"[ubtest] z^N compile FAILED: {calcZN.LastError}"); return 16; }
            if (calcZN.AnalyticPattern.Kind != AnalyticDEKind.MandelbulbN || Math.Abs(calcZN.AnalyticPattern.Power - 8) > 1e-9)
            { Console.WriteLine("[ubtest] z^N pattern detection FAILED — expected MandelbulbN(8)"); return 17; }
            var swZN = System.Diagnostics.Stopwatch.StartNew();
            calcZN.Calculate();
            swZN.Stop();
            int hitsZN = 0;
            for (int i = 0; i < calcZN.ColorBuffer.Length; i++)
                if (calcZN.ColorBuffer[i] != bg) hitsZN++;
            Console.WriteLine($"[ubtest] z^N done in {swZN.ElapsedMilliseconds} ms, hits={hitsZN}/{calcZN.ColorBuffer.Length}");
            if (hitsZN != hits3)
            { Console.WriteLine($"[ubtest] z^N hits mismatch vs triplex({hits3}): {hitsZN}"); return 18; }

            // let-binding inline emit: no IIFE/Func<> in emitted body.
            var letExpr = SandboxBulbExpression.Parse("let p = triplex(z, 8) in p + c", Array.Empty<string>());
            var letEmit = UserBulbSandboxEmitter.Emit(letExpr.Root, Array.Empty<string>(), false);
            Console.WriteLine($"[ubtest] Emitter let inline: {letEmit.Body}");
            if (!letEmit.Ok)
            { Console.WriteLine($"[ubtest] let emit FAILED: {letEmit.Error}"); return 19; }
            if (letEmit.Body!.Contains("System.Func<") || letEmit.Body.Contains("=>"))
            { Console.WriteLine("[ubtest] let emit still uses IIFE — expected inline substitution"); return 20; }
            // End-to-end: compile the inlined emit via Roslyn and verify hits match z^N path.
            var calcLet = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters { UserBulbSource = letEmit.Body!, UserBulbCompiler = UserBulbCompilerKind.Roslyn }
            };
            calcLet.Compile(calcLet.FractalParameters.UserBulbSource!);
            if (!calcLet.IsCompiled)
            { Console.WriteLine($"[ubtest] let-inline Roslyn compile FAILED: {calcLet.LastError}"); return 21; }
            calcLet.Calculate();
            int hitsLet = 0;
            for (int i = 0; i < calcLet.ColorBuffer.Length; i++)
                if (calcLet.ColorBuffer[i] != bg) hitsLet++;
            Console.WriteLine($"[ubtest] let-inline E2E hits={hitsLet}/{calcLet.ColorBuffer.Length}");
            if (Math.Abs(hitsLet - hits3) > (int)(hits3 * 0.05))
            { Console.WriteLine($"[ubtest] let-inline parity FAILED hits={hitsLet} vs {hits3}"); return 22; }

            // Quat componentwise transcendentals: must reject.
            try
            {
                var bad = SandboxBulbExpression.Parse("sin(z)", new[] { "t" });
                var env = bad.NewEnv();
                bad.EvalStepQuat(new Quat(1, 0, 0, 0), Quat.Zero, 0, env, new double[] { 0 });
                Console.WriteLine("[ubtest] sin(quat) interpreter SHOULD have thrown");
                return 23;
            }
            catch (InvalidOperationException ex)
            { Console.WriteLine($"[ubtest] sin(quat) correctly rejected: {ex.Message}"); }

            try
            {
                var badExpr = SandboxBulbExpression.Parse("sin(z)", new[] { "t" });
                var er = UserBulbSandboxEmitter.Emit(badExpr.Root, new[] { "t" }, true);
                if (er.Ok) { Console.WriteLine("[ubtest] Emitter sin(quat) SHOULD have failed"); return 24; }
                Console.WriteLine($"[ubtest] Emitter sin(quat) correctly rejected: {er.Error}");
            }
            catch { /* acceptable */ }

            // abs(quat) must still work — per-axis fold has geometric meaning.
            var absExpr = SandboxBulbExpression.Parse("abs(z) + c", new[] { "t" });
            var absEnv = absExpr.NewEnv();
            var absResult = absExpr.EvalStepQuat(new Quat(-1, -2, 3, -4), new Quat(0, 0, 0, 0), 0, absEnv, new double[] { 0 });
            Console.WriteLine($"[ubtest] abs(quat) = ({absResult.W}, {absResult.X}, {absResult.Y}, {absResult.Z})");
            if (absResult.W != 1 || absResult.X != 2 || absResult.Y != 3 || absResult.Z != 4)
            { Console.WriteLine("[ubtest] abs(quat) wrong result"); return 25; }

            // Parser error spans: SbxParseException carries position + length.
            Console.WriteLine("[ubtest] Error span tests…");
            try
            {
                SandboxBulbExpression.Parse("z + bogus + c", Array.Empty<string>());
                Console.WriteLine("[ubtest] error span: unknown ident should have thrown"); return 26;
            }
            catch (SbxParseException e)
            {
                Console.WriteLine($"[ubtest] error span ok: pos={e.Position} len={e.Length} msg='{e.Message}'");
                if (e.Length != 5) { Console.WriteLine($"[ubtest] expected len=5 (bogus), got {e.Length}"); return 27; }
            }

            try
            {
                SandboxBulbExpression.Parse("triplex(z)", Array.Empty<string>());
                Console.WriteLine("[ubtest] arity check should have thrown"); return 28;
            }
            catch (SbxParseException e)
            {
                Console.WriteLine($"[ubtest] arity span ok: pos={e.Position} len={e.Length} msg='{e.Message}'");
            }

            var calcBad = new UserBulbCalculator(w, h)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbSource = "z + bogus + c",
                }
            };
            calcBad.Compile(calcBad.FractalParameters.UserBulbSource!);
            Console.WriteLine($"[ubtest] calc error pos={calcBad.LastErrorPosition} len={calcBad.LastErrorLength}");
            if (calcBad.LastErrorPosition < 0)
            { Console.WriteLine("[ubtest] calc didn't surface error position"); return 29; }

            // Translator cache: first call populates; second call should hit cache.
            Console.WriteLine("[ubtest] Translator cache test…");
            UserBulbIlgpuTranslator.ResetCacheForTesting();
            const string cacheSrc = "return Vec3.Pow(z, 8) + c;";
            var t1 = UserBulbIlgpuTranslator.Translate(cacheSrc);
            var t2 = UserBulbIlgpuTranslator.Translate(cacheSrc);
            if (!t1.Ok || !t2.Ok || !ReferenceEquals(t1, t2))
            { Console.WriteLine($"[ubtest] cache hit failed: t1.Ok={t1.Ok} t2.Ok={t2.Ok} same-ref={ReferenceEquals(t1, t2)}"); return 30; }
            Console.WriteLine("[ubtest] Translator cache hit confirmed (same record ref)");

            // Chain analytic DE: abs-fold → triplex chain should detect.
            Console.WriteLine("[ubtest] Chain analytic detect…");
            var calcChainAn = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbChain = new System.Collections.Generic.List<UserBulbChainStep>
                    {
                        new() { OutputName = "folded", Source = "abs(z)" },
                        new() { OutputName = "out",    Source = "triplex(folded, 8) + c" },
                    }
                }
            };
            calcChainAn.Compile(string.Empty);
            Console.WriteLine($"[ubtest] Chain-analytic Pattern={calcChainAn.AnalyticPattern.Kind} power={calcChainAn.AnalyticPattern.Power}");
            if (calcChainAn.AnalyticPattern.Kind != AnalyticDEKind.MandelbulbN || Math.Abs(calcChainAn.AnalyticPattern.Power - 8) > 1e-9)
            { Console.WriteLine("[ubtest] Chain-analytic FAILED: expected MandelbulbN(8)"); return 31; }
            var swCA = System.Diagnostics.Stopwatch.StartNew();
            calcChainAn.Calculate();
            swCA.Stop();
            int hitsCA = 0;
            for (int i = 0; i < calcChainAn.ColorBuffer.Length; i++)
                if (calcChainAn.ColorBuffer[i] != bg) hitsCA++;
            Console.WriteLine($"[ubtest] Chain-analytic done in {swCA.ElapsedMilliseconds} ms, hits={hitsCA}/{calcChainAn.ColorBuffer.Length}");

            // Non-Lipschitz chain prefix (sin) → must NOT detect.
            var calcChainBad = new UserBulbCalculator(w, h)
            {
                MaxIterations = 96,
                FractalParameters = new FractalParameters
                {
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserBulbChain = new System.Collections.Generic.List<UserBulbChainStep>
                    {
                        new() { OutputName = "warped", Source = "sin(z)" },
                        new() { OutputName = "out",    Source = "triplex(warped, 8) + c" },
                    }
                }
            };
            calcChainBad.Compile(string.Empty);
            Console.WriteLine($"[ubtest] Non-Lipschitz chain pattern={calcChainBad.AnalyticPattern.Kind}");
            if (calcChainBad.AnalyticPattern.Kind != AnalyticDEKind.None)
            { Console.WriteLine("[ubtest] Non-Lipschitz chain should NOT detect — got false positive"); return 32; }

            return (hits > 0 && hits2 > 0 && hits3 > 0 && hits4 > 0 && hits5 > 0 && hitsE2E > 0) ? 0 : 2;
        }
    }
}
