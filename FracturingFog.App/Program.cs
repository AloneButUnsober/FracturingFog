// FracturingFog.App entry point — scaffolding stub.
//
// Phase X.0 / Slice 0.5: returns 0 so `dotnet build` succeeds and the
// cross-platform CI matrix has a target to publish against. The real
// Program.cs (benchmark / saprobe / gentest / --silk-smoke / --batch /
// --server / --winforms / Avalonia bootstrap) moves in once the Engine,
// Audio, Hosting file-move slices land and the closure resolves.

namespace FracturingFog.App;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Phase X.5 / Slice 5.1 — per-RID ILGPU device-kind smoke.
        // Asserts the CPU fallback is reachable on the current host. Used
        // by the release-workflow smoke step on Linux + macOS legs where
        // CUDA/OpenCL drivers are absent. Writes ilgpu-probe.out next to
        // the exe and echoes to stdout. Exit 0 on PASS, 1 on FAIL.
        if (args.Length > 0 && args[0] == "--ilgpu-probe")
        {
            bool ok = FracturingFog.Calculators.AcceleratorProbe.RunSmoke(out string report);
            string outPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "ilgpu-probe.out");
            try { System.IO.File.WriteAllText(outPath, report); } catch { }
            System.Console.Write(report);
            return ok ? 0 : 1;
        }

        System.Console.WriteLine("FracturingFog.App scaffold — Phase X.0 / Slice 0.5.");
        System.Console.WriteLine("The full cross-platform entry point activates when");
        System.Console.WriteLine("the file-move slices land. See");
        System.Console.WriteLine("Docs/Technical/CrossPlatform-ImplementationPlan.md.");
        return 0;
    }
}
