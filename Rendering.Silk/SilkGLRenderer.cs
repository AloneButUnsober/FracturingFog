// SilkGLRenderer.cs
// Cross-platform OpenGL 3.3 core implementation of IFractalRenderer.
// Mirrors the DirectXRenderer geometry — a single SV_VertexID-style
// full-screen triangle textured with the CPU-side BGRA colour buffer.
//
// Context-agnostic design: the renderer takes a live Silk.NET GL instance and
// two delegate hooks (make-current / swap-buffers). The host is responsible
// for creating the GL context against the platform-native window
// (WGL on Windows, GLX on X11, CGL on macOS) and routing present back to the
// platform's swap-chain. This keeps Rendering.Silk free of P/Invoke per
// platform and lets the same renderer drop into Avalonia.OpenGL,
// Silk.NET.Windowing IWindow, or a foreign HWND surface unchanged.

using System;
using Silk.NET.OpenGL;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering.Silk;

public sealed class SilkGLRenderer : IFractalRenderer
{
    // GLSL mirror of the DX HLSL. Same SV_VertexID trick: three vertex IDs
    // span [-1,1] NDC; UV interpolates 0..1 across the visible screen quad.
    // No vertex buffer needed.
    //
    // Phase X.4 / Slice 4.3 — the #version line is injected at compile time
    // by CreateProgram so the renderer can retry at #version 410 core when
    // 330 is rejected (some macOS GL stacks accept only 410 forward-compat).
    // The shader bodies stay identical between the two versions; the GLSL
    // features used (in/out, texture(), vec2 swizzles) are the same on 330
    // and 410 core.
    private const string VertexShaderBody = @"
out vec2 vUv;
void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = uv;
    gl_Position = vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}
";

    // S-X6 (2026-06-23) — .bgra swizzle. Upload path packs CPU buffer as
    // GL_RGBA + GL_UNSIGNED_BYTE because GL_BGRA is deprecated in 3.3 core
    // (NVIDIA's strict core context rejects it with GL_INVALID_OPERATION).
    // Source layout from MandelbrotCalculator is BGRA bytes per uint, so
    // stored texel R = source B, B = source R; swizzle here restores order.
    private const string FragmentShaderBody = @"
in vec2 vUv;
uniform sampler2D uTex;
out vec4 fColor;
void main()
{
    fColor = texture(uTex, vUv).bgra;
}
";

    private static readonly string[] s_glslVersions = { "#version 330 core\n", "#version 410 core\n" };

    private readonly GL _gl;
    private readonly Action _makeCurrent;
    private readonly Action _swap;
    // S-X6 (2026-06-23) — releases ctx from the calling thread. WGL/GLX/EGL
    // require ctx be unbound on the prior owner thread before another thread
    // can acquire it; without this, calc-thread UpdateTexture/Render fail
    // silently with GL_INVALID_OPERATION because ctx is still pinned to the
    // UI ctor thread. Adapters supply real impls; null = no-op (single-thread
    // hosts pay no cost).
    private readonly Action _releaseCurrent;

    private uint _vao;
    private uint _program;
    private uint _tex;

    private int _width;
    private int _height;
    private int _texWidth;
    private int _texHeight;
    private bool _disposed;

    // S-X6 diag — set FF_GL_DEBUG=1 to print GL errors + first-frame stats.
    // Default off so production builds stay silent.
    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("FF_GL_DEBUG"), "1", StringComparison.Ordinal);
    private int _uploadCount;
    private int _renderCount;

    public string RendererDescription { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// GL swap interval is owned by the swap-chain init (host's window
    /// creation), not per-Render. This property tracks intent so callers
    /// see consistent state, but flipping at runtime does not change the
    /// GL pacing — the host must call wglSwapIntervalEXT / glXSwapIntervalEXT
    /// to act on it. Acceptable for now: the GL backend is not the hot
    /// video-record path on Windows (DX11/DX12 are).
    /// </remarks>
    public bool VSync { get; set; } = true;

    public SilkGLRenderer(GL gl, int width, int height,
                         Action makeCurrent, Action swap,
                         Action? releaseCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(makeCurrent);
        ArgumentNullException.ThrowIfNull(swap);

        _gl = gl;
        _makeCurrent = makeCurrent;
        _swap = swap;
        _releaseCurrent = releaseCurrent ?? (() => { });
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);

        _makeCurrent();

        string vendor   = SafeGetString(StringName.Vendor);
        string renderer = SafeGetString(StringName.Renderer);
        string version  = SafeGetString(StringName.Version);
        RendererDescription = $"OpenGL ({renderer.Trim()} — {vendor.Trim()} — {version.Trim()})";

        CreateGeometry();
        CheckGlError("after CreateGeometry");
        CreateProgram();
        CheckGlError("after CreateProgram");
        CreateTextureObject();
        CheckGlError("after CreateTextureObject");

        if (s_diag)
        {
            Console.Error.WriteLine($"[SilkGL] init {_width}x{_height} vao={_vao} program={_program} tex={_tex}");
            Console.Error.WriteLine($"[SilkGL] {RendererDescription}");
        }

        // S-X6 — drop ctx from ctor thread so calc thread can wglMakeCurrent.
        _releaseCurrent();
    }

    private void CheckGlError(string stage)
    {
        if (!s_diag) return;
        GLEnum err;
        while ((err = _gl.GetError()) != GLEnum.NoError)
            Console.Error.WriteLine($"[SilkGL] GL error 0x{(int)err:X4} ({err}) — {stage}");
    }

    private string SafeGetString(StringName name)
    {
        try { unsafe { byte* p = _gl.GetString(name); return p == null ? "?" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)p) ?? "?"; } }
        catch { return "?"; }
    }

    private void CreateGeometry()
    {
        // Empty VAO bound for draws — gl_VertexID alone drives positions.
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
    }

    private void CreateProgram()
    {
        // Phase X.4 / Slice 4.3 — try every supported #version in order. macOS
        // GL stacks that reject 330 typically accept 410 core; Linux + Windows
        // accept 330 universally so the retry path is a no-op there.
        string? lastLog = null;
        foreach (var version in s_glslVersions)
        {
            if (TryCreateProgramAtVersion(version, out lastLog)) return;
        }
        throw new InvalidOperationException(
            "GL shader compile / link failed at every supported #version " +
            $"({string.Join(", ", s_glslVersions).Trim()}): {lastLog ?? "(no log)"}");
    }

    private bool TryCreateProgramAtVersion(string versionLine, out string? failLog)
    {
        failLog = null;
        uint vs = TryCompileShader(ShaderType.VertexShader, versionLine + VertexShaderBody, out failLog);
        if (vs == 0) return false;

        uint fs = TryCompileShader(ShaderType.FragmentShader, versionLine + FragmentShaderBody, out failLog);
        if (fs == 0)
        {
            _gl.DeleteShader(vs);
            return false;
        }

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, GLEnum.LinkStatus, out int linked);
        if (linked == 0)
        {
            failLog = _gl.GetProgramInfoLog(program);
            _gl.DeleteProgram(program);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            return false;
        }

        _gl.DetachShader(program, vs);
        _gl.DetachShader(program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        _program = program;
        _gl.UseProgram(_program);
        int loc = _gl.GetUniformLocation(_program, "uTex");
        if (loc >= 0) _gl.Uniform1(loc, 0);
        return true;
    }

    private uint TryCompileShader(ShaderType type, string source, out string? failLog)
    {
        uint id = _gl.CreateShader(type);
        _gl.ShaderSource(id, source);
        _gl.CompileShader(id);
        _gl.GetShader(id, GLEnum.CompileStatus, out int ok);
        if (ok == 0)
        {
            failLog = $"({type}) {_gl.GetShaderInfoLog(id)}";
            _gl.DeleteShader(id);
            return 0;
        }
        failLog = null;
        return id;
    }

    private void CreateTextureObject()
    {
        _tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    public unsafe void UpdateTexture(uint[] colorBuffer, int width, int height)
    {
        if (_disposed) return;
        if (colorBuffer == null) throw new ArgumentNullException(nameof(colorBuffer));
        if (colorBuffer.Length < width * height)
            throw new ArgumentException("colorBuffer too small for given dimensions", nameof(colorBuffer));

        _makeCurrent();
        try
        {
        _gl.BindTexture(TextureTarget.Texture2D, _tex);

        // Source layout from MandelbrotCalculator is BGRA per uint (little
        // endian byte order: B, G, R, A). GL_BGRA + GL_UNSIGNED_INT_8_8_8_8_REV
        // (= PixelType.UnsignedInt8888Rev) consumes the packed uint correctly
        // on little-endian platforms — same trick the DX backend relies on via
        // Format.B8G8R8A8_UNorm.
        // S-X6 (2026-06-23) — GL_RGBA + GL_UNSIGNED_BYTE. GL_BGRA is not in
        // the 3.3 core profile spec; NVIDIA's strict core context returns
        // GL_INVALID_OPERATION and leaves the texture empty (→ all-black
        // window). Mesa accepts BGRA but the cross-plat baseline must be
        // core-conformant. The .bgra swizzle in FragmentShaderBody undoes
        // the channel re-order so MandelbrotCalculator's BGRA byte layout
        // renders with correct colours.
        fixed (uint* p = colorBuffer)
        {
            if (width != _texWidth || height != _texHeight)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0,
                    (int)InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
                _texWidth = width;
                _texHeight = height;
                CheckGlError($"TexImage2D {width}x{height}");
            }
            else
            {
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                    (uint)width, (uint)height,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
                CheckGlError($"TexSubImage2D {width}x{height}");
            }
        }

        if (s_diag && _uploadCount < 3)
        {
            uint pix0 = colorBuffer[0];
            uint pixMid = colorBuffer[(width * height) / 2];
            Console.Error.WriteLine($"[SilkGL] upload #{_uploadCount} {width}x{height} pix0=0x{pix0:X8} pixMid=0x{pixMid:X8}");
        }
        _uploadCount++;
        }
        finally { _releaseCurrent(); }
    }

    public void Render()
    {
        if (_disposed) return;
        _makeCurrent();
        try
        {
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _tex);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            CheckGlError($"DrawArrays render #{_renderCount}");

            _swap();
            CheckGlError($"swap render #{_renderCount}");

            if (s_diag && _renderCount < 3)
                Console.Error.WriteLine($"[SilkGL] render #{_renderCount} viewport={_width}x{_height} tex={_texWidth}x{_texHeight}");
            _renderCount++;
        }
        finally { _releaseCurrent(); }
    }

    public void Resize(int width, int height)
    {
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);
        // Viewport reapplied at next Render(); the host's swap chain handles
        // its own framebuffer resize.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _makeCurrent();
            if (_tex != 0) _gl.DeleteTexture(_tex);
            if (_program != 0) _gl.DeleteProgram(_program);
            if (_vao != 0) _gl.DeleteVertexArray(_vao);
        }
        catch { /* context may already be dead */ }

        _tex = _program = _vao = 0;
    }
}
