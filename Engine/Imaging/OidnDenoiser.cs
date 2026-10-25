// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/OidnDenoiser.cs
//
// #1338 — Intel Open Image Denoise (OIDN, Apache-2.0), a learned denoiser, as an
// optional native runtime. Nothing is linked at build time: the library is found
// at runtime (FF_OIDN_DIR, then the copy OidnInstaller puts in the user data
// folder, then the OS loader search), its C API is bound through function
// pointers, and a missing or broken install reads as "not available" — the render
// then falls back (see ReliefDenoisePass / ScreenSpacePost.ApplyOidn). Like the
// CUDA path, a native failure never takes the process down.
//
// The CPU device is used always: it is deterministic for a given machine, needs
// no GPU driver (CUDA is blocked over RDP, #1164), and is the only device module
// the installer unpacks. One device for the process; one RT ("ray tracing")
// filter per image configuration, cached; calls are serialised (the filters share
// the device's thread pool).
//
// #1392 — a call can be cancelled: OIDN's progress monitor polls the caller's
// cancel predicate and stops the filter (the live view's background denoise of a
// settled frame, OidnAsync, cancels a frame the view has moved on from).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace FracturingFog.Imaging;

/// <summary>#1338 — the Intel Open Image Denoise runtime (see the file header).</summary>
public static unsafe class OidnDenoiser
{
    /// <summary>The release the installer fetches and these bindings target (C API 2.x).</summary>
    public const string Version = "2.5.1";

    private static readonly object s_lock = new();
    private static bool s_probed;
    private static IntPtr s_lib, s_device;
    private static string? s_error, s_loadedFrom;
    private static readonly Dictionary<(int W, int H, bool Albedo, bool Normal, bool Hdr, bool Srgb), IntPtr> s_filters = new();

    // The C API (oidn.h, 2.x).
    private static delegate* unmanaged[Cdecl]<int, IntPtr> p_newDevice;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> p_commitDevice;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> p_getDeviceInt;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> p_setDeviceInt;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int> p_getDeviceError;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> p_newFilter;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, void*, int, nuint, nuint, nuint, nuint, nuint, void> p_setSharedFilterImage;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte, void> p_setFilterBool;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> p_setFilterInt;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> p_commitFilter;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> p_executeFilter;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> p_releaseFilter;
    // #1392 — oidnSetFilterProgressMonitorFunction(filter, func, userPtr); null when absent.
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> p_setProgressMonitor;
    private static Func<bool>? s_cancel;   // the running call's cancel predicate (calls are serialised)

    private const int DeviceTypeCpu = 1;
    private const int FormatFloat3 = 3;

    /// <summary>The library's file name on this OS.</summary>
    public static string LibraryFileName
        => OperatingSystem.IsWindows() ? "OpenImageDenoise.dll"
         : OperatingSystem.IsMacOS() ? "libOpenImageDenoise.2.dylib"
         : "libOpenImageDenoise.so.2";

    /// <summary>Where <see cref="OidnInstaller"/> unpacks the runtime (user data
    /// folder, writable on every platform — an AppImage's own folder is not).</summary>
    public static string InstallDirectory
        => Path.Combine(FracturingFog.Abstractions.AppDataPaths.Root, "oidn", Version);

    /// <summary>Treat the runtime as absent even when installed: a kill switch
    /// (FF_OIDN_DISABLE=1) and the tests' stand-in for a machine without it.</summary>
    public static bool Disabled { get; set; } = Environment.GetEnvironmentVariable("FF_OIDN_DISABLE") == "1";

    /// <summary>True when the runtime loads and its CPU device starts.</summary>
    public static bool IsAvailable
    {
        get { lock (s_lock) { if (Disabled) return false; Probe(); return s_device != IntPtr.Zero; } }
    }

    /// <summary>#1392 — the runtime has already loaded and started its device. Never
    /// blocks (a running filter holds the lock for its whole run); false before the
    /// first probe.</summary>
    public static bool IsLoaded => !Disabled && Volatile.Read(ref s_ready);

    /// <summary>#1392 — a call is running right now (diagnostics: how soon a cancelled
    /// filter lets go).</summary>
    public static bool IsBusy
    {
        get
        {
            if (!Monitor.TryEnter(s_lock)) return true;
            Monitor.Exit(s_lock);
            return false;
        }
    }
    private static bool s_ready;

    /// <summary>Why the runtime is unavailable (null when it is available or untried).</summary>
    public static string? LoadError { get { lock (s_lock) { if (Disabled) return "Open Image Denoise is disabled."; Probe(); return s_error; } } }

    /// <summary>The folder the runtime was loaded from (null when unavailable).</summary>
    public static string? LoadedFrom { get { lock (s_lock) { Probe(); return s_loadedFrom; } } }

    /// <summary>Forget a failed probe so the next call looks again (after an install).
    /// A loaded runtime stays loaded.</summary>
    public static void Refresh()
    {
        lock (s_lock) { if (s_device == IntPtr.Zero) s_probed = false; }
    }

    private static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("FF_OIDN_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return Path.Combine(env, LibraryFileName);
            yield return Path.Combine(env, "bin", LibraryFileName);
            yield return Path.Combine(env, "lib", LibraryFileName);
        }
        string inst;
        try { inst = InstallDirectory; } catch { inst = ""; }
        if (inst.Length > 0) yield return Path.Combine(inst, LibraryFileName);
        yield return Path.Combine(AppContext.BaseDirectory, LibraryFileName);
        yield return LibraryFileName;   // the OS loader search (PATH / LD_LIBRARY_PATH)
    }

    private static void Probe()
    {
        if (s_probed) return;
        s_probed = true;
        s_error = null;
        try
        {
            foreach (var path in Candidates())
            {
                bool rooted = Path.IsPathRooted(path);
                if (rooted && !File.Exists(path)) continue;
                if (!NativeLibrary.TryLoad(path, out var lib)) continue;
                s_lib = lib;
                s_loadedFrom = rooted ? Path.GetDirectoryName(path) : "(system search path)";
                break;
            }
            if (s_lib == IntPtr.Zero) { s_error = "Intel Open Image Denoise is not installed."; return; }

            p_newDevice = (delegate* unmanaged[Cdecl]<int, IntPtr>)Export("oidnNewDevice");
            p_commitDevice = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("oidnCommitDevice");
            p_getDeviceInt = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)Export("oidnGetDeviceInt");
            p_setDeviceInt = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void>)Export("oidnSetDeviceInt");
            p_getDeviceError = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Export("oidnGetDeviceError");
            p_newFilter = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)Export("oidnNewFilter");
            p_setSharedFilterImage = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void*, int, nuint, nuint, nuint, nuint, nuint, void>)Export("oidnSetSharedFilterImage");
            p_setFilterBool = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte, void>)Export("oidnSetFilterBool");
            p_setFilterInt = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void>)Export("oidnSetFilterInt");
            p_commitFilter = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("oidnCommitFilter");
            p_executeFilter = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("oidnExecuteFilter");
            p_releaseFilter = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("oidnReleaseFilter");
            p_setProgressMonitor = NativeLibrary.TryGetExport(s_lib, "oidnSetFilterProgressMonitorFunction", out var pm)
                ? (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)pm : null;

            var dev = p_newDevice(DeviceTypeCpu);
            if (dev == IntPtr.Zero) { s_error = "Open Image Denoise could not create its CPU device."; return; }
            p_commitDevice(dev);
            if (DeviceError(dev) is string e) { s_error = "Open Image Denoise: " + e; return; }
            int major;
            fixed (byte* n = "versionMajor\0"u8) major = p_getDeviceInt(dev, n);
            if (major != 2) { s_error = $"Open Image Denoise {major}.x is not supported (2.x is)."; return; }
            s_device = dev;
            Volatile.Write(ref s_ready, true);
        }
        catch (Exception ex)
        {
            s_error = "Open Image Denoise failed to load: " + ex.Message;
            s_device = IntPtr.Zero;
        }
    }

    private static IntPtr Export(string name)
        => NativeLibrary.TryGetExport(s_lib, name, out var p) ? p
         : throw new EntryPointNotFoundException(name);

    private static string? DeviceError(IntPtr dev)
    {
        IntPtr msg;
        int code = p_getDeviceError(dev, &msg);
        if (code == 0) return null;
        return (msg != IntPtr.Zero ? Marshal.PtrToStringUTF8(msg) : null) ?? $"error {code}";
    }

    /// <summary>Denoise <paramref name="color"/> (w·h·3 floats, RGB) into
    /// <paramref name="output"/> (same layout; may be the same array). Optional
    /// noise-free guides: <paramref name="albedo"/> (w·h·3, [0,1]) and
    /// <paramref name="normal"/> (w·h·3, any space; OIDN uses a normal only with an
    /// albedo, so a normal without an albedo is ignored). <paramref name="hdr"/>: linear
    /// HDR colour; else LDR in [0,1], <paramref name="srgb"/> when it is gamma-encoded
    /// (the output keeps the encoding). False (with <paramref name="error"/>) when the
    /// runtime is unavailable or the filter failed; <paramref name="output"/> is then
    /// untouched. <paramref name="cancel"/> (#1392): polled while the filter runs; true
    /// stops it (false, error "cancelled"; <paramref name="output"/> may be partly
    /// written).</summary>
    public static bool TryDenoise(float[] color, float[]? albedo, float[]? normal, int w, int h,
        bool hdr, bool srgb, float[] output, out string? error, Func<bool>? cancel = null)
    {
        error = null;
        long n3 = (long)w * h * 3;
        if (w <= 0 || h <= 0 || color.Length < n3 || output.Length < n3
            || (albedo != null && albedo.Length < n3) || (normal != null && normal.Length < n3))
        { error = "bad image size"; return false; }
        if (albedo == null) normal = null;
        lock (s_lock)
        {
            if (Disabled) { error = "Open Image Denoise is disabled."; return false; }
            Probe();
            if (s_device == IntPtr.Zero) { error = s_error ?? "unavailable"; return false; }
            var key = (w, h, albedo != null, normal != null, hdr, srgb && !hdr);
            try
            {
                if (!s_filters.TryGetValue(key, out var filter))
                {
                    fixed (byte* rt = "RT\0"u8) filter = p_newFilter(s_device, rt);
                    if (filter == IntPtr.Zero) { error = DeviceError(s_device) ?? "no RT filter"; return false; }
                    fixed (byte* nHdr = "hdr\0"u8) p_setFilterBool(filter, nHdr, (byte)(hdr ? 1 : 0));
                    fixed (byte* nSrgb = "srgb\0"u8) p_setFilterBool(filter, nSrgb, (byte)(key.Item6 ? 1 : 0));
                    // The guides are the render's own noise-free AOVs.
                    if (albedo != null) fixed (byte* nc = "cleanAux\0"u8) p_setFilterBool(filter, nc, 1);
                    s_filters[key] = filter;
                }
                fixed (float* pc = color, po = output, pa = albedo, pn = normal)
                {
                    fixed (byte* nColor = "color\0"u8) p_setSharedFilterImage(filter, nColor, pc, FormatFloat3, (nuint)w, (nuint)h, 0, 0, 0);
                    fixed (byte* nOut = "output\0"u8) p_setSharedFilterImage(filter, nOut, po, FormatFloat3, (nuint)w, (nuint)h, 0, 0, 0);
                    if (albedo != null) fixed (byte* nA = "albedo\0"u8) p_setSharedFilterImage(filter, nA, pa, FormatFloat3, (nuint)w, (nuint)h, 0, 0, 0);
                    if (normal != null) fixed (byte* nN = "normal\0"u8) p_setSharedFilterImage(filter, nN, pn, FormatFloat3, (nuint)w, (nuint)h, 0, 0, 0);
                    // #1392 — the monitor only when the caller can cancel (a cached
                    // filter keeps the last one set, so clear it otherwise).
                    s_cancel = cancel;
                    if (p_setProgressMonitor != null)
                        p_setProgressMonitor(filter, cancel != null ? (IntPtr)(delegate* unmanaged[Cdecl]<void*, double, byte>)&Progress : IntPtr.Zero, IntPtr.Zero);
                    p_commitFilter(filter);
                    if (DeviceError(s_device) is string ce) { error = ce; return false; }
                    if (cancel?.Invoke() == true) { error = "cancelled"; return false; }
                    // An in-place call (output == color) is allowed by OIDN.
                    p_executeFilter(filter);
                }
                if (DeviceError(s_device) is string ee) { error = cancel?.Invoke() == true ? "cancelled" : ee; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { s_cancel = null; }
        }
    }

    /// <summary>True when the loaded runtime can stop a running filter (#1392).</summary>
    public static bool SupportsCancel { get { lock (s_lock) { Probe(); return p_setProgressMonitor != null; } } }

    // OIDN's progress monitor (OIDNProgressMonitorFunction: bool (void*, double)):
    // returning false cancels the filter. Called on OIDN's worker threads.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte Progress(void* userPtr, double n) => s_cancel?.Invoke() == true ? (byte)0 : (byte)1;

    /// <summary>Denoise a straight-alpha BGRA beauty in place (LDR, gamma-encoded),
    /// guided by an optional BGRA <paramref name="albedo"/> and a float
    /// <paramref name="normalXyz"/> (w·h·3). Alpha is kept. False (beauty untouched)
    /// when the runtime is unavailable or failed (or <paramref name="cancel"/>, #1392,
    /// stopped it).</summary>
    public static bool TryDenoiseBgra(uint[] beauty, uint[]? albedo, float[]? normalXyz, int w, int h, out string? error,
        Func<bool>? cancel = null)
    {
        long n = (long)w * h;
        error = null;
        if (beauty.Length < n) { error = "bad image size"; return false; }
        var c = ToFloat3(beauty, n);
        var a = albedo != null && albedo.Length >= n ? ToFloat3(albedo, n, linear: true) : null;
        var nrm = normalXyz != null && normalXyz.Length >= n * 3 ? normalXyz : null;
        var o = new float[n * 3];
        if (!TryDenoise(c, a, nrm, w, h, hdr: false, srgb: true, o, out error, cancel)) return false;
        for (long i = 0; i < n; i++)
        {
            uint px = beauty[i];
            uint r = Byte(o[i * 3]), g = Byte(o[i * 3 + 1]), b = Byte(o[i * 3 + 2]);
            beauty[i] = (px & 0xFF000000u) | (r << 16) | (g << 8) | b;
        }
        return true;
    }

    private static uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    // BGRA bytes → RGB floats in [0,1]; linear: undo the sRGB curve (an albedo is linear).
    private static float[] ToFloat3(uint[] px, long n, bool linear = false)
    {
        var f = new float[n * 3];
        for (long i = 0; i < n; i++)
        {
            uint p = px[i];
            float r = ((p >> 16) & 255) / 255f, g = ((p >> 8) & 255) / 255f, b = (p & 255) / 255f;
            if (linear) { r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b); }
            f[i * 3] = r; f[i * 3 + 1] = g; f[i * 3 + 2] = b;
        }
        return f;
    }

    private static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}
