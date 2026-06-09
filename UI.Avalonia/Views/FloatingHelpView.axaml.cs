using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>FloatingHelp</c>. Tab-based help window. Help text
/// comes from the host's <see cref="FracturingFog.Help.IHelpContentProvider"/>;
/// the host also handles <see cref="FloatingHelpViewModel.LinkRequested"/>
/// to launch URLs in the system browser. Esc closes.
/// </summary>
public sealed partial class FloatingHelpView : Window
{
    public FloatingHelpView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FloatingHelpViewModel vm)
                vm.CloseRequested += (_, _) => Close();
        };
    }
}
