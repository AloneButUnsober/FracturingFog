// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/Editors/DesktopEyedropper.cs
//
// Global desktop pixel sampler. Begin() installs a WH_MOUSE_LL hook and
// changes the cursor to a crosshair. The next left-button-down anywhere
// on screen captures the pixel under the cursor, fires the callback, and
// uninstalls the hook. ESC, right-click, or any modifier+click cancels.
//
// We avoid full-screen overlay windows because they would either steal
// focus from the target (bad for sampling app UI) or fight DPI scaling
// when spanning multi-monitor setups. A low-level mouse hook is the
// cleanest cross-app surface available without elevation.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FracturingFog.Views.Editors
{
    public static class DesktopEyedropper
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // ── Public API ──────────────────────────────────────────────────────

        public static bool IsActive => _hookId != IntPtr.Zero;

        /// <summary>
        /// Begin sampling. The next left-click anywhere on the desktop fires
        /// <paramref name="onPicked"/> with the screen-pixel color. Right-click
        /// or Escape fires <paramref name="onCancel"/>. The cursor is restored
        /// in either case. Calling Begin while already active is a no-op.
        /// </summary>
        public static void Begin(Action<Color> onPicked, Action? onCancel = null)
        {
            if (_hookId != IntPtr.Zero) return;

            _onPicked = onPicked;
            _onCancel = onCancel;
            _proc = HookCallback;

            using var curMod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curMod.ModuleName), 0);

            if (_hookId == IntPtr.Zero)
            {
                _proc = null;
                _onPicked = null;
                _onCancel = null;
                throw new InvalidOperationException("Failed to install eyedropper hook.");
            }

            try { Cursor.Current = Cursors.Cross; } catch { }
        }

        public static void Cancel()
        {
            if (_hookId == IntPtr.Zero) return;
            Stop();
            _onCancel?.Invoke();
            _onPicked = null;
            _onCancel = null;
        }

        // ── Hook ────────────────────────────────────────────────────────────

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelMouseProc? _proc;
        private static Action<Color>? _onPicked;
        private static Action? _onCancel;

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN)
            {
                GetCursorPos(out var pt);
                Color c = SamplePixel(pt.X, pt.Y);
                Stop();
                var cb = _onPicked;
                _onPicked = null;
                _onCancel = null;
                // Defer the callback so the hook fully unwinds before we
                // re-enter the editor UI thread.
                BeginInvoke(() => cb?.Invoke(c));
                // Swallow the click so it doesn't reach the underlying app.
                return (IntPtr)1;
            }
            if (msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                Stop();
                var cb = _onCancel;
                _onPicked = null;
                _onCancel = null;
                BeginInvoke(() => cb?.Invoke());
                return (IntPtr)1;
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _proc = null;
            try { Cursor.Current = Cursors.Default; } catch { }
        }

        /// <summary>Grab one pixel from the desktop at the given screen
        /// coordinates.</summary>
        public static Color SamplePixel(int screenX, int screenY)
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(screenX, screenY, 0, 0, new Size(1, 1));
            return bmp.GetPixel(0, 0);
        }

        // Schedule a callback on the UI thread. We don't have a Form handle
        // here so use Control.Invoke via a hidden marshaller created on demand.
        private static Control? _marshal;
        private static void BeginInvoke(Action a)
        {
            try
            {
                if (_marshal == null || _marshal.IsDisposed)
                {
                    _marshal = new Control();
                    _ = _marshal.Handle; // force handle creation
                }
                _marshal.BeginInvoke(a);
            }
            catch
            {
                // Fall back to inline if marshalling fails — rare and the
                // worst case is the editor refreshes a frame late.
                a();
            }
        }
    }
}
