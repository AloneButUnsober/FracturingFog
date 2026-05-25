using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Controls;

namespace FracturingFog.UI.Avalonia.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var surface = this.FindControl<GpuSurfaceControl>("GpuSurface");
        var status = this.FindControl<TextBlock>("StatusText");

        if (surface != null)
        {
            surface.SurfaceReady += (_, _) =>
            {
                if (surface.Surface == null) return;

                if (status != null)
                {
                    status.Text = $"Surface ready: kind={surface.Surface.Kind} " +
                                  $"handle=0x{surface.Surface.Handle.ToInt64():X} " +
                                  $"{surface.Surface.PixelWidth}×{surface.Surface.PixelHeight} " +
                                  $"@ {surface.Surface.DpiScale:F2}x";
                }

                // Hand the live surface to whoever set the bootstrap callback
                // (the WinExe's Program.cs in --avalonia mode). The callback
                // owns the renderer's lifetime from this point forward.
                AvaloniaShell.OnSurfaceReady?.Invoke(surface.Surface);
            };
        }
    }
}
