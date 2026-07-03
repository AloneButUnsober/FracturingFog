using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// General application-settings dialog (Avalonia). VM holds all state; the
/// window closes on <see cref="AppSettingsViewModel.CloseRequested"/> — true
/// after OK populates <see cref="AppSettingsViewModel.Result"/>, false on
/// Cancel so the host discards edits.
/// </summary>
public sealed partial class AppSettingsView : Window
{
    public AppSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AppSettingsViewModel vm)
            {
                vm.CloseRequested -= OnCloseRequested;
                vm.CloseRequested += OnCloseRequested;
            }
        };
    }

    private void OnCloseRequested(object? sender, bool result) => Close(result);
}
