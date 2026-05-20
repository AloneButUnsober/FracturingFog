// UserEquationCalculator.cs
//
// Renders an escape-time fractal whose per-iteration step function is supplied
// at runtime by the user as a C# expression / statement block, compiled via
// Roslyn scripting. Runs scalar (no SIMD) — delegate-call overhead per pixel
// means it is slower than the typed kernels, but interactive at 800×600 with
// modest iteration counts.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class UserEquationCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Most recent compile error, or empty string when last compile succeeded.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>True if last compile produced a usable delegate.</summary>
    public bool IsCompiled => _compiled != null;

    private Func<Complex, Complex, int, Complex>? _compiled;
    private string _compiledSource = string.Empty;

    public UserEquationCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    /// <summary>
    /// Compiles the user source. The source is the BODY of:
    ///   Complex Step(Complex z, Complex c, int n) { ... }
    /// It must return a Complex value. Available APIs: full System.Numerics.Complex
    /// methods plus standard Math wrappers (Re-exported as Sin/Cos/Exp/Log/Pow/Abs).
    /// </summary>
    public void Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            _compiled = null;
            LastError = "Source is empty";
            return;
        }

        try
        {
            string code = WrapUserSource(source);
            var options = ScriptOptions.Default
                .AddReferences(typeof(Complex).Assembly, typeof(object).Assembly, typeof(Math).Assembly)
                .AddImports("System", "System.Numerics", "System.Math");

            var script = CSharpScript.Create<Func<Complex, Complex, int, Complex>>(code, options);
            var compilation = script.Compile();
            if (compilation.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var diag in compilation)
                    sb.AppendLine(diag.ToString());
                LastError = sb.ToString();
                _compiled = null;
                return;
            }
            var result = script.RunAsync().GetAwaiter().GetResult();
            _compiled = result.ReturnValue;
            _compiledSource = source;
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _compiled = null;
        }
    }

    private static string WrapUserSource(string body)
    {
        // The script's "return value" is itself a Func — i.e. the script body
        // is a single Func<...> expression. Wrap with a local Step method
        // and return a delegate pointing to it.
        string wrappedBody = body.Contains("return") ? body : $"return {body};";
        return $@"
Complex __Step(Complex z, Complex c, int n)
{{
    {wrappedBody}
}}
return (Func<Complex, Complex, int, Complex>)((Complex z, Complex c, int n) => __Step(z, c, n));
";
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (_compiled == null)
        {
            // Try compiling from FractalParameters.UserEquationSource lazily.
            if (!string.IsNullOrWhiteSpace(FractalParameters.UserEquationSource)
                && FractalParameters.UserEquationSource != _compiledSource)
            {
                Compile(FractalParameters.UserEquationSource);
            }
        }

        var fn = _compiled;
        if (fn == null)
        {
            // No compiled equation — fill with theme InSetColor so the screen
            // is at least not stale.
            Array.Clear(ColorBuffer);
            uint bg = ColorMap.InSetColor;
            for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;
            return;
        }

        ColorMap.MaxIterations = MaxIterations;
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        double centerX = CenterX;
        double centerY = CenterY;
        int width = Width;
        int height = Height;
        const double bailout2 = 1024.0; // generous bailout for arbitrary maps

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                var c = new Complex(cx, cy);
                var z = Complex.Zero;
                int iter;
                for (iter = 0; iter < maxIt; iter++)
                {
                    double r2 = z.Real * z.Real + z.Imaginary * z.Imaginary;
                    if (r2 >= bailout2) break;
                    try { z = fn(z, c, iter); }
                    catch { iter = maxIt; break; }
                }
                int idx = rowBase + x;
                if (iter >= maxIt)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                }
                else
                {
                    double mag = Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
                    float smooth = (float)(iter + 1.0 - Math.Log2(Math.Max(1e-10, Math.Log2(Math.Max(mag, 1.0 + 1e-10)))));
                    ColorBuffer[idx] = (uint)ColorMap.Map(smooth, 0f, maxIt);
                }
            }
        });
    }
}
