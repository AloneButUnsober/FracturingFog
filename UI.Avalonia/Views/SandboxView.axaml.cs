using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>SandboxDialog</c>. Modeless editor for the restricted
/// Sandbox expression DSL. Host wires the VM's events: NamePromptRequested,
/// ConfirmDeleteRequested, SaveFilePromptRequested, OpenFilePromptRequested,
/// MessageRequested, CompileRequested, PromotionChanged.
/// </summary>
public sealed partial class SandboxView : Window
{
    public SandboxView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }
}
