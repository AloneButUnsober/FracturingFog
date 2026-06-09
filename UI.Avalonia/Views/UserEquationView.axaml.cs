using System;
using Avalonia.Controls;
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
public sealed partial class UserEquationView : Window
{
    private TextBox? _userEqEditor;
    private TextBox? _dslEditor;
    private UserEquationViewModel? _vm;

    public UserEquationView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        _userEqEditor = this.FindControl<TextBox>("UserEquationEditor");
        _dslEditor    = this.FindControl<TextBox>("DslEditor");
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
    {
        var view = new HelpViewerView
        {
            DataContext = new ViewModels.HelpViewerViewModel(docId, anchor, title),
        };
        view.Show(this);
    }

    // Select the offending substring in the tab's editor so the user sees
    // exactly what tripped the validator. Selection IS Avalonia's native
    // highlight — no custom adorner / overlay needed for PR2. Zero-length
    // spans clear any prior selection by collapsing the caret.
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
        editor.SelectionStart = start;
        editor.SelectionEnd = end;
    }
}
