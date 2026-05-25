using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace FracturingFog.UI.Avalonia;

/// <summary>
/// Public entry point invoked from the existing FracturingFogCLD WinExe
/// when launched with the --avalonia flag. Owns the Avalonia AppBuilder so
/// the legacy WinForms Program.cs does not need any Avalonia using-directives
/// beyond this single static call.
/// </summary>
public static class AvaloniaShell
{
    public static int Run(string[] args)
    {
        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args ?? Array.Empty<string>());
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
