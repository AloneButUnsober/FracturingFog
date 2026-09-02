// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FroxelGpuKernel.cs — GPU froxel volume compute pass (roadmap S6, #389 / #408).
//
// D3D11 two-pass compute dispatch of FroxelKernelSource: CSFroxelIntegrate
// populates + integrates the camera-framed froxel volume (one thread per column),
// then CSFroxelComposite composites it over the fog-free beauty by per-pixel
// world depth (one thread per pixel). The GPU twin of the pure-CPU froxel pass
// (FroxelVolumePass.Populate + FroxelCameraVolume.CompositeWorldDepth); the
// --froxelgpu gate diffs a dispatch of this against that CPU pass over identical
// inputs (both driven by the SAME FroxelGrid + FroxelMedium via FroxelGpuUniforms).
//
// Buffers — b0 = FroxelParams cbuffer (224 B, 14 float4 rows); the volume buffer
// (float4/cell) is written as u0 by the integrate pass then read as t2 by the
// composite pass; t0 = beauty (uint/pixel), t1 = worldDepth (float/pixel),
// u0 = output (uint/pixel) in the composite pass.
//
// Thread-affine like ReliefRaymarchGpuKernel: one caller drives Composite from
// the calc thread under the shared D3D gate.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering;

/// <summary>D3D11 compute dispatch of the froxel volume pass (#408). See the file
/// header for the buffer bindings and the parity contract.</summary>
[SupportedOSPlatform("windows")]
public sealed class FroxelGpuKernel : IDisposable, IFroxelVolumeKernel
{
    // FroxelParams cbuffer twin. 14 float4 rows (224 B). Field order MUST track
    // FroxelKernelSource.Hlsl's cbuffer.
    [StructLayout(LayoutKind.Sequential)]
    private struct FroxelParamsBlob
    {
        public int Nx, Ny, Nz, W;
        public int H; public float Near, Far, Extent;
        public float BaseDensity, Extinction, Anisotropy, NoiseAmount;
        public float NoiseScale; public int NoiseOctaves; public float ViewX, ViewY;
        public float ViewZ; public int NumLights; public float Feedback; public int HistoryValid;
        public int Type0; public uint Color0; public float I0, Range0;
        public float Dir0x, Dir0y, Dir0z, Inner0;
        public float Pos0x, Pos0y, Pos0z, Outer0;
        public int Type1; public uint Color1; public float I1, Range1;
        public float Dir1x, Dir1y, Dir1z, Inner1;
        public float Pos1x, Pos1y, Pos1z, Outer1;
        public int Type2; public uint Color2; public float I2, Range2;
        public float Dir2x, Dir2y, Dir2z, Inner2;
        public float Pos2x, Pos2y, Pos2z, Outer2;
    }

    private const int ParamBytes = 224;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly object _d3dGate;

    private ID3D11ComputeShader _csIntegrate = null!;
    private ID3D11ComputeShader _csComposite = null!;
    private ID3D11Buffer _paramsBuf = null!;

    private ID3D11Buffer? _volumeBuf;                 // float4/cell — UAV (integrate) + SRV (composite)
    private ID3D11UnorderedAccessView? _volumeUav;
    private ID3D11ShaderResourceView? _volumeSrv;
    private int _volumeCells;

    // S6 #408 temporal — persistent device-side previous-frame PRE-integration
    // scatter+ext grid (float4/cell), the GPU twin of FroxelHistory. Survives
    // across Composite calls (kernel is host-owned, one per render host), keyed by
    // the grid identity so a camera move that changes the slab re-seeds cleanly.
    private ID3D11Buffer? _historyBuf;                // float4/cell — RW (u1) in the integrate pass
    private ID3D11UnorderedAccessView? _historyUav;
    private int _historyCells;
    private long _historyKey;
    private bool _historyValid;

    private ID3D11Buffer? _beautyBuf, _depthBuf;      // t0, t1 — output-sized
    private ID3D11ShaderResourceView? _beautySrv, _depthSrv;
    private ID3D11Buffer? _outBuf, _outStaging;       // u0 + readback
    private ID3D11UnorderedAccessView? _outUav;
    private int _outPixels;

    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }

    public FroxelGpuKernel(ID3D11Device device, ID3D11DeviceContext context, object d3dGate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _d3dGate = d3dGate ?? throw new ArgumentNullException(nameof(d3dGate));

        _csIntegrate = CompileEntry(FroxelKernelSource.IntegrateEntry);
        _csComposite = CompileEntry(FroxelKernelSource.CompositeEntry);

        _paramsBuf = _device.CreateBuffer(new BufferDescription(
            byteWidth: ParamBytes, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
    }

    private ID3D11ComputeShader CompileEntry(string entry)
        => D3DShaderCache.CompileOrLoad(       // #456 — machine-cached FXC bytecode
            _device,
            FroxelKernelSource.Build(),
            entryPoint: entry,
            profile: "cs_5_0",
            sourceName: "Froxel.hlsl",
            errorLabel: $"FroxelGpuKernel ({entry})");

    private void EnsureVolumeBuffer(int cells)
    {
        if (_volumeBuf != null && _volumeCells == cells) return;
        _volumeUav?.Dispose(); _volumeSrv?.Dispose(); _volumeBuf?.Dispose();
        _volumeBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(cells * 4 * sizeof(float)),   // float4/cell
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 4 * sizeof(float),
        });
        _volumeUav = _device.CreateUnorderedAccessView(_volumeBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)cells, Flags = 0 },
        });
        _volumeSrv = _device.CreateShaderResourceView(_volumeBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)cells },
        });
        _volumeCells = cells;
    }

    // S6 #408 — (re)create the temporal history buffer when the cell count changes.
    // A size change also drops validity (the old contents no longer map), forcing a
    // clean re-seed that frame. Returns false when no buffer could be provided.
    private void EnsureHistoryBuffer(int cells)
    {
        if (_historyBuf != null && _historyCells == cells) return;
        _historyUav?.Dispose(); _historyBuf?.Dispose();
        _historyBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(cells * 4 * sizeof(float)),   // float4/cell
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = 4 * sizeof(float),
        });
        _historyUav = _device.CreateUnorderedAccessView(_historyBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)cells, Flags = 0 },
        });
        _historyCells = cells;
        _historyValid = false;   // fresh buffer → re-seed
    }

    private void EnsureOutputBuffers(int n)
    {
        if (_outBuf != null && _outPixels == n) return;
        _beautySrv?.Dispose(); _depthSrv?.Dispose(); _outUav?.Dispose();
        _beautyBuf?.Dispose(); _depthBuf?.Dispose(); _outBuf?.Dispose(); _outStaging?.Dispose();

        var srvBuf = new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        };
        _beautyBuf = _device.CreateBuffer(srvBuf);
        _depthBuf = _device.CreateBuffer(srvBuf with { StructureByteStride = sizeof(float) });

        var srvView = new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)n },
        };
        _beautySrv = _device.CreateShaderResourceView(_beautyBuf, srvView);
        _depthSrv = _device.CreateShaderResourceView(_depthBuf, srvView);

        _outBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _outUav = _device.CreateUnorderedAccessView(_outBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        });
        _outStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
        _outPixels = n;
    }

    /// <summary>Populate + integrate the froxel volume for <paramref name="u"/>
    /// and composite it over <paramref name="beauty"/> by <paramref name="worldDepth"/>,
    /// writing the packed-ARGB result into <paramref name="dst"/>. The GPU twin of
    /// <see cref="FroxelCameraVolume.Apply"/>.</summary>
    public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst)
        => Composite(in u, beauty, worldDepth, w, h, dst, feedback: 0.0);

    /// <summary>Temporal overload (roadmap S6, #408). When <paramref name="feedback"/>
    /// &gt; 0 the pre-integration scatter + extinction is exponentially blended with
    /// this kernel's persistent device-side history for the SAME grid identity, then
    /// stored back — the GPU twin of <see cref="FroxelHistory.BlendAndStore"/>. The
    /// grid key is derived from the uniforms; a change (camera move) re-seeds cleanly.
    /// <paramref name="feedback"/> &lt;= 0 is byte-identical to the single-frame
    /// <see cref="Composite(in FroxelGpuUniforms,uint[],float[],int,int,uint[])"/>.</summary>
    public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst,
        double feedback)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FroxelGpuKernel));
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        if (worldDepth == null) throw new ArgumentNullException(nameof(worldDepth));
        if (dst == null) throw new ArgumentNullException(nameof(dst));
        int n = w * h;
        if (w <= 0 || h <= 0) return;
        if (beauty.Length < n || worldDepth.Length < n || dst.Length < n)
            throw new ArgumentException("froxel GPU buffer too small for w*h");

        var g = u.Grid;
        int cells = g.DimX * g.DimY * g.DimZ;

        // Clamp to match FroxelHistory.BlendAndStore (keep some current in).
        double fb = feedback;
        if (fb < 0.0) fb = 0.0; else if (fb > 0.999) fb = 0.999;
        bool temporal = fb > 0.0;
        long key = temporal ? FroxelHistory.GridKey(g) : 0;

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureVolumeBuffer(cells);
            EnsureOutputBuffers(n);

            // History validity: only reuse when temporal is on AND the buffer already
            // holds the previous frame for THIS grid. EnsureHistoryBuffer drops
            // validity on a size change; a key change invalidates here.
            bool historyValid = false;
            if (temporal)
            {
                EnsureHistoryBuffer(cells);
                historyValid = _historyValid && _historyKey == key && _historyCells >= cells;
            }

            UploadColors(_beautyBuf!, beauty, n);
            UploadFloats(_depthBuf!, worldDepth, n);

            var p = BuildBlob(in u, w, h, (float)fb, historyValid ? 1 : 0);
            var mapped = _ctx.Map(_paramsBuf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
            unsafe { *(FroxelParamsBlob*)mapped.DataPointer = p; }
            _ctx.Unmap(_paramsBuf, 0);

            _ctx.CSSetConstantBuffer(0, _paramsBuf);

            // Pass 1 — populate + integrate every column into the volume (u0). When
            // temporal, the history grid rides u1 (read previous, write blended).
            _ctx.CSSetShader(_csIntegrate);
            _ctx.CSSetUnorderedAccessView(0, _volumeUav);
            if (temporal) _ctx.CSSetUnorderedAccessView(1, _historyUav);
            _ctx.Dispatch((uint)((g.DimX + 7) / 8), (uint)((g.DimY + 7) / 8), 1);
            _ctx.CSUnsetUnorderedAccessView(0);
            if (temporal)
            {
                _ctx.CSUnsetUnorderedAccessView(1);
                // The blended scatter+ext is now the previous frame for the next call.
                _historyKey = key;
                _historyValid = true;
            }

            // Pass 2 — composite over the beauty by per-pixel depth. The volume is
            // now read as an SRV (t2); beauty (t0) + depth (t1) feed the blend.
            _ctx.CSSetShader(_csComposite);
            _ctx.CSSetShaderResource(0, _beautySrv);
            _ctx.CSSetShaderResource(1, _depthSrv);
            _ctx.CSSetShaderResource(2, _volumeSrv);
            _ctx.CSSetUnorderedAccessView(0, _outUav);
            _ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);

            _ctx.CSUnsetUnorderedAccessView(0);
            _ctx.CSSetShaderResource(0, null);
            _ctx.CSSetShaderResource(1, null);
            _ctx.CSSetShaderResource(2, null);

            _ctx.CopyResource(_outStaging!, _outBuf!);
            long tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();

            var map = _ctx.Map(_outStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    uint* src = (uint*)map.DataPointer;
                    fixed (uint* d = dst)
                        Buffer.MemoryCopy(src, d, (long)n * sizeof(uint), (long)n * sizeof(uint));
                }
            }
            finally { _ctx.Unmap(_outStaging!, 0); }

            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
            LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
        }
    }

    private static FroxelParamsBlob BuildBlob(in FroxelGpuUniforms u, int w, int h, float feedback, int historyValid)
    {
        var g = u.Grid;
        var m = u.Medium;
        var lights = m.Lights;
        int nl = lights?.Length ?? 0;

        FroxelLight L0 = nl > 0 ? lights![0] : default;
        FroxelLight L1 = nl > 1 ? lights![1] : default;
        FroxelLight L2 = nl > 2 ? lights![2] : default;

        return new FroxelParamsBlob
        {
            Nx = g.DimX, Ny = g.DimY, Nz = g.DimZ, W = w,
            H = h, Near = (float)g.Near, Far = (float)g.Far, Extent = (float)m.WorldExtent,
            BaseDensity = (float)m.BaseDensity, Extinction = (float)m.Extinction,
            Anisotropy = (float)m.Anisotropy, NoiseAmount = (float)m.NoiseAmount,
            NoiseScale = (float)m.NoiseScale, NoiseOctaves = m.NoiseOctaves,
            ViewX = (float)m.ViewDx, ViewY = (float)m.ViewDy,
            ViewZ = (float)m.ViewDz, NumLights = nl, Feedback = feedback, HistoryValid = historyValid,
            Type0 = L0.Type, Color0 = L0.Color, I0 = (float)L0.Intensity, Range0 = (float)L0.Range,
            Dir0x = (float)L0.Lx, Dir0y = (float)L0.Ly, Dir0z = (float)L0.Lz, Inner0 = (float)L0.InnerCos,
            Pos0x = (float)L0.PosX, Pos0y = (float)L0.PosY, Pos0z = (float)L0.PosZ, Outer0 = (float)L0.OuterCos,
            Type1 = L1.Type, Color1 = L1.Color, I1 = (float)L1.Intensity, Range1 = (float)L1.Range,
            Dir1x = (float)L1.Lx, Dir1y = (float)L1.Ly, Dir1z = (float)L1.Lz, Inner1 = (float)L1.InnerCos,
            Pos1x = (float)L1.PosX, Pos1y = (float)L1.PosY, Pos1z = (float)L1.PosZ, Outer1 = (float)L1.OuterCos,
            Type2 = L2.Type, Color2 = L2.Color, I2 = (float)L2.Intensity, Range2 = (float)L2.Range,
            Dir2x = (float)L2.Lx, Dir2y = (float)L2.Ly, Dir2z = (float)L2.Lz, Inner2 = (float)L2.InnerCos,
            Pos2x = (float)L2.PosX, Pos2y = (float)L2.PosY, Pos2z = (float)L2.PosZ, Outer2 = (float)L2.OuterCos,
        };
    }

    private void UploadColors(ID3D11Buffer buf, uint[] src, int count)
    {
        var m = _ctx.Map(buf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            uint* dst = (uint*)m.DataPointer;
            fixed (uint* s = src) { for (int i = 0; i < count; i++) dst[i] = s[i]; }
        }
        _ctx.Unmap(buf, 0);
    }

    private void UploadFloats(ID3D11Buffer buf, float[] src, int count)
    {
        var m = _ctx.Map(buf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            float* dst = (float*)m.DataPointer;
            fixed (float* s = src) { for (int i = 0; i < count; i++) dst[i] = s[i]; }
        }
        _ctx.Unmap(buf, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _volumeUav?.Dispose(); } catch { }
        try { _volumeSrv?.Dispose(); } catch { }
        try { _volumeBuf?.Dispose(); } catch { }
        try { _historyUav?.Dispose(); } catch { }
        try { _historyBuf?.Dispose(); } catch { }
        try { _beautySrv?.Dispose(); } catch { }
        try { _depthSrv?.Dispose(); } catch { }
        try { _beautyBuf?.Dispose(); } catch { }
        try { _depthBuf?.Dispose(); } catch { }
        try { _outUav?.Dispose(); } catch { }
        try { _outBuf?.Dispose(); } catch { }
        try { _outStaging?.Dispose(); } catch { }
        try { _paramsBuf?.Dispose(); } catch { }
        try { _csIntegrate?.Dispose(); } catch { }
        try { _csComposite?.Dispose(); } catch { }
    }
}
