// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using FracturingFog.UI.Avalonia.Controls;
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
public sealed partial class UserBulbView : UserControl
{
    private UserBulbViewModel? _vm;
    private DslCodeEditor? _sourceEditor;
    private ItemsControl? _chainItems;

    public UserBulbView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _sourceEditor = this.FindControl<DslCodeEditor>("SourceEditor");
        _chainItems = this.FindControl<ItemsControl>("ChainItems");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.HelpRequested -= OnHelpRequested;
            _vm.ErrorSpanChanged -= OnErrorSpanChanged;
        }
        _vm = DataContext as UserBulbViewModel;
        if (_vm != null)
        {
            _vm.HelpRequested += OnHelpRequested;
            _vm.ErrorSpanChanged += OnErrorSpanChanged;
        }
    }

    private void OnHelpRequested(object? sender, (string DocId, string? Anchor, string Title) args)
        => HelpViewerLauncher.Show(TopLevel.GetTopLevel(this) as Window, args.DocId, args.Anchor, args.Title);

    // #1365 — route the compile error to the box it belongs to: the source
    // editor, or (#1102) the chain step's editor, and clear the others.
    // DslCodeEditor.ShowError underlines in code view and keeps the deferred
    // selection in plain view.
    private void OnErrorSpanChanged(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        int step = _vm.ErrorSpanStep;
        int start = _vm.ErrorSpanStart, len = _vm.ErrorSpanLength;
        string? message = _vm.StatusMessage;

        if (step < 0) _sourceEditor?.ShowError(start, len, message);
        else _sourceEditor?.ClearError();

        if (_chainItems == null) return;
        for (int i = 0; i < _chainItems.ItemCount; i++)
        {
            var editor = _chainItems.ContainerFromIndex(i)?
                .GetVisualDescendants().OfType<DslCodeEditor>().FirstOrDefault();
            if (editor == null) continue;
            if (i == step) editor.ShowError(start, len, message);
            else editor.ClearError();
        }
    }
}
