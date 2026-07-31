// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefRaymarchGpuKernel.cs — Relief 3D Slice 3b (#160).
//
// D3D11 compute-shader dispatch of the Relief 3D sphere-trace
// (ReliefRaymarchKernelSource, entry CSRelief). The GPU twin of
// HeightfieldRaymarch2D's oblique raymarch, restricted to the Slice-3 shader
// scope (flat three-light Lambert + ambient + gradient sky). Correctness is
// proven against the CPU parity twin ReliefRaymarchGpu.RenderCpuMirror by the
// --reliefgpuraymarch gate; the two share the ReliefUniforms cbuffer twin.
//
// Buffers — b0 = ReliefParams cbuffer (256 B, 16 float4 rows); t0 = height
// (StructuredBuffer<float>, one/cell); t1 = albedo (StructuredBuffer<uint>,
// packed ARGB, one/pixel); t2 = cull mask (StructuredBuffer<uint>, one/cell,
// 0 = culled — always bound; gHasKeep gates the read); u0 = packed-ARGB output.
//
// Thread-affine like MandelbrotGpuKernel: one caller drives Run from the calc
// thread under the shared D3D gate.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering;

/// <summary>D3D11 compute dispatch of the Relief 3D raymarch kernel (#160).
/// See the file header for the buffer bindings and the parity contract.</summary>
[SupportedOSPlatform("windows")]
public sealed class ReliefRaymarchGpuKernel : IDisposable, FracturingFog.Rendering.Lighting.IReliefRaymarchKernel
{
    // ReliefParams cbuffer twin. 16 float4 rows (256 B): every HLSL float3 is
    // followed by a scalar that fills its row, and every float3 starts a fresh
    // row, so a flat sequential struct of 64 4-byte fields matches the cbuffer
    // byte-for-byte. Field order MUST track ReliefRaymarchKernelSource.Hlsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReliefParamsBlob
    {
        public int W, H, Hw, Hh;
        public float Sy, Aspect, InvLip; public int Ortho;
        public float CamX, CamY, CamZ, TanHalf;
        public float FwdX, FwdY, FwdZ, OrthoHalfV;
        public float RightX, RightY, RightZ, Eps0;
        public float UpX, UpY, UpZ, PixelAngle;
        public float Bx, By, Bz; public int MaxSteps;
        public int GroundPlane, ShowSky, Isolate, HasKeep;
        public float L0x, L0y, L0z, I0; public float C0r, C0g, C0b, Pad0;
        public float L1x, L1y, L1z, I1; public float C1r, C1g, C1b, Pad1;
        public float L2x, L2y, L2z, I2; public float C2r, C2g, C2b, Pad2;
        public float Ambient, FloorBx, FloorBz, Pad3;
        public uint BgTop, BgBottom, FloorAlbedo, DropColor;
        public float SpecStrength, Roughness, Metallic, PadS;   // 4a
        public int ShadowSteps; public float ShadowSoftK; public int ShadowMask; public float PadSh;   // 4b
    }

    private const int ParamBytes = 288;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly object _d3dGate;

    private ID3D11ComputeShader _cs = null!;
    private ID3D11Buffer _paramsBuf = null!;

    private ID3D11Buffer? _heightBuf, _keepBuf;   // t0, t2 — field-sized (hn)
    private ID3D11ShaderResourceView? _heightSrv, _keepSrv;
    private int _fieldCells;

    private ID3D11Buffer? _albedoBuf;             // t1 — output-sized (n)
    private ID3D11ShaderResourceView? _albedoSrv;
    private ID3D11Buffer? _colorBuf, _colorStaging;   // u0 + readback
    private ID3D11UnorderedAccessView? _colorUav;
    private int _outPixels;

    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }

    public ReliefRaymarchGpuKernel(ID3D11Device device, ID3D11DeviceContext context, object d3dGate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _d3dGate = d3dGate ?? throw new ArgumentNullException(nameof(d3dGate));

        var hr = Compiler.Compile(
            ReliefRaymarchKernelSource.Build(),
            entryPoint: ReliefRaymarchKernelSource.EntryPoint,
            sourceName: "ReliefRaymarch.hlsl",
            profile: "cs_5_0",
            out var blob, out var errBlob);
        if (hr.Failure || blob == null)
        {
            string msg = errBlob?.AsString() ?? hr.ToString();
            errBlob?.Dispose();
            throw new InvalidOperationException($"ReliefRaymarchGpuKernel: HLSL compile failed — {msg}");
        }
        try { _cs = _device.CreateComputeShader(blob.AsSpan()); }
        finally { blob.Dispose(); errBlob?.Dispose(); }

        _paramsBuf = _device.CreateBuffer(new BufferDescription(
            byteWidth: ParamBytes, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));
    }

    private void EnsureFieldBuffers(int cells)
    {
        if (_heightBuf != null && _fieldCells == cells) return;
        _heightSrv?.Dispose(); _keepSrv?.Dispose();
        _heightBuf?.Dispose(); _keepBuf?.Dispose();

        var fDesc = new BufferDescription
        {
            ByteWidth = (uint)(cells * sizeof(float)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        };
        _heightBuf = _device.CreateBuffer(fDesc);
        var uDesc = fDesc with { StructureByteStride = sizeof(uint) };
        _keepBuf = _device.CreateBuffer(uDesc);

        var srv = new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)cells },
        };
        _heightSrv = _device.CreateShaderResourceView(_heightBuf, srv);
        _keepSrv = _device.CreateShaderResourceView(_keepBuf, srv);
        _fieldCells = cells;
    }

    private void EnsureOutputBuffers(int n)
    {
        if (_albedoBuf != null && _outPixels == n) return;
        _albedoSrv?.Dispose(); _colorUav?.Dispose();
        _albedoBuf?.Dispose(); _colorBuf?.Dispose(); _colorStaging?.Dispose();

        _albedoBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _albedoSrv = _device.CreateShaderResourceView(_albedoBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)n },
        });

        _colorBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _colorUav = _device.CreateUnorderedAccessView(_colorBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, Flags = 0 },
        });
        _colorStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(n * sizeof(uint)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
        _outPixels = n;
    }

    /// <summary>Dispatch the relief raymarch for <paramref name="u"/> and read
    /// the packed-ARGB result into <paramref name="dst"/> (length ≥ W·H). The
    /// compressed height field (<paramref name="hbuf"/>, W·H = Hw·Hh cells),
    /// optional cull mask (<paramref name="keep"/>) and albedo are uploaded to
    /// the GPU each call. The GPU twin of
    /// <see cref="ReliefRaymarchGpu.RenderCpuMirror"/>.</summary>
    public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReliefRaymarchGpuKernel));
        int w = u.W, h = u.H, hn = u.Hw * u.Hh, n = w * h;
        if (w <= 0 || h <= 0) return;
        if (hbuf.Length < hn || albedo.Length < n || dst.Length < n)
            throw new ArgumentException("relief GPU input buffer too small for the uniforms");

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureFieldBuffers(hn);
            EnsureOutputBuffers(n);

            UploadFloats(_heightBuf!, hbuf, hn);
            UploadColors(_albedoBuf!, albedo, n);
            UploadKeep(_keepBuf!, keep, hn);

            var p = BuildBlob(in u, keep != null);
            var mapped = _ctx.Map(_paramsBuf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
            unsafe { *(ReliefParamsBlob*)mapped.DataPointer = p; }
            _ctx.Unmap(_paramsBuf, 0);

            _ctx.CSSetShader(_cs);
            _ctx.CSSetConstantBuffer(0, _paramsBuf);
            _ctx.CSSetShaderResource(0, _heightSrv);
            _ctx.CSSetShaderResource(1, _albedoSrv);
            _ctx.CSSetShaderResource(2, _keepSrv);
            _ctx.CSSetUnorderedAccessView(0, _colorUav);

            _ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);

            _ctx.CSUnsetUnorderedAccessView(0);
            _ctx.CSSetShaderResource(0, null);
            _ctx.CSSetShaderResource(1, null);
            _ctx.CSSetShaderResource(2, null);

            _ctx.CopyResource(_colorStaging!, _colorBuf!);
            long tDispatch = System.Diagnostics.Stopwatch.GetTimestamp();

            var map = _ctx.Map(_colorStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
            try
            {
                unsafe
                {
                    uint* src = (uint*)map.DataPointer;
                    fixed (uint* d = dst)
                        Buffer.MemoryCopy(src, d, (long)n * sizeof(uint), (long)n * sizeof(uint));
                }
            }
            finally { _ctx.Unmap(_colorStaging!, 0); }

            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
            LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
        }
    }

    private static ReliefParamsBlob BuildBlob(in ReliefUniforms u, bool hasKeep)
    {
        var c = u.Cam;
        return new ReliefParamsBlob
        {
            W = u.W, H = u.H, Hw = u.Hw, Hh = u.Hh,
            Sy = (float)u.Sy, Aspect = (float)u.Aspect, InvLip = (float)u.InvLip,
            Ortho = c.Ortho ? 1 : 0,
            CamX = (float)c.CamX, CamY = (float)c.CamY, CamZ = (float)c.CamZ, TanHalf = (float)c.TanHalf,
            FwdX = (float)c.FX, FwdY = (float)c.FY, FwdZ = (float)c.FZ, OrthoHalfV = (float)c.OrthoHalfV,
            RightX = (float)c.RX, RightY = 0f, RightZ = (float)c.RZ, Eps0 = (float)c.Eps0,
            UpX = (float)c.UX, UpY = (float)c.UY, UpZ = (float)c.UZ, PixelAngle = (float)c.PixelAngle,
            Bx = (float)c.Bx, By = (float)c.By, Bz = (float)c.Bz, MaxSteps = c.MaxSteps,
            GroundPlane = c.GroundPlane ? 1 : 0,
            ShowSky = u.ShowSky ? 1 : 0, Isolate = u.Isolate ? 1 : 0, HasKeep = hasKeep ? 1 : 0,
            L0x = (float)u.L0x, L0y = (float)u.L0y, L0z = (float)u.L0z, I0 = (float)u.I0,
            C0r = (float)u.C0r, C0g = (float)u.C0g, C0b = (float)u.C0b, Pad0 = 0f,
            L1x = (float)u.L1x, L1y = (float)u.L1y, L1z = (float)u.L1z, I1 = (float)u.I1,
            C1r = (float)u.C1r, C1g = (float)u.C1g, C1b = (float)u.C1b, Pad1 = 0f,
            L2x = (float)u.L2x, L2y = (float)u.L2y, L2z = (float)u.L2z, I2 = (float)u.I2,
            C2r = (float)u.C2r, C2g = (float)u.C2g, C2b = (float)u.C2b, Pad2 = 0f,
            Ambient = (float)u.Ambient, FloorBx = (float)c.FloorBx, FloorBz = (float)c.FloorBz, Pad3 = 0f,
            BgTop = u.BgTop, BgBottom = u.BgBottom, FloorAlbedo = u.FloorAlbedo, DropColor = u.DropColor,
            SpecStrength = (float)u.SpecStrength, Roughness = (float)u.Roughness, Metallic = (float)u.Metallic, PadS = 0f,
            ShadowSteps = u.ShadowSteps, ShadowSoftK = (float)u.ShadowSoftK, ShadowMask = u.ShadowLightMask, PadSh = 0f,
        };
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

    // keep byte[] (0/1) → uint per cell. Null → all 1 (gHasKeep gates the read
    // anyway; a valid buffer is still bound so t2 is never dangling).
    private void UploadKeep(ID3D11Buffer buf, byte[]? keep, int count)
    {
        var m = _ctx.Map(buf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
        unsafe
        {
            uint* dst = (uint*)m.DataPointer;
            if (keep != null) { for (int i = 0; i < count; i++) dst[i] = keep[i]; }
            else { for (int i = 0; i < count; i++) dst[i] = 1u; }
        }
        _ctx.Unmap(buf, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _heightSrv?.Dispose(); } catch { }
        try { _keepSrv?.Dispose(); } catch { }
        try { _albedoSrv?.Dispose(); } catch { }
        try { _colorUav?.Dispose(); } catch { }
        try { _heightBuf?.Dispose(); } catch { }
        try { _keepBuf?.Dispose(); } catch { }
        try { _albedoBuf?.Dispose(); } catch { }
        try { _colorBuf?.Dispose(); } catch { }
        try { _colorStaging?.Dispose(); } catch { }
        try { _paramsBuf?.Dispose(); } catch { }
        try { _cs?.Dispose(); } catch { }
    }
}
