// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Avalonia embedded-mode Video Settings panel. OK populates
/// <c>VideoSettingsViewModel.Result</c>; Cancel leaves it null. Closing is
/// driven from the OK/Cancel Click handlers via <see cref="CloseRequested"/>
/// (this view closes from code-behind rather than VM commands), which the
/// host window binds to — so the view stays a plain <see cref="UserControl"/>
/// able to dock or pop out.</summary>
public sealed partial class VideoSettingsView : UserControl, IClosableDialog
{
    public VideoSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public event EventHandler<bool>? CloseRequested;

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        // Avalonia raises Click BEFORE executing Command, and the host reads
        // vm.Result after close — so Commit synchronously here to populate
        // Result before requesting close, else the edits are discarded.
        if (DataContext is VideoSettingsViewModel vm)
            vm.Commit();
        CloseRequested?.Invoke(this, true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, false);

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/Capture-Guide.md",
            "Video Zoom",
            "Video Settings — Help");
}
