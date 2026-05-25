using System;
using Avalonia;
using Avalonia.ReactiveUI;
using FracturingFog.Abstractions;

namespace FracturingFog.UI.Avalonia;

/// <summary>
/// Public entry point invoked from the existing FracturingFogCLD WinExe
/// when launched with the --avalonia flag. Owns the Avalonia AppBuilder so
/// the legacy WinForms Program.cs does not need any Avalonia using-directives
/// beyond this single static call.
/// </summary>
public static class AvaloniaShell
{
    /// <summary>
    /// Optional callback fired by <see cref="Views.MainWindow"/> the first
    /// time its GPU surface becomes available. The WinExe's Program.cs sets
    /// this before calling <see cref="Run"/> so the Avalonia shell never has
    /// to reference the DirectX renderer directly — that keeps UI.Avalonia
    /// portable to platforms where Vortice is not available.
    ///
    /// Set to <c>null</c> (the default) to launch the Avalonia shell with no
    /// renderer wiring, useful for layout-only testing.
    /// </summary>
    public static Action<IGpuSurface>? OnSurfaceReady { get; set; }

    public static int Run(string[] args)
    {
        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args ?? Array.Empty<string>());
    }

    /// <summary>
    /// Convenience overload — assigns the renderer bootstrap callback and
    /// then launches the shell in a single call.
    /// </summary>
    public static int Run(string[] args, Action<IGpuSurface>? onSurfaceReady)
    {
        OnSurfaceReady = onSurfaceReady;
        return Run(args);
    }

    /// <summary>
    /// Standalone AppBuilder used by both the in-process launcher above and
    /// the Avalonia XAML previewer (which expects a parameterless factory
    /// named BuildAvaloniaApp on the assembly).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
