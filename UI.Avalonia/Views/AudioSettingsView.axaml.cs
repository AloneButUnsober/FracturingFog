using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>AudioSettingsDialog</c>. VM holds all state. Host
/// drives meter refresh by calling <see cref="AudioSettingsViewModel.Tick"/>
/// periodically (e.g. via DispatcherTimer) so the dialog stays renderer-
/// agnostic.
/// </summary>
public sealed partial class AudioSettingsView : Window
{
    public AudioSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AudioSettingsViewModel vm)
            {
                vm.CloseRequested -= OnCloseRequested;
                vm.CloseRequested += OnCloseRequested;
            }
        };
    }

    private void OnCloseRequested(object? sender, bool result) => Close(result);
}
