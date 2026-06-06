// Abstractions/Input/InputEvents.cs
//
// Shell-neutral input events. Both the WinForms MainForm adapter (during
// the Phase 2.3 transition) and the Avalonia GpuSurfaceControl adapter
// translate their respective platform events into these records before
// handing them to FractalInputController.
//
// Keeping the event surface this tight means the input controller doesn't
// need to know which shell it's running under, and a future Mac/Linux
// adapter can plug in by emitting the same records.

using System;

namespace FracturingFog.Input
{
    /// <summary>Which mouse button (or combination) was pressed.</summary>
    [Flags]
    public enum PointerButton
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 4,
    }

    /// <summary>Which non-character modifier keys are held.</summary>
    [Flags]
    public enum InputModifiers
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
    }

    /// <summary>Canonical key codes the controller cares about.</summary>
    public enum InputKey
    {
        None = 0,
        // Letters used by 2D + 3D bindings
        W, A, S, D, Q, E,
        // Universal commands
        M, T, R, V,
        // Diagnostics (Ctrl+Shift+S, Ctrl+Shift+A)
        DiagToggleSeries,
        DiagToggleAcceleration,
        // 3D camera
        Up, Down, Left, Right,
        // 3D light
        PageUp, PageDown, Home, End,
        // Window
        Escape,
    }

    /// <summary>One pointer-down / pointer-up / pointer-move / pointer-double-click event.</summary>
    public readonly record struct PointerInput(
        int X,
        int Y,
        int ClientWidth,
        int ClientHeight,
        PointerButton Buttons,
        InputModifiers Modifiers);

    /// <summary>One mouse-wheel event. <paramref name="Delta"/> is positive
    /// for forward / zoom-in scrolls.</summary>
    public readonly record struct WheelInput(
        int X,
        int Y,
        int ClientWidth,
        int ClientHeight,
        int Delta,
        InputModifiers Modifiers);

    /// <summary>One key-down event.</summary>
    public readonly record struct KeyInput(
        InputKey Key,
        InputModifiers Modifiers,
        int ClientWidth,
        int ClientHeight);

    /// <summary>Pixel-space rubber-band rectangle the input layer raises while
    /// the user is right-drag-selecting a zoom region (non-3D only). X/Y is
    /// the top-left corner; Width/Height are positive. Null Rect means the
    /// drag ended or was cancelled — host should clear any preview overlay.</summary>
    public readonly record struct SelectionBoxChange(
        int X,
        int Y,
        int Width,
        int Height,
        int ClientWidth,
        int ClientHeight);
}
