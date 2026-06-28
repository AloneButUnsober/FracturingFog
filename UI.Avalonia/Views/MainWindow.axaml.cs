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
using System.Runtime.InteropServices;
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
    private WatermarkEditorView? _watermarkEditorWin;
    private FloatingHelpView? _helpWin;
    private FFClientView? _ffClientWin;
    private ServerAdminView? _serverAdminWin;
    private ClusterDashboardView? _clusterDashboardWin;
    private JobListView? _jobListWin;
    private JobDetailView? _jobDetailWin;
    private MiniMapWindow? _miniMapWin;
    private MiniDepthWindow? _miniDepthWin;
    private MiniWindowTether? _miniMapTether;
    private MiniWindowTether? _miniDepthTether;

    // S-X8 (2026-06-27) — hold the delegates ConfigureMiniDepth subscribes
    // to RenderHost.ColorMapChanged / FrameCompleted so DetachShell can
    // remove them. Without the field, the lambda capture pinned the window
    // on the long-lived RenderHost event list across shell rebuilds and
    // mini-depth open/close cycles, accumulating one handler per cycle.
    private EventHandler? _miniDepthColorMapHandler;
    private EventHandler<FracturingFog.Render.RenderFrameInfo>? _miniDepthFrameCompletedHandler;

    // Mini Mode (#12) — saved geometry restored on exit.
    private bool _miniModeActive;
    private global::Avalonia.Controls.WindowState _preMiniState;
    private global::Avalonia.Controls.WindowDecorations _preMiniDecorations;
    private global::Avalonia.PixelPoint _preMiniPosition;
    private double _preMiniWidth;
    private double _preMiniHeight;
    private double _preMiniMinWidth;
    private double _preMiniMinHeight;
    private bool _preMiniTopmost;
    private bool _preMiniToolbar;
    private bool _preMiniStatus;
    private bool _preMiniVcr;

    // Toy Mode — even smaller than Mini, no chrome at all, left-click-drag
    // moves the window. Mutually exclusive with Mini Mode.
    private bool _toyModeActive;
    private global::Avalonia.Controls.WindowState _preToyState;
    private global::Avalonia.Controls.WindowDecorations _preToyDecorations;
    private global::Avalonia.PixelPoint _preToyPosition;
    private double _preToyWidth;
    private double _preToyHeight;
    private double _preToyMinWidth;
    private double _preToyMinHeight;
    private bool _preToyTopmost;
    private bool _preToyToolbar;
    private bool _preToyStatus;
    private bool _preToyVcr;

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
        AttachStatusBarDrag();

        // Pull keyboard focus back onto the sponge whenever the user clicks
        // the GPU surface. The native HWND swallows WM_MOUSE* so the
        // sponge's own PointerPressed → Focus() path never fires — without
        // this hook, a focused toolbar ComboBox keeps capturing R/M/T/V
        // type-ahead after the click.
        AvaloniaShell.RenderSurfaceFocusRequested = FocusSponge;
    }

    // #12 follow-up: status bar acts as a drag handle so the user can move
    // the borderless mini-mode window. Wired once on first Opened; safe to
    // leave attached in normal mode (clicks on the status bar elsewhere
    // hand off to children first, so it's not intrusive).
    private bool _statusDragAttached;
    private void AttachStatusBarDrag()
    {
        if (_statusDragAttached) return;
        var status = this.FindControl<Border>("StatusBar");
        if (status == null) return;
        status.PointerPressed += OnStatusBarPointerPressed;
        _statusDragAttached = true;
    }

    private void OnStatusBarPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        // Only start a window drag on a primary-button click directly on the
        // status bar background. Lets child controls (e.g. server indicator
        // tooltip) still receive normal events.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); }
            catch { /* not all platforms support move-drag; ignore */ }
        }
    }

    // Right-click menu on the render surface. Built in code-behind because
    // it dispatches against ShellViewModel rather than compiled bindings.
    //
    // Open path: the GPU swap-chain HWND composites on top of every Avalonia
    // pixel and intercepts every WM_MOUSE* — so neither the InputSponge's
    // ContextRequested nor a window-level PointerReleased ever fires for a
    // click over the render area. NativeMouseForwarder subclasses that HWND,
    // and on WM_RBUTTONUP it raises AvaloniaShell.ContextMenuRequested with
    // a drag flag computed from down/up timestamp + distance. MainWindow
    // listens here and pops the menu, suppressing in 3D modes when the click
    // looked like a camera-rotate drag (matching legacy MainForm).
    private bool _contextMenuAttached;
    private ContextMenu? _contextMenu;
    private Border? _contextMenuTarget;
    private Action? _contextMenuSync;

    private void AttachContextMenu(Border sponge, ShellViewModel shell)
    {
        if (_contextMenuAttached) return;
        _contextMenuAttached = true;

        (_contextMenu, _contextMenuSync) = BuildContextMenu(shell);
        _contextMenuTarget = sponge;
        // Assign so the menu is parented to a control that's in the visual
        // tree (Open() needs a PlacementTarget that's attached); the assign
        // does not affect the Avalonia auto-open path because no
        // PointerReleased ever reaches the sponge.
        sponge.ContextMenu = _contextMenu;

        AvaloniaShell.ContextMenuRequested = wasDrag =>
        {
            // Drag suppresses menu in both 2D + 3D: 3D drag = camera rotate;
            // 2D drag = right-click rubber-band zoom (FractalInputController
            // applies the zoom on release). Plain right-click still pops.
            if (wasDrag) return;
            if (_contextMenu == null || _contextMenuTarget == null) return;
            // ContextMenu.Open(control) shows at the cursor by default
            // (Placement = Pointer is the framework default for ContextMenu).
            if (_contextMenu.IsOpen) _contextMenu.Close();
            // Sync dynamic item state (slideshow labels/enable) before opening
            // — ContextMenu.Opening isn't reliably raised on programmatic
            // .Open() in Avalonia 11, so do it here.
            _contextMenuSync?.Invoke();
            _contextMenu.Open(_contextMenuTarget);
        };
    }

    private (ContextMenu menu, Action sync) BuildContextMenu(ShellViewModel shell)
    {
        var menu = new ContextMenu();
        var toolbarItem = new MenuItem { Header = "Toolbar" };
        toolbarItem.Click += (_, _) => shell.IsToolbarVisible = !shell.IsToolbarVisible;
        menu.Items.Add(toolbarItem);
        AddItem(menu, "Menu",               () => shell.IsFloatingMenuVisible = !shell.IsFloatingMenuVisible);
        var statusItem = new MenuItem { Header = "Status" };
        statusItem.Click += (_, _) => shell.IsStatusBarVisible = !shell.IsStatusBarVisible;
        menu.Items.Add(statusItem);
        var onTopItem = new MenuItem { Header = "On Top" };
        onTopItem.Click += (_, _) => Topmost = !Topmost;
        menu.Items.Add(onTopItem);
        AddItem(menu, "Reset View",         () => shell.Main.ResetViewCommand.Execute().Subscribe());
        AddItem(menu, "Grid",               () => shell.Main.ShowGrid      = !shell.Main.ShowGrid);
        menu.Items.Add(new Separator());
        AddItem(menu, "Span Monitors",      () => shell.ToggleSpanCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());
        AddItem(menu, "Mini Map",           () => shell.ToggleMiniMapCommand.Execute().Subscribe());
        AddItem(menu, "Mini Depth",         () => shell.ToggleMiniDepthCommand.Execute().Subscribe());
        var miniModeItem = new MenuItem { Header = "Mini Mode" };
        miniModeItem.Click += (_, _) => shell.ToggleMiniModeCommand.Execute().Subscribe();
        menu.Items.Add(miniModeItem);
        var toyModeItem = new MenuItem { Header = "Toy Mode" };
        toyModeItem.Click += (_, _) => shell.ToggleToyModeCommand.Execute().Subscribe();
        menu.Items.Add(toyModeItem);
        AddItem(menu, "Slideshow",          () => shell.ToggleSlideshowCommand.Execute().Subscribe());
        // Slideshow-specific items. Header text + enable state updated each
        // time the menu opens (see Opening handler in BuildContextMenu's
        // caller path) to reflect current SlideshowEngine state.
        var lockRegionItem = new MenuItem { Header = "Slideshow: Lock Region" };
        lockRegionItem.Click += (_, _) => shell.ToggleSlideshowLockRegionCommand.Execute().Subscribe();
        menu.Items.Add(lockRegionItem);
        var focusItem = new MenuItem { Header = "Slideshow: More Colors" };
        focusItem.Click += (_, _) => shell.ToggleSlideshowFocusCommand.Execute().Subscribe();
        menu.Items.Add(focusItem);
        AddItem(menu, "Watermark",          () => shell.Main.ShowWatermark = !shell.Main.ShowWatermark);
        menu.Items.Add(new Separator());
        AddItem(menu, "Video",              () => shell.ToggleVideoCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());
        AddItem(menu, "Save Current Region",() => shell.SaveRegionCommand.Execute().Subscribe());
        AddItem(menu, "Save Image…",        () => shell.ScreenshotCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());
        AddItem(menu, "Params",             () => shell.ShowFractalParamsCommand.Execute().Subscribe());
        AddItem(menu, "Edit Theme",         () => shell.ShowColorThemeEditorCommand.Execute().Subscribe());
        AddItem(menu, "ColorGen Editor…",   () => shell.ShowColorGenEditorCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());
        AddItem(menu, "Help…",              () => shell.ShowHelpCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());
        AddItem(menu, "Close Program",      () => shell.FloatingMenu.CloseProgramCommand.Execute().Subscribe());

        // Refresh slideshow item state every time the menu opens. Avalonia's
        // MenuItem doesn't have a built-in checked indicator, so we encode
        // toggle state via the header prefix ("✓ ") + enable state via
        // IsEnabled. Mirrors legacy MainForm's slideshowLockRegionItem.Text /
        // slideshowFocusItem.Text logic. Invoked from the caller before
        // ContextMenu.Open() — Avalonia 11's MenuBase.Opening doesn't reliably
        // raise on programmatic Open(), so we drive sync directly.
        Action sync = () =>
        {
            bool running = shell.IsSlideshowRunning;
            lockRegionItem.IsEnabled = running;
            lockRegionItem.Header = (shell.SlideshowLockRegion ? "✓ " : "")
                                  + "Slideshow: Lock Region";
            focusItem.IsEnabled = running;
            // Label = next action (what a click will switch to), matching
            // legacy MainForm:
            //   FocusRegion=true  (3 themes/region)  → click → 8 themes  → "More Colors"
            //   FocusRegion=false (8 themes/region)  → click → 3 themes  → "More Regions"
            focusItem.Header = shell.SlideshowFocusRegion
                ? "Slideshow: More Colors"
                : "Slideshow: More Regions";
            onTopItem.Header = (Topmost ? "✓ " : "") + "On Top";

            // Toy Mode hides toolbar + status entirely — toggling them from
            // the menu would be a no-op (or worse, a confusing surprise on
            // exit). Greyed out for the duration; mirror Mini Mode handling
            // for the toolbar item only (Mini keeps the status bar visible
            // as a drag handle).
            toolbarItem.IsEnabled = !_toyModeActive && !_miniModeActive;
            statusItem.IsEnabled  = !_toyModeActive;
            miniModeItem.Header = (_miniModeActive ? "✓ " : "") + "Mini Mode";
            toyModeItem.Header  = (_toyModeActive  ? "✓ " : "") + "Toy Mode";
        };
        menu.Opening += (_, _) => sync();
        return (menu, sync);
    }

    private static void AddItem(ContextMenu menu, string header, Action invoke)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => invoke();
        menu.Items.Add(mi);
    }

    private void FocusSponge() => _sponge?.Focus();

    // ── Command-key routing ───────────────────────────────────────────────

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell == null || e.Handled) return;

        // Esc closes an open context menu before anything else looks at the
        // key. Without this, OnWindowKeyDown routes Esc to HandleCommandKey
        // (cancel-run) while the menu stays open, surprising the user.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None
            && _contextMenu != null && _contextMenu.IsOpen)
        {
            _contextMenu.Close();
            e.Handled = true;
            return;
        }

        // Backspace = Back: pop the most recent nav snapshot off the shell's
        // history stack. Like Escape, allowed even when a non-text combo has
        // focus so the user doesn't have to click the surface first.
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None
            && !(FocusManager?.GetFocusedElement() is TextBox))
        {
            if (_shell.GoBack()) { e.Handled = true; return; }
        }

        // Don't steal keys from an editable control (toolbar combos / dialog
        // fields). Escape is always allowed so it can cancel span / a run.
        if (e.Key != Key.Escape && IsEditableFocused()) return;

        // Shift+H = reset the perf HUD's rolling buffers so a new region /
        // video capture starts clean. Handled before the unmodified switch
        // so it doesn't fall through to plain H (toggle).
        if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.Shift)
        {
            _shell.Main.ResetPerfStats();
            e.Handled = true;
            return;
        }

        // Ctrl+G = toggle T3.1 GPU compute on the SP Mandelbrot path.
        // Handled before the unmodified switch so it doesn't fall through
        // to plain G (Grid toggle).
        if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
        {
            _shell.Main.UseGpuCompute = !_shell.Main.UseGpuCompute;
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+A / Ctrl+Shift+S — diagnostic toggles for the legacy
        // MandelbrotCalculator HP path. Bare A/S are reserved for WASD 3D
        // camera input, so the unblocked diagnostic combo is Ctrl+Shift.
        // Title gains a [ACCEL OFF] / [SA OFF] suffix while on. Used to
        // isolate deep-zoom pixelation regressions (BLA vs SA vs QD math).
        const KeyModifiers ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        if (e.Key == Key.A && e.KeyModifiers == ctrlShift)
        {
            _shell.ToggleMandelbrotAcceleration();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && e.KeyModifiers == ctrlShift)
        {
            _shell.ToggleMandelbrotSeriesApproximation();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.D && e.KeyModifiers == ctrlShift)
        {
            _shell.ToggleMandelbrotDdBla();
            e.Handled = true;
            return;
        }

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

            // Overlay / dialog toggles. Active in every fractal type so the
            // shortcuts work consistently regardless of selected mode.
            //   G  = Grid           K  = Watermark    H = Perf HUD (Shift+H = reset)
            //   P  = Params dialog  F1 = Help window
            switch (e.Key)
            {
                case Key.G:
                    _shell.Main.ShowGrid = !_shell.Main.ShowGrid;
                    e.Handled = true;
                    return;
                case Key.K:
                    _shell.Main.ShowWatermark = !_shell.Main.ShowWatermark;
                    e.Handled = true;
                    return;
                case Key.H:
                    _shell.Main.ShowPerfHud = !_shell.Main.ShowPerfHud;
                    e.Handled = true;
                    return;
                case Key.P:
                    _shell.ShowFractalParamsCommand.Execute().Subscribe();
                    e.Handled = true;
                    return;
                case Key.F1:
                    _shell.ShowHelpCommand.Execute().Subscribe();
                    e.Handled = true;
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
            // Right-click menu attached to the InputSponge — pops only on the
            // rendered image area, matching legacy MainForm where the
            // ContextMenuStrip lived on _renderPanel.
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
        shell.MiniModeToggleRequested += OnMiniModeToggleRequested;
        shell.ToyModeToggleRequested  += OnToyModeToggleRequested;

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
            _shell.MiniModeToggleRequested -= OnMiniModeToggleRequested;
            _shell.ToyModeToggleRequested  -= OnToyModeToggleRequested;

            // S-X8 (2026-06-27) — drop MiniDepth handlers off the long-lived
            // RenderHost event list so the captured window can be collected
            // and re-attach doesn't double-fire.
            if (_miniDepthColorMapHandler != null)
                _shell.Main.RenderHost.ColorMapChanged -= _miniDepthColorMapHandler;
            if (_miniDepthFrameCompletedHandler != null)
                _shell.Main.RenderHost.FrameCompleted -= _miniDepthFrameCompletedHandler;
            _miniDepthColorMapHandler = null;
            _miniDepthFrameCompletedHandler = null;
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
            case nameof(ShellViewModel.IsWatermarkEditorVisible):
            case nameof(ShellViewModel.WatermarkEditor):
                SyncWatermarkEditor();
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
            case nameof(ShellViewModel.IsClusterDashboardVisible):
            case nameof(ShellViewModel.ClusterDashboard):
                SyncClusterDashboard();
                break;
            case nameof(ShellViewModel.IsJobListVisible):
            case nameof(ShellViewModel.JobList):
                SyncJobList();
                break;
            case nameof(ShellViewModel.IsJobDetailVisible):
            case nameof(ShellViewModel.JobDetail):
                SyncJobDetail();
                break;
            case nameof(ShellViewModel.IsMiniMapVisible):
                SyncMiniMap();
                break;
            case nameof(ShellViewModel.IsMiniDepthVisible):
                SyncMiniDepth();
                break;
            case nameof(ShellViewModel.IsSlideshowVcrVisible):
                // Slideshow start path flips this true unconditionally; in
                // mini/toy mode we want it suppressed. Capture the intended
                // visibility so exit restores it, then force false.
                if ((_miniModeActive || _toyModeActive) && _shell != null
                    && _shell.IsSlideshowVcrVisible)
                {
                    if (_miniModeActive) _preMiniVcr = true;
                    if (_toyModeActive)  _preToyVcr  = true;
                    _shell.IsSlideshowVcrVisible = false;
                }
                break;
        }
    }

    private void SyncMiniDepth()
    {
        if (_shell == null) return;
        if (_shell.IsMiniDepthVisible)
        {
            if (_miniDepthWin == null)
            {
                _miniDepthWin = new MiniDepthWindow();
                ConfigureMiniDepth(_miniDepthWin);
                _miniDepthWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsMiniDepthVisible = false;
                };
            }
            if (!_miniDepthWin.IsVisible)
            {
                _miniDepthWin.Show(this);
                if (_miniDepthTether == null)
                {
                    _miniDepthTether = new MiniWindowTether(
                        this, _miniDepthWin, MiniWindowTether.AnchorCorner.BottomLeft);
                    _miniDepthWin.ResetAnchorRequested += (_, _) => _miniDepthTether?.ResetAnchor();
                }
                // Defer initial positioning so Show's own PositionChanged
                // (centered placement) settles before tether takes ownership;
                // otherwise it would be misread as a user drag.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _miniDepthTether?.Apply(),
                    global::Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        else
        {
            _miniDepthWin?.Hide();
        }
    }

    private void ConfigureMiniDepth(MiniDepthWindow win)
    {
        if (_shell == null) return;
        var shell = _shell;
        win.Inner.Configure(
            getZoom:           () => shell.Main.ViewState.Zoom,
            getZoomMax:        () => shell.Main.ViewState.Quality?.ZoomMax ?? 1e13,
            getMaxIterations:  () =>
            {
                var s = shell.Main.ViewState;
                return s.IterLocked
                    ? s.LockedIterations
                    : (s.Quality?.ComputeIterations(s.Zoom) ?? 256);
            },
            sampleColor:       smoothIter => shell.SamplePaletteColor?.Invoke(smoothIter) ?? 0xFF808080u,
            getSwatchArgb:     () => shell.GetCurrentSwatchArgb?.Invoke() ?? 0xFF808080u);

        // Initial gradient build using the active theme.
        win.Inner.RequestRedraw();

        // S-X8 (2026-06-27) — held as fields so DetachShell can unsub.
        // Theme/region/type change → rebuild gradient.
        _miniDepthColorMapHandler = (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => win.Inner.RequestRedraw());
        shell.Main.RenderHost.ColorMapChanged += _miniDepthColorMapHandler;
        // Refresh indicator each frame to track pan/zoom.
        _miniDepthFrameCompletedHandler = (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => win.Inner.RefreshIndicator());
        shell.Main.RenderHost.FrameCompleted += _miniDepthFrameCompletedHandler;
    }

    private void OnMiniModeToggleRequested(object? sender, bool enter)
    {
        if (enter == _miniModeActive) return;
        if (enter) EnterMiniMode();
        else        ExitMiniMode();
    }

    private void EnterMiniMode()
    {
        if (_miniModeActive || _shell == null) return;

        // Mutually exclusive with Toy Mode.
        if (_toyModeActive)
        {
            ExitToyMode();
            _shell.IsToyMode = false;
        }

        _preMiniState       = WindowState;
        _preMiniDecorations = WindowDecorations;
        _preMiniPosition    = Position;
        _preMiniWidth       = Width;
        _preMiniHeight      = Height;
        _preMiniMinWidth    = MinWidth;
        _preMiniMinHeight   = MinHeight;
        _preMiniTopmost     = Topmost;
        _preMiniToolbar     = _shell.IsToolbarVisible;
        _preMiniStatus      = _shell.IsStatusBarVisible;
        _preMiniVcr         = _shell.IsSlideshowVcrVisible;

        WindowState        = global::Avalonia.Controls.WindowState.Normal;
        WindowDecorations  = global::Avalonia.Controls.WindowDecorations.None;
        Topmost            = true;
        // XAML pins MinWidth=640 / MinHeight=400, which would clamp the
        // mini window back to that size. Drop the floor while in mini mode.
        MinWidth           = 0;
        MinHeight          = 0;
        Width              = 320;
        Height             = 240;
        _shell.IsToolbarVisible   = false;
        // Status bar stays visible (per #12 follow-up): it's the user's
        // drag handle for moving the borderless window. Drag is wired on
        // the status Border via OnStatusBarPointerPressed.
        _shell.IsStatusBarVisible = true;
        // VCR transport eats too much vertical space at mini dims — hide
        // while in mini mode; original visibility restores on exit.
        _shell.IsSlideshowVcrVisible = false;

        _miniModeActive = true;
    }

    private void ExitMiniMode()
    {
        if (!_miniModeActive || _shell == null) return;

        WindowState        = _preMiniState;
        WindowDecorations  = _preMiniDecorations;
        Topmost            = _preMiniTopmost;
        MinWidth           = _preMiniMinWidth;
        MinHeight          = _preMiniMinHeight;
        Width              = _preMiniWidth;
        Height             = _preMiniHeight;
        Position           = _preMiniPosition;
        _shell.IsToolbarVisible   = _preMiniToolbar;
        _shell.IsStatusBarVisible = _preMiniStatus;
        _shell.IsSlideshowVcrVisible = _preMiniVcr;

        _miniModeActive = false;
    }

    // ── Toy Mode ──────────────────────────────────────────────────────────
    // Tighter than Mini Mode: no toolbar, no status, smaller default size,
    // and left-click-drag on the render surface moves the window itself.
    // The drag is wired through AvaloniaShell.LeftDragWindowHook so
    // it intercepts the swap-chain HWND's WM_LBUTTONDOWN BEFORE the pan
    // controller sees it. Right-click still pops the context menu via the
    // existing ContextMenuRequested path.
    private void OnToyModeToggleRequested(object? sender, bool enter)
    {
        if (enter == _toyModeActive) return;
        if (enter) EnterToyMode();
        else        ExitToyMode();
    }

    private void EnterToyMode()
    {
        if (_toyModeActive || _shell == null) return;

        // Mutually exclusive with Mini Mode. If Mini is active, restore
        // first so the saved geometry stays accurate (otherwise we'd
        // overwrite "pre-mini" geometry with the mini 320x240).
        if (_miniModeActive)
        {
            ExitMiniMode();
            _shell.IsMiniMode = false;
        }

        _preToyState       = WindowState;
        _preToyDecorations = WindowDecorations;
        _preToyPosition    = Position;
        _preToyWidth       = Width;
        _preToyHeight      = Height;
        _preToyMinWidth    = MinWidth;
        _preToyMinHeight   = MinHeight;
        _preToyTopmost     = Topmost;
        _preToyToolbar     = _shell.IsToolbarVisible;
        _preToyStatus      = _shell.IsStatusBarVisible;
        _preToyVcr         = _shell.IsSlideshowVcrVisible;

        WindowState        = global::Avalonia.Controls.WindowState.Normal;
        WindowDecorations  = global::Avalonia.Controls.WindowDecorations.None;
        Topmost            = true;
        // XAML MinWidth=640 / MinHeight=400 would clamp the toy window back
        // up to that size. Drop the floor for the duration of toy mode.
        MinWidth           = 0;
        MinHeight          = 0;
        Width              = 200;
        Height             = 150;
        _shell.IsToolbarVisible   = false;
        _shell.IsStatusBarVisible = false;
        _shell.IsSlideshowVcrVisible = false;

        AvaloniaShell.LeftDragWindowHook = ToyDragWindow;
        AttachToySpongeDrag();
        _toyModeActive = true;
    }

    private void ExitToyMode()
    {
        if (!_toyModeActive || _shell == null) return;

        AvaloniaShell.LeftDragWindowHook = null;
        DetachToySpongeDrag();

        WindowState        = _preToyState;
        WindowDecorations  = _preToyDecorations;
        Topmost            = _preToyTopmost;
        MinWidth           = _preToyMinWidth;
        MinHeight          = _preToyMinHeight;
        Width              = _preToyWidth;
        Height             = _preToyHeight;
        Position           = _preToyPosition;
        _shell.IsToolbarVisible   = _preToyToolbar;
        _shell.IsStatusBarVisible = _preToyStatus;
        _shell.IsSlideshowVcrVisible = _preToyVcr;

        _toyModeActive = false;
    }

    // Phase X.3 / Slice 3.3 — cross-platform toy-mode drag via Avalonia
    // PointerPressed → BeginMoveDrag(e). Only fires when the pointer event
    // actually reaches the InputSponge — on Win with the DX swap-chain
    // HWND covering the surface the event is swallowed by the native
    // HWND, so the Win32 fallback path (NativeMouseForwarder →
    // ToyDragWindow Win32 trick) remains the active drag on that host.
    // On Linux/macOS (Silk OpenGL composited through Avalonia) and on
    // Windows under `--renderer skia` the event reaches here and
    // BeginMoveDrag drives the move natively.
    private void AttachToySpongeDrag()
    {
        _sponge ??= this.FindControl<Border>("InputSponge");
        if (_sponge == null) return;
        _sponge.PointerPressed += OnToySpongePointerPressed;
    }

    private void DetachToySpongeDrag()
    {
        if (_sponge == null) return;
        _sponge.PointerPressed -= OnToySpongePointerPressed;
    }

    private void OnToySpongePointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!_toyModeActive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try { BeginMoveDrag(e); }
        catch { /* compositor refused (Wayland seat-focus); fall through */ }
    }

    // Win32 window-move kick. Called from NativeMouseForwarder (Win-only by
    // construction) when a left-click lands on the swap-chain HWND while Toy
    // Mode is active. ReleaseCapture undoes whatever the OS auto-set on
    // WM_LBUTTONDOWN; SendMessage(WM_NCLBUTTONDOWN, HTCAPTION) then tells
    // Windows to treat the press as if it had landed on the title bar — the
    // OS does the rest of the drag.
    //
    // Phase X.3 / Slice 3.1: `OperatingSystem.IsWindows()` guard so the CA1416
    // analyzer can prove the Win32 calls are unreachable on non-Win hosts.
    // The Avalonia BeginMoveDrag path above (AttachToySpongeDrag) handles
    // every other RID; this stays as the Win+DX fallback because the
    // swap-chain HWND eats pointer events before Avalonia sees them.
    private bool ToyDragWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle == null) return false;
            ReleaseCapture();
            SendMessage(handle.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int  HTCAPTION        = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void SyncMiniMap()
    {
        if (_shell == null) return;
        if (_shell.IsMiniMapVisible)
        {
            if (_miniMapWin == null)
            {
                _miniMapWin = new MiniMapWindow { DataContext = _shell.MiniMap };
                _miniMapWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsMiniMapVisible = false;
                };
            }
            if (!_miniMapWin.IsVisible)
            {
                _miniMapWin.Show(this);
                if (_miniMapTether == null)
                {
                    _miniMapTether = new MiniWindowTether(
                        this, _miniMapWin, MiniWindowTether.AnchorCorner.BottomRight);
                    _miniMapWin.ResetAnchorRequested += (_, _) => _miniMapTether?.ResetAnchor();
                }
                // Defer initial positioning so Show's own PositionChanged
                // settles before tether takes ownership; otherwise it would
                // be misread as a user drag.
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _miniMapTether?.Apply(),
                    global::Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        else
        {
            _miniMapWin?.Hide();
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
                _editorWin.Closing += async (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell == null) return;
                    var vm = _shell.ColorThemeEditor;
                    // Unsaved-changes guard: if the editor is dirty, prompt
                    // the user. Save → keep open + focus Name field; Discard
                    // → fall through and hide; Cancel → just abort the close.
                    if (vm != null && vm.IsDirty)
                    {
                        var choice = await vm.PromptUnsavedAsync();
                        if (choice == FracturingFog.UI.Avalonia.ViewModels.UnsavedChangesChoice.Cancel)
                            return;
                        if (choice == FracturingFog.UI.Avalonia.ViewModels.UnsavedChangesChoice.Save)
                        {
                            vm.RequestFocusNameField();
                            return;
                        }
                        // Discard → fall through to hide.
                    }
                    _shell.IsColorThemeEditorVisible = false;
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

    private void SyncWatermarkEditor()
    {
        if (_shell == null) return;
        if (_shell.IsWatermarkEditorVisible && _shell.WatermarkEditor != null)
        {
            if (_watermarkEditorWin == null)
            {
                _watermarkEditorWin = new WatermarkEditorView { DataContext = _shell.WatermarkEditor };
                _watermarkEditorWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsWatermarkEditorVisible = false;
                };
            }
            else if (_watermarkEditorWin.DataContext != _shell.WatermarkEditor)
            {
                _watermarkEditorWin.DataContext = _shell.WatermarkEditor;
            }
            if (!_watermarkEditorWin.IsVisible) _watermarkEditorWin.Show(this);
        }
        else
        {
            _watermarkEditorWin?.Hide();
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

    private void SyncClusterDashboard()
    {
        if (_shell == null) return;
        if (_shell.IsClusterDashboardVisible && _shell.ClusterDashboard != null)
        {
            if (_clusterDashboardWin == null)
            {
                _clusterDashboardWin = new ClusterDashboardView { DataContext = _shell.ClusterDashboard };
                _clusterDashboardWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsClusterDashboardVisible = false;
                };
            }
            else if (_clusterDashboardWin.DataContext != _shell.ClusterDashboard)
            {
                _clusterDashboardWin.DataContext = _shell.ClusterDashboard;
            }
            if (!_clusterDashboardWin.IsVisible) _clusterDashboardWin.Show(this);
        }
        else
        {
            _clusterDashboardWin?.Hide();
        }
    }

    private void SyncJobList()
    {
        if (_shell == null) return;
        if (_shell.IsJobListVisible && _shell.JobList != null)
        {
            if (_jobListWin == null)
            {
                _jobListWin = new JobListView { DataContext = _shell.JobList };
                _jobListWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsJobListVisible = false;
                };
            }
            else if (_jobListWin.DataContext != _shell.JobList)
            {
                _jobListWin.DataContext = _shell.JobList;
            }
            if (!_jobListWin.IsVisible) _jobListWin.Show(this);
        }
        else
        {
            _jobListWin?.Hide();
        }
    }

    private void SyncJobDetail()
    {
        if (_shell == null) return;
        if (_shell.IsJobDetailVisible && _shell.JobDetail != null)
        {
            if (_jobDetailWin == null)
            {
                _jobDetailWin = new JobDetailView { DataContext = _shell.JobDetail };
                _jobDetailWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsJobDetailVisible = false;
                };
            }
            else if (_jobDetailWin.DataContext != _shell.JobDetail)
            {
                _jobDetailWin.DataContext = _shell.JobDetail;
            }
            if (!_jobDetailWin.IsVisible) _jobDetailWin.Show(this);
            // Bring to front when re-opened with a different jobId so the
            // user knows the swap landed rather than seeing the same chrome
            // unchanged behind another window.
            else _jobDetailWin.Activate();
        }
        else
        {
            _jobDetailWin?.Hide();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shuttingDown = true;
        AvaloniaShell.ContextMenuRequested = null;
        AvaloniaShell.RenderSurfaceFocusRequested = null;
        AvaloniaShell.LeftDragWindowHook = null;
        _inputAdapter?.Dispose();
        _inputAdapter = null;

        _miniMapTether?.Dispose();
        _miniDepthTether?.Dispose();
        _miniMapTether = null;
        _miniDepthTether = null;

        _menuWin?.Close();
        _editorWin?.Close();
        _helpWin?.Close();
        _ffClientWin?.Close();
        _serverAdminWin?.Close();
        _clusterDashboardWin?.Close();
        _jobListWin?.Close();
        _jobDetailWin?.Close();
        _miniMapWin?.Close();
        _miniDepthWin?.Close();

        DetachShell();
    }
}
