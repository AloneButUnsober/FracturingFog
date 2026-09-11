// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>UserEquationDialog</c>. Most state is VM-driven via
/// two-way bindings + host-callback events (CompileRequested, RenderRequested,
/// PromotionChanged, NamePromptRequested, ConfirmDeleteRequested, HotLoadRequested).
///
/// The one piece the view DOES own: applying the VM's <c>ErrorSpan*</c> to the
/// correct TextBox's <c>SelectionStart/End</c>. Avalonia's TextBox uses
/// SelectionStart/End as the canonical highlight mechanism; binding both
/// two-way fights the user's own caret moves, so we listen to an event from
/// the VM and apply once per validation cycle.
/// </summary>
public sealed partial class UserEquationView : UserControl
{
    private TextBox? _userEqEditor;
    private TextBox? _dslEditor;
    private UserEquationViewModel? _vm;

    // Pending span — applied only when the relevant editor is NOT focused.
    // Applying SelectionStart/End while the user is typing replaces the
    // user's next keystroke with the selected text (Avalonia TextBox follows
    // the standard convention: typing replaces selection). Defer the apply
    // to LostFocus so the highlight is non-destructive. Status-bar message
    // still updates immediately so the user sees the error.
    //
    // -1 sentinel means "no pending span".
    private int _pendingTab = -1;
    private int _pendingStart = -1;
    private int _pendingEnd = -1;

    public UserEquationView()
    {
        AvaloniaXamlLoader.Load(this);
        _userEqEditor = this.FindControl<TextBox>("UserEquationEditor");
        _dslEditor    = this.FindControl<TextBox>("DslEditor");
        if (_userEqEditor != null)
            _userEqEditor.LostFocus += (_, _) => { if (_pendingTab == 0) FlushPending(_userEqEditor); };
        if (_dslEditor != null)
            _dslEditor.LostFocus    += (_, _) => { if (_pendingTab == 1) FlushPending(_dslEditor); };
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

    // Receives the VM's "I just produced a new error span" notification. We
    // stash the span and apply it ONLY when the editor isn't focused — that
    // way the user's typing never gets clobbered by a selection sweep.
    // When the editor later loses focus (Alt+Tab, click elsewhere, focus
    // the Apply Fix button, etc.) the LostFocus handler flushes the stash.
    private void ApplyErrorSpan(int tab)
    {
        if (_vm == null) return;
        var editor = tab == 1 ? _dslEditor : _userEqEditor;
        if (editor == null) return;

        int start = _vm.ErrorSpanStart;
        int len = _vm.ErrorSpanLength;
        int textLen = editor.Text?.Length ?? 0;
        start = Math.Clamp(start, 0, textLen);
        int end = Math.Clamp(start + len, start, textLen);

        if (len == 0)
        {
            // Clear request — no highlight to apply; drop any pending one.
            _pendingTab = -1;
            _pendingStart = _pendingEnd = -1;
            return;
        }

        _pendingTab = tab;
        _pendingStart = start;
        _pendingEnd = end;
        if (!editor.IsFocused) FlushPending(editor);
    }

    // Copy the interpreted-math LaTeX (#754) to the clipboard. The view owns
    // the clipboard call (TopLevel.Clipboard is a UI-layer accessor, like the
    // ErrorSpan selection handling above); the VM only supplies the string and
    // the confirmation status. Fire-and-forget — a copy failure logs and shows
    // a status note but never throws a modal.
    private async void OnCopyLatex(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        string latex = _vm.PreviewLatexText ?? string.Empty;
        if (string.IsNullOrEmpty(latex)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetValueAsync(DataFormat.Text, latex);
            _vm.ShowStatus("✓ LaTeX copied");
        }
        catch (Exception ex)
        {
            _vm.ShowStatus($"Copy failed: {ex.Message}", isError: true);
        }
    }

    private void FlushPending(TextBox editor)
    {
        if (_pendingStart < 0) return;
        editor.SelectionStart = _pendingStart;
        editor.SelectionEnd = _pendingEnd;
        _pendingTab = -1;
        _pendingStart = _pendingEnd = -1;
    }
}
