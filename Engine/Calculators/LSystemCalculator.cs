// LSystemCalculator.cs
//
// Rewrites the L-system axiom N times using the production rules, then walks
// the result as turtle graphics, drawing line segments into ColorBuffer.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class LSystemCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public LSystemCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        // Clear to in-set color (theme background).
        ColorMap.MaxIterations = 256;
        uint bg = ColorMap.InSetColor;
        for (int i = 0; i < ColorBuffer.Length; i++) ColorBuffer[i] = bg;

        if (!LSystemPresets.All.TryGetValue(FractalParameters.LSystemPresetName, out var def))
            def = LSystemPresets.All["Hilbert"];

        int depth = Math.Clamp(FractalParameters.LSystemDepth, 0, 12);
        string str = Rewrite(def.Axiom, def.Rules, depth, ct);
        if (ct.IsCancellationRequested) return;

        // First pass: trace turtle to compute bbox, no drawing.
        var bbox = TraceBBox(str, def);
        if (bbox.W <= 0 || bbox.H <= 0) return;

        // Map turtle path → world coords (Mandelbrot convention).
        double worldSpan = Math.Max(bbox.W, bbox.H);
        double mapFit = 3.0 / Math.Max(1e-9, worldSpan);
        double mx = bbox.MinX + bbox.W * 0.5;
        double my = bbox.MinY + bbox.H * 0.5;
        double pixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;

        // Second pass: draw with Bresenham. Color samples gradient via
        // step index → smooth iteration count fed to IColorMap.
        DrawTurtle(str, def, mapFit, mx, my, pixelScale, CenterX, CenterY, ct);
    }

    private static string Rewrite(string axiom, Dictionary<char, string> rules, int depth, CancellationToken ct)
    {
        string cur = axiom;
        var sb = new StringBuilder();
        for (int d = 0; d < depth; d++)
        {
            if (ct.IsCancellationRequested) return cur;
            sb.Clear();
            sb.EnsureCapacity(cur.Length * 4);
            foreach (char ch in cur)
            {
                if (rules.TryGetValue(ch, out var r)) sb.Append(r);
                else sb.Append(ch);
            }
            cur = sb.ToString();
            if (cur.Length > 5_000_000) break; // safety cap
        }
        return cur;
    }

    private record struct BBox(double MinX, double MinY, double W, double H);

    private static BBox TraceBBox(string str, LSystemDefinition def)
    {
        double x = 0, y = 0;
        double angle = def.StartAngleDegrees * Math.PI / 180.0;
        double turn = def.AngleDegrees * Math.PI / 180.0;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var stack = new Stack<(double X, double Y, double Angle)>();

        foreach (char ch in str)
        {
            switch (ch)
            {
                case 'F': case 'A': case 'B':
                    x += Math.Cos(angle);
                    y += Math.Sin(angle);
                    if (x < minX) minX = x; else if (x > maxX) maxX = x;
                    if (y < minY) minY = y; else if (y > maxY) maxY = y;
                    break;
                case 'f':
                    x += Math.Cos(angle); y += Math.Sin(angle);
                    break;
                case '+': angle += turn; break;
                case '-': angle -= turn; break;
                case '[': stack.Push((x, y, angle)); break;
                case ']': if (stack.Count > 0) (x, y, angle) = stack.Pop(); break;
                case '|': angle += Math.PI; break;
            }
        }
        return new BBox(minX, minY, maxX - minX, maxY - minY);
    }

    private void DrawTurtle(string str, LSystemDefinition def,
        double mapFit, double mx, double my,
        double pixelScale, double centerX, double centerY,
        CancellationToken ct)
    {
        double x = 0, y = 0;
        double angle = def.StartAngleDegrees * Math.PI / 180.0;
        double turn = def.AngleDegrees * Math.PI / 180.0;
        var stack = new Stack<(double X, double Y, double Angle)>();

        int stepIdx = 0;
        // Pre-count drawing steps for color cycling.
        int totalSteps = 0;
        foreach (char ch in str) if (ch == 'F' || ch == 'A' || ch == 'B') totalSteps++;
        if (totalSteps < 1) totalSteps = 1;

        foreach (char ch in str)
        {
            if (ct.IsCancellationRequested) return;
            switch (ch)
            {
                case 'F': case 'A': case 'B':
                    {
                        double nx = x + Math.Cos(angle);
                        double ny = y + Math.Sin(angle);
                        int px1 = WorldToPixelX(x, mx, mapFit, pixelScale, centerX);
                        int py1 = WorldToPixelY(y, my, mapFit, pixelScale, centerY);
                        int px2 = WorldToPixelX(nx, mx, mapFit, pixelScale, centerX);
                        int py2 = WorldToPixelY(ny, my, mapFit, pixelScale, centerY);
                        float t = stepIdx / (float)totalSteps;
                        uint color = (uint)ColorMap.Map(t * 256f, 0f, 256);
                        DrawLine(px1, py1, px2, py2, color);
                        x = nx; y = ny;
                        stepIdx++;
                        break;
                    }
                case 'f':
                    x += Math.Cos(angle); y += Math.Sin(angle);
                    break;
                case '+': angle += turn; break;
                case '-': angle -= turn; break;
                case '[': stack.Push((x, y, angle)); break;
                case ']': if (stack.Count > 0) (x, y, angle) = stack.Pop(); break;
                case '|': angle += Math.PI; break;
            }
        }
    }

    private int WorldToPixelX(double rawX, double mx, double mapFit, double pixelScale, double centerX)
    {
        double worldX = (rawX - mx) * mapFit;
        return (int)((worldX - centerX) / pixelScale + Width * 0.5);
    }

    private int WorldToPixelY(double rawY, double my, double mapFit, double pixelScale, double centerY)
    {
        // Flip Y so positive turtle Y appears at TOP of screen.
        double worldY = -(rawY - my) * mapFit;
        return (int)((worldY - centerY) / pixelScale + Height * 0.5);
    }

    private void DrawLine(int x0, int y0, int x1, int y1, uint color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x0 < (uint)Width && (uint)y0 < (uint)Height)
                ColorBuffer[y0 * Width + x0] = color;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
