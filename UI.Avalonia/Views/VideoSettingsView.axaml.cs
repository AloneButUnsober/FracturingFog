using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Avalonia embedded-mode Video Settings dialog. OK populates
/// <c>VideoSettingsViewModel.Result</c>; Cancel leaves it null.</summary>
public sealed partial class VideoSettingsView : Window
{
    public VideoSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
