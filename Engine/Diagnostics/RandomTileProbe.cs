// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// RandomTileProbe.cs
//
// #332 gate — headless checks on the RandomTile (Bourke random space-filling)
// calculator. Verifies the three contracts P1 promises:
//   1. Determinism — same (size, seed, count, exponent) → byte-identical buffer.
//   2. Seed sensitivity — a different seed produces a different tiling.
//   3. Monotonicity — a higher shape count paints more (non-background) pixels.
//   4. Relief — the dome height field (SmoothBuffer) is non-trivial.
// Writes randomtileprobe.out next to the exe. Returns 0 on pass, 1 on fail.

using System;
using System.IO;
using System.Text;

using FracturingFog.Models;

namespace FracturingFog.Diagnostics;

public static class RandomTileProbe
{
    public static int RunGate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RandomTile gate (#332) — Bourke random space filling");
        bool ok = true;

        const int W = 320, H = 240;

        static RandomTileCalculator Render(int seed, int count)
        {
            var calc = new RandomTileCalculator(W, H)
            {
                FractalParameters = new FractalParameters
                {
                    RandomTileSeed = seed,
                    RandomTileCount = count,
                    RandomTileSizeExponent = 1.6,
                    RandomTileGap = 0.0,
                    RandomTileMinPixelRadius = 0.75,
                    RandomTileRelief = 1.0,
                },
            };
            calc.Calculate();
            return calc;
        }

        static int PaintedPixels(uint[] buf)
        {
            int n = 0;
            foreach (uint p in buf) if ((p & 0x00FFFFFFu) != 0) n++;
            return n;
        }

        // 1. Determinism.
        var a = Render(seed: 7, count: 3000);
        var b = Render(seed: 7, count: 3000);
        bool identical = a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer);
        sb.AppendLine($"  determinism (seed 7 ×2)          : {(identical ? "PASS" : "FAIL")}");
        ok &= identical;

        // 2. Seed sensitivity.
        var c = Render(seed: 8, count: 3000);
        bool differs = !a.ColorBuffer.AsSpan().SequenceEqual(c.ColorBuffer);
        sb.AppendLine($"  seed sensitivity (7 vs 8)        : {(differs ? "PASS" : "FAIL")}");
        ok &= differs;

        // 3. Monotonicity — more shapes → more painted pixels.
        int few = PaintedPixels(Render(seed: 7, count: 300).ColorBuffer);
        int many = PaintedPixels(Render(seed: 7, count: 6000).ColorBuffer);
        bool mono = many > few && few > 0;
        sb.AppendLine($"  monotonic paint ({few} → {many})   : {(mono ? "PASS" : "FAIL")}");
        ok &= mono;

        // 4. Relief field non-trivial.
        int reliefNonZero = 0;
        foreach (float h in a.SmoothBuffer) if (h > 0f) reliefNonZero++;
        bool relief = reliefNonZero > 0;
        sb.AppendLine($"  relief field ({reliefNonZero} px > 0)   : {(relief ? "PASS" : "FAIL")}");
        ok &= relief;

        sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        string outPath = Path.Combine(AppContext.BaseDirectory, "randomtileprobe.out");
        File.WriteAllText(outPath, sb.ToString());
        Console.Write(sb.ToString());
        Console.WriteLine($"(wrote {outPath})");
        return ok ? 0 : 1;
    }
}
