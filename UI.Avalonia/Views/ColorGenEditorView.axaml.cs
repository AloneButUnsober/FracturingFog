// Views/ColorGenEditorView.axaml.cs
//
// Code-behind for the ColorGenEditor dialog. Mirrors UserEquationView —
// nothing happens here beyond InitializeComponent; all interactive logic
// lives in ColorGenEditorViewModel (binding) or the host wiring in
// AvaloniaShellBootstrap (HotLoad / Generate / NamePrompt / ConfirmDelete).

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ColorGenEditorView : Window
{
    public ColorGenEditorView()
    {
        InitializeComponent();
        EscapeCloseBehavior.Attach(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
