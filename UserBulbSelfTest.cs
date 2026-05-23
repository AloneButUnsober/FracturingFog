// UserBulbSelfTest.cs — invoked via `dotnet run -- --ubtest`. Headlessly
// compiles the default UserBulb source and runs Calculate on a small grid,
// then prints diagnostics (compile error, hit %) so we can confirm the
// Roslyn compile path is reachable outside the WinForms harness.

using System;
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
            return hits > 0 ? 0 : 2;
        }
    }
}
