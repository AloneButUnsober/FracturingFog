// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/BlackHoleD3D11Kernel.cs (#1972, black hole shadows epic #1922)
//
// D3D11 compute backend of the black hole trace: BlackHoleKernelSource compiled by FXC (cs_5_0)
// on a device this kernel owns (hardware, or WARP for tests), so it does not contend with the
// renderer's device. The double kernel needs D3D11 fp64 shader ops (FeatureDataDoubles); without
// them only the single-precision kernel runs.
//
// TDR safety: the frame is traced in bands of pixels, each its own Dispatch followed by an event
// query the host waits on, so no single submission runs long enough to trip the Windows GPU
// timeout. The band size adapts toward ~100 ms per band from the measured time of the last band
// (the #1318 / #1481 banding pattern).

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

[SupportedOSPlatform("windows")]
public sealed class BlackHoleD3D11Kernel : IBlackHoleTraceKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Width, Height, HoleCount, Pinhole, MaxSteps, BandStart, BandEnd, Pad0; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly bool _ownsDevice;
    private readonly object _gate = new();
    private ID3D11ComputeShader? _csDouble, _csSingle;
    private readonly ID3D11Buffer _intsBuf;
    private readonly ID3D11Query _done;
    private readonly bool _doubles;
    private int _bandPixels = 4096;

    public string Backend => "D3D11";
    public string DeviceName { get; }
    public string LastError { get; private set; } = "";
    /// <summary>Target time per band (ms).</summary>
    public const double BandTargetMs = 100;

    public BlackHoleD3D11Kernel(ID3D11Device device, ID3D11DeviceContext ctx, bool ownsDevice, string name)
    {
        _device = device; _ctx = ctx; _ownsDevice = ownsDevice; DeviceName = name;
        _intsBuf = _device.CreateBuffer(new BufferDescription(byteWidth: 32, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
        _done = _device.CreateQuery(new QueryDescription(QueryType.Event));
        try { _doubles = _device.CheckFeatureSupport<FeatureDataDoubles>(Feature.Doubles).DoublePrecisionFloatShaderOps; }
        catch { _doubles = false; }
    }

    /// <summary>A kernel on its own hardware device (or WARP when <paramref name="warp"/>), or null.</summary>
    public static BlackHoleD3D11Kernel? TryCreateWithOwnDevice(bool warp = false)
    {
        try
        {
            var hr = D3D11.D3D11CreateDevice(null, warp ? DriverType.Warp : DriverType.Hardware, DeviceCreationFlags.None,
                null!, out ID3D11Device? dev, out _, out ID3D11DeviceContext? ctx);
            if (hr.Failure || dev == null || ctx == null) return null;
            string name = warp ? "WARP" : "D3D11 device";
            try
            {
                using var dxgi = dev.QueryInterface<Vortice.DXGI.IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                name = adapter.Description.Description;
            }
            catch { }
            return new BlackHoleD3D11Kernel(dev, ctx, ownsDevice: true, name);
        }
        catch (Exception) { return null; }
    }

    private ID3D11ComputeShader Shader(bool dbl)
    {
        if (dbl && _csDouble != null) return _csDouble;
        if (!dbl && _csSingle != null) return _csSingle;
        var cs = D3DShaderCache.CompileOrLoad(_device, BlackHoleKernelSource.Build(dbl), BlackHoleKernelSource.Entry, "cs_5_0",
            dbl ? "BlackHole.f64.hlsl" : "BlackHole.f32.hlsl", "BlackHoleD3D11Kernel");
        if (dbl) _csDouble = cs; else _csSingle = cs;
        return cs;
    }

    public BlackHoleShadowGpuCalculator.Outcomes? Trace(in BlackHoleGpuParams p, bool single, CancellationToken ct = default)
    {
        if (!single && !_doubles) { LastError = $"{DeviceName}: no D3D11 fp64 shader ops (use single precision)"; return null; }
        int total = p.Width * p.Height;
        if (total <= 0) return null;
        lock (_gate)
        {
            ID3D11Buffer? pBuf = null, packedBuf = null, dataBuf = null, packedStage = null, dataStage = null;
            ID3D11ShaderResourceView? pSrv = null;
            ID3D11UnorderedAccessView? packedUav = null, dataUav = null;
            try
            {
                var cs = Shader(!single);
                int perPixel = single ? 4 : 8;   // uints of data per pixel
                // Parameters (t0).
                if (single)
                {
                    var f = BlackHoleTraceBackends.PackSingle(p);
                    pBuf = Structured<float>(f, sizeof(float));
                }
                else
                {
                    var d = BlackHoleTraceBackends.Pack(p);
                    pBuf = Structured<double>(d, sizeof(double));
                }
                pSrv = _device.CreateShaderResourceView(pBuf, new ShaderResourceViewDescription
                {
                    Format = Vortice.DXGI.Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
                    Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = BlackHoleKernelSource.ParamCount },
                });
                (packedBuf, packedUav) = Uav(total);
                (dataBuf, dataUav) = Uav(total * perPixel);
                packedStage = Staging(total);
                dataStage = Staging(total * perPixel);

                _ctx.CSSetShader(cs);
                _ctx.CSSetShaderResource(0, pSrv);
                _ctx.CSSetUnorderedAccessView(0, packedUav);
                _ctx.CSSetUnorderedAccessView(1, dataUav);
                _ctx.CSSetConstantBuffer(0, _intsBuf);
                int start = 0;
                while (start < total)
                {
                    if (ct.IsCancellationRequested) return null;
                    int end = Math.Min(total, start + _bandPixels);
                    WriteInts(p, (uint)start, (uint)end);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ctx.Dispatch((uint)((end - start + BlackHoleKernelSource.GroupSize - 1) / BlackHoleKernelSource.GroupSize), 1, 1);
                    _ctx.End(_done);
                    _ctx.Flush();
                    while (_ctx.GetData(_done, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        Thread.Yield();
                    }
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    // Steer the next band toward the target time (bounded growth / shrink).
                    double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                    _bandPixels = (int)Math.Clamp(_bandPixels * scale, 256, 1 << 22);
                    start = end;
                }
                _ctx.CSSetUnorderedAccessView(0, null);
                _ctx.CSSetUnorderedAccessView(1, null);
                _ctx.CSSetShaderResource(0, null);
                if (ct.IsCancellationRequested) return null;

                _ctx.CopyResource(packedStage, packedBuf);
                _ctx.CopyResource(dataStage, dataBuf);
                var packed = new int[total];
                var data = new double[total * BlackHoleShadowGpuCalculator.DataStride];
                ReadBack(packedStage, MemoryMarshal.AsBytes(packed.AsSpan()));
                if (single)
                {
                    var f = new float[total * 4];
                    ReadBack(dataStage, MemoryMarshal.AsBytes(f.AsSpan()));
                    for (int i = 0; i < f.Length; i++) data[i] = f[i];
                }
                else ReadBack(dataStage, MemoryMarshal.AsBytes(data.AsSpan()));
                LastError = "";
                return new BlackHoleShadowGpuCalculator.Outcomes(packed, data);
            }
            catch (Exception ex)
            {
                LastError = $"BlackHoleShadow D3D11 trace failed: {ex.Message}";
                return null;
            }
            finally
            {
                pSrv?.Dispose(); packedUav?.Dispose(); dataUav?.Dispose();
                pBuf?.Dispose(); packedBuf?.Dispose(); dataBuf?.Dispose(); packedStage?.Dispose(); dataStage?.Dispose();
            }
        }
    }

    private unsafe void WriteInts(in BlackHoleGpuParams p, uint start, uint end)
    {
        var v = new Ints
        {
            Width = (uint)p.Width, Height = (uint)p.Height, HoleCount = (uint)Math.Clamp(p.HoleCount, 1, 4),
            Pinhole = (uint)p.Pinhole, MaxSteps = (uint)Math.Max(1, p.MaxSteps), BandStart = start, BandEnd = end,
        };
        var m = _ctx.Map(_intsBuf, 0, MapMode.WriteDiscard, MapFlags.None);
        *(Ints*)m.DataPointer = v;
        _ctx.Unmap(_intsBuf, 0);
    }

    private unsafe ID3D11Buffer Structured<T>(T[] values, int stride) where T : unmanaged
    {
        fixed (T* ptr = values)
        {
            return _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(values.Length * stride), BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Immutable,
                MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = (uint)stride,
            }, new SubresourceData((IntPtr)ptr));
        }
    }

    private (ID3D11Buffer, ID3D11UnorderedAccessView) Uav(int uints)
    {
        var b = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(uints * sizeof(uint)), BindFlags = BindFlags.UnorderedAccess, Usage = ResourceUsage.Default,
            MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = sizeof(uint),
        });
        var u = _device.CreateUnorderedAccessView(b, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)uints },
        });
        return (b, u);
    }

    private ID3D11Buffer Staging(int uints) => _device.CreateBuffer(new BufferDescription
    {
        ByteWidth = (uint)(uints * sizeof(uint)), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
    });

    private unsafe void ReadBack(ID3D11Buffer staging, Span<byte> dst)
    {
        var m = _ctx.Map(staging, 0, MapMode.Read, MapFlags.None);
        try { new ReadOnlySpan<byte>((void*)m.DataPointer, dst.Length).CopyTo(dst); }
        finally { _ctx.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _csDouble?.Dispose(); _csSingle?.Dispose(); _intsBuf.Dispose(); _done.Dispose();
            if (_ownsDevice) { _ctx.Dispose(); _device.Dispose(); }
        }
    }
}
