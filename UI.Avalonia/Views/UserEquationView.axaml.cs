using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>UserEquationDialog</c>. Owns no layout logic — VM drives
/// all state via two-way bindings + the four host-callback events
/// (CompileRequested, RenderRequested, PromotionChanged, NamePromptRequested,
/// ConfirmDeleteRequested). Modeless by intent — open and leave open while
/// the user iterates on the equation source.
/// </summary>
public sealed partial class UserEquationView : Window
{
    public UserEquationView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }
}
