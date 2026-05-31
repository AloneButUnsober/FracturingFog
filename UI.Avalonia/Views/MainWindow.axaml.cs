// Views/MainWindow.axaml.cs
//
// Phase 2.3 F.2. Top-level Avalonia window. Binds to ShellViewModel.
//
// Responsibilities:
//   • Forward the GpuSurfaceControl's SurfaceReady to
//     AvaloniaShell.OnSurfaceReady — that's how the host bootstrapper
//     hands the native HWND off to the renderer.
//   • Attach the IFractalInputController to the InputSponge Border so
//     pointer/wheel/key events flow into FractalInputController. The
//     sponge sits above the NativeControlHost because native HWND
//     children do not forward pointer events back into Avalonia.
//   • Manage three modeless child windows (FloatingMenu /
//     ColorThemeEditor / FloatingHelp) by tracking ShellViewModel's
//     IsXxxVisible flags. Clicking the OS close button cancels the
//     close and flips the flag false so the next Show works.

using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FracturingFog.Input;
using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public sealed partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private IDisposable? _inputAdapter;
    private Border? _sponge;
    private bool _sortMenusAttached;

    private FloatingMenuView? _menuWin;
    private ColorThemeEditorView? _editorWin;
    private FloatingHelpView? _helpWin;
    private FFClientView? _ffClientWin;
    private ServerAdminView? _serverAdminWin;

    // Set true in OnClosed so per-window Closing handlers stop cancelling
    // the close (otherwise app shutdown leaves child windows orphaned).
    private bool _shuttingDown;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var surface = this.FindControl<GpuSurfaceControl>("GpuSurface");
        if (surface != null)
        {
            surface.SurfaceReady += (_, _) =>
            {
                if (surface.Surface == null) return;
                // Hand the live native surface to whoever set the bootstrap
                // callback (the WinExe's Program.cs in --avalonia mode).
                // The callback owns renderer construction from here.
                AvaloniaShell.OnSurfaceReady?.Invoke(surface.Surface);
            };
        }

        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;

        // Grab keyboard focus onto the InputSponge as soon as the window
        // opens so WASD/QE pan-zoom and the 3D camera/light keys work
        // before the user's first click. A Focusable Border is not
        // auto-focused by Avalonia, so without this the controller never
        // sees a KeyDown until the surface is clicked.
        Opened += OnOpened;

        // Command-level shortcuts (M/T/R/V/Escape). Pan/zoom/3D keys are
        // consumed by the InputSponge's AvaloniaInputAdapter and never reach
        // here; the controller returns false for these UI commands, so they
        // bubble up unhandled and we route them to the shell. Mirrors the
        // universal shortcuts in WinForms MainForm.OnKeyDown.
        KeyDown += OnWindowKeyDown;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _sponge ??= this.FindControl<Border>("InputSponge");
        _sponge?.Focus();
    }

    // Right-click menu on the render surface. Built in code-behind (not in
    // XAML) because the ContextMenu lives outside the visual tree, so its
    // {Binding} expressions don't see the ShellViewModel under compiled
    // bindings. Same pattern ComboSortMenu uses for the toolbar combos —
    // ContextRequested + MenuFlyout.ShowAt with direct command invocation.
    private bool _contextMenuAttached;
    private void AttachContextMenu(Border sponge, ShellViewModel shell)
    {
        if (_contextMenuAttached) return;
        _contextMenuAttached = true;

        sponge.ContextRequested += (_, e) =>
        {
            var flyout = new MenuFlyout();
            AddItem(flyout, "Toolbar",            () => shell.IsToolbarVisible   = !shell.IsToolbarVisible);
            AddItem(flyout, "Menu",               () => shell.IsFloatingMenuVisible = !shell.IsFloatingMenuVisible);
            AddItem(flyout, "Status",             () => shell.IsStatusBarVisible = !shell.IsStatusBarVisible);
            AddItem(flyout, "Reset View",         () => shell.Main.ResetViewCommand.Execute().Subscribe());
            AddItem(flyout, "Grid",               () => shell.Main.ShowGrid      = !shell.Main.ShowGrid);
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Span Monitors",      () => shell.ToggleSpanCommand.Execute().Subscribe());
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Slideshow",          () => shell.ToggleSlideshowCommand.Execute().Subscribe());
            AddItem(flyout, "Watermark",          () => shell.Main.ShowWatermark = !shell.Main.ShowWatermark);
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Video",              () => shell.ToggleVideoCommand.Execute().Subscribe());
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Save Current Region",() => shell.SaveRegionCommand.Execute().Subscribe());
            AddItem(flyout, "Save Image…",        () => shell.ScreenshotCommand.Execute().Subscribe());
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Params",             () => shell.ShowFractalParamsCommand.Execute().Subscribe());
            AddItem(flyout, "Edit Theme",         () => shell.ShowColorThemeEditorCommand.Execute().Subscribe());
            flyout.Items.Add(new Separator());
            AddItem(flyout, "Help…",              () => shell.ShowHelpCommand.Execute().Subscribe());

            flyout.ShowAt(sponge, showAtPointer: true);
            e.Handled = true;
        };
    }

    private static void AddItem(MenuFlyout flyout, string header, Action invoke)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => invoke();
        flyout.Items.Add(mi);
    }

    private void FocusSponge() => _sponge?.Focus();

    // ── Command-key routing ───────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell == null || e.Handled) return;

        // Don't steal keys from an editable control (toolbar combos / dialog
        // fields). Escape is always allowed so it can cancel span / a run.
        if (e.Key != Key.Escape && IsEditableFocused()) return;

        // Command keys (M/T/R/V/Escape) — unmodified only; Ctrl/Alt/Shift
        // combos are reserved (diagnostic toggles, precise-pan).
        if (e.KeyModifiers == KeyModifiers.None)
        {
            InputKey cmd = e.Key switch
            {
                Key.M => InputKey.M,
                Key.T => InputKey.T,
                Key.R => InputKey.R,
                Key.V => InputKey.V,
                Key.Escape => InputKey.Escape,
                _ => InputKey.None,
            };
            if (cmd != InputKey.None)
            {
                if (_shell.HandleCommandKey(cmd)) e.Handled = true;
                return;
            }
        }

        // Pan / zoom / 3-D camera + light keys. Forwarded to the controller
        // here so they still work when keyboard focus sits on a toolbar
        // button (after a click) rather than the input sponge. When the
        // sponge IS focused its adapter handles the key first and sets
        // e.Handled, so this is skipped. A focused ComboBox is caught by the
        // IsEditableFocused() guard above, so its own arrow / type-ahead
        // navigation is preserved.
        if (_sponge == null) return;
        var ki = AvaloniaInputAdapter.BuildKeyInput(e, _sponge);
        if (ki.Key != InputKey.None && _shell.Main.Input.OnKeyDown(ki))
            e.Handled = true;
    }

    private bool IsEditableFocused()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox or AutoCompleteBox or NumericUpDown;
    }

    // ── Shell wiring ──────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachShell();
        if (DataContext is ShellViewModel shell)
            AttachShell(shell);
    }

    private void AttachShell(ShellViewModel shell)
    {
        _shell = shell;

        _sponge ??= this.FindControl<Border>("InputSponge");
        if (_sponge != null)
        {
            _inputAdapter = AvaloniaInputAdapter.Attach(_sponge, shell.Main.Input);
            AttachContextMenu(_sponge, shell);
        }

        // Right-click sort menus on the toolbar Region / Theme combos. The
        // build callbacks read the live _shell so they stay correct if the
        // DataContext is swapped; attach once so ContextRequested handlers
        // don't stack on re-attach.
        if (!_sortMenusAttached)
        {
            ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarRegionCombo"),
                () => _shell?.FloatingMenu.BuildRegionSortMenu() ?? System.Array.Empty<ComboMenuItem>());
            ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarThemeCombo"),
                () => _shell?.FloatingMenu.BuildThemeSortMenu() ?? System.Array.Empty<ComboMenuItem>());
            _sortMenusAttached = true;
        }

        shell.PropertyChanged += OnShellPropertyChanged;
        shell.Main.PropertyChanged += OnMainPropertyChanged;

        // Initial sync in case the shell already has flags set.
        SyncMenu();
        SyncEditor();
        SyncHelp();
    }

    private void DetachShell()
    {
        _inputAdapter?.Dispose();
        _inputAdapter = null;

        if (_shell != null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _shell.Main.PropertyChanged -= OnMainPropertyChanged;
        }
        _shell = null;
    }

    // Picking a fractal type / quality from a toolbar combo leaves keyboard
    // focus on that combo, so the WASD/QE pan-zoom + arrow/PgUp/etc. 3-D
    // camera keys would route to the combo instead of the controller. Pull
    // focus back to the input sponge after the selection lands so the keys
    // immediately drive the fractal — no extra click on the surface needed.
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedFractalType)
                           or nameof(MainViewModel.SelectedFractalEntry)
                           or nameof(MainViewModel.SelectedQuality))
            FocusSponge();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsFloatingMenuVisible):
                SyncMenu();
                break;
            case nameof(ShellViewModel.IsColorThemeEditorVisible):
            case nameof(ShellViewModel.ColorThemeEditor):
                SyncEditor();
                break;
            case nameof(ShellViewModel.IsHelpVisible):
            case nameof(ShellViewModel.Help):
                SyncHelp();
                break;
            case nameof(ShellViewModel.IsFFClientVisible):
            case nameof(ShellViewModel.FFClient):
                SyncFFClient();
                break;
            case nameof(ShellViewModel.IsServerAdminVisible):
            case nameof(ShellViewModel.ServerAdmin):
                SyncServerAdmin();
                break;
        }
    }

    // ── Child window sync (lazy create, Show / Hide) ──────────────────────

    private void SyncMenu()
    {
        if (_shell == null) return;
        if (_shell.IsFloatingMenuVisible)
        {
            if (_menuWin == null)
            {
                _menuWin = new FloatingMenuView { DataContext = _shell.FloatingMenu };
                _menuWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsFloatingMenuVisible = false;
                };
            }
            if (!_menuWin.IsVisible) _menuWin.Show(this);
        }
        else
        {
            _menuWin?.Hide();
        }
    }

    private void SyncEditor()
    {
        if (_shell == null) return;
        if (_shell.IsColorThemeEditorVisible && _shell.ColorThemeEditor != null)
        {
            if (_editorWin == null)
            {
                _editorWin = new ColorThemeEditorView { DataContext = _shell.ColorThemeEditor };
                _editorWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsColorThemeEditorVisible = false;
                };
            }
            else if (_editorWin.DataContext != _shell.ColorThemeEditor)
            {
                // Editor VM was re-created (rare — happens if shell rebuilds).
                _editorWin.DataContext = _shell.ColorThemeEditor;
            }
            if (!_editorWin.IsVisible) _editorWin.Show(this);
        }
        else
        {
            _editorWin?.Hide();
        }
    }

    private void SyncHelp()
    {
        if (_shell == null) return;
        if (_shell.IsHelpVisible && _shell.Help != null)
        {
            if (_helpWin == null)
            {
                _helpWin = new FloatingHelpView { DataContext = _shell.Help };
                _helpWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsHelpVisible = false;
                };
            }
            else if (_helpWin.DataContext != _shell.Help)
            {
                _helpWin.DataContext = _shell.Help;
            }
            if (!_helpWin.IsVisible) _helpWin.Show(this);
        }
        else
        {
            _helpWin?.Hide();
        }
    }

    private void SyncFFClient()
    {
        if (_shell == null) return;
        if (_shell.IsFFClientVisible && _shell.FFClient != null)
        {
            if (_ffClientWin == null)
            {
                _ffClientWin = new FFClientView { DataContext = _shell.FFClient };
                _ffClientWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsFFClientVisible = false;
                };
            }
            else if (_ffClientWin.DataContext != _shell.FFClient)
            {
                _ffClientWin.DataContext = _shell.FFClient;
            }
            if (!_ffClientWin.IsVisible) _ffClientWin.Show(this);
        }
        else
        {
            _ffClientWin?.Hide();
        }
    }

    private void SyncServerAdmin()
    {
        if (_shell == null) return;
        if (_shell.IsServerAdminVisible && _shell.ServerAdmin != null)
        {
            if (_serverAdminWin == null)
            {
                _serverAdminWin = new ServerAdminView { DataContext = _shell.ServerAdmin };
                _serverAdminWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsServerAdminVisible = false;
                };
            }
            else if (_serverAdminWin.DataContext != _shell.ServerAdmin)
            {
                _serverAdminWin.DataContext = _shell.ServerAdmin;
            }
            if (!_serverAdminWin.IsVisible) _serverAdminWin.Show(this);
        }
        else
        {
            _serverAdminWin?.Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shuttingDown = true;
        _inputAdapter?.Dispose();
        _inputAdapter = null;

        _menuWin?.Close();
        _editorWin?.Close();
        _helpWin?.Close();
        _ffClientWin?.Close();
        _serverAdminWin?.Close();

        DetachShell();
    }
}
