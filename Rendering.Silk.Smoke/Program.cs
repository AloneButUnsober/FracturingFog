using System;
using FracturingFog.Rendering.Silk;

namespace FracturingFog.Rendering.Silk.Smoke;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string desc = SilkStandaloneRunner.SmokeOneFrame();
            Console.WriteLine($"silk-smoke OK: {desc}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"silk-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
