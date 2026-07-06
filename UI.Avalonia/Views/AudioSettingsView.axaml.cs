using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>AudioSettingsDialog</c>. VM holds all state and raises
/// <see cref="AudioSettingsViewModel.CloseRequested"/>; the host window
/// (<see cref="Services.PanelHostWindow"/>) or shell owns closing. Host drives
/// meter refresh by calling <see cref="AudioSettingsViewModel.Tick"/>
/// periodically so the dialog stays renderer-agnostic.
/// </summary>
public sealed partial class AudioSettingsView : UserControl
{
    public AudioSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnHelpClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/Slideshow-AudioReactive-Guide.md",
            "Audio-Reactive Engine",
            "Audio-Reactive Slideshow — Help");
}
