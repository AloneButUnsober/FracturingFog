// SilkStandaloneRunner.cs
//
// Self-contained Silk.NET window + GL context + render loop. Drives a
// SilkGLRenderer with frames supplied by a caller-provided callback. Two
// purposes:
//
//   1. Cross-platform smoke test for CI runners that do not have a real X
//      server (xvfb wrapping handled outside this assembly). Constructs an
//      IWindow via the GLFW backend, runs N frames, then exits — proves the
//      GL stack loaded and the renderer survived its DXGI-equivalent path.
//
//   2. Optional standalone viewer for Linux/macOS users that don't want to
//      go through Avalonia. CLI mode in Program.cs hooks the calculator
//      output here directly.
//
// Avalonia integration uses a different path (foreign-window adoption via
// SilkWin32ContextAdapter / SilkGLXContextAdapter); this runner is for when
// the host wants Silk to own the window outright.

using System;
using FracturingFog.Abstractions;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace FracturingFog.Rendering.Silk;

public sealed class SilkStandaloneRunner : IDisposable
{
    private readonly IWindow _window;
    private GL? _gl;
    private SilkGLRenderer? _renderer;
    private readonly Func<int, int, uint[]> _frameSource;
    private readonly Action<SilkStandaloneRunner>? _onReady;
    private int _frameBudget;
    private int _framesRendered;
    private bool _disposed;

    public SilkGLRenderer? Renderer => _renderer;
    public IWindow Window => _window;
    public int FramesRendered => _framesRendered;

    /// <summary>
    /// Constructs the runner but does not open the window. Call <see cref="Run"/>
    /// to enter the GLFW message loop. <paramref name="frameSource"/> is invoked
    /// once per Update with the current pixel size and must return a BGRA buffer
    /// of length width*height; null returns are uploaded as black frames.
    /// </summary>
    public SilkStandaloneRunner(
        int width,
        int height,
        string title,
        Func<int, int, uint[]> frameSource,
        int frameBudget = int.MaxValue,
        Action<SilkStandaloneRunner>? onReady = null)
    {
        ArgumentNullException.ThrowIfNull(frameSource);
        _frameSource = frameSource;
        _onReady = onReady;
        _frameBudget = System.Math.Max(1, frameBudget);

        var opts = WindowOptions.Default with
        {
            Size = new global::Silk.NET.Maths.Vector2D<int>(System.Math.Max(64, width),
                                                            System.Math.Max(64, height)),
            Title = title,
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion(3, 3))
        };
        _window = global::Silk.NET.Windowing.Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += OnResize;
    }

    private void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _renderer = new SilkGLRenderer(
            _gl,
            _window.Size.X,
            _window.Size.Y,
            makeCurrent: () => _window.MakeCurrent(),
            swap: () => _window.SwapBuffers());
        _onReady?.Invoke(this);
    }

    private void OnResize(global::Silk.NET.Maths.Vector2D<int> size)
        => _renderer?.Resize(size.X, size.Y);

    private void OnRender(double deltaSeconds)
    {
        if (_renderer == null) return;
        int w = _window.Size.X;
        int h = _window.Size.Y;
        uint[] frame = _frameSource(w, h);
        if (frame != null && frame.Length >= w * h)
            _renderer.UpdateTexture(frame, w, h);
        _renderer.Render();
        _framesRendered++;
        if (_framesRendered >= _frameBudget)
            _window.Close();
    }

    public int Run()
    {
        _window.Run();
        return _framesRendered;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer?.Dispose();
        _window.Dispose();
    }

    /// <summary>
    /// Headless smoke convenience: opens a GLFW window, uploads one solid-grey
    /// frame, renders it, then exits. Returns the renderer description string
    /// so the caller can assert the GL stack loaded. Throws if context creation
    /// fails — the CI runner surfaces the exception as a failed step.
    /// </summary>
    public static string SmokeOneFrame(int width = 256, int height = 256)
    {
        string desc = "?";
        using var runner = new SilkStandaloneRunner(
            width, height, "Silk Smoke",
            frameSource: (w, h) =>
            {
                var buf = new uint[w * h];
                Array.Fill(buf, 0xFF202020u);   // solid dark grey BGRA
                return buf;
            },
            frameBudget: 1,
            onReady: r => { if (r.Renderer != null) desc = r.Renderer.RendererDescription; });
        runner.Run();
        return desc;
    }
}
