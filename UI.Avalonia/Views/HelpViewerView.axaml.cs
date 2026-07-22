// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Markdown-backed contextual Help viewer. Hybrid-shell: a UserControl hosted
/// modeless by <see cref="HelpViewerLauncher"/>, which owns the window chrome
/// and wires the VM's <c>CloseRequested</c> to the host window's close.
/// </summary>
public sealed partial class HelpViewerView : UserControl
{
    private HelpViewerViewModel? _hooked;

    public HelpViewerView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_hooked != null) _hooked.ScrollToRequested -= OnScrollToRequested;
        _hooked = DataContext as HelpViewerViewModel;
        if (_hooked != null) _hooked.ScrollToRequested += OnScrollToRequested;
    }

    // A TOC/cross-doc link resolved to a heading — scroll it into view. Posted
    // to the dispatcher so a freshly-navigated doc has completed layout first.
    private void OnScrollToRequested(Control target)
    {
        Dispatcher.UIThread.Post(() => target.BringIntoView(), DispatcherPriority.Background);
    }
}
