using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Avalonia embedded-mode Video Settings dialog. OK populates
/// <c>VideoSettingsViewModel.Result</c>; Cancel leaves it null.</summary>
public sealed partial class VideoSettingsView : Window
{
    public VideoSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        // Avalonia raises Click BEFORE executing Command. If we Close here
        // first, the window's Closed handler reads vm.Result before
        // OkCommand → Commit has populated it, so the host's
        // ApplyEditedVideoSettings(null) silently discards the user's edits
        // (ThemeFadeEnabled / ThemesPerLeg etc. revert to whatever the
        // saved config had). Execute the command synchronously here so
        // Result is populated before Close triggers the Closed handler.
        if (DataContext is VideoSettingsViewModel vm)
            vm.Commit();
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(this,
            "User/Capture-Guide.md",
            "Video Zoom",
            "Video Settings — Help");
}
