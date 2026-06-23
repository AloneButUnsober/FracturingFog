// Hosting/X11InputBridge.cs
//
// S-X7.2 (2026-06-23) — Linux X11 native input bridge. Cross-plat analogue of
// FracturingFog.Win.NativeMouseForwarder. Avalonia's NativeControlHost on X11
// creates a child X11 subwindow for GpuSurfaceControl; X11 stacking order
// puts that subwindow above the parent, so the X server delivers pointer
// events straight to it without Avalonia's input pump ever seeing them. The
// XAML InputSponge sibling Border above the GpuSurface in the visual tree
// gets nothing because the OS-level child window occludes it for input
// purposes (same root cause as the Win+DX swap-chain HWND case).
//
// This bridge XSelectInputs ButtonPress/Release + PointerMotion + ScrollWheel
// on the foreign child XID, runs a dedicated XNextEvent pump thread, and
// marshals each X event into the shell-neutral IFractalInputController calls
// the rest of the input layer consumes.
//
// Display handling: opens its own XOpenDisplay(NULL) so this adapter does
// not have to extract Avalonia's internal display pointer. X11 allows
// multiple per-process connections to the same server and SelectInput is
// per-connection — Avalonia did not select pointer events on this child
// window, so we are the only client receiving them.
//
// Threading: the X pump runs on a dedicated thread (X event loops are
// blocking). IFractalInputController is called from the pump thread; the
// downstream Avalonia VM hops back to UI via Dispatcher.UIThread.Post when
// it touches XAML state.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Input;

namespace FracturingFog.Hosting;

internal sealed class X11InputBridge : INativeInputBridge
{
    // X11 event masks (X.h)
    private const long KeyPressMask         = 1L << 0;
    private const long ButtonPressMask      = 1L << 2;
    private const long ButtonReleaseMask    = 1L << 3;
    private const long EnterWindowMask      = 1L << 4;
    private const long LeaveWindowMask      = 1L << 5;
    private const long PointerMotionMask    = 1L << 6;
    private const long Button1MotionMask    = 1L << 8;
    private const long ButtonMotionMask     = 1L << 13;
    private const long StructureNotifyMask  = 1L << 17;

    // X11 event types (X.h)
    private const int KeyPress       = 2;
    private const int KeyRelease     = 3;
    private const int ButtonPress    = 4;
    private const int ButtonRelease  = 5;
    private const int MotionNotify   = 6;
    private const int EnterNotify    = 7;
    private const int LeaveNotify    = 8;

    // X11 key state mask bits (X.h XKeyEvent.state)
    private const uint ShiftMask     = 1 << 0;
    private const uint ControlMask   = 1 << 2;
    private const uint Mod1Mask      = 1 << 3;   // typically Alt
    private const uint Button1Mask   = 1 << 8;
    private const uint Button2Mask   = 1 << 9;
    private const uint Button3Mask   = 1 << 10;

    private IntPtr _display;
    private nuint _window;
    private IFractalInputController? _input;
    private Thread? _pumpThread;
    private volatile bool _running;

    // Double-click detection — X11 has no native double-click event.
    private long _lastClickMs;
    private int _lastClickX, _lastClickY;
    private PointerButton _lastClickButton;
    private const long DoubleClickThresholdMs = 400;
    private const int DoubleClickRadiusPx = 4;

    // Drag tracking for right-click context-menu suppression on drag-release.
    private bool _rightDown;
    private int _rightDownX, _rightDownY;
    private long _rightDownTicks;

    public Action<bool>? ContextMenuRequested { private get; set; }
    public Action? FocusRequested { private get; set; }
    public Func<bool>? LeftDragWindowHook { private get; set; }
    public Func<int, int, bool>? InspectClickHook { private get; set; }

    public void Attach(IntPtr surfaceHandle, IFractalInputController input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (surfaceHandle == IntPtr.Zero) return;
        _input = input;

        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
        {
            Console.Error.WriteLine("[X11InputBridge] XOpenDisplay failed; mouse input disabled.");
            return;
        }
        _window = (nuint)surfaceHandle.ToInt64();

        XSelectInput(_display, _window,
            ButtonPressMask | ButtonReleaseMask | PointerMotionMask |
            ButtonMotionMask | EnterWindowMask | LeaveWindowMask |
            StructureNotifyMask);
        XFlush(_display);

        _running = true;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "X11InputBridge.Pump",
        };
        _pumpThread.Start();
    }

    public void Detach()
    {
        _running = false;
        try { if (_display != IntPtr.Zero) XFlush(_display); } catch { }
        try { _pumpThread?.Join(250); } catch { }
        try { if (_display != IntPtr.Zero) XCloseDisplay(_display); } catch { }
        _display = IntPtr.Zero;
        _window = 0;
        _input = null;
    }

    public bool TrySampleClient(IntPtr surfaceHandle, int clientX, int clientY,
                                out byte r, out byte g, out byte b)
    {
        // Eyedropper sampling deferred — wire when the Linux IColorSampleBridge
        // lands. The shell falls back gracefully when this returns false.
        r = g = b = 0;
        return false;
    }

    private void PumpLoop()
    {
        // XEvent is a union; the longest member tops out at ~192 bytes. Use a
        // sufficiently large IntPtr-aligned buffer + parse fields by offset.
        var ev = new XEvent();
        while (_running && _display != IntPtr.Zero)
        {
            // XPending lets us spin-wait so Detach can break us out without
            // having to fabricate a synthetic event. Cheap when idle.
            if (XPending(_display) == 0)
            {
                Thread.Sleep(8);
                continue;
            }
            try { XNextEvent(_display, ref ev); }
            catch { break; }

            try { Dispatch(ref ev); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[X11InputBridge] dispatch error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Dispatch(ref XEvent ev)
    {
        if (_input == null) return;

        switch (ev.type)
        {
            case ButtonPress:    HandleButtonPress(ref ev.xbutton); break;
            case ButtonRelease:  HandleButtonRelease(ref ev.xbutton); break;
            case MotionNotify:   HandleMotion(ref ev.xmotion); break;
            case EnterNotify:    FocusRequested?.Invoke(); break;
        }
    }

    private void HandleButtonPress(ref XButtonEvent e)
    {
        // X11 wheel arrives as buttons 4 (up) + 5 (down); some servers also
        // emit 6/7 for horizontal scroll. Translate to a WheelInput tick.
        if (e.button == 4 || e.button == 5)
        {
            int delta = e.button == 4 ? 120 : -120;
            _input!.OnWheel(new WheelInput(e.x, e.y, GetWindowWidth(), GetWindowHeight(),
                delta, MapModifiers(e.state)));
            return;
        }
        if (e.button == 6 || e.button == 7) return; // horizontal wheel ignored

        var btn = MapButton(e.button);
        var pi = new PointerInput(e.x, e.y, GetWindowWidth(), GetWindowHeight(),
            btn, MapModifiers(e.state));

        // Track right-button down for drag-vs-click on release.
        if (btn == PointerButton.Right)
        {
            _rightDown = true;
            _rightDownX = e.x;
            _rightDownY = e.y;
            _rightDownTicks = Environment.TickCount64;
        }

        // Pull focus so subsequent keyboard events arrive at the InputSponge.
        FocusRequested?.Invoke();

        // Double-click detection: same button, within threshold + radius.
        long now = Environment.TickCount64;
        bool isDouble = btn == _lastClickButton
                     && (now - _lastClickMs) <= DoubleClickThresholdMs
                     && Math.Abs(e.x - _lastClickX) <= DoubleClickRadiusPx
                     && Math.Abs(e.y - _lastClickY) <= DoubleClickRadiusPx;
        _lastClickMs = now;
        _lastClickX = e.x; _lastClickY = e.y;
        _lastClickButton = btn;

        // Left-click toy-drag hook: when the shell wants the click to start
        // a window move (toy mode), it returns true and we skip dispatch.
        if (btn == PointerButton.Left && (LeftDragWindowHook?.Invoke() ?? false))
            return;

        if (isDouble) _input!.OnPointerDoubleClick(pi);
        else _input!.OnPointerDown(pi);
    }

    private void HandleButtonRelease(ref XButtonEvent e)
    {
        if (e.button == 4 || e.button == 5 || e.button == 6 || e.button == 7) return;
        var btn = MapButton(e.button);
        var pi = new PointerInput(e.x, e.y, GetWindowWidth(), GetWindowHeight(),
            btn, MapModifiers(e.state));
        _input!.OnPointerUp(pi);

        // Right-click release: raise context-menu request unless it was a drag.
        if (btn == PointerButton.Right && _rightDown)
        {
            _rightDown = false;
            bool wasDrag = Math.Abs(e.x - _rightDownX) > DoubleClickRadiusPx
                        || Math.Abs(e.y - _rightDownY) > DoubleClickRadiusPx
                        || (Environment.TickCount64 - _rightDownTicks) > 400;
            try { ContextMenuRequested?.Invoke(wasDrag); } catch { }
        }
    }

    private void HandleMotion(ref XMotionEvent e)
    {
        var pi = new PointerInput(e.x, e.y, GetWindowWidth(), GetWindowHeight(),
            MapMotionButtons(e.state), MapModifiers(e.state));
        _input!.OnPointerMove(pi);
    }

    private static PointerButton MapButton(uint xbutton) => xbutton switch
    {
        1 => PointerButton.Left,
        2 => PointerButton.Middle,
        3 => PointerButton.Right,
        _ => PointerButton.None,
    };

    private static PointerButton MapMotionButtons(uint state)
    {
        var b = PointerButton.None;
        if ((state & Button1Mask) != 0) b |= PointerButton.Left;
        if ((state & Button2Mask) != 0) b |= PointerButton.Middle;
        if ((state & Button3Mask) != 0) b |= PointerButton.Right;
        return b;
    }

    private static InputModifiers MapModifiers(uint state)
    {
        var m = InputModifiers.None;
        if ((state & ShiftMask)   != 0) m |= InputModifiers.Shift;
        if ((state & ControlMask) != 0) m |= InputModifiers.Control;
        if ((state & Mod1Mask)    != 0) m |= InputModifiers.Alt;
        return m;
    }

    private int GetWindowWidth()
    {
        XGetGeometry(_display, _window, out _, out _, out _, out uint w, out _, out _, out _);
        return (int)w;
    }
    private int GetWindowHeight()
    {
        XGetGeometry(_display, _window, out _, out _, out _, out _, out uint h, out _, out _);
        return (int)h;
    }

    // ── X11 P/Invoke ──────────────────────────────────────────────────────
    // XEvent union approximation. We only need type + xbutton + xmotion
    // fields; the union is sized large enough that overlapping accesses are
    // safe (XEvent in C is 192 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XButtonEvent xbutton;
        [FieldOffset(0)] public XMotionEvent xmotion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XButtonEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public IntPtr display;
        public nuint window;
        public nuint root;
        public nuint subwindow;
        public nuint time;
        public int x, y;
        public int x_root, y_root;
        public uint state;
        public uint button;
        public int same_screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XMotionEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public IntPtr display;
        public nuint window;
        public nuint root;
        public nuint subwindow;
        public nuint time;
        public int x, y;
        public int x_root, y_root;
        public uint state;
        public byte is_hint;
        public int same_screen;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int    XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XSelectInput(IntPtr display, nuint w, long event_mask);
    [DllImport("libX11.so.6")] private static extern int    XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XNextEvent(IntPtr display, ref XEvent ev);
    [DllImport("libX11.so.6")] private static extern int    XGetGeometry(IntPtr display, nuint d,
        out nuint root, out int x, out int y, out uint width, out uint height,
        out uint borderWidth, out uint depth);
}
