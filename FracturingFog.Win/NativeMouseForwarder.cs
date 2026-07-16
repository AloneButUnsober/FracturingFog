// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/NativeMouseForwarder.cs
//
// Win32 mouse-message bridge for the Avalonia shell.
//
// The GPU swap chain lives on a native child HWND hosted by Avalonia's
// NativeControlHost (GpuSurfaceControl). On Windows the OS composites that
// HWND on top of all Avalonia content and routes every WM_MOUSE* /
// WM_*BUTTON* / WM_MOUSEWHEEL straight to it — so the transparent
// "InputSponge" Border layered above the surface in XAML never receives a
// pointer event (Avalonia hit-testing cannot reach behind the native
// window). Keyboard still works because logical focus stays on the Avalonia
// sponge, but mouse pan / zoom / 3D-orbit were dead.
//
// This subclass intercepts the native HWND's mouse messages and translates
// them into the shell-neutral PointerInput / WheelInput records the
// IFractalInputController already understands — the same records the
// AvaloniaInputAdapter emits from the sponge. Lives in the main WinExe
// because it needs Win32 P/Invoke (UI.Avalonia stays platform-neutral).

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using FracturingFog.Input;

namespace FracturingFog.Hosting
{
    [SupportedOSPlatform("windows")]
    public static class NativeMouseForwarder
    {
        // Keep the delegate rooted so the GC never collects the thunk the
        // subclass table points at.
        private static SUBCLASSPROC? s_proc;
        private static IFractalInputController? s_controller;
        private static IntPtr s_hwnd;

        // ── Context-menu callback ────────────────────────────────────────────
        // Fired from WM_RBUTTONUP after the controller has been notified.
        // The bool argument is true when the click looked like a drag (>1 s
        // held or pointer moved beyond a small dead-zone), which is the cue
        // the UI uses to suppress the menu in 3D fractal modes where the
        // right button is overloaded for camera rotation.
        public static Action<bool>? ContextMenuRequested;

        // ── Focus-pull callback ─────────────────────────────────────────────
        // Fired on any mouse-button down over the native HWND. The Avalonia
        // shell uses this to grab keyboard focus back onto its InputSponge
        // so a toolbar ComboBox that still holds logical focus stops
        // swallowing single-key shortcuts (R/M/T/V) intended for the render
        // window. Without it, typing "R" after clicking the Theme combo and
        // then clicking the render area selects a theme starting with "R"
        // instead of resetting the view.
        public static Action? FocusRequested;

        // ── Inspect-click callback ──────────────────────────────────────────
        // Fired on WM_LBUTTONDOWN BEFORE the controller sees the event.
        // Receives the click in HWND client coordinates. Returning true
        // signals "consumed" — the forwarder swallows the message so the
        // Color Theme Editor's Inspect mode does not also trigger a pan.
        // The shell installs this when the editor is open; uninstalls
        // when the editor closes.
        public static Func<int, int, bool>? InspectClickHook;

        // ── Window-drag hook (Toy Mode) ─────────────────────────────────────
        // When non-null, a WM_LBUTTONDOWN over the swap-chain HWND is handed
        // off to this callback instead of starting a fractal pan. Toy Mode
        // sets this to a delegate that initiates an OS-driven window move on
        // the top-level frame (SendMessage WM_NCLBUTTONDOWN HTCAPTION), so
        // the user can drag the borderless toy window from anywhere on the
        // render surface. Returning true signals "consumed" — the forwarder
        // swallows the click so no pan starts underneath.
        public static Func<bool>? LeftDragWindowHook;

        private static DateTime s_rightDownUtc;
        private static int s_rightDownX, s_rightDownY;
        private const int RightHoldSuppressMs = 1000;
        private const int RightMoveSuppressPx = 5;

        // ── Window messages ──────────────────────────────────────────────────
        private const uint WM_MOUSEMOVE     = 0x0200;
        private const uint WM_LBUTTONDOWN   = 0x0201;
        private const uint WM_LBUTTONUP     = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONDOWN   = 0x0204;
        private const uint WM_RBUTTONUP     = 0x0205;
        private const uint WM_MBUTTONDOWN   = 0x0207;
        private const uint WM_MBUTTONUP     = 0x0208;
        private const uint WM_MOUSEWHEEL    = 0x020A;

        // ── wParam button/modifier flags ─────────────────────────────────────
        private const int MK_LBUTTON = 0x0001;
        private const int MK_RBUTTON = 0x0002;
        private const int MK_SHIFT   = 0x0004;
        private const int MK_CONTROL = 0x0008;
        private const int MK_MBUTTON = 0x0010;

        // ── Class style ──────────────────────────────────────────────────────
        private const int GCL_STYLE   = -26;
        private const int CS_DBLCLKS  = 0x0008;

        private static readonly UIntPtr SubclassId = (UIntPtr)1;

        /// <summary>Subclass <paramref name="hwnd"/> (the swap-chain native
        /// window) so its mouse messages drive <paramref name="controller"/>.
        /// Must be called on the thread that owns the HWND (the UI thread).</summary>
        public static void Attach(IntPtr hwnd, IFractalInputController controller)
        {
            if (hwnd == IntPtr.Zero || controller == null) return;
            if (!OperatingSystem.IsWindows()) return;

            s_controller = controller;
            s_hwnd = hwnd;
            s_proc = WndProc;

            // Ensure the class delivers WM_*BUTTONDBLCLK so double-click
            // recenter works (Avalonia's native child class may lack CS_DBLCLKS).
            EnableDoubleClicks(hwnd);

            SetWindowSubclass(hwnd, s_proc, SubclassId, UIntPtr.Zero);
        }

        public static void Detach()
        {
            if (s_hwnd != IntPtr.Zero && s_proc != null)
            {
                try { RemoveWindowSubclass(s_hwnd, s_proc, SubclassId); } catch { /* ignore */ }
            }
            s_hwnd = IntPtr.Zero;
            s_proc = null;
            s_controller = null;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                      UIntPtr id, UIntPtr data)
        {
            var c = s_controller;
            if (c != null)
            {
                switch (msg)
                {
                    case WM_LBUTTONDOWN:
                        // Toy-Mode window-drag hook gets the very first shot.
                        // When installed, every left-click on the surface
                        // becomes a window move; no pan starts. Returning
                        // false lets the click fall through to the normal
                        // inspect/pan handlers.
                        {
                            var dragHook = LeftDragWindowHook;
                            if (dragHook != null)
                            {
                                bool consumed = false;
                                try { consumed = dragHook(); } catch { /* UI errors must not crash native callback */ }
                                if (consumed) return IntPtr.Zero;
                            }
                        }
                        // Inspect-click hook gets next shot. If it returns
                        // true the click was a probe, not a pan — pull
                        // keyboard focus but don't notify the controller.
                        {
                            var hook = InspectClickHook;
                            if (hook != null)
                            {
                                int ix = LoWordSigned(lParam);
                                int iy = HiWordSigned(lParam);
                                bool consumed = false;
                                try { consumed = hook(ix, iy); } catch { /* UI errors must not crash native callback */ }
                                if (consumed)
                                {
                                    try { FocusRequested?.Invoke(); } catch { /* UI errors must not crash native callback */ }
                                    return IntPtr.Zero;
                                }
                            }
                        }
                        SetCapture(hWnd);
                        try { FocusRequested?.Invoke(); } catch { /* UI errors must not crash native callback */ }
                        c.OnPointerDown(Pointer(hWnd, lParam, PointerButton.Left, wParam));
                        return IntPtr.Zero;
                    case WM_RBUTTONDOWN:
                        SetCapture(hWnd);
                        try { FocusRequested?.Invoke(); } catch { /* UI errors must not crash native callback */ }
                        s_rightDownUtc = DateTime.UtcNow;
                        s_rightDownX = LoWordSigned(lParam);
                        s_rightDownY = HiWordSigned(lParam);
                        c.OnPointerDown(Pointer(hWnd, lParam, PointerButton.Right, wParam));
                        return IntPtr.Zero;
                    case WM_MBUTTONDOWN:
                        try { FocusRequested?.Invoke(); } catch { /* UI errors must not crash native callback */ }
                        c.OnPointerDown(Pointer(hWnd, lParam, PointerButton.Middle, wParam));
                        return IntPtr.Zero;
                    case WM_MOUSEMOVE:
                        c.OnPointerMove(Pointer(hWnd, lParam, ButtonsFromWParam(wParam), wParam));
                        return IntPtr.Zero;
                    case WM_LBUTTONUP:
                        ReleaseCapture();
                        c.OnPointerUp(Pointer(hWnd, lParam, PointerButton.Left, wParam));
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                        ReleaseCapture();
                        c.OnPointerUp(Pointer(hWnd, lParam, PointerButton.Right, wParam));
                        {
                            int upX = LoWordSigned(lParam);
                            int upY = HiWordSigned(lParam);
                            double heldMs = (DateTime.UtcNow - s_rightDownUtc).TotalMilliseconds;
                            int dx = upX - s_rightDownX;
                            int dy = upY - s_rightDownY;
                            bool moved = (dx * dx + dy * dy) > (RightMoveSuppressPx * RightMoveSuppressPx);
                            bool wasDrag = heldMs > RightHoldSuppressMs || moved;
                            try { ContextMenuRequested?.Invoke(wasDrag); } catch { /* UI errors must not crash native callback */ }
                        }
                        return IntPtr.Zero;
                    case WM_MBUTTONUP:
                        c.OnPointerUp(Pointer(hWnd, lParam, PointerButton.Middle, wParam));
                        return IntPtr.Zero;
                    case WM_LBUTTONDBLCLK:
                        c.OnPointerDoubleClick(Pointer(hWnd, lParam, PointerButton.Left, wParam));
                        return IntPtr.Zero;
                    case WM_MOUSEWHEEL:
                        c.OnWheel(Wheel(hWnd, wParam, lParam));
                        return IntPtr.Zero;
                }
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        // ── Record builders ──────────────────────────────────────────────────

        private static PointerInput Pointer(IntPtr hWnd, IntPtr lParam, PointerButton btn, IntPtr wParam)
        {
            int x = LoWordSigned(lParam);
            int y = HiWordSigned(lParam);
            GetClientSize(hWnd, out int w, out int h);
            return new PointerInput(x, y, w, h, btn, ModsFromFlags(LoWord(wParam)));
        }

        private static WheelInput Wheel(IntPtr hWnd, IntPtr wParam, IntPtr lParam)
        {
            // WM_MOUSEWHEEL coords are screen-relative; map them to client space.
            var pt = new POINT { X = LoWordSigned(lParam), Y = HiWordSigned(lParam) };
            ScreenToClient(hWnd, ref pt);
            GetClientSize(hWnd, out int w, out int h);
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            return new WheelInput(pt.X, pt.Y, w, h, delta, ModsFromFlags(LoWord(wParam)));
        }

        private static PointerButton ButtonsFromWParam(IntPtr wParam)
        {
            int f = LoWord(wParam);
            var b = PointerButton.None;
            if ((f & MK_LBUTTON) != 0) b |= PointerButton.Left;
            if ((f & MK_RBUTTON) != 0) b |= PointerButton.Right;
            if ((f & MK_MBUTTON) != 0) b |= PointerButton.Middle;
            return b;
        }

        private static InputModifiers ModsFromFlags(int f)
        {
            var m = InputModifiers.None;
            if ((f & MK_SHIFT) != 0)   m |= InputModifiers.Shift;
            if ((f & MK_CONTROL) != 0) m |= InputModifiers.Control;
            return m;
        }

        private static void GetClientSize(IntPtr h, out int w, out int hgt)
        {
            if (GetClientRect(h, out RECT r))
            {
                w = Math.Max(1, r.Right - r.Left);
                hgt = Math.Max(1, r.Bottom - r.Top);
            }
            else { w = 1; hgt = 1; }
        }

        private static void EnableDoubleClicks(IntPtr hwnd)
        {
            try
            {
                long s = GetClassLongPtr(hwnd, GCL_STYLE).ToInt64();
                if ((s & CS_DBLCLKS) == 0)
                    SetClassLongPtr(hwnd, GCL_STYLE, new IntPtr(s | CS_DBLCLKS));
            }
            catch { /* non-fatal: double-click recenter just won't fire */ }
        }

        private static int LoWord(IntPtr v) => (int)(v.ToInt64() & 0xFFFF);
        private static int LoWordSigned(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
        private static int HiWordSigned(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);

        // ── P/Invoke ─────────────────────────────────────────────────────────

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
                                             UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                     UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
                                                        UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
    }
}
