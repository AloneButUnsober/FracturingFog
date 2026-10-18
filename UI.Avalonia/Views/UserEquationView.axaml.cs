// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>UserEquationDialog</c>. Most state is VM-driven via
/// two-way bindings + host-callback events (CompileRequested, RenderRequested,
/// PromotionChanged, NamePromptRequested, ConfirmDeleteRequested, HotLoadRequested).
///
/// The one piece the view DOES own: handing the VM's <c>ErrorSpan*</c> to the
/// editor (#1089: one editor). #1365 — <see cref="DslCodeEditor.ShowError"/>
/// underlines it in code view (message on hover) and, in plain view, keeps the
/// old selection deferred to focus loss, so typing is never clobbered.
/// </summary>
public sealed partial class UserEquationView : UserControl
{
    private DslCodeEditor? _editor;
    private UserEquationViewModel? _vm;

    public UserEquationView()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<DslCodeEditor>("EquationEditor");
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.ErrorSpanChanged -= ApplyErrorSpan;
            _vm.HelpRequested -= OnHelpRequested;
        }
        _vm = DataContext as UserEquationViewModel;
        if (_vm != null)
        {
            _vm.ErrorSpanChanged += ApplyErrorSpan;
            _vm.HelpRequested += OnHelpRequested;
        }
    }

    private void OnHelpRequested(string docId, string? anchor, string title)
        => HelpViewerLauncher.Show(TopLevel.GetTopLevel(this) as Window, docId, anchor, title);

    // Receives the VM's "I just produced a new error span" notification
    // (length 0 = cleared). The status text is set before the span, so it is
    // the message for the hover tooltip; a non-error status (C#-style hint)
    // draws as a hint underline.
    private void ApplyErrorSpan()
    {
        if (_vm == null || _editor == null) return;
        _editor.ShowError(_vm.ErrorSpanStart, _vm.ErrorSpanLength, _vm.StatusText, _vm.StatusIsError);
    }

    // Copy the interpreted-math LaTeX (#754) / MathML (#756) to the clipboard.
    // The view owns the clipboard call (TopLevel.Clipboard is a UI-layer
    // accessor, like the ErrorSpan selection handling above); the VM only
    // supplies the string and the confirmation status. Fire-and-forget — a copy
    // failure shows a status note but never throws a modal.
    private void OnCopyLatex(object? sender, RoutedEventArgs e)
        => _ = CopyToClipboard(_vm?.PreviewLatexText, "LaTeX");

    private void OnCopyMathml(object? sender, RoutedEventArgs e)
        => _ = CopyToClipboard(_vm?.PreviewMathmlText, "MathML");

    private async System.Threading.Tasks.Task CopyToClipboard(string? text, string label)
    {
        if (_vm == null || string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
            _vm.ShowStatus($"✓ {label} copied");
        }
        catch (Exception ex)
        {
            _vm.ShowStatus($"Copy failed: {ex.Message}", isError: true);
        }
    }

    // #764 / #765 — read MathML / LaTeX off the clipboard and import it into the
    // editor. The view owns the clipboard read (UI-layer accessor); the VM
    // owns the conversion and status reporting.
    private async void OnPasteMathml(object? sender, RoutedEventArgs e)
    {
        string? text = await ReadClipboardText();
        if (text != null) _vm?.ImportMathmlFromText(text);
    }

    private async void OnPasteLatex(object? sender, RoutedEventArgs e)
    {
        string? text = await ReadClipboardText();
        if (text != null) _vm?.ImportLatexFromText(text);
    }

    private async System.Threading.Tasks.Task<string?> ReadClipboardText()
    {
        if (_vm == null) return null;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return null;
        try
        {
            return await clipboard.TryGetTextAsync() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _vm.ShowStatus($"Clipboard read failed: {ex.Message}", isError: true);
            return null;
        }
    }
}
