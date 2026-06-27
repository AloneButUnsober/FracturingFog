// Hosting/X11ColorSampleBridge.cs
//
// S-X8 (2026-06-27) — Linux X11 IColorSampleBridge. Cross-plat analogue of
// the WindowsColorSampleBridge LL-mouse-hook bridge.
//
// Mechanism: XGrabPointer with a crosshair cursor + ButtonPress mask. Runs
// its own XNextEvent pump on a dedicated background thread (XNextEvent is
// blocking; spinning on the Avalonia dispatcher thread would hang the UI).
// On the next button press the bridge reads the pixel at root-relative
// (x_root, y_root) via XGetImage on the root window, then XUngrabPointer
// and fires the picked callback. Right-click or middle-click cancels.
//
// Limitations:
//   * X11 only — XWayland may reject the grab or pass only the root-window
//     pointer for synthesized events. Acceptable: the Avalonia shell on
//     Linux runs under X (or XWayland) and the bridge gracefully cancels
//     on grab failure.
//   * Opens its own XOpenDisplay connection so it doesn't have to share
//     Xlib state with X11InputBridge or Avalonia's internal display.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Hosting;

namespace FracturingFog.Hosting;

internal sealed class X11ColorSampleBridge : IColorSampleBridge
{
    // X11 event masks (X.h)
    private const long ButtonPressMask   = 1L << 2;
    private const long ButtonReleaseMask = 1L << 3;

    // X11 grab modes (X.h)
    private const int GrabModeAsync = 1;
    private const int GrabSuccess   = 0;

    // X11 event types (X.h)
    private const int ButtonPress = 4;

    // X11 cursor font glyph for crosshair (cursorfont.h XC_crosshair).
    private const uint XC_CROSSHAIR = 34;

    // XGetImage format (X.h).
    private const int ZPixmap = 2;
    private const int XImageDataOffset = 16;

    // CurrentTime sentinel for X server time fields (X.h).
    private const nuint CurrentTime = 0;

    private volatile bool _active;
    private readonly object _lock = new();

    public bool IsActive => _active;

    public void Begin(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        ArgumentNullException.ThrowIfNull(onCancelled);

        lock (_lock)
        {
            if (_active) { onCancelled(); return; }
            _active = true;
        }

        var pumpThread = new Thread(() => RunPump(onPicked, onCancelled))
        {
            IsBackground = true,
            Name = "X11ColorSampleBridge.Pump",
        };
        pumpThread.Start();
    }

    private void RunPump(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        IntPtr display = IntPtr.Zero;
        nuint cursor = 0;
        bool grabbed = false;
        bool fired = false;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) { onCancelled(); return; }

            nuint root = XDefaultRootWindow(display);
            cursor = XCreateFontCursor(display, XC_CROSSHAIR);

            int rc = XGrabPointer(display, root, 0,
                ButtonPressMask | ButtonReleaseMask,
                GrabModeAsync, GrabModeAsync,
                0, cursor, CurrentTime);
            if (rc != GrabSuccess) { onCancelled(); return; }
            grabbed = true;
            XFlush(display);

            var ev = new XEvent();
            // Cap the wait so a stuck grab can't lock the bridge forever.
            // 30 s is enough for a user to cancel via right-click; if XNextEvent
            // is never reached the finally still releases the grab.
            long deadline = Environment.TickCount64 + 30000;
            while (Environment.TickCount64 < deadline)
            {
                if (XPending(display) == 0) { Thread.Sleep(10); continue; }
                XNextEvent(display, ref ev);
                if (ev.type != ButtonPress) continue;

                uint button = ev.xbutton.button;
                int rx = ev.xbutton.x_root;
                int ry = ev.xbutton.y_root;

                if (button == 1)
                {
                    if (TrySampleRoot(display, root, rx, ry,
                                       out byte r, out byte g, out byte b))
                    {
                        fired = true;
                        try { onPicked((r, g, b)); } catch { }
                        return;
                    }
                    // Sample failed — treat as cancel.
                    break;
                }
                // Any non-primary button cancels.
                break;
            }
        }
        catch { /* swallow — cancel below */ }
        finally
        {
            if (grabbed && display != IntPtr.Zero)
            {
                try { XUngrabPointer(display, CurrentTime); } catch { }
            }
            if (cursor != 0 && display != IntPtr.Zero)
            {
                try { XFreeCursor(display, cursor); } catch { }
            }
            if (display != IntPtr.Zero)
            {
                try { XFlush(display); } catch { }
                try { XCloseDisplay(display); } catch { }
            }
            _active = false;
            if (!fired)
            {
                try { onCancelled(); } catch { }
            }
        }
    }

    private static bool TrySampleRoot(IntPtr display, nuint root, int x, int y,
                                       out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        IntPtr img = IntPtr.Zero;
        try
        {
            img = XGetImage(display, root, x, y, 1, 1,
                            unchecked((nuint)~0UL), ZPixmap);
            if (img == IntPtr.Zero) return false;
            IntPtr data = Marshal.ReadIntPtr(img, XImageDataOffset);
            if (data == IntPtr.Zero) return false;
            int pixel = Marshal.ReadInt32(data, 0);
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
                try { XDestroyImage(img); } catch { }
            }
        }
    }

    // ── XEvent union approximation (X.h) ──────────────────────────────────

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(0)] public XButtonEvent xbutton;
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

    // ── libX11 P/Invoke ──────────────────────────────────────────────────

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int    XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int    XNextEvent(IntPtr display, ref XEvent ev);
    [DllImport("libX11.so.6")] private static extern nuint  XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern nuint  XCreateFontCursor(IntPtr display, uint shape);
    [DllImport("libX11.so.6")] private static extern int    XFreeCursor(IntPtr display, nuint cursor);
    [DllImport("libX11.so.6")] private static extern int    XGrabPointer(IntPtr display, nuint grab_window,
        int owner_events, long event_mask, int pointer_mode, int keyboard_mode,
        nuint confine_to, nuint cursor, nuint time);
    [DllImport("libX11.so.6")] private static extern int    XUngrabPointer(IntPtr display, nuint time);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetImage(IntPtr display, nuint d,
        int x, int y, uint width, uint height, nuint plane_mask, int format);
    [DllImport("libX11.so.6")] private static extern int    XDestroyImage(IntPtr ximg);
}
