// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/WorkspaceLayout.cs
//
// Window-arrangement workspace (#433, slice 1/3 — #469). The serializable shape
// of a saved window layout: the render window's mode/size/position/monitor plus
// each persistent (modeless) tool window's placement. Capturing and restoring
// live windows is slice 3 (#471); the live registry that enumerates them is
// slice 2 (#470). This file is the data contract only.
//
// UI-free by design (lives in Abstractions): no Avalonia types. Window display
// state and the render-window mode are local enums, and monitors are recorded
// as an index + pixel bounds so a restore can match by index, then by bounds,
// then fall back to the primary screen (slice 3 owns that logic). Modal config
// dialogs are deliberately excluded — they block and are mutually exclusive, so
// a multi-dialog arrangement cannot be restored (decision recorded on #433).

using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Models
{
    /// <summary>Which persistent (modeless) window a captured placement refers
    /// to. Shared with the slice-2 live registry so a role round-trips between
    /// capture and the reopen-by-role restore path. Modal dialogs are not
    /// listed — they are out of workspace scope.</summary>
    public enum WindowRole
    {
        RenderWindow = 0,
        MiniMap = 1,
        MiniDepth = 2,
        PostFxHud = 3,
        AsciiFx = 4,
        ColorThemeEditor = 5,
        UserEquation = 6,
        Sandbox = 7,
        UserBulb = 8,
        LightingFx = 9,
        Relief3D = 10,

        // Detached Control Center section panels (#494) — one role per section so a
        // workspace can capture/restore/close each independently.
        DetachedViewPanel = 11,
        DetachedExplorePanel = 12,
        DetachedColorLightPanel = 13,
        DetachedCapturePanel = 14,
        DetachedAssetsPanel = 15,
        DetachedAdvancedPanel = 16,

        // Modeless singleton editors made workspace-aware (#497).
        SceneEditor = 17,
        AnimationEditor = 18,
        ColorGenEditor = 19,

        // Floating standalone status-bar panel (#499).
        StatusPanel = 20,
    }

    /// <summary>Render-window mode. Mirrors the Standard/Mini/Toy/Span shapes the
    /// shell toggles; stored as its own enum so Abstractions needs no UI types.</summary>
    public enum RenderWindowShape
    {
        Standard = 0,
        Mini = 1,
        Toy = 2,
        Span = 3,
    }

    /// <summary>UI-neutral mirror of the platform window state (Avalonia's
    /// <c>WindowState</c>), so the model carries no Avalonia dependency.</summary>
    public enum WindowDisplayState
    {
        Normal = 0,
        Minimized = 1,
        Maximized = 2,
        FullScreen = 3,
    }

    /// <summary>A monitor recorded at capture time: its index in the screen list
    /// plus its pixel bounds. Restore matches by <see cref="Index"/> first, then
    /// by bounds overlap, then falls back to the primary screen when the monitor
    /// is gone (slice 3). All-zero bounds means "monitor was unknown".</summary>
    public sealed class MonitorRef
    {
        public int Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public MonitorRef Clone() => new()
        {
            Index = Index, X = X, Y = Y, Width = Width, Height = Height,
        };
    }

    /// <summary>Captured state of the render window (MainWindow): its mode,
    /// geometry, display state, chosen resolution, always-on-top flag, and the
    /// monitor it sat on.</summary>
    public sealed class RenderWindowState
    {
        public RenderWindowShape Shape { get; set; } = RenderWindowShape.Standard;
        public WindowDisplayState DisplayState { get; set; } = WindowDisplayState.Normal;

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Selected resolution preset name (the Control Center combo),
        /// or null when none/custom. Nullable so an unset value stays out of the
        /// JSON.</summary>
        public string? ResolutionName { get; set; }

        /// <summary>"Keep render window on top" — render floats above non-FF
        /// windows while FF dialogs may still overlay it (default off). Captured
        /// so a workspace restores it too.</summary>
        public bool Topmost { get; set; }

        /// <summary>Toolbar visibility (part of the captured chrome arrangement).
        /// Defaults true so an older workspace without the field restores the
        /// toolbar shown rather than hidden.</summary>
        public bool ToolbarVisible { get; set; } = true;

        /// <summary>Status-bar visibility. Defaults true for the same
        /// back-compat reason as <see cref="ToolbarVisible"/>.</summary>
        public bool StatusBarVisible { get; set; } = true;

        /// <summary>Captured fractal type — the <c>FractalType</c> enum name,
        /// stored as a string for JSON stability. Null when not captured (older
        /// workspaces); restore then leaves the current type untouched (#476).</summary>
        public string? FractalType { get; set; }

        /// <summary>For a promoted user-equation entry, the registered fractal's
        /// name, so restore re-selects that exact entry rather than just its base
        /// type. Null for built-in types.</summary>
        public string? PromotedFractalName { get; set; }

        public MonitorRef? Monitor { get; set; }

        public RenderWindowState Clone() => new()
        {
            Shape = Shape,
            DisplayState = DisplayState,
            X = X, Y = Y, Width = Width, Height = Height,
            ResolutionName = ResolutionName,
            Topmost = Topmost,
            ToolbarVisible = ToolbarVisible,
            StatusBarVisible = StatusBarVisible,
            FractalType = FractalType,
            PromotedFractalName = PromotedFractalName,
            Monitor = Monitor?.Clone(),
        };
    }

    /// <summary>Captured placement of one persistent tool window.</summary>
    public sealed class SatelliteWindowState
    {
        public WindowRole Role { get; set; }

        /// <summary>Whether the window was open at capture time. A restore reopens
        /// it (via the slice-2 role→open map) when true and it is currently
        /// closed.</summary>
        public bool Visible { get; set; }

        public WindowDisplayState DisplayState { get; set; } = WindowDisplayState.Normal;

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public MonitorRef? Monitor { get; set; }

        public SatelliteWindowState Clone() => new()
        {
            Role = Role,
            Visible = Visible,
            DisplayState = DisplayState,
            X = X, Y = Y, Width = Width, Height = Height,
            Monitor = Monitor?.Clone(),
        };
    }

    /// <summary>One named window-arrangement preset: the render window plus every
    /// captured satellite. The unit the Control Center droplist selects and the
    /// Asset Manager imports/exports.</summary>
    public sealed class WorkspaceLayout
    {
        public string Name { get; set; } = string.Empty;

        public RenderWindowState RenderWindow { get; set; } = new();

        public List<SatelliteWindowState> Satellites { get; set; } = new();

        public WorkspaceLayout Clone() => new()
        {
            Name = Name,
            RenderWindow = RenderWindow?.Clone() ?? new RenderWindowState(),
            Satellites = Satellites?.Select(s => s.Clone()).ToList() ?? new List<SatelliteWindowState>(),
        };
    }
}
