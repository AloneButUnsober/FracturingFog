// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.Loop.cs
//
// Explore ▸ Find — after Snap to spiral (finder S3b, #1235):
//   • the self-similar loop at the snapped Misiurewicz point: "Loop video"
//     loads the Command panel with the live command plus
//     --mode video --video-motion loop --loop-multiplier … (the headless and
//     in-app Run render the same frames, through MisiurewiczLoopRenderer);
//   • a J_c preview — the Julia set at the snapped c, whose shape near c the
//     parameter plane repeats (Tan Lei). z² + c only.

using System;
using System.Linq;
using System.Reactive;
using System.Runtime.InteropServices;

using global::Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    public const int JuliaPreviewWidth = 240, JuliaPreviewHeight = 135;

    private MisiurewiczLoop? _loop;
    private DeepComplex _loopPoint;
    private string _loopInfo = "";
    private WriteableBitmap? _juliaPreview;

    public ReactiveCommand<Unit, Unit> LoopVideoCommand { get; private set; } = null!;

    /// <summary>True after a snap whose multiplier is a rotation-scaling (a
    /// loop exists). Cleared by the next finder action.</summary>
    public bool HasLoop => _loop != null;

    public string LoopInfo
    {
        get => _loopInfo;
        private set => this.RaiseAndSetIfChanged(ref _loopInfo, value);
    }

    /// <summary>J_c at the snapped point (z² + c), or null.</summary>
    public WriteableBitmap? JuliaPreview
    {
        get => _juliaPreview;
        private set
        {
            this.RaiseAndSetIfChanged(ref _juliaPreview, value);
            this.RaisePropertyChanged(nameof(HasJuliaPreview));
        }
    }

    public bool HasJuliaPreview => _juliaPreview != null;

    private void InitLoop()
        => LoopVideoCommand = ReactiveCommand.Create(SeedLoopVideo, this.WhenAnyValue(x => x.HasLoop));

    /// <summary>Remember the snapped point's loop (null multiplier angle = not
    /// conformal, no loop) and show its J_c preview (z² + c only).</summary>
    private void SetLoopTarget(DeepComplex point, double magnitude, double? angleDegrees, int period, bool juliaPreview)
    {
        _loopPoint = point;
        _loop = angleDegrees is double deg && magnitude > 1 ? MisiurewiczLoop.For(magnitude, deg, period) : null;
        this.RaisePropertyChanged(nameof(HasLoop));
        LoopInfo = _loop is { } l
            ? $"Loop: ×{Fmt(l.LoopZoom)} zoom over {l.Cycles} cycle{(l.Cycles == 1 ? "" : "s")}, seamless. Loop video loads it into Command (Run renders it)."
            : "";
        JuliaPreview = juliaPreview ? RenderJuliaBitmap(point.Re.X0, point.Im.X0) : null;
    }

    private void ClearLoopTarget()
    {
        if (_loop == null && _juliaPreview == null) return;
        _loop = null;
        this.RaisePropertyChanged(nameof(HasLoop));
        LoopInfo = "";
        JuliaPreview = null;
    }

    private WriteableBitmap? RenderJuliaBitmap(double cre, double cim)
    {
        uint[]? px;
        try { px = Shell.RenderJuliaPreview?.Invoke(cre, cim, JuliaPreviewWidth, JuliaPreviewHeight); }
        catch { return null; }
        if (px == null || px.Length < JuliaPreviewWidth * JuliaPreviewHeight) return null;
        var bmp = new WriteableBitmap(new PixelSize(JuliaPreviewWidth, JuliaPreviewHeight), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Premul);
        var row = new int[JuliaPreviewWidth];
        using (var fb = bmp.Lock())
            for (int y = 0; y < JuliaPreviewHeight; y++)
            {
                for (int x = 0; x < JuliaPreviewWidth; x++)
                    row[x] = unchecked((int)(px[y * JuliaPreviewWidth + x] | 0xFF000000u));   // opaque preview
                Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), JuliaPreviewWidth);
            }
        return bmp;
    }

    // Load the Command panel with the live command re-centred on the snapped
    // point (full limbs) plus the loop tokens, and switch to it.
    private void SeedLoopVideo()
    {
        if (_loop is not { } loop) return;
        var seed = BuildLiveSeed(FracturingFog.Batch.BatchDefaults.Width, FracturingFog.Batch.BatchDefaults.Height);
        var args = seed.Args.ToList();
        void Put(string flag, string value)
        {
            int i = args.IndexOf(flag);
            if (i >= 0 && i + 1 < args.Count) args[i + 1] = value;
            else { args.Add(flag); args.Add(value); }
        }
        var re = _loopPoint.Re; var im = _loopPoint.Im;
        Put(FracturingFog.Batch.BatchFlags.X, FracturingFog.Cli.BatchCommandBuilder.Coordinate(
            re.X0, new[] { re.X1, re.X2, re.X3, re.X4, re.X5, re.X6, re.X7 }));
        Put(FracturingFog.Batch.BatchFlags.Y, FracturingFog.Cli.BatchCommandBuilder.Coordinate(
            im.X0, new[] { im.X1, im.X2, im.X3, im.X4, im.X5, im.X6, im.X7 }));
        args.AddRange(FracturingFog.Cli.BatchCommandBuilder.LoopVideoArgs(loop));
        CommandBuilder.Composer.Load(args);
        SelectedSection = ControlCenterSection.Command;
        FinderStatus = "Loop video loaded into Command: set --out and press Run (one loop = the whole video; it wraps).";
    }
}
