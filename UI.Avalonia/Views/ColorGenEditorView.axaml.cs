// Views/ColorGenEditorView.axaml.cs
//
// Code-behind for the ColorGen editor. Hybrid-shell: a UserControl hosted
// modeless by AvaloniaShellBootstrap (PanelHostWindow); the host owns chrome +
// close, and Bootstrap wires HotLoad / Generate / NamePrompt / ConfirmDelete.
// Interactive logic lives in ColorGenEditorViewModel (binding).

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ColorGenEditorView : UserControl
{
    public ColorGenEditorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => HelpViewerLauncher.Show(
            TopLevel.GetTopLevel(this) as Window,
            "User/ColorGen-UserGuide.md",
            null,
            "ColorGen Editor — Help");
}
