// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>SandboxDialog</c>. Modeless editor for the restricted
/// Sandbox expression DSL. Host wires the VM's events: NamePromptRequested,
/// ConfirmDeleteRequested, SaveFilePromptRequested, OpenFilePromptRequested,
/// MessageRequested, CompileRequested, PromotionChanged. The view owns
/// HelpRequested → opens HelpViewerView in-process, and (#1365) hands each
/// compile result's error span to the editor.
/// </summary>
public sealed partial class SandboxView : UserControl
{
    private SandboxViewModel? _vm;
    private readonly DslCodeEditor? _editor;

    public SandboxView()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<DslCodeEditor>("SourceEditor");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.HelpRequested -= OnHelpRequested;
            _vm.ErrorSpanChanged -= OnErrorSpanChanged;
        }
        _vm = DataContext as SandboxViewModel;
        if (_vm != null)
        {
            _vm.HelpRequested += OnHelpRequested;
            _vm.ErrorSpanChanged += OnErrorSpanChanged;
        }
    }

    private void OnErrorSpanChanged()
    {
        if (_vm == null || _editor == null) return;
        _editor.ShowError(_vm.ErrorSpanStart, _vm.ErrorSpanLength, _vm.StatusText, _vm.StatusIsError);
    }

    private void OnHelpRequested(string docId, string? anchor, string title)
        => HelpViewerLauncher.Show(TopLevel.GetTopLevel(this) as Window, docId, anchor, title);
}
