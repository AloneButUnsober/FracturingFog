using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>UserBulbDialog</c>. Modeless 3D bulb editor. Host
/// wires the VM's events: CompileRequested, RenderRequested, PromotionChanged,
/// NamePromptRequested, ConfirmDeleteRequested, OpenFilePromptRequested,
/// SaveFilePromptRequested, MessageRequested, ExportMeshRequested. Host
/// also drives <see cref="ViewModels.UserBulbViewModel.AnimationTick"/>
/// from its own 30 Hz timer while IsPlaying is true and calls
/// <see cref="ViewModels.UserBulbViewModel.NotifyRenderDone"/> when a frame
/// finishes uploading. The view itself owns <see cref="ViewModels.UserBulbViewModel.HelpRequested"/>
/// — opens HelpViewerView in-process.
/// </summary>
public sealed partial class UserBulbView : Window
{
    private UserBulbViewModel? _vm;

    public UserBulbView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.HelpRequested -= OnHelpRequested;
        _vm = DataContext as UserBulbViewModel;
        if (_vm != null) _vm.HelpRequested += OnHelpRequested;
    }

    private void OnHelpRequested(object? sender, (string DocId, string? Anchor, string Title) args)
    {
        var view = new HelpViewerView
        {
            DataContext = new HelpViewerViewModel(args.DocId, args.Anchor, args.Title),
        };
        view.Show(this);
    }
}
