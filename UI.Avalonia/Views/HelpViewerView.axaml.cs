using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public sealed partial class HelpViewerView : Window
{
    public HelpViewerView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        if (DataContext is HelpViewerViewModel vm) vm.CloseRequested += Close;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HelpViewerViewModel v) v.CloseRequested += Close;
        };
    }
}
