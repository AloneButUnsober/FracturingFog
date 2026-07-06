using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of the legacy WinForms <c>SlideshowSettingsDialog</c>.
/// A plain <see cref="UserControl"/> so it can dock in the shell or pop out via
/// <see cref="Services.PanelHostWindow"/>; window chrome + placement live on the
/// host (see <c>AvaloniaDialogs.ShowSlideshowSettingsAsync</c>). Closing is
/// driven from the OK/Cancel/Start handlers via <see cref="CloseRequested"/>,
/// which the host window binds to.
/// </summary>
public sealed partial class SlideshowSettingsView : UserControl, IClosableDialog
{
    public SlideshowSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    /// <inheritdoc />
    public event EventHandler<bool>? CloseRequested;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is SlideshowSettingsViewModel vm)
        {
            vm.StartRequestedRaised -= OnStartRequestedRaised;
            vm.StartRequestedRaised += OnStartRequestedRaised;
            vm.NameFocusRequested -= OnNameFocusRequested;
            vm.NameFocusRequested += OnNameFocusRequested;
        }
    }

    // Start routes through the VM (StartCommand → ProceedToStart, or the
    // unsaved-changes prompt) which raises StartRequestedRaised; treat it as a
    // successful close so the host reads vm.Result (StartRequested flag set).
    private void OnStartRequestedRaised(object? sender, System.EventArgs e)
        => CloseRequested?.Invoke(this, true);

    private void OnNameFocusRequested(object? sender, System.EventArgs e)
    {
        var combo = this.FindControl<ComboBox>("NameCombo");
        combo?.Focus();
    }

    // OK/Cancel: the bound OkCommand/CancelCommand run alongside these Click
    // handlers (OkCommand.Commit populates vm.Result); we only request the
    // host close. Result is read by the host's Closed handler.
    private void OnOkClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, true);

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, false);

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/Slideshow-AudioReactive-Guide.md",
            null,
            "Slideshow Settings — Help");
}
