// silk-smoke entry point.
//
// Two modes:
//   default       — SmokeOneFrameOffscreen (FBO + glReadPixels, no swapchain).
//                   Works on macOS without an interactive session; Linux still
//                   needs xvfb-run because GLFW links X11.
//   --windowed    — SmokeOneFrame (opens visible GLFW window, swaps once).
//                   Retained for parity testing against the foreign-window
//                   adapters that drive a real swapchain on Windows.
//
// Exit code 0 on success, 2 on exception.

using System;
using FracturingFog.Rendering.Silk;

namespace FracturingFog.Rendering.Silk.Smoke;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool windowed = Array.Exists(args, a => string.Equals(a, "--windowed", StringComparison.OrdinalIgnoreCase));
        try
        {
            string desc = windowed
                ? SilkStandaloneRunner.SmokeOneFrame()
                : SilkStandaloneRunner.SmokeOneFrameOffscreen();
            string mode = windowed ? "windowed" : "offscreen";
            Console.WriteLine($"silk-smoke OK ({mode}): {desc}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"silk-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
