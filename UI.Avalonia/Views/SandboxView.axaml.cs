using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>SandboxDialog</c>. Modeless editor for the restricted
/// Sandbox expression DSL. Host wires the VM's events: NamePromptRequested,
/// ConfirmDeleteRequested, SaveFilePromptRequested, OpenFilePromptRequested,
/// MessageRequested, CompileRequested, PromotionChanged. The view owns
/// HelpRequested → opens HelpViewerView in-process.
/// </summary>
public sealed partial class SandboxView : Window
{
    private SandboxViewModel? _vm;

    public SandboxView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.HelpRequested -= OnHelpRequested;
        _vm = DataContext as SandboxViewModel;
        if (_vm != null) _vm.HelpRequested += OnHelpRequested;
    }

    private void OnHelpRequested(string docId, string? anchor, string title)
    {
        var view = new HelpViewerView
        {
            DataContext = new HelpViewerViewModel(docId, anchor, title),
        };
        view.Show(this);
    }
}
