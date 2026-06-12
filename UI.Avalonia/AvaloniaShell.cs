using System;
using Avalonia;
using ReactiveUI.Avalonia;
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

    /// <summary>
    /// Invoked by the host bootstrap (from its native HWND mouse subclass)
    /// when the user releases the right mouse button over the render surface.
    /// The Avalonia <see cref="Views.MainWindow"/> assigns this to its
    /// context-menu opener. The bool argument is <c>true</c> when the click
    /// looked like a drag (long-hold or moved beyond a small dead-zone), so
    /// the menu can suppress itself in 3D fractal modes where the right
    /// button is overloaded for camera rotation.
    /// </summary>
    public static Action<bool>? ContextMenuRequested { get; set; }

    /// <summary>
    /// Invoked by the host bootstrap (from the native HWND mouse subclass)
    /// when any mouse button goes down over the render surface. The
    /// Avalonia <see cref="Views.MainWindow"/> uses this to pull keyboard
    /// focus back onto the input sponge — otherwise a toolbar ComboBox
    /// keeps logical focus after a click on the GPU surface (the native
    /// HWND child composites over the InputSponge and intercepts every
    /// WM_MOUSE*, so the sponge's PointerPressed → Focus() path never
    /// fires).
    /// </summary>
    public static Action? RenderSurfaceFocusRequested { get; set; }

    /// <summary>
    /// Toy Mode hook. When non-null, a left-click on the render surface is
    /// routed here instead of starting a fractal pan; the implementation
    /// (MainWindow code-behind) initiates an OS-driven window move on the
    /// top-level frame. Returns <c>true</c> if the click was consumed.
    /// Mirrors <see cref="ContextMenuRequested"/> in shape so the host
    /// bridge (NativeMouseForwarder via AvaloniaShellBootstrap) keeps the
    /// platform-specific P/Invoke out of UI.Avalonia.
    /// </summary>
    public static Func<bool>? LeftDragWindowHook { get; set; }

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
            .UseReactiveUI(rxui => { });
}
