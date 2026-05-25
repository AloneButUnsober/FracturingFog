using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of the legacy WinForms <c>SlideshowSettingsDialog</c>.
/// Owns no layout logic — everything visual lives in the .axaml file with
/// DIP-based sizing so it scales correctly at any DPI / resolution.
///
/// Use as a modal dialog:
/// <code>
///   var vm = new SlideshowSettingsViewModel(currentSettings, audioReactive);
///   var view = new SlideshowSettingsView { DataContext = vm };
///   var ok = await view.ShowDialog&lt;bool&gt;(owner);
///   if (ok) { settings = vm.Result; audioReactive = vm.AudioReactiveResult; }
/// </code>
/// </summary>
public sealed partial class SlideshowSettingsView : Window
{
    public SlideshowSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
