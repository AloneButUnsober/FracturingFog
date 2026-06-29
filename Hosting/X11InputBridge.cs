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

using Avalonia.Threading;

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
        // S-X8 (2026-06-27) — XGetImage on the surface window at (clientX,
        // clientY) returns a 1×1 ZPixmap whose first 32-bit word is the
        // pixel value. On TrueColor visuals (every modern Linux desktop)
        // the layout is BGRX little-endian, matching the WindowsNativeInputBridge
        // GdiGetPixel decode. Sampling the foreign Drawable directly avoids
        // having to compose window manager + root-window state.
        r = g = b = 0;
        if (surfaceHandle == IntPtr.Zero || _display == IntPtr.Zero) return false;
        nuint win = (nuint)surfaceHandle.ToInt64();
        IntPtr img = IntPtr.Zero;
        try
        {
            img = XGetImage(_display, win, clientX, clientY, 1, 1,
                            unchecked((nuint)~0UL), ZPixmap);
            if (img == IntPtr.Zero) return false;

            // XImage data pointer lives at offset 16 (width:4, height:4,
            // xoffset:4, format:4, data:ptr). Stable across Xlib versions.
            IntPtr dataPtr = Marshal.ReadIntPtr(img, XImageDataOffset);
            if (dataPtr == IntPtr.Zero) return false;
            int pixel = Marshal.ReadInt32(dataPtr, 0);
            b = (byte)(pixel & 0xFF);
            g = (byte)((pixel >> 8) & 0xFF);
            r = (byte)((pixel >> 16) & 0xFF);
            return true;
        }
        catch { return false; }
        finally
        {
            if (img != IntPtr.Zero)
            {
                try { XDestroyImage(img); } catch { /* libX11 without symbol */ }
            }
        }
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

        // S-X7.2b (2026-06-23) — marshal every dispatch onto the Avalonia
        // UI thread. The X event pump runs on a dedicated background thread
        // (X11 event loops are blocking) but the downstream IFractalInputController
        // writes to FractalViewState whose PropertyChanged subscribers touch
        // Avalonia visuals, and ContextMenuRequested directly opens an
        // Avalonia ContextMenu. Both throw InvalidOperationException
        // ("The calling thread cannot access this object because a different
        // thread owns it") when called off the UI thread. Win's NativeMouseForwarder
        // gets this for free because Win32 messages dispatch on the UI thread
        // already; on Linux we have to hop manually.
        switch (ev.type)
        {
            case ButtonPress:
            {
                var snap = ev.xbutton; // copy by value out of the union
                Dispatcher.UIThread.Post(() => HandleButtonPress(snap),
                    DispatcherPriority.Input);
                break;
            }
            case ButtonRelease:
            {
                var snap = ev.xbutton;
                Dispatcher.UIThread.Post(() => HandleButtonRelease(snap),
                    DispatcherPriority.Input);
                break;
            }
            case MotionNotify:
            {
                var snap = ev.xmotion;
                Dispatcher.UIThread.Post(() => HandleMotion(snap),
                    DispatcherPriority.Input);
                break;
            }
            case EnterNotify:
                Dispatcher.UIThread.Post(() => FocusRequested?.Invoke(),
                    DispatcherPriority.Input);
                break;
        }
    }

    private void HandleButtonPress(XButtonEvent e)
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

        // S-X8 (2026-06-27) — Color Theme Editor Inspect hook. Mirrors the
        // Win NativeMouseForwarder branch: a left-button-down while Inspect
        // mode is on samples the pixel under the cursor and feeds it into
        // the editor instead of starting a pan. Returning true means the
        // click was consumed — bail before the pan dispatch.
        if (btn == PointerButton.Left)
        {
            var inspectHook = InspectClickHook;
            if (inspectHook != null)
            {
                bool consumed = false;
                try { consumed = inspectHook(e.x, e.y); }
                catch { /* UI errors must not crash the X11 pump callback */ }
                if (consumed) return;
            }
        }

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
        // On Linux the shell can't drive the move itself (no Avalonia
        // PointerPressedEventArgs reaches the sponge — this bridge already
        // consumed the X ButtonPress), so we issue the EWMH _NET_WM_MOVERESIZE
        // request directly to the compositor here.
        if (btn == PointerButton.Left && (LeftDragWindowHook?.Invoke() ?? false))
        {
            try { BeginX11MoveDrag(e.x_root, e.y_root); }
            catch { /* compositor refused; click already swallowed */ }
            return;
        }

        if (isDouble) _input!.OnPointerDoubleClick(pi);
        else _input!.OnPointerDown(pi);
    }

    private void HandleButtonRelease(XButtonEvent e)
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

    private void HandleMotion(XMotionEvent e)
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

    // ── Toy-mode window move ─────────────────────────────────────────────
    // EWMH _NET_WM_MOVERESIZE protocol: tell the WM/compositor to drag the
    // window from the current cursor position. Direction 8 = _NET_WM_MOVERESIZE_MOVE;
    // button 1 = left; source 1 = "normal application". Sent to the root
    // window with Substructure{Redirect,Notify}Mask per the EWMH spec.
    //
    // Must release the implicit pointer grab the X server installed on
    // ButtonPress, otherwise the WM cannot acquire the grab it needs to
    // track the drag.
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask  = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int _NET_WM_MOVERESIZE_MOVE = 8;
    private static readonly IntPtr CurrentTime = IntPtr.Zero;

    private void BeginX11MoveDrag(int rootX, int rootY)
    {
        if (_display == IntPtr.Zero || _window == 0) return;

        IntPtr atom = XInternAtom(_display, "_NET_WM_MOVERESIZE", false);
        if (atom == IntPtr.Zero) return;

        XUngrabPointer(_display, CurrentTime);

        var ev = new XEvent();
        ev.type = ClientMessage;
        ev.xclient.type = ClientMessage;
        ev.xclient.send_event = 1;
        ev.xclient.display = _display;
        ev.xclient.window = _window;
        ev.xclient.message_type = atom;
        ev.xclient.format = 32;
        ev.xclient.data0 = (IntPtr)rootX;
        ev.xclient.data1 = (IntPtr)rootY;
        ev.xclient.data2 = (IntPtr)_NET_WM_MOVERESIZE_MOVE;
        ev.xclient.data3 = (IntPtr)1;  // left button
        ev.xclient.data4 = (IntPtr)1;  // source: normal application

        nuint root = XDefaultRootWindow(_display);
        XSendEvent(_display, root, 0,
            SubstructureNotifyMask | SubstructureRedirectMask, ref ev);
        XFlush(_display);
    }

    // ── X11 P/Invoke ──────────────────────────────────────────────────────
    // XEvent union approximation. We only need type + xbutton + xmotion +
    // xclient fields; the union is sized large enough that overlapping
    // accesses are safe (XEvent in C is 192 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XButtonEvent xbutton;
        [FieldOffset(0)] public XMotionEvent xmotion;
        [FieldOffset(0)] public XClientMessageEvent xclient;
    }

    // EWMH ClientMessage layout — 5 long-sized data slots after the header.
    // Field types use IntPtr so the longs are LP64-sized on 64-bit Linux.
    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int type;
        public nuint serial;
        public int send_event;
        public IntPtr display;
        public nuint window;
        public IntPtr message_type;
        public int format;
        public IntPtr data0;
        public IntPtr data1;
        public IntPtr data2;
        public IntPtr data3;
        public IntPtr data4;
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

    // ZPixmap format for XGetImage (X.h).
    private const int ZPixmap = 2;
    // Byte offset of `data` field within XImage struct (width:int, height:int,
    // xoffset:int, format:int, data:pointer). 16 on both ILP32 + LP64.
    private const int XImageDataOffset = 16;

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetImage(IntPtr display, nuint d,
        int x, int y, uint width, uint height, nuint plane_mask, int format);
    [DllImport("libX11.so.6")] private static extern int    XDestroyImage(IntPtr ximg);
    [DllImport("libX11.so.6")] private static extern int    XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XSelectInput(IntPtr display, nuint w, long event_mask);
    [DllImport("libX11.so.6")] private static extern int    XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XNextEvent(IntPtr display, ref XEvent ev);
    [DllImport("libX11.so.6")] private static extern int    XGetGeometry(IntPtr display, nuint d,
        out nuint root, out int x, out int y, out uint width, out uint height,
        out uint borderWidth, out uint depth);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display,
        [MarshalAs(UnmanagedType.LPStr)] string atom_name, [MarshalAs(UnmanagedType.Bool)] bool only_if_exists);
    [DllImport("libX11.so.6")] private static extern nuint  XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XSendEvent(IntPtr display, nuint w,
        int propagate, long event_mask, ref XEvent event_send);
    [DllImport("libX11.so.6")] private static extern int    XUngrabPointer(IntPtr display, IntPtr time);
}
