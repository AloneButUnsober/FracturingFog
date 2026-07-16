// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

// Wave 2.9 — Animation: morph equations (D-6.25). Dialog VM that lets the
// user pick two source equations from the cookbook (or paste raw DSL),
// chooses a frame count + output directory, and renders an N-frame PNG
// sequence as the synth DSL `(1-t)*(A) + t*(B)` sweeps t from 0 → 1.
//
// VM owns the loop; the host wires the actual render via RenderAndSaveRequested.
// Each call installs the synth DSL via hot-load, fires Trigger(), awaits one
// AnimationFrameUploaded, and saves the post-FX buffer. Cancellation cuts the
// loop between frames — the in-flight calculate is not cancelled (acceptable;
// the user usually wants this frame done either way before stopping).
public sealed class EquationMorphViewModel : ViewModelBase
{
    public ObservableCollection<CookbookEntry> Cookbook { get; }

    private CookbookEntry? _entryA;
    public CookbookEntry? EntryA
    {
        get => _entryA;
        set
        {
            this.RaiseAndSetIfChanged(ref _entryA, value);
            if (value is { } e) DslSourceA = e.DslSource;
            this.RaisePropertyChanged(nameof(EntryANameDisplay));
        }
    }

    private CookbookEntry? _entryB;
    public CookbookEntry? EntryB
    {
        get => _entryB;
        set
        {
            this.RaiseAndSetIfChanged(ref _entryB, value);
            if (value is { } e) DslSourceB = e.DslSource;
            this.RaisePropertyChanged(nameof(EntryBNameDisplay));
        }
    }

    public string EntryANameDisplay => _entryA?.Name ?? "(custom)";
    public string EntryBNameDisplay => _entryB?.Name ?? "(custom)";

    private string _dslSourceA;
    public string DslSourceA
    {
        get => _dslSourceA;
        set => this.RaiseAndSetIfChanged(ref _dslSourceA, value);
    }

    private string _dslSourceB;
    public string DslSourceB
    {
        get => _dslSourceB;
        set => this.RaiseAndSetIfChanged(ref _dslSourceB, value);
    }

    private int _frameCount = 60;
    public int FrameCount
    {
        get => _frameCount;
        set => this.RaiseAndSetIfChanged(ref _frameCount, Math.Clamp(value, 2, 600));
    }

    private string _outputDir = DefaultOutputDir();
    public string OutputDir
    {
        get => _outputDir;
        set => this.RaiseAndSetIfChanged(ref _outputDir, value);
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _statusIsError;
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    private int _progressFrame;
    public int ProgressFrame
    {
        get => _progressFrame;
        private set => this.RaiseAndSetIfChanged(ref _progressFrame, value);
    }

    private bool _running;
    public bool Running
    {
        get => _running;
        private set
        {
            this.RaiseAndSetIfChanged(ref _running, value);
            this.RaisePropertyChanged(nameof(NotRunning));
        }
    }
    public bool NotRunning => !_running;

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseOutputCommand { get; }

    /// <summary>Host runs one morph frame: install synth DSL via hot-load,
    /// trigger render, await upload, save PNG at <c>outputPath</c>. Returns
    /// null on success or an error message.</summary>
    public event Func<string, string, CancellationToken, Task<string?>>? RenderAndSaveRequested;

    /// <summary>Host shows a folder picker, returns the chosen path (or null
    /// on cancel). Synchronous from the VM's perspective.</summary>
    public event Func<string?, string?>? BrowseFolderRequested;

    /// <summary>View closes the window when raised.</summary>
    public event Action? CloseRequested;

    private CancellationTokenSource? _cts;

    public EquationMorphViewModel()
    {
        Cookbook = new ObservableCollection<CookbookEntry>(EquationCookbook.Entries);
        _entryA = Cookbook.Count > 0 ? Cookbook[0] : null;
        _entryB = Cookbook.Count > 1 ? Cookbook[1] : Cookbook.Count > 0 ? Cookbook[0] : null;
        _dslSourceA = _entryA?.DslSource ?? "z*z + c";
        _dslSourceB = _entryB?.DslSource ?? "z*z*z + c";

        var canStart = this.WhenAnyValue(x => x.Running).Select(r => !r);
        var canStop = this.WhenAnyValue(x => x.Running);
        StartCommand = ReactiveCommand.CreateFromTask(OnStart, canStart);
        StopCommand = ReactiveCommand.Create(OnStop, canStop);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
        BrowseOutputCommand = ReactiveCommand.Create(OnBrowseOutput);
    }

    private void OnBrowseOutput()
    {
        var picked = BrowseFolderRequested?.Invoke(_outputDir);
        if (!string.IsNullOrWhiteSpace(picked)) OutputDir = picked!;
    }

    private void OnStop()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
        StatusIsError = false;
    }

    private async Task OnStart()
    {
        var handler = RenderAndSaveRequested;
        if (handler == null)
        {
            StatusText = "Render delegate not wired by host.";
            StatusIsError = true;
            return;
        }

        string? err = EquationMorph.Validate(_dslSourceA, _dslSourceB);
        if (err != null) { StatusText = err; StatusIsError = true; return; }

        try { Directory.CreateDirectory(_outputDir); }
        catch (Exception ex)
        {
            StatusText = $"Output dir: {ex.Message}";
            StatusIsError = true;
            return;
        }

        int frames = _frameCount;
        Running = true;
        ProgressFrame = 0;
        StatusText = $"Rendering frame 1 / {frames}…";
        StatusIsError = false;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int saved = 0;
        try
        {
            for (int i = 0; i < frames; i++)
            {
                if (ct.IsCancellationRequested) break;
                double t = frames <= 1 ? 0.0 : (double)i / (frames - 1);
                string synth;
                try { synth = EquationMorph.Synthesize(_dslSourceA, _dslSourceB, t); }
                catch (Exception ex)
                {
                    StatusText = $"Synth failed at frame {i}: {ex.Message}";
                    StatusIsError = true;
                    return;
                }

                string outPath = Path.Combine(_outputDir,
                    $"morph_{i:D4}.png");
                ProgressFrame = i + 1;
                StatusText = $"Rendering frame {i + 1} / {frames} (t = {t:F3})…";

                // No ConfigureAwait(false): the continuation MUST land back on
                // the UI thread because the next iteration mutates reactive
                // properties bound by Avalonia. ReactiveCommand.CreateFromTask
                // already schedules OnStart on the main thread; await resumes
                // on the captured sync ctx so we stay on UI for the rest of
                // the loop.
                string? frameErr = await handler.Invoke(synth, outPath, ct);
                if (frameErr != null)
                {
                    StatusText = $"Frame {i + 1}: {frameErr}";
                    StatusIsError = true;
                    return;
                }
                saved++;
            }

            if (ct.IsCancellationRequested)
            {
                StatusText = $"Cancelled after {saved} frame{(saved == 1 ? "" : "s")} → {_outputDir}";
                StatusIsError = false;
            }
            else
            {
                StatusText = $"Done. {saved} frame{(saved == 1 ? "" : "s")} → {_outputDir}";
                StatusIsError = false;
            }
        }
        finally
        {
            Running = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string DefaultOutputDir()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "FracturingFog", "Morph");
    }
}
