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
        public int AoSamples; public float AoStrength; public float PadA0, PadA1;   // 4c
        public float IblStrength; public int SkyMode; public float TriplanarStrength, TriplanarScale;   // 4d
        public int TriplanarKind; public uint TriplanarTint; public float PadT0, PadT1;   // 4d
        public float FogDensity, FogHeightFalloff; public int VolumeSteps; public float VolumeStepsFalloff;   // 4e
        public int EmptySkip, MipW, MipH, MipBlk;   // 4f
        public int HasHdri, PadH0, PadH1, PadH2;   // 4d-ii
        public float ReflStrength; public int ReflSteps, MaxBounces, UseGgx;   // 4e-ii reflections
        public float VolNoiseAmount, VolNoiseScale, VolNoiseSpeed; public int VolNoiseOctaves;   // 4e-ii FBM
        public float VolSelfShadow; public int VolSelfShadowSteps; public float SceneTime, VolAnisotropy;   // 4e-ii + #184 Slice 3 (B)
        public uint FogColor; public float VolPaletteStrength; public int HasPalette, PaletteLen;   // #184 Slice 3 (C) + #185 slice D
        public float DofAperture, DofFocus; public int DofSamples; public int EmitAov;   // S3 (#389) DOF + S4 (#402) AOV emit
    }

    private const int ParamBytes = 464;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly object _d3dGate;

    private ID3D11ComputeShader _cs = null!;      // pinhole (aperture 0) — eager
    private ID3D11ComputeShader? _csDof;          // DOF variant — compiled lazily
    private ID3D11Buffer _paramsBuf = null!;

    private ID3D11Buffer? _heightBuf, _keepBuf;   // t0, t2 — field-sized (hn)
    private ID3D11ShaderResourceView? _heightSrv, _keepSrv;
    private int _fieldCells;

    private ID3D11Buffer? _mipBuf;                // t3 — 4f coarse max-height grid
    private ID3D11ShaderResourceView? _mipSrv;
    private int _mipCells;

    private ID3D11Buffer? _hdriBuf;               // t4 — 4d-ii flattened HDRI env
    private ID3D11ShaderResourceView? _hdriSrv;
    private int _hdriFloats;

    private ID3D11Buffer? _paletteBuf;            // t5 — #185 theme ramp LUT
    private ID3D11ShaderResourceView? _paletteSrv;
    private int _paletteLen;

    private ID3D11Buffer? _albedoBuf;             // t1 — output-sized (n)
    private ID3D11ShaderResourceView? _albedoSrv;
    private ID3D11Buffer? _colorBuf, _colorStaging;   // u0 + readback
    private ID3D11UnorderedAccessView? _colorUav;
    private int _outPixels;

    private ID3D11Buffer? _aovBuf, _aovStaging;       // u1 — S4 (#402) normal.xyz + depth
    private ID3D11UnorderedAccessView? _aovUav;
    private int _aovPixels;

    private bool _disposed;

    public double LastDispatchMs { get; private set; }
    public double LastReadbackMs { get; private set; }

    public ReliefRaymarchGpuKernel(ID3D11Device device, ID3D11DeviceContext context, object d3dGate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _d3dGate = d3dGate ?? throw new ArgumentNullException(nameof(d3dGate));

        // Compile the pinhole variant eagerly. The DOF variant is compiled lazily on
        // the first aperture-open dispatch — its lens [loop] around TracePixel makes
        // FXC compile pathologically slowly, so a non-DOF render must never pay it.
        _cs = CompileVariant(dof: false);

        _paramsBuf = _device.CreateBuffer(new BufferDescription(
            byteWidth: ParamBytes, bindFlags: BindFlags.ConstantBuffer,
            usage: ResourceUsage.Dynamic, cpuAccessFlags: CpuAccessFlags.Write));

        // Bind a 1-pixel AOV stub so u1 is never dangling on colour-only dispatches.
        EnsureAovBuffers(1);
    }

    // Compile one CSRelief variant (pinhole or DOF). Kept separate so the DOF
    // variant's slow FXC compile only happens when a render opens the aperture.
    private ID3D11ComputeShader CompileVariant(bool dof)
    {
        var hr = Compiler.Compile(
            ReliefRaymarchKernelSource.Build(dof),
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
        try { return _device.CreateComputeShader(blob.AsSpan()); }
        finally { blob.Dispose(); errBlob?.Dispose(); }
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

    // 4f — (re)allocate the coarse max-height grid SRV (t3) to hold `cells` floats.
    private void EnsureMipBuffer(int cells)
    {
        if (_mipBuf != null && _mipCells == cells) return;
        _mipSrv?.Dispose(); _mipBuf?.Dispose();
        _mipBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(cells * sizeof(float)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _mipSrv = _device.CreateShaderResourceView(_mipBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)cells },
        });
        _mipCells = cells;
    }

    // 4d-ii — (re)allocate the flattened-HDRI SRV (t4) to hold `count` uints.
    private void EnsureHdriBuffer(int count)
    {
        if (_hdriBuf != null && _hdriFloats == count) return;
        _hdriSrv?.Dispose(); _hdriBuf?.Dispose();
        _hdriBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(count * sizeof(uint)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _hdriSrv = _device.CreateShaderResourceView(_hdriBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)count },
        });
        _hdriFloats = count;
    }

    // #185 — (re)allocate the theme-ramp SRV (t5) to hold `count` packed-ARGB uints.
    private void EnsurePaletteBuffer(int count)
    {
        if (_paletteBuf != null && _paletteLen == count) return;
        _paletteSrv?.Dispose(); _paletteBuf?.Dispose();
        _paletteBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(count * sizeof(uint)),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(uint),
        });
        _paletteSrv = _device.CreateShaderResourceView(_paletteBuf, new ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)count },
        });
        _paletteLen = count;
    }

    // S4 (#402) — (re)allocate the AOV UAV (u1): 4 floats/pixel (normal.xyz + depth)
    // + a staging buffer for readback. A 1-pixel stub is bound when not emitting so
    // u1 is never dangling (gEmitAov gates the shader write).
    private void EnsureAovBuffers(int pixels)
    {
        if (_aovBuf != null && _aovPixels == pixels) return;
        _aovUav?.Dispose(); _aovBuf?.Dispose(); _aovStaging?.Dispose();
        int floats = Math.Max(1, pixels) * 4;
        _aovBuf = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(floats * sizeof(float)),
            BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = sizeof(float),
        });
        _aovUav = _device.CreateUnorderedAccessView(_aovBuf, new UnorderedAccessViewDescription
        {
            Format = Vortice.DXGI.Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)floats, Flags = 0 },
        });
        _aovStaging = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(floats * sizeof(float)),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
        _aovPixels = pixels;
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
    public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
        float[]? aovNormalXyz = null, float[]? aovDepth = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReliefRaymarchGpuKernel));
        int w = u.W, h = u.H, hn = u.Hw * u.Hh, n = w * h;
        if (w <= 0 || h <= 0) return;
        if (hbuf.Length < hn || albedo.Length < n || dst.Length < n)
            throw new ArgumentException("relief GPU input buffer too small for the uniforms");

        // S4 (#402) — emit the primary-hit normal/depth AOVs into the caller's guide
        // buffers when both are supplied and large enough; else colour only.
        bool emitAov = aovNormalXyz != null && aovDepth != null
                       && aovNormalXyz.Length >= (long)n * 3 && aovDepth.Length >= n;

        lock (_d3dGate)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureFieldBuffers(hn);
            EnsureOutputBuffers(n);
            if (emitAov) EnsureAovBuffers(n);

            UploadFloats(_heightBuf!, hbuf, hn);
            UploadColors(_albedoBuf!, albedo, n);
            UploadKeep(_keepBuf!, keep, hn);

            // 4f — build + upload the coarse max-height grid when the skip is on.
            if (u.EmptySkip != 0)
            {
                var mip = ReliefHeightMip.BuildMaxGrid(hbuf, u.Hw, u.Hh, u.MipBlk, out _, out _);
                EnsureMipBuffer(mip.Length);
                UploadFloats(_mipBuf!, mip, mip.Length);
            }

            // 4d-ii — upload the flattened HDRI env when SkyMode == Hdri resolved.
            if (u.HdriBuf != null)
            {
                EnsureHdriBuffer(u.HdriBuf.Length);
                UploadColors(_hdriBuf!, u.HdriBuf, u.HdriBuf.Length);
            }

            // #185 — upload the theme ramp when the palette map is active.
            bool hasPalette = u.VolPaletteStrength > 0.0 && u.VolPalette != null && u.VolPalette.Length >= 2;
            if (hasPalette)
            {
                EnsurePaletteBuffer(u.VolPalette!.Length);
                UploadColors(_paletteBuf!, u.VolPalette, u.VolPalette.Length);
            }

            var p = BuildBlob(in u, keep != null, emitAov);
            var mapped = _ctx.Map(_paramsBuf, 0, Vortice.Direct3D11.MapMode.WriteDiscard, MapFlags.None);
            unsafe { *(ReliefParamsBlob*)mapped.DataPointer = p; }
            _ctx.Unmap(_paramsBuf, 0);

            // Pick the variant: DOF averages lens taps, pinhole traces one ray. The
            // DOF shader compiles on first use only (slow FXC compile of its loop).
            bool dof = u.DofAperture > 0.0;
            if (dof) _csDof ??= CompileVariant(dof: true);
            _ctx.CSSetShader(dof ? _csDof! : _cs);
            _ctx.CSSetConstantBuffer(0, _paramsBuf);
            _ctx.CSSetShaderResource(0, _heightSrv);
            _ctx.CSSetShaderResource(1, _albedoSrv);
            _ctx.CSSetShaderResource(2, _keepSrv);
            _ctx.CSSetShaderResource(3, u.EmptySkip != 0 ? _mipSrv : null);
            _ctx.CSSetShaderResource(4, u.HdriBuf != null ? _hdriSrv : null);
            _ctx.CSSetShaderResource(5, hasPalette ? _paletteSrv : null);
            _ctx.CSSetUnorderedAccessView(0, _colorUav);
            _ctx.CSSetUnorderedAccessView(1, _aovUav);   // S4 — always bound (stub when not emitting)

            _ctx.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);

            _ctx.CSUnsetUnorderedAccessView(0);
            _ctx.CSUnsetUnorderedAccessView(1);
            _ctx.CSSetShaderResource(0, null);
            _ctx.CSSetShaderResource(1, null);
            _ctx.CSSetShaderResource(2, null);
            _ctx.CSSetShaderResource(3, null);
            _ctx.CSSetShaderResource(4, null);
            _ctx.CSSetShaderResource(5, null);

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

            // S4 (#402) — read back the AOV plane and split into normal.xyz + depth.
            if (emitAov)
            {
                _ctx.CopyResource(_aovStaging!, _aovBuf!);
                var am = _ctx.Map(_aovStaging!, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);
                try
                {
                    unsafe
                    {
                        float* src = (float*)am.DataPointer;
                        for (int i = 0; i < n; i++)
                        {
                            aovNormalXyz![i * 3] = src[i * 4];
                            aovNormalXyz[i * 3 + 1] = src[i * 4 + 1];
                            aovNormalXyz[i * 3 + 2] = src[i * 4 + 2];
                            aovDepth![i] = src[i * 4 + 3];
                        }
                    }
                }
                finally { _ctx.Unmap(_aovStaging!, 0); }
            }

            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            LastDispatchMs = (tDispatch - t0) * 1000.0 / freq;
            LastReadbackMs = (tEnd - tDispatch) * 1000.0 / freq;
        }
    }

    private static ReliefParamsBlob BuildBlob(in ReliefUniforms u, bool hasKeep, bool emitAov)
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
            AoSamples = u.AoSamples, AoStrength = (float)u.AoStrength, PadA0 = 0f, PadA1 = 0f,
            IblStrength = (float)u.IblStrength, SkyMode = u.SkyMode,
            TriplanarStrength = (float)u.TriplanarStrength, TriplanarScale = (float)u.TriplanarScale,
            TriplanarKind = u.TriplanarKind, TriplanarTint = u.TriplanarTint, PadT0 = 0f, PadT1 = 0f,
            FogDensity = (float)u.FogDensity, FogHeightFalloff = (float)u.FogHeightFalloff,
            VolumeSteps = u.VolumeSteps, VolumeStepsFalloff = (float)u.VolumeStepsFalloff,
            EmptySkip = u.EmptySkip, MipW = u.MipW, MipH = u.MipH, MipBlk = u.MipBlk,
            HasHdri = u.HdriBuf != null ? 1 : 0,
            ReflStrength = (float)u.ReflectionStrength, ReflSteps = u.ReflectionSteps,
            MaxBounces = u.MaxBounces, UseGgx = u.UseGgxSampling ? 1 : 0,
            VolNoiseAmount = (float)u.VolumeNoiseAmount, VolNoiseScale = (float)u.VolumeNoiseScale,
            VolNoiseSpeed = (float)u.VolumeNoiseSpeed, VolNoiseOctaves = u.VolumeNoiseOctaves,
            VolSelfShadow = (float)u.VolumeSelfShadow, VolSelfShadowSteps = u.VolumeSelfShadowSteps,
            SceneTime = (float)u.SceneTime, VolAnisotropy = (float)u.VolAnisotropy,
            FogColor = u.FogColor,
            VolPaletteStrength = (float)u.VolPaletteStrength,
            HasPalette = (u.VolPaletteStrength > 0.0 && u.VolPalette != null && u.VolPalette.Length >= 2) ? 1 : 0,
            PaletteLen = u.VolPalette?.Length ?? 0,
            DofAperture = (float)u.DofAperture, DofFocus = (float)u.DofFocus, DofSamples = u.DofSamples,
            EmitAov = emitAov ? 1 : 0,
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
        try { _mipSrv?.Dispose(); } catch { }
        try { _hdriSrv?.Dispose(); } catch { }
        try { _paletteSrv?.Dispose(); } catch { }
        try { _albedoSrv?.Dispose(); } catch { }
        try { _colorUav?.Dispose(); } catch { }
        try { _heightBuf?.Dispose(); } catch { }
        try { _keepBuf?.Dispose(); } catch { }
        try { _mipBuf?.Dispose(); } catch { }
        try { _hdriBuf?.Dispose(); } catch { }
        try { _paletteBuf?.Dispose(); } catch { }
        try { _albedoBuf?.Dispose(); } catch { }
        try { _colorBuf?.Dispose(); } catch { }
        try { _colorStaging?.Dispose(); } catch { }
        try { _aovUav?.Dispose(); } catch { }
        try { _aovBuf?.Dispose(); } catch { }
        try { _aovStaging?.Dispose(); } catch { }
        try { _paramsBuf?.Dispose(); } catch { }
        try { _cs?.Dispose(); } catch { }
        try { _csDof?.Dispose(); } catch { }
    }
}
