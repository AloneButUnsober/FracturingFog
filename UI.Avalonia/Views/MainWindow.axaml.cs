// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FracturingFog.Input;
using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

public sealed partial class MainWindow : Window
{
    private ShellViewModel? _shell;
    private IDisposable? _inputAdapter;
    private Border? _sponge;
    private bool _sortMenusAttached;

    private FloatingMenuView? _menuWin;
    private PanelHostWindow? _controlCenterWin;
    private PanelHostWindow? _editorWin;
    private PanelHostWindow? _watermarkEditorWin;
    private PanelHostWindow? _animationEditorWin;
    private PanelHostWindow? _sceneEditorWin;
    private PanelHostWindow? _regionEditorWin;
    private PanelHostWindow? _assetManagerWin;
    private PanelHostWindow? _helpWin;
    private PanelHostWindow? _ffClientWin;
    private PanelHostWindow? _serverAdminWin;
    private PanelHostWindow? _clusterDashboardWin;
    private PanelHostWindow? _jobListWin;
    private PanelHostWindow? _jobDetailWin;
    private PanelHostWindow? _workerDetailWin;
    private PanelHostWindow? _masterConfigWin;
    private MiniMapWindow? _miniMapWin;
    private MiniDepthWindow? _miniDepthWin;
    private MiniWindowTether? _miniMapTether;
    private MiniWindowTether? _miniDepthTether;
    private PostFxHudWindow? _postFxHudWin;
    private MiniWindowTether? _postFxHudTether;
    private StatusPanelWindow? _statusPanelWin;

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

        // Workspace registry (#433 slice 2): the render window is role 0. Register
        // once here so capture/restore (slice 3) can find its geometry + monitor.
        FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
            FracturingFog.Models.WindowRole.RenderWindow, this);

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

        // Escape is handled on KeyUp, not KeyDown: Avalonia 12.0.4 swallows the
        // Escape KeyDown before it is raised as a routed event (verified live —
        // no window, focused-control, or class handler ever sees it), but the
        // Escape KeyUp routes normally. Same workaround used for dialogs in
        // EscapeCloseBehavior / FfmpegSetupDialog. All other command keys stay
        // on KeyDown.
        KeyUp += OnWindowKeyUp;
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

        // Escape closes the menu. The menu is hosted in its own popup
        // top-level, so its keyboard events never reach OnWindowKeyUp; and
        // Avalonia 12.0.4's built-in menu Escape-close runs on the swallowed
        // KeyDown, so it no longer fires either. Handle Escape on the menu's
        // own KeyUp (a focused MenuItem's KeyUp bubbles up to here).
        menu.AddHandler(InputElement.KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && menu.IsOpen)
            {
                menu.Close();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // S2 reorg — the render-window right-click is now a SHORT quick-access
        // menu: view toggles + capture essentials + the running-slideshow
        // controls, with everything else (Params, editors, Asset Manager,
        // ColorGen, Watermark, Mini*/Toy, Span) moved into the Control Center.
        AddItem(menu, "Control Center…",    () => shell.ShowControlCenterCommand.Execute().Subscribe());
        // "Menu (legacy)" removed — the FloatingMenu view is deprecated; the
        // Control Center is the main menu form going forward.
        menu.Items.Add(new Separator());

        var toolbarItem = new MenuItem { Header = "Toolbar" };
        toolbarItem.Click += (_, _) => shell.IsToolbarVisible = !shell.IsToolbarVisible;
        menu.Items.Add(toolbarItem);
        var statusItem = new MenuItem { Header = "Status" };
        statusItem.Click += (_, _) => shell.IsStatusBarVisible = !shell.IsStatusBarVisible;
        menu.Items.Add(statusItem);
        AddItem(menu, "Grid",               () => shell.Main.ShowGrid = !shell.Main.ShowGrid);
        var onTopItem = new MenuItem { Header = "On Top" };
        onTopItem.Click += (_, _) => shell.IsRenderTopmost = !shell.IsRenderTopmost;
        menu.Items.Add(onTopItem);
        AddItem(menu, "Reset View",         () => shell.Main.ResetViewCommand.Execute().Subscribe());

        // Workspaces submenu (#498) — children rebuilt each open (in the sync
        // closure) from the saved-workspace library; selecting one recalls it.
        var workspacesItem = new MenuItem { Header = "Workspaces" };
        menu.Items.Add(workspacesItem);
        menu.Items.Add(new Separator());

        AddItem(menu, "Save Image…",        () => shell.ScreenshotCommand.Execute().Subscribe());
        AddItem(menu, "Save Text Art…",     () => shell.AsciiArtCommand.Execute().Subscribe());
        AddItem(menu, "Save Current Region",() => shell.SaveRegionCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());

        AddItem(menu, "Slideshow",          () => shell.ToggleSlideshowCommand.Execute().Subscribe());
        // Slideshow-specific items. Header text + enable state updated each time
        // the menu opens (sync closure below) to reflect current engine state.
        var lockRegionItem = new MenuItem { Header = "Slideshow: Lock Region" };
        lockRegionItem.Click += (_, _) => shell.ToggleSlideshowLockRegionCommand.Execute().Subscribe();
        menu.Items.Add(lockRegionItem);
        var focusItem = new MenuItem { Header = "Slideshow: More Colors" };
        focusItem.Click += (_, _) => shell.ToggleSlideshowFocusCommand.Execute().Subscribe();
        menu.Items.Add(focusItem);
        AddItem(menu, "Video",              () => shell.ToggleVideoCommand.Execute().Subscribe());
        menu.Items.Add(new Separator());

        AddItem(menu, "Help…",              () => shell.ShowHelpCommand.Execute().Subscribe());
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

            // Rebuild the Workspaces submenu each open so new/renamed/deleted
            // presets always reflect. Selecting one recalls it via the same
            // WorkspaceService.Restore the Control Center's Recall button uses.
            workspacesItem.Items.Clear();
            var wsFile = FracturingFog.Models.WorkspaceLayoutLibrary.Load();
            if (wsFile.Layouts.Count == 0)
            {
                workspacesItem.Items.Add(new MenuItem { Header = "(none saved)", IsEnabled = false });
            }
            else
            {
                foreach (var w in wsFile.Layouts)
                {
                    string wsName = w.Name;
                    var child = new MenuItem { Header = wsName };
                    child.Click += (_, _) =>
                    {
                        var f = FracturingFog.Models.WorkspaceLayoutLibrary.Load();
                        var layout = FracturingFog.Models.WorkspaceLayoutLibrary.Get(f, wsName);
                        if (layout != null)
                            FracturingFog.UI.Avalonia.Services.WorkspaceService.Restore(layout, shell);
                    };
                    workspacesItem.Items.Add(child);
                }
            }
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

        // NOTE: Escape is NOT handled here — Avalonia 12.0.4 never raises its
        // KeyDown. Context-menu close + exit-span / stop-run live in
        // OnWindowKeyUp instead.

        // Backspace = Back: pop the most recent nav snapshot off the shell's
        // history stack. Like Escape, allowed even when a non-text combo has
        // focus so the user doesn't have to click the surface first.
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None
            && !(FocusManager?.GetFocusedElement() is TextBox))
        {
            if (_shell.GoBack()) { e.Handled = true; return; }
        }

        // Don't steal keys from an editable control (toolbar combos / dialog
        // fields).
        if (IsEditableFocused()) return;

        // Shift+H = reset the perf HUD's rolling buffers so a new region /
        // video capture starts clean. Handled before the unmodified switch
        // so it doesn't fall through to plain H (toggle).
        if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.Shift)
        {
            _shell.Main.ResetPerfStats();
            e.Handled = true;
            return;
        }

        // Ctrl+G = toggle T3.1 GPU compute on the SP Mandelbrot path. Drives
        // whichever compute backend this session attached via the host's
        // GpuKernelFactory — D3D11 on a default Windows run, or the Vulkan /
        // SPIR-V kernel under --renderer vulkan (#288: one shared toggle, not a
        // separate Vulkan control). Handled before the unmodified switch so it
        // doesn't fall through to plain G (Grid toggle).
        if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
        {
            _shell.Main.UseGpuCompute = !_shell.Main.UseGpuCompute;
            // Keep the Control Center checkbox in lock-step with the hotkey
            // (read-back reflects "didn't engage" when no device can attach).
            _shell.FloatingMenu.SetGpuComputeState(_shell.Main.UseGpuCompute);
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
        // Ctrl+Shift+G — toggle the GPU relief-raymarch path (default ON since
        // Slice 4 #158 reached full FX parity). Off forces the CPU sphere-trace
        // (parity oracle). Title gains [RELIEF GPU OFF] while relief raymarch is
        // active. Ctrl+Shift so it doesn't collide with Ctrl+G (Mandelbrot GPU
        // compute) or plain G (grid).
        if (e.Key == Key.G && e.KeyModifiers == ctrlShift)
        {
            _shell.ToggleReliefGpuRaymarch();
            e.Handled = true;
            return;
        }

        // Command keys (M/T/R/V) — unmodified only; Ctrl/Alt/Shift combos are
        // reserved (diagnostic toggles, precise-pan). Escape is handled in
        // OnWindowKeyUp (its KeyDown is swallowed by Avalonia 12.0.4).
        if (e.KeyModifiers == KeyModifiers.None)
        {
            InputKey cmd = e.Key switch
            {
                Key.M => InputKey.M,
                Key.T => InputKey.T,
                Key.R => InputKey.R,
                Key.V => InputKey.V,
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
            //   P  = Params dialog  F1 = Help window  X = Post-FX HUD overlay
            switch (e.Key)
            {
                case Key.G:
                    _shell.Main.ShowGrid = !_shell.Main.ShowGrid;
                    e.Handled = true;
                    return;
                case Key.X:
                    _shell.TogglePostFxHudCommand.Execute().Subscribe();
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

    // Escape-only handler. Avalonia 12.0.4 swallows the Escape KeyDown before
    // it becomes a routed event (verified live), so the two Escape behaviours
    // that used to live in OnWindowKeyDown run here on KeyUp instead.
    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_shell == null || e.Handled) return;
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;

        // Esc closes an open context menu first. Without this it would route to
        // HandleCommandKey (cancel-run) while the menu stays open.
        if (_contextMenu != null && _contextMenu.IsOpen)
        {
            _contextMenu.Close();
            e.Handled = true;
            return;
        }

        // Otherwise route to the shell (exit span / stop run). Allowed even
        // when an editable control has focus so the user doesn't have to click
        // the surface first.
        if (_shell.HandleCommandKey(InputKey.Escape)) e.Handled = true;
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

        // Right-click sort menus on the toolbar Type / Region / Theme combos.
        // The build callbacks read the live _shell so they stay correct if the
        // DataContext is swapped; attach once so ContextRequested handlers
        // don't stack on re-attach.
        if (!_sortMenusAttached)
        {
            ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarTypeCombo"),
                () => _shell?.Main.BuildFractalTypeSortMenu() ?? System.Array.Empty<ComboMenuItem>());
            // Region combo: "Edit region…" (from the Edit-Region enhancement)
            // sits above the restored filter-by-fractal-type entries so both
            // live in one flyout. Prepending here (rather than in the VM) keeps
            // the ShowRegionEditor command coupling in the view layer.
            ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarRegionCombo"),
                BuildRegionComboMenu);
            ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarThemeCombo"),
                () => _shell?.FloatingMenu.BuildThemeSortMenu() ?? System.Array.Empty<ComboMenuItem>());
            _sortMenusAttached = true;
        }

        shell.PropertyChanged += OnShellPropertyChanged;
        shell.Main.PropertyChanged += OnMainPropertyChanged;
        shell.MiniModeToggleRequested += OnMiniModeToggleRequested;
        shell.ToyModeToggleRequested  += OnToyModeToggleRequested;
        shell.TerminalModeToggleRequested += OnTerminalModeToggleRequested;
        shell.SideBySideModeToggleRequested += OnSideBySideModeToggleRequested;
        shell.AsciiFxPanelRequested += OnAsciiFxPanelRequested;
        shell.FxPanel.Changed += OnAsciiFxPanelChanged;

        // Initial sync in case the shell already has flags set.
        SyncMenu();
        SyncEditor();
        SyncHelp();
        SyncAsciiMode();
    }

    // Region combo right-click menu: "Edit region…" + separator, then the
    // FloatingMenu's filter-by-fractal-type entries (RegionSortMode). Rebuilt
    // on every open so the filter's checked state stays live. Returns just the
    // Edit entry if the shell isn't attached yet.
    private System.Collections.Generic.IReadOnlyList<ComboMenuItem> BuildRegionComboMenu()
    {
        var items = new System.Collections.Generic.List<ComboMenuItem>
        {
            ComboMenuItem.Item("Edit region…", false,
                () => _shell?.ShowRegionEditorCommand.Execute().Subscribe()),
        };
        if (_shell != null)
        {
            items.Add(ComboMenuItem.Separator);
            items.AddRange(_shell.FloatingMenu.BuildRegionSortMenu());
        }
        return items;
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
            _shell.TerminalModeToggleRequested -= OnTerminalModeToggleRequested;
            _shell.SideBySideModeToggleRequested -= OnSideBySideModeToggleRequested;
            _shell.AsciiFxPanelRequested -= OnAsciiFxPanelRequested;
            _shell.FxPanel.Changed -= OnAsciiFxPanelChanged;
            StopAsciiPump();

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

        // In an ASCII mode, replay the one-shot reveal (typewriter / dissolve)
        // over the newly-displayed view when the region or fractal type changes.
        if (e.PropertyName is nameof(MainViewModel.SelectedFractalType)
                           or nameof(MainViewModel.SelectedFractalEntry)
                           or nameof(MainViewModel.SelectedRegion))
            RetriggerAsciiReveal();
    }

    // Restart the reveal clock so typewriter / dissolve wipe in again over the
    // newly-selected view. No-op unless an ASCII mode is active.
    //
    // Deliberately does NOT pump here: the type / region change kicks off a new
    // render whose FrameBufferChanged will paint the fresh frame. Pumping now
    // would capture the STALE pre-change buffer and, via the single-slot
    // coalescing gate, drop that real new-frame pump — leaving the ASCII view one
    // selection behind whenever no animation timer is running to recover it.
    // When a reveal (or any animated FX) is on, UpdateAsciiFxTimer keeps the
    // ~30fps timer running, which both animates the wipe and repaints promptly.
    private void RetriggerAsciiReveal()
    {
        if (_asciiFrameHandler == null) return;
        _asciiTransitionClock.Restart();
        UpdateAsciiFxTimer();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shell == null) return;
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.AsciiRampFromColor):
                // Repaint the ASCII view immediately so the ramp-source toggle
                // shows without waiting for the next render.
                if (_asciiFrameHandler != null) PumpAsciiFrame();
                break;
            case nameof(ShellViewModel.AsciiFxHue):
            case nameof(ShellViewModel.AsciiFxBreathe):
            case nameof(ShellViewModel.AsciiFxAudioReactive):
            case nameof(ShellViewModel.SelectedAsciiFxPreset):
                // Possibly-animated FX: start/stop the repaint timer, then repaint.
                UpdateAsciiFxTimer();
                if (_asciiFrameHandler != null) PumpAsciiFrame();
                break;
            case nameof(ShellViewModel.AsciiFxCrt):
                // Static FX: just repaint.
                if (_asciiFrameHandler != null) PumpAsciiFrame();
                break;
            case nameof(ShellViewModel.IsFloatingMenuVisible):
                SyncMenu();
                break;
            case nameof(ShellViewModel.IsControlCenterVisible):
            case nameof(ShellViewModel.ControlCenter):
                SyncControlCenter();
                break;
            case nameof(ShellViewModel.IsRenderTopmost):
                Topmost = _shell.IsRenderTopmost;
                break;
            case nameof(ShellViewModel.IsStatusPanelVisible):
                SyncStatusPanel();
                break;
            case nameof(ShellViewModel.IsColorThemeEditorVisible):
            case nameof(ShellViewModel.ColorThemeEditor):
                SyncEditor();
                break;
            case nameof(ShellViewModel.IsWatermarkEditorVisible):
            case nameof(ShellViewModel.WatermarkEditor):
                SyncWatermarkEditor();
                break;
            case nameof(ShellViewModel.IsAnimationEditorVisible):
            case nameof(ShellViewModel.AnimationEditor):
                SyncAnimationEditor();
                break;
            case nameof(ShellViewModel.IsSceneEditorVisible):
            case nameof(ShellViewModel.SceneEditor):
                SyncSceneEditor();
                break;
            case nameof(ShellViewModel.IsRegionEditorVisible):
            case nameof(ShellViewModel.RegionEditor):
                SyncRegionEditor();
                break;
            case nameof(ShellViewModel.IsAssetManagerVisible):
            case nameof(ShellViewModel.AssetManager):
                SyncAssetManager();
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
            case nameof(ShellViewModel.IsWorkerDetailVisible):
            case nameof(ShellViewModel.WorkerDetail):
                SyncWorkerDetail();
                break;
            case nameof(ShellViewModel.IsMasterConfigVisible):
            case nameof(ShellViewModel.MasterConfig):
                SyncMasterConfig();
                break;
            case nameof(ShellViewModel.IsMiniMapVisible):
                SyncMiniMap();
                break;
            case nameof(ShellViewModel.IsMiniDepthVisible):
                SyncMiniDepth();
                break;
            case nameof(ShellViewModel.IsPostFxHudVisible):
                SyncPostFxHud();
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

    // Floating standalone status panel (#499). Mirrors the docked status bar's
    // content in a borderless, drag-to-move window, bound to the same shell so it
    // stays live. Toggled by ShellViewModel.IsStatusPanelVisible; registered for
    // workspace capture/restore.
    private void SyncStatusPanel()
    {
        if (_shell == null) return;
        if (_shell.IsStatusPanelVisible)
        {
            if (_statusPanelWin == null)
            {
                _statusPanelWin = new StatusPanelWindow { DataContext = _shell };
                _statusPanelWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsStatusPanelVisible = false;
                };
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.StatusPanel, _statusPanelWin);
            }
            if (!_statusPanelWin.IsVisible) _statusPanelWin.Show(this);
        }
        else
        {
            _statusPanelWin?.Hide();
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
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.MiniDepth, _miniDepthWin);
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

    // ── Terminal Mode ASCII pump (#228) ─────────────────────────────────
    // On FrameCompleted (a background render thread) we generate an AsciiFrame
    // from the render host — cheap, off the UI thread — then marshal the paint
    // to the UI thread. A single-slot coalescing gate drops intermediate frames
    // when the UI can't keep up during a fast pan/zoom.
    private AsciiView? _asciiView;
    private EventHandler? _asciiFrameHandler;
    private EventHandler? _asciiColumnsHandler;
    private int _asciiUpdateQueued; // 0 = idle, 1 = a UI update is already posted
    private readonly System.Diagnostics.Stopwatch _asciiFxClock = new();
    // Separate clock for the one-shot reveal transitions (typewriter / dissolve):
    // restarts on entering an ASCII mode and on region / fractal-type change, so a
    // reveal replays over each newly-displayed view without disturbing the
    // continuously-running FX on the main clock.
    private readonly System.Diagnostics.Stopwatch _asciiTransitionClock = new();
    private global::Avalonia.Threading.DispatcherTimer? _asciiFxTimer;

    // Terminal and Side-by-side are mutually exclusive; entering one clears the
    // other (its setter re-enters here, harmlessly, and SyncAsciiMode is
    // idempotent). Both funnel through SyncAsciiMode → layout + pump lifecycle.
    private void OnTerminalModeToggleRequested(object? sender, bool enter)
    {
        if (enter && _shell != null && _shell.IsSideBySideMode) _shell.IsSideBySideMode = false;
        SyncAsciiMode();
    }

    private void OnSideBySideModeToggleRequested(object? sender, bool enter)
    {
        if (enter && _shell != null && _shell.IsTerminalMode) _shell.IsTerminalMode = false;
        SyncAsciiMode();
    }

    private void SyncAsciiMode()
    {
        ApplyAsciiLayout();
        bool wantAscii = _shell != null && (_shell.IsTerminalMode || _shell.IsSideBySideMode);
        if (wantAscii) StartAsciiPump();
        else            StopAsciiPump();
    }

    // Position/size the GPU surface, ASCII view, and input sponge for the active
    // mode. Normal: GPU spans both columns, ASCII hidden. Terminal: ASCII spans
    // both, GPU hidden (native HWND gone so it can't occlude the ASCII). Split:
    // GPU in column 0, ASCII in column 1 — separate columns, so the native HWND
    // never overlaps the Avalonia ASCII view. The input sponge sits over the GPU
    // side so panning targets the render.
    private void ApplyAsciiLayout()
    {
        if (_shell == null) return;
        var gpu = this.FindControl<GpuSurfaceControl>("GpuSurface");
        _asciiView ??= this.FindControl<AsciiView>("AsciiSurface");
        _sponge ??= this.FindControl<Border>("InputSponge");
        if (gpu == null || _asciiView == null) return;

        if (_shell.IsSideBySideMode)
        {
            gpu.IsVisible = true;
            global::Avalonia.Controls.Grid.SetColumn(gpu, 0);
            global::Avalonia.Controls.Grid.SetColumnSpan(gpu, 1);
            _asciiView.IsVisible = true;
            global::Avalonia.Controls.Grid.SetColumn(_asciiView, 1);
            global::Avalonia.Controls.Grid.SetColumnSpan(_asciiView, 1);
            if (_sponge != null)
            {
                global::Avalonia.Controls.Grid.SetColumn(_sponge, 0);
                global::Avalonia.Controls.Grid.SetColumnSpan(_sponge, 1);
            }
        }
        else if (_shell.IsTerminalMode)
        {
            gpu.IsVisible = false;
            _asciiView.IsVisible = true;
            global::Avalonia.Controls.Grid.SetColumn(_asciiView, 0);
            global::Avalonia.Controls.Grid.SetColumnSpan(_asciiView, 2);
            if (_sponge != null)
            {
                global::Avalonia.Controls.Grid.SetColumn(_sponge, 0);
                global::Avalonia.Controls.Grid.SetColumnSpan(_sponge, 2);
            }
        }
        else // Normal
        {
            gpu.IsVisible = true;
            global::Avalonia.Controls.Grid.SetColumn(gpu, 0);
            global::Avalonia.Controls.Grid.SetColumnSpan(gpu, 2);
            _asciiView.IsVisible = false;
            if (_sponge != null)
            {
                global::Avalonia.Controls.Grid.SetColumn(_sponge, 0);
                global::Avalonia.Controls.Grid.SetColumnSpan(_sponge, 2);
            }
        }
    }

    private void StartAsciiPump()
    {
        if (_shell == null || _asciiFrameHandler != null) return;
        _asciiView ??= this.FindControl<AsciiView>("AsciiSurface");
        if (_asciiView == null) return;

        // FrameBufferChanged (not FrameCompleted): fires on EVERY upload,
        // including post-FX / adaptive repaints, so brightness / contrast /
        // adaptive-sweep changes update the ASCII live, not just re-renders.
        _asciiFrameHandler = (_, _) => PumpAsciiFrame();
        _shell.Main.RenderHost.FrameBufferChanged += _asciiFrameHandler;
        // Resize re-pump: a window / column-width change alters the fitted
        // column count but fires no buffer-change event, so re-render the ASCII
        // grid at the new resolution instead of letterboxing the old one.
        _asciiColumnsHandler = (_, _) => PumpAsciiFrame();
        _asciiView.LiveColumnsChanged += _asciiColumnsHandler;
        _asciiFxClock.Restart();
        _asciiTransitionClock.Restart(); // reveal plays on entering the mode
        UpdateAsciiFxTimer();
        // Paint the current frame immediately so entering the mode isn't blank
        // until the next render lands.
        PumpAsciiFrame();
    }

    private void StopAsciiPump()
    {
        // Leaving an ASCII mode while recording finalises the capture (fires the
        // toggle handler → save dialog).
        if (_shell != null && _shell.IsAsciiRecording) _shell.IsAsciiRecording = false;

        if (_shell != null && _asciiFrameHandler != null)
            _shell.Main.RenderHost.FrameBufferChanged -= _asciiFrameHandler;
        _asciiFrameHandler = null;
        if (_asciiView != null && _asciiColumnsHandler != null)
            _asciiView.LiveColumnsChanged -= _asciiColumnsHandler;
        _asciiColumnsHandler = null;
        _asciiFxTimer?.Stop();
        _asciiFxClock.Stop();
        _asciiTransitionClock.Stop();
        System.Threading.Interlocked.Exchange(ref _asciiUpdateQueued, 0);
        _asciiView?.Clear();
    }

    // Run a ~30fps repaint timer only while an ASCII mode is active AND an
    // animated FX is on — those vary with time, so the buffer-change pump alone
    // won't advance them on a static fractal. Static FX and the base render still
    // refresh via FrameBufferChanged. The built settings (preset + quick-toggles)
    // report whether anything animates.
    private void UpdateAsciiFxTimer()
    {
        bool want = _asciiFrameHandler != null && _shell != null
                    && ((_shell.BuildAsciiFxSettings(0.0)?.AnyAnimated ?? false)
                        // #261 — audio-reactive keeps the pump alive even before
                        // audio spins up, so it re-evaluates once samples arrive.
                        || _shell.AsciiFxAudioReactive);
        if (want)
        {
            if (_asciiFxTimer == null)
            {
                _asciiFxTimer = new global::Avalonia.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(33) };
                _asciiFxTimer.Tick += (_, _) => PumpAsciiFrame();
            }
            if (!_asciiFxTimer.IsEnabled) _asciiFxTimer.Start();
        }
        else
        {
            _asciiFxTimer?.Stop();
        }
    }

    // The floating ASCII FX panel window (a separate top-level so the native GPU
    // HWND can't occlude it). Reused across opens.
    private Views.AsciiFxPanelWindow? _asciiFxPanelWindow;

    private void OnAsciiFxPanelRequested(object? sender, EventArgs e)
    {
        if (_shell == null) return;
        if (_asciiFxPanelWindow == null)
        {
            _asciiFxPanelWindow = new Views.AsciiFxPanelWindow { DataContext = _shell.FxPanel };
            _asciiFxPanelWindow.Closed += (_, _) => _asciiFxPanelWindow = null;
            FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                FracturingFog.Models.WindowRole.AsciiFx, _asciiFxPanelWindow);
            _asciiFxPanelWindow.Show(this);
        }
        else
        {
            _asciiFxPanelWindow.Activate();
        }
    }

    // Any FX-panel edit: restart the animation timer (a newly-enabled animated
    // effect must start ticking) and repaint the live view immediately.
    private void OnAsciiFxPanelChanged(object? sender, EventArgs e)
    {
        UpdateAsciiFxTimer();
        if (_asciiFrameHandler != null) PumpAsciiFrame();
    }

    private void PumpAsciiFrame()
    {
        var view = _asciiView;
        var host = _shell?.Main.RenderHost;
        if (view == null || host == null) return;

        // Coalesce: if a paint is already queued, skip regenerating — the queued
        // one will pull the latest buffer when it runs.
        if (System.Threading.Interlocked.Exchange(ref _asciiUpdateQueued, 1) == 1) return;

        int cols = view.LiveColumns;            // volatile — safe off UI thread
        double aspect = view.CellAspect;        // constant after metrics
        bool rampFromColor = _shell?.AsciiRampFromColor ?? false;
        var fx = _shell?.BuildAsciiFxSettings(
            _asciiFxClock.Elapsed.TotalSeconds,
            _asciiTransitionClock.Elapsed.TotalSeconds);
        var frame = host.RenderLastFrameAscii(
            cols, aspect, color: true, invert: false, fineRamp: false,
            rampFromColor: rampFromColor, fx: fx);

        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Interlocked.Exchange(ref _asciiUpdateQueued, 0);
            if (_asciiFrameHandler == null) return; // mode left before this ran
            if (frame.HasValue) view.Update(frame.Value);
        });
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
        if (OperatingSystem.IsWindows())
        {
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

        // Linux: X11InputBridge consumed the ButtonPress before the Avalonia
        // sponge could see it, so AttachToySpongeDrag's BeginMoveDrag path
        // never fires. Signal "yes, drag the window" — the bridge itself
        // issues _NET_WM_MOVERESIZE to the compositor since it owns the X
        // display + window handles.
        if (OperatingSystem.IsLinux() && _toyModeActive)
            return true;

        return false;
    }

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int  HTCAPTION        = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void SyncPostFxHud()
    {
        if (_shell == null) return;
        if (_shell.IsPostFxHudVisible)
        {
            if (_postFxHudWin == null)
            {
                _postFxHudWin = new PostFxHudWindow { DataContext = _shell.FloatingMenu };
                _postFxHudWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsPostFxHudVisible = false;
                };
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.PostFxHud, _postFxHudWin);
            }
            if (!_postFxHudWin.IsVisible)
            {
                _postFxHudWin.Show(this);
                if (_postFxHudTether == null)
                {
                    _postFxHudTether = new MiniWindowTether(
                        this, _postFxHudWin, MiniWindowTether.AnchorCorner.TopLeft);
                    _postFxHudWin.ResetAnchorRequested += (_, _) => _postFxHudTether?.ResetAnchor();
                }
                // Defer initial placement so Show's own PositionChanged settles
                // before the tether takes ownership (else read as a user drag).
                global::Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _postFxHudTether?.Apply(),
                    global::Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        else
        {
            _postFxHudWin?.Hide();
        }
    }

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
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.MiniMap, _miniMapWin);
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

    // DEPRECATED: shows the retired FloatingMenu view. Dormant — no path sets
    // IsFloatingMenuVisible = true anymore (Menu button + "M" hotkey open the
    // Control Center). Retained so the field/close plumbing stays valid; do not
    // resurrect. The Control Center is the main menu form going forward.
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

    // Phase S1 Control Center shell — modeless, close => hide (same family as
    // the other Sync* windows). Wraps the ControlCenterView UserControl.
    private void SyncControlCenter()
    {
        if (_shell == null) return;
        if (_shell.IsControlCenterVisible && _shell.ControlCenter != null)
        {
            if (_controlCenterWin == null)
            {
                _controlCenterWin = new PanelHostWindow(
                    new ControlCenterView(),
                    new PanelHostOptions(
                        "Fracturing Fog — Control Center",
                        Width: 1000, Height: 760, MinWidth: 820, MinHeight: 560,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterScreen,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.ControlCenter,
                };
                _controlCenterWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsControlCenterVisible = false;
                };
            }
            else if (_controlCenterWin.DataContext != _shell.ControlCenter)
            {
                _controlCenterWin.DataContext = _shell.ControlCenter;
            }
            if (!_controlCenterWin.IsVisible) _controlCenterWin.Show(this);
            else _controlCenterWin.Activate();
        }
        else
        {
            _controlCenterWin?.Hide();
        }
    }

    private void SyncEditor()
    {
        if (_shell == null) return;
        if (_shell.IsColorThemeEditorVisible && _shell.ColorThemeEditor != null)
        {
            if (_editorWin == null)
            {
                _editorWin = new PanelHostWindow(
                    new ColorThemeEditorView(),
                    new PanelHostOptions(
                        "Color Theme Editor",
                        Width: 980, Height: 900, MinWidth: 780, MinHeight: 600,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.ColorThemeEditor,
                };
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.ColorThemeEditor, _editorWin);
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
                _watermarkEditorWin = new PanelHostWindow(
                    new WatermarkEditorView(),
                    new PanelHostOptions(
                        "Watermark Editor",
                        Width: 640, Height: 640, MinWidth: 520, MinHeight: 500,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.WatermarkEditor,
                };
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

    private void SyncAnimationEditor()
    {
        if (_shell == null) return;
        if (_shell.IsAnimationEditorVisible && _shell.AnimationEditor != null)
        {
            if (_animationEditorWin == null)
            {
                _animationEditorWin = new PanelHostWindow(
                    new AnimationEditorView(),
                    new PanelHostOptions(
                        "Animation Editor",
                        Width: 780, Height: 700, MinWidth: 640, MinHeight: 560,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.AnimationEditor,
                };
                _animationEditorWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsAnimationEditorVisible = false;
                };
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.AnimationEditor, _animationEditorWin);
            }
            else if (_animationEditorWin.DataContext != _shell.AnimationEditor)
            {
                _animationEditorWin.DataContext = _shell.AnimationEditor;
            }
            if (!_animationEditorWin.IsVisible) _animationEditorWin.Show(this);
        }
        else
        {
            _animationEditorWin?.Hide();
        }
    }

    private void SyncSceneEditor()
    {
        if (_shell == null) return;
        if (_shell.IsSceneEditorVisible && _shell.SceneEditor != null)
        {
            if (_sceneEditorWin == null)
            {
                _sceneEditorWin = new PanelHostWindow(
                    new SceneEditorView(),
                    new PanelHostOptions(
                        "Scene Editor",
                        Width: 900, Height: 760, MinWidth: 720, MinHeight: 580,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.SceneEditor,
                };
                _sceneEditorWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsSceneEditorVisible = false;
                };
                FracturingFog.UI.Avalonia.Services.WindowService.RegisterWindow(
                    FracturingFog.Models.WindowRole.SceneEditor, _sceneEditorWin);
            }
            else if (_sceneEditorWin.DataContext != _shell.SceneEditor)
            {
                _sceneEditorWin.DataContext = _shell.SceneEditor;
            }
            if (!_sceneEditorWin.IsVisible) _sceneEditorWin.Show(this);
        }
        else
        {
            _sceneEditorWin?.Hide();
        }
    }

    private void SyncRegionEditor()
    {
        if (_shell == null) return;
        if (_shell.IsRegionEditorVisible && _shell.RegionEditor != null)
        {
            if (_regionEditorWin == null)
            {
                _regionEditorWin = new PanelHostWindow(
                    new RegionEditorView(),
                    new PanelHostOptions(
                        "Region Editor",
                        Width: 560, Height: 520, MinWidth: 460, MinHeight: 420,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.RegionEditor,
                };
                _regionEditorWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsRegionEditorVisible = false;
                };
            }
            else if (_regionEditorWin.DataContext != _shell.RegionEditor)
            {
                // Rebuilt per Show (targets the currently-selected region) —
                // swap the DataContext so the open window retargets.
                _regionEditorWin.DataContext = _shell.RegionEditor;
            }
            if (!_regionEditorWin.IsVisible) _regionEditorWin.Show(this);
        }
        else
        {
            _regionEditorWin?.Hide();
        }
    }

    private void SyncAssetManager()
    {
        if (_shell == null) return;
        if (_shell.IsAssetManagerVisible && _shell.AssetManager != null)
        {
            if (_assetManagerWin == null)
            {
                _assetManagerWin = new PanelHostWindow(
                    new AssetManagerView(),
                    new PanelHostOptions(
                        "Asset Manager",
                        Width: 820, Height: 540, MinWidth: 640, MinHeight: 380,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterOwner,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.AssetManager,
                };
                _assetManagerWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsAssetManagerVisible = false;
                };
            }
            else if (_assetManagerWin.DataContext != _shell.AssetManager)
            {
                _assetManagerWin.DataContext = _shell.AssetManager;
            }
            if (!_assetManagerWin.IsVisible) _assetManagerWin.Show(this);
        }
        else
        {
            _assetManagerWin?.Hide();
        }
    }

    private void SyncHelp()
    {
        if (_shell == null) return;
        if (_shell.IsHelpVisible && _shell.Help != null)
        {
            if (_helpWin == null)
            {
                _helpWin = new PanelHostWindow(
                    new FloatingHelpView(),
                    new PanelHostOptions(
                        "Help",
                        Width: 720, Height: 780, MinWidth: 520, MinHeight: 420,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        StartupLocation: WindowStartupLocation.CenterScreen,
                        Background: new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16))))
                {
                    DataContext = _shell.Help,
                };
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
                _ffClientWin = new PanelHostWindow(
                    new FFClientView(),
                    new PanelHostOptions(
                        "FracturingFog — Remote Client",
                        Width: 720, Height: 780, MinWidth: 600, MinHeight: 600,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = _shell.FFClient,
                };
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
                var vm = _shell.ServerAdmin;
                _serverAdminWin = new PanelHostWindow(
                    new ServerAdminView(),
                    new PanelHostOptions(
                        "FracturingFog — Server Admin",
                        Width: 560, Height: 680, MinWidth: 480, MinHeight: 480,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = vm,
                };
                _serverAdminWin.Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
                _serverAdminWin.Closed += (_, _) => vm.StopPolling();
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
                var vm = _shell.ClusterDashboard;
                _clusterDashboardWin = new PanelHostWindow(
                    new ClusterDashboardView(),
                    new PanelHostOptions(
                        "FracturingFog — Cluster Dashboard",
                        Width: 980, Height: 640, MinWidth: 720, MinHeight: 420,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = vm,
                };
                _clusterDashboardWin.Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
                _clusterDashboardWin.Closed += (_, _) => vm.StopPolling();
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
                var vm = _shell.JobList;
                _jobListWin = new PanelHostWindow(
                    new JobListView(),
                    new PanelHostOptions(
                        "FracturingFog — Cluster Jobs",
                        Width: 1040, Height: 640, MinWidth: 760, MinHeight: 420,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = vm,
                };
                _jobListWin.Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
                _jobListWin.Closed += (_, _) => vm.StopPolling();
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
                var vm = _shell.JobDetail;
                _jobDetailWin = new PanelHostWindow(
                    new JobDetailView(),
                    new PanelHostOptions(
                        "FracturingFog — Job Detail",
                        Width: 780, Height: 780, MinWidth: 520, MinHeight: 540,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = vm,
                };
                _jobDetailWin.Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
                _jobDetailWin.Closed += (_, _) => vm.StopPolling();
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

    private void SyncWorkerDetail()
    {
        if (_shell == null) return;
        if (_shell.IsWorkerDetailVisible && _shell.WorkerDetail != null)
        {
            if (_workerDetailWin == null)
            {
                var vm = _shell.WorkerDetail;
                _workerDetailWin = new PanelHostWindow(
                    new WorkerDetailView(),
                    new PanelHostOptions(
                        "FracturingFog — Worker Detail",
                        Width: 640, Height: 640, MinWidth: 480, MinHeight: 540,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = vm,
                };
                _workerDetailWin.Opened += (_, _) => { _ = vm.PollOnceAsync(); vm.StartPolling(); };
                _workerDetailWin.Closed += (_, _) => vm.StopPolling();
                _workerDetailWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsWorkerDetailVisible = false;
                };
            }
            else if (_workerDetailWin.DataContext != _shell.WorkerDetail)
            {
                _workerDetailWin.DataContext = _shell.WorkerDetail;
            }
            if (!_workerDetailWin.IsVisible) _workerDetailWin.Show(this);
            else _workerDetailWin.Activate();
        }
        else
        {
            _workerDetailWin?.Hide();
        }
    }

    private void SyncMasterConfig()
    {
        if (_shell == null) return;
        if (_shell.IsMasterConfigVisible && _shell.MasterConfig != null)
        {
            if (_masterConfigWin == null)
            {
                // Hybrid-shell: view is a UserControl wrapped in a generic
                // modeless host that carries the former Window chrome. Close =>
                // hide is owned here (the VM's CloseRequested already flips
                // IsMasterConfigVisible via ShellViewModel, routing to Hide).
                _masterConfigWin = new PanelHostWindow(
                    new MasterConfigView(),
                    new PanelHostOptions(
                        "FracturingFog — Master Config",
                        Width: 540, Height: 600, MinWidth: 460, MinHeight: 420,
                        SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                        Background: new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17))))
                {
                    DataContext = _shell.MasterConfig,
                };
                _masterConfigWin.Closing += (_, ev) =>
                {
                    if (_shuttingDown) return;
                    ev.Cancel = true;
                    if (_shell != null) _shell.IsMasterConfigVisible = false;
                };
            }
            else if (_masterConfigWin.DataContext != _shell.MasterConfig)
            {
                _masterConfigWin.DataContext = _shell.MasterConfig;
            }
            if (!_masterConfigWin.IsVisible) _masterConfigWin.Show(this);
            else _masterConfigWin.Activate();
        }
        else
        {
            _masterConfigWin?.Hide();
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
        _controlCenterWin?.Close();
        _editorWin?.Close();
        // Editors whose Closing handler cancels + hides (guarded by
        // _shuttingDown) — must be force-closed here too, else they linger
        // in Avalonia's window collection and OnLastWindowClose never fires,
        // leaving the process alive after the main window is gone (notably
        // after playing a Scene, which requires the Scene Editor open).
        _watermarkEditorWin?.Close();
        _animationEditorWin?.Close();
        _sceneEditorWin?.Close();
        _regionEditorWin?.Close();
        _helpWin?.Close();
        _ffClientWin?.Close();
        _serverAdminWin?.Close();
        _clusterDashboardWin?.Close();
        _jobListWin?.Close();
        _jobDetailWin?.Close();
        _workerDetailWin?.Close();
        _masterConfigWin?.Close();
        _miniMapWin?.Close();
        _miniDepthWin?.Close();
        _postFxHudWin?.Close();
        _statusPanelWin?.Close();
        _assetManagerWin?.Close();

        DetachShell();
    }
}
