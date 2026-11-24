// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/CosmicWebVolumeD3D11Kernel.cs (#2024, Cosmic Web epic #2020)
//
// D3D11 compute backend of the Cosmic Web volume raymarch: CosmicWebVolumeKernelSource compiled by
// FXC (cs_5_0) on a device this kernel owns (hardware, or WARP for tests). The mapped grid is
// uploaded once per grid instance and kept, so camera moves only re-march.
//
// TDR safety (the #1318 / #1481 / #1972 pattern): the frame is marched in bands of pixels, each its
// own Dispatch followed by an event query the host waits on; the band size is steered toward
// ~100 ms from the measured time of the last band.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.CosmicWeb;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

[SupportedOSPlatform("windows")]
public sealed class CosmicWebVolumeD3D11Kernel : ICosmicWebVolumeKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Width, Height, Grid, Steps, DepthColour, Background, BandStart, BandEnd; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly bool _ownsDevice;
    private readonly object _gate = new();
    private ID3D11ComputeShader? _cs;
    private readonly ID3D11Buffer _intsBuf;
    private readonly ID3D11Query _done;
    private int _bandPixels = 4096;
    // The grid kept on the device (weak, so a dropped grid is not pinned by the kernel).
    private WeakReference<CosmicWebDensityGrid>? _gridKey;
    private ID3D11Buffer? _gridBuf;
    private ID3D11ShaderResourceView? _gridSrv;

    public string Backend => "D3D11";
    public string DeviceName { get; }
    public string LastError { get; private set; } = "";
    public const double BandTargetMs = 100;

    public CosmicWebVolumeD3D11Kernel(ID3D11Device device, ID3D11DeviceContext ctx, bool ownsDevice, string name)
    {
        _device = device; _ctx = ctx; _ownsDevice = ownsDevice; DeviceName = name;
        _intsBuf = _device.CreateBuffer(new BufferDescription(byteWidth: 32, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
        _done = _device.CreateQuery(new QueryDescription(QueryType.Event));
    }

    /// <summary>A kernel on its own hardware device (or WARP when <paramref name="warp"/>), or null.</summary>
    public static CosmicWebVolumeD3D11Kernel? TryCreateWithOwnDevice(bool warp = false)
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
            return new CosmicWebVolumeD3D11Kernel(dev, ctx, ownsDevice: true, name);
        }
        catch (Exception) { return null; }
    }

    private ID3D11ComputeShader Shader()
        => _cs ??= D3DShaderCache.CompileOrLoad(_device, CosmicWebVolumeKernelSource.Source, CosmicWebVolumeKernelSource.Entry, "cs_5_0",
            "CosmicWebVolume.hlsl", "CosmicWebVolumeD3D11Kernel");

    private ID3D11ShaderResourceView GridSrv(CosmicWebDensityGrid grid)
    {
        if (_gridSrv != null && _gridKey != null && _gridKey.TryGetTarget(out var held) && ReferenceEquals(held, grid)) return _gridSrv;
        _gridSrv?.Dispose(); _gridBuf?.Dispose();
        _gridSrv = null; _gridBuf = null;
        _gridBuf = Structured(grid.Value, sizeof(float));
        _gridSrv = Srv(_gridBuf, grid.Value.Length);
        _gridKey = new WeakReference<CosmicWebDensityGrid>(grid);
        return _gridSrv;
    }

    public CosmicWebVolumeFrame? Render(in CosmicWebVolumeParams p, CosmicWebDensityGrid grid, uint[] lut, CancellationToken ct = default)
    {
        int total = p.Width * p.Height;
        if (total <= 0) return null;
        lock (_gate)
        {
            ID3D11Buffer? pBuf = null, lutBuf = null, colBuf = null, depBuf = null, colStage = null, depStage = null;
            ID3D11ShaderResourceView? pSrv = null, lutSrv = null;
            ID3D11UnorderedAccessView? colUav = null, depUav = null;
            try
            {
                var cs = Shader();
                var gridSrv = GridSrv(grid);
                var f = p.PackFloats();
                pBuf = Structured(f, sizeof(float));
                pSrv = Srv(pBuf, f.Length);
                lutBuf = Structured(lut, sizeof(uint));
                lutSrv = Srv(lutBuf, lut.Length);
                (colBuf, colUav) = Uav(total);
                (depBuf, depUav) = Uav(total);
                colStage = Staging(total);
                depStage = Staging(total);

                _ctx.CSSetShader(cs);
                _ctx.CSSetShaderResource(0, pSrv);
                _ctx.CSSetShaderResource(1, lutSrv);
                _ctx.CSSetShaderResource(2, gridSrv);
                _ctx.CSSetUnorderedAccessView(0, colUav);
                _ctx.CSSetUnorderedAccessView(1, depUav);
                _ctx.CSSetConstantBuffer(0, _intsBuf);
                int start = 0;
                while (start < total)
                {
                    if (ct.IsCancellationRequested) break;
                    int end = Math.Min(total, start + _bandPixels);
                    WriteInts(p, (uint)start, (uint)end);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ctx.Dispatch((uint)((end - start + CosmicWebVolumeKernelSource.GroupSize - 1) / CosmicWebVolumeKernelSource.GroupSize), 1, 1);
                    _ctx.End(_done);
                    _ctx.Flush();
                    while (_ctx.GetData(_done, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        Thread.Yield();
                    }
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                    _bandPixels = (int)Math.Clamp(_bandPixels * scale, 256, 1 << 21);   // <= 32768 groups (D3D11 caps a dimension at 65535)
                    start = end;
                }
                _ctx.CSSetUnorderedAccessView(0, null);
                _ctx.CSSetUnorderedAccessView(1, null);
                _ctx.CSSetShaderResource(0, null);
                _ctx.CSSetShaderResource(1, null);
                _ctx.CSSetShaderResource(2, null);
                if (ct.IsCancellationRequested) return null;

                _ctx.CopyResource(colStage, colBuf);
                _ctx.CopyResource(depStage, depBuf);
                var colour = new uint[total];
                var depth = new float[total];
                ReadBack(colStage, MemoryMarshal.AsBytes(colour.AsSpan()));
                ReadBack(depStage, MemoryMarshal.AsBytes(depth.AsSpan()));
                LastError = "";
                return new CosmicWebVolumeFrame(colour, depth);
            }
            catch (Exception ex)
            {
                LastError = $"CosmicWeb D3D11 volume failed: {ex.Message}";
                return null;
            }
            finally
            {
                pSrv?.Dispose(); lutSrv?.Dispose(); colUav?.Dispose(); depUav?.Dispose();
                pBuf?.Dispose(); lutBuf?.Dispose(); colBuf?.Dispose(); depBuf?.Dispose(); colStage?.Dispose(); depStage?.Dispose();
            }
        }
    }

    private unsafe void WriteInts(in CosmicWebVolumeParams p, uint start, uint end)
    {
        var v = new Ints
        {
            Width = (uint)p.Width, Height = (uint)p.Height, Grid = (uint)p.Grid, Steps = (uint)p.Steps,
            DepthColour = p.DepthColour ? 1u : 0u, Background = p.Background, BandStart = start, BandEnd = end,
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

    private ID3D11ShaderResourceView Srv(ID3D11Buffer b, int count) => _device.CreateShaderResourceView(b, new ShaderResourceViewDescription
    {
        Format = Vortice.DXGI.Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
        Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)count },
    });

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
            _gridSrv?.Dispose(); _gridBuf?.Dispose();
            _cs?.Dispose(); _intsBuf.Dispose(); _done.Dispose();
            if (_ownsDevice) { _ctx.Dispose(); _device.Dispose(); }
        }
    }
}
