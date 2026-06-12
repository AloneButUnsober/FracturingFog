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
        System.Console.WriteLine("FracturingFog.App scaffold — Phase X.0 / Slice 0.5.");
        System.Console.WriteLine("The full cross-platform entry point activates when");
        System.Console.WriteLine("the file-move slices land. See");
        System.Console.WriteLine("Docs/Technical/CrossPlatform-ImplementationPlan.md.");
        return 0;
    }
}
