// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FracturingFog.Win/WindowsColorSampleBridge.cs
//
// S-X8 (2026-06-27) — Win32 IColorSampleBridge that replaces the legacy
// WinForms-bound DesktopEyedropper bridge wired from FracturingFogCLD's
// Program.cs. Lives in FracturingFog.Win so WindowsBootstrap.Install can
// register it for BOTH entry points (legacy WinExe and the cross-platform
// App on its net10.0-windows leg).
//
// Mechanism: WH_MOUSE_LL global hook plus a GDI screen-pixel read on the
// next left-button-down. Hook installs on the calling thread (the Avalonia
// UI thread); LL hooks deliver via the install thread's Win32 message pump
// which Avalonia already drives on Windows. ESC, right-click, middle-click,
// or any modifier-bearing left-click cancels.
//
// Differs from the WinForms DesktopEyedropper:
//   * No Control marshaller — IColorSampleBridge consumers await a TCS and
//     resume on the UI SynchronizationContext, so the callback can be
//     invoked synchronously from the hook callback.
//   * Cursor change via Win32 SetCursor (no Cursor.Current).
//   * Pixel read uses GetDC(NULL) + GdiGetPixel, the same path
//     WindowsNativeInputBridge.TrySampleClient already uses for inspect.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using FracturingFog.Hosting;

namespace FracturingFog.Win;

[SupportedOSPlatform("windows")]
internal sealed class WindowsColorSampleBridge : IColorSampleBridge
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private static IntPtr s_hookId = IntPtr.Zero;
    private static LowLevelMouseProc? s_proc;
    private static Action<(byte R, byte G, byte B)>? s_onPicked;
    private static Action? s_onCancelled;
    private static readonly object s_lock = new();

    public bool IsActive => s_hookId != IntPtr.Zero;

    public void Begin(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        ArgumentNullException.ThrowIfNull(onCancelled);

        lock (s_lock)
        {
            if (s_hookId != IntPtr.Zero) { onCancelled(); return; }

            s_onPicked = onPicked;
            s_onCancelled = onCancelled;
            s_proc = HookCallback;

            using var curMod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
            s_hookId = SetWindowsHookEx(WH_MOUSE_LL, s_proc, GetModuleHandle(curMod.ModuleName), 0);
            if (s_hookId == IntPtr.Zero)
            {
                s_proc = null;
                s_onPicked = null;
                s_onCancelled = null;
                onCancelled();
                return;
            }
        }

        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_CROSS)); } catch { }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(s_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        if (msg == WM_LBUTTONDOWN)
        {
            GetCursorPos(out var pt);
            (byte r, byte g, byte b, bool ok) = SamplePixel(pt.X, pt.Y);
            Action<(byte R, byte G, byte B)>? picked;
            Action? cancel;
            lock (s_lock)
            {
                Stop();
                picked = s_onPicked;
                cancel = s_onCancelled;
                s_onPicked = null;
                s_onCancelled = null;
            }
            try
            {
                if (ok) picked?.Invoke((r, g, b));
                else cancel?.Invoke();
            }
            catch { /* never crash the LL hook callback */ }
            return (IntPtr)1; // swallow click so target app doesn't see it
        }
        if (msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
        {
            Action? cancel;
            lock (s_lock)
            {
                Stop();
                cancel = s_onCancelled;
                s_onPicked = null;
                s_onCancelled = null;
            }
            try { cancel?.Invoke(); } catch { }
            return (IntPtr)1;
        }

        return CallNextHookEx(s_hookId, nCode, wParam, lParam);
    }

    private static void Stop()
    {
        if (s_hookId != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(s_hookId); } catch { }
            s_hookId = IntPtr.Zero;
        }
        s_proc = null;
    }

    // S-X10 (2026-07-28) — DWM-aware desktop-wide sampling. GetDC(NULL) +
    // GetPixel returns only THIS app's pixels under DWM on Win 8+, so a bare
    // GetPixel regressed cross-app sampling vs the legacy WinForms
    // DesktopEyedropper (which used Graphics.CopyFromScreen == BitBlt). We
    // BitBlt(SRCCOPY|CAPTUREBLT) the target screen pixel into a 1×1 compatible
    // bitmap, then GetPixel from that memory DC. CAPTUREBLT includes layered /
    // DWM-composited windows so any app's pixel is captured. See issue #116.
    private static (byte R, byte G, byte B, bool Ok) SamplePixel(int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return (0, 0, 0, false);

        IntPtr memDc = IntPtr.Zero, bmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return (0, 0, 0, false);
            bmp = CreateCompatibleBitmap(screenDc, 1, 1);
            if (bmp == IntPtr.Zero) return (0, 0, 0, false);
            oldBmp = SelectObject(memDc, bmp);

            if (!BitBlt(memDc, 0, 0, 1, 1, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                return (0, 0, 0, false);

            uint colorRef = GetPixel(memDc, 0, 0);
            if (colorRef == CLR_INVALID) return (0, 0, 0, false);
            byte r = (byte)(colorRef & 0xFF);
            byte g = (byte)((colorRef >> 8) & 0xFF);
            byte b = (byte)((colorRef >> 16) & 0xFF);
            return (r, g, b, true);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────

    private const uint CLR_INVALID = 0xFFFFFFFF;
    private static readonly IntPtr IDC_CROSS = (IntPtr)32515;

    // BitBlt raster-op codes (wingdi.h).
    private const uint SRCCOPY   = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000; // include layered/DWM windows

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);
}
