// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
public sealed partial class UserBulbView : UserControl
{
    private UserBulbViewModel? _vm;
    private TextBox? _sourceEditor;
    // Pending span to apply on next LostFocus — avoids clobbering an active
    // edit caret while the user is typing.
    private int _pendingStart = -1;
    private int _pendingEnd = -1;

    public UserBulbView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _sourceEditor = this.FindControl<TextBox>("SourceEditor");
        if (_sourceEditor != null) _sourceEditor.LostFocus += OnSourceLostFocus;
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

    private void OnErrorSpanChanged(object? sender, EventArgs e)
    {
        if (_vm == null || _sourceEditor == null) return;
        int start = _vm.ErrorSpanStart;
        int len = _vm.ErrorSpanLength;
        int textLen = _sourceEditor.Text?.Length ?? 0;
        start = Math.Clamp(start, 0, textLen);
        int end = Math.Clamp(start + len, start, textLen);

        if (len == 0)
        {
            _pendingStart = _pendingEnd = -1;
            return;
        }

        _pendingStart = start;
        _pendingEnd = end;
        // Apply immediately only when editor isn't focused — otherwise wait
        // for LostFocus so the user's caret/IME composition isn't clobbered.
        if (!_sourceEditor.IsFocused) FlushPending();
    }

    private void OnSourceLostFocus(object? sender, RoutedEventArgs e) => FlushPending();

    private void FlushPending()
    {
        if (_pendingStart < 0 || _sourceEditor == null) return;
        _sourceEditor.SelectionStart = _pendingStart;
        _sourceEditor.SelectionEnd = _pendingEnd;
        _pendingStart = _pendingEnd = -1;
    }
}
