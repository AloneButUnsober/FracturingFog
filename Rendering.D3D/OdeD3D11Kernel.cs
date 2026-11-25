// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.D3D/OdeD3D11Kernel.cs (#2049, physics epic #2048)
//
// D3D11 compute backend of the ODE template (OdeKernelSource + a family's equations), FXC
// cs_5_0, on a device this kernel owns (hardware, or WARP for tests), so it does not contend
// with the renderer's device. The double kernel needs D3D11 fp64 shader ops
// (FeatureDataDoubles); without them only the single-precision kernel runs.
//
// The job is traced in chunks of OdeTraceBackends.ChunkItems items (launch and outcome buffers
// sized to a chunk); each chunk in bands, each band its own Dispatch followed by an event query
// the host waits on, the band size steered toward ~100 ms (the #1318 / #1481 / #1972 TDR
// pattern) and capped at 65,535 groups (#2045).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog.Rendering;

[SupportedOSPlatform("windows")]
public sealed class OdeD3D11Kernel : IOdeTraceKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Ints { public uint Count, BandStart, BandEnd, MaxSteps, Stride, Pad0, Pad1, Pad2; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly bool _ownsDevice;
    private readonly object _gate = new();
    private readonly Dictionary<(string, bool), ID3D11ComputeShader> _shaders = new();
    private readonly ID3D11Buffer _intsBuf;
    private readonly ID3D11Query _done;
    private readonly bool _doubles;
    private int _bandItems = 4096;

    public string Backend => "D3D11";
    public string DeviceName { get; }
    public string LastError { get; private set; } = "";
    public bool SupportsDouble => _doubles;
    public const double BandTargetMs = 100;

    public OdeD3D11Kernel(ID3D11Device device, ID3D11DeviceContext ctx, bool ownsDevice, string name)
    {
        _device = device; _ctx = ctx; _ownsDevice = ownsDevice; DeviceName = name;
        _intsBuf = _device.CreateBuffer(new BufferDescription(byteWidth: 32, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
        _done = _device.CreateQuery(new QueryDescription(QueryType.Event));
        try { _doubles = _device.CheckFeatureSupport<FeatureDataDoubles>(Feature.Doubles).DoublePrecisionFloatShaderOps; }
        catch { _doubles = false; }
    }

    /// <summary>A kernel on its own hardware device (or WARP when <paramref name="warp"/>), or null.</summary>
    public static OdeD3D11Kernel? TryCreateWithOwnDevice(bool warp = false)
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
            return new OdeD3D11Kernel(dev, ctx, ownsDevice: true, name);
        }
        catch (Exception) { return null; }
    }

    private ID3D11ComputeShader Shader(OdeFamily fam, bool dbl)
    {
        if (_shaders.TryGetValue((fam.Key, dbl), out var cs)) return cs;
        cs = D3DShaderCache.CompileOrLoad(_device, OdeKernelSource.Build(fam.Hlsl, fam.Dimension, fam.Channels, dbl),
            OdeKernelSource.Entry, "cs_5_0", $"Ode.{fam.Key}.{(dbl ? "f64" : "f32")}.hlsl", "OdeD3D11Kernel");
        _shaders[(fam.Key, dbl)] = cs;
        return cs;
    }

    public OdeResult? Trace(OdeJob job, CancellationToken ct = default)
    {
        bool dbl = !job.Single;
        if (dbl && !_doubles) { LastError = $"{DeviceName}: no D3D11 fp64 shader ops (use single precision)"; return null; }
        var fam = job.Family;
        int dim = fam.Dimension, stride = OdeKernelSource.Stride(dim, fam.Channels, dbl);
        var result = new OdeResult(job.Count, dim, fam.Channels);
        if (job.Count == 0) return result;
        lock (_gate)
        {
            ID3D11Buffer? pBuf = null;
            ID3D11ShaderResourceView? pSrv = null;
            try
            {
                var cs = Shader(fam, dbl);
                var par = OdeTraceBackends.Params(job);
                pBuf = dbl ? Structured(par, sizeof(double)) : Structured(ToFloat(par), sizeof(float));
                pSrv = Srv(pBuf, par.Length);
                int chunk = Math.Min(OdeTraceBackends.ChunkItems, job.Count);
                var launch = new double[chunk * dim];
                var words = new uint[chunk * stride];
                for (int first = 0; first < job.Count; first += chunk)
                {
                    if (ct.IsCancellationRequested) return null;
                    int n = Math.Min(chunk, job.Count - first);
                    job.Launches(first, n, launch);
                    if (!RunChunk(cs, pSrv, launch, n, dim, stride, dbl, job.MaxSteps, words, ct)) return null;
                    OdeTraceBackends.Decode(words, first, n, fam, !dbl, result);
                }
                LastError = "";
                return result;
            }
            catch (Exception ex)
            {
                LastError = $"ODE D3D11 trace failed: {ex.Message}";
                return null;
            }
            finally { pSrv?.Dispose(); pBuf?.Dispose(); }
        }
    }

    private bool RunChunk(ID3D11ComputeShader cs, ID3D11ShaderResourceView pSrv, double[] launch, int n, int dim, int stride,
        bool dbl, int maxSteps, uint[] words, CancellationToken ct)
    {
        ID3D11Buffer? lBuf = null, oBuf = null, stage = null;
        ID3D11ShaderResourceView? lSrv = null;
        ID3D11UnorderedAccessView? oUav = null;
        try
        {
            if (dbl) lBuf = Structured(launch.AsSpan(0, n * dim).ToArray(), sizeof(double));
            else lBuf = Structured(ToFloat(launch.AsSpan(0, n * dim)), sizeof(float));
            lSrv = Srv(lBuf, n * dim);
            int uints = n * stride;
            oBuf = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(uints * sizeof(uint)), BindFlags = BindFlags.UnorderedAccess, Usage = ResourceUsage.Default,
                MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = sizeof(uint),
            });
            oUav = _device.CreateUnorderedAccessView(oBuf, new UnorderedAccessViewDescription
            {
                Format = Vortice.DXGI.Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)uints },
            });
            stage = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(uints * sizeof(uint)), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
            });
            _ctx.CSSetShader(cs);
            _ctx.CSSetShaderResource(0, lSrv);
            _ctx.CSSetShaderResource(1, pSrv);
            _ctx.CSSetUnorderedAccessView(0, oUav);
            _ctx.CSSetConstantBuffer(0, _intsBuf);
            int start = 0;
            while (start < n)
            {
                if (ct.IsCancellationRequested) break;
                int end = Math.Min(n, start + _bandItems);
                WriteInts((uint)n, (uint)start, (uint)end, (uint)Math.Max(1, maxSteps), (uint)stride);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _ctx.Dispatch((uint)((end - start + OdeKernelSource.GroupSize - 1) / OdeKernelSource.GroupSize), 1, 1);
                _ctx.End(_done);
                _ctx.Flush();
                while (_ctx.GetData(_done, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)
                {
                    if (ct.IsCancellationRequested) break;
                    Thread.Yield();
                }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double scale = ms > 0.01 ? Math.Clamp(BandTargetMs / ms, 0.25, 4.0) : 4.0;
                _bandItems = (int)Math.Clamp(_bandItems * scale, 256, OdeKernelSource.MaxBandPixels);
                start = end;
            }
            _ctx.CSSetUnorderedAccessView(0, null);
            _ctx.CSSetShaderResource(0, null);
            _ctx.CSSetShaderResource(1, null);
            if (ct.IsCancellationRequested) return false;
            _ctx.CopyResource(stage, oBuf);
            var m = _ctx.Map(stage, 0, MapMode.Read, MapFlags.None);
            try
            {
                unsafe { new ReadOnlySpan<uint>((void*)m.DataPointer, uints).CopyTo(words); }
            }
            finally { _ctx.Unmap(stage, 0); }
            return true;
        }
        finally
        {
            lSrv?.Dispose(); oUav?.Dispose(); lBuf?.Dispose(); oBuf?.Dispose(); stage?.Dispose();
        }
    }

    private static float[] ToFloat(ReadOnlySpan<double> d)
    {
        var f = new float[d.Length];
        for (int i = 0; i < f.Length; i++) f[i] = (float)d[i];
        return f;
    }

    private unsafe void WriteInts(uint count, uint start, uint end, uint maxSteps, uint stride)
    {
        var v = new Ints { Count = count, BandStart = start, BandEnd = end, MaxSteps = maxSteps, Stride = stride };
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
                ByteWidth = (uint)(Math.Max(1, values.Length) * stride), BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Immutable,
                MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = (uint)stride,
            }, new SubresourceData((IntPtr)ptr));
        }
    }

    private ID3D11ShaderResourceView Srv(ID3D11Buffer b, int count) => _device.CreateShaderResourceView(b, new ShaderResourceViewDescription
    {
        Format = Vortice.DXGI.Format.Unknown, ViewDimension = ShaderResourceViewDimension.Buffer,
        Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)Math.Max(1, count) },
    });

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var cs in _shaders.Values) cs.Dispose();
            _shaders.Clear();
            _intsBuf.Dispose(); _done.Dispose();
            if (_ownsDevice) { _ctx.Dispose(); _device.Dispose(); }
        }
    }
}
