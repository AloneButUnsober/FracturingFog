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
    // GLSL 330 core mirror of the DX HLSL. Same SV_VertexID trick: three
    // vertex IDs span [-1,1] NDC; UV interpolates 0..1 across the visible
    // screen quad. No vertex buffer needed.
    private const string VertexShaderSrc = @"#version 330 core
out vec2 vUv;
void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vUv = uv;
    gl_Position = vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}
";

    private const string FragmentShaderSrc = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
out vec4 fColor;
void main()
{
    fColor = texture(uTex, vUv);
}
";

    private readonly GL _gl;
    private readonly Action _makeCurrent;
    private readonly Action _swap;

    private uint _vao;
    private uint _program;
    private uint _tex;

    private int _width;
    private int _height;
    private int _texWidth;
    private int _texHeight;
    private bool _disposed;

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
                         Action makeCurrent, Action swap)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(makeCurrent);
        ArgumentNullException.ThrowIfNull(swap);

        _gl = gl;
        _makeCurrent = makeCurrent;
        _swap = swap;
        _width = System.Math.Max(1, width);
        _height = System.Math.Max(1, height);

        _makeCurrent();

        string vendor   = SafeGetString(StringName.Vendor);
        string renderer = SafeGetString(StringName.Renderer);
        string version  = SafeGetString(StringName.Version);
        RendererDescription = $"OpenGL ({renderer.Trim()} — {vendor.Trim()} — {version.Trim()})";

        CreateGeometry();
        CreateProgram();
        CreateTextureObject();
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
        uint vs = CompileShader(ShaderType.VertexShader, VertexShaderSrc);
        uint fs = CompileShader(ShaderType.FragmentShader, FragmentShaderSrc);

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = _gl.GetProgramInfoLog(_program);
            _gl.DeleteProgram(_program);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            throw new InvalidOperationException($"GL program link failed: {log}");
        }
        _gl.DetachShader(_program, vs);
        _gl.DetachShader(_program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        _gl.UseProgram(_program);
        int loc = _gl.GetUniformLocation(_program, "uTex");
        if (loc >= 0) _gl.Uniform1(loc, 0);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint id = _gl.CreateShader(type);
        _gl.ShaderSource(id, source);
        _gl.CompileShader(id);
        _gl.GetShader(id, GLEnum.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetShaderInfoLog(id);
            _gl.DeleteShader(id);
            throw new InvalidOperationException($"GL shader compile failed ({type}): {log}");
        }
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
        _gl.BindTexture(TextureTarget.Texture2D, _tex);

        // Source layout from MandelbrotCalculator is BGRA per uint (little
        // endian byte order: B, G, R, A). GL_BGRA + GL_UNSIGNED_INT_8_8_8_8_REV
        // (= PixelType.UnsignedInt8888Rev) consumes the packed uint correctly
        // on little-endian platforms — same trick the DX backend relies on via
        // Format.B8G8R8A8_UNorm.
        fixed (uint* p = colorBuffer)
        {
            if (width != _texWidth || height != _texHeight)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0,
                    (int)InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                    PixelFormat.Bgra, PixelType.UnsignedInt8888Rev, p);
                _texWidth = width;
                _texHeight = height;
            }
            else
            {
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                    (uint)width, (uint)height,
                    PixelFormat.Bgra, PixelType.UnsignedInt8888Rev, p);
            }
        }
    }

    public void Render()
    {
        if (_disposed) return;
        _makeCurrent();

        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _swap();
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
