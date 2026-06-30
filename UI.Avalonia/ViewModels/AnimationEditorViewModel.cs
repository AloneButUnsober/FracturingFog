// ViewModels/AnimationEditorViewModel.cs
//
// Animation Roadmap Phase 3c. Editor VM for AnimationData assets. Mirrors the
// WatermarkEditorViewModel shape: load-existing / new-blank / revert / save /
// delete, plus a Live-Preview toggle that pumps the in-progress animation
// onto the app-scoped AnimationBusHost so the user can see motion while
// authoring. Persistence routes through IColorThemeService so the VM has no
// dependency on the Engine project (where AnimationLibrary lives).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>One row in the editor's tracks list. Bound to the per-param row
/// in the .axaml. Toggling <see cref="Enabled"/> includes/excludes the track
/// in the built <see cref="AnimationData"/>; the other fields populate the
/// resulting <see cref="AnimationTrack"/>.</summary>
public sealed class AnimationTrackRowViewModel : ReactiveObject
{
    private readonly Action _onChanged;

    public AnimationTrackRowViewModel(AnimatableParamDescriptor descriptor, Action onChanged)
    {
        Descriptor = descriptor;
        _onChanged = onChanged;
        // Sensible procedural defaults from the descriptor.
        _min = descriptor.Min;
        _max = descriptor.Max;
        _mode = descriptor.Kind == AnimatableParamKind.Complex
            ? AnimationMode.Lissajous
            : AnimationMode.Sine;
    }

    public AnimatableParamDescriptor Descriptor { get; }

    public string ParamName => Descriptor.ParamName;
    public string KindLabel => Descriptor.Kind.ToString();
    public string CostLabel => Descriptor.Cost.ToString();
    public string? Notes => Descriptor.Notes;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Descriptor.Notes);

    public IReadOnlyList<AnimationMode> AvailableModes { get; } = new[]
    {
        AnimationMode.Hold, AnimationMode.Sine, AnimationMode.Triangle,
        AnimationMode.Linear, AnimationMode.Lissajous,
    };

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { this.RaiseAndSetIfChanged(ref _enabled, value); _onChanged(); }
    }

    private AnimationMode _mode;
    public AnimationMode Mode
    {
        get => _mode;
        set { this.RaiseAndSetIfChanged(ref _mode, value); _onChanged(); }
    }

    private double _min;
    public double Min
    {
        get => _min;
        set { this.RaiseAndSetIfChanged(ref _min, value); _onChanged(); }
    }

    private double _max;
    public double Max
    {
        get => _max;
        set { this.RaiseAndSetIfChanged(ref _max, value); _onChanged(); }
    }

    private double _frequencyHz = 0.1;
    public double FrequencyHz
    {
        get => _frequencyHz;
        set { this.RaiseAndSetIfChanged(ref _frequencyHz, value); _onChanged(); }
    }

    private double _phaseOffsetRadians;
    public double PhaseOffsetRadians
    {
        get => _phaseOffsetRadians;
        set { this.RaiseAndSetIfChanged(ref _phaseOffsetRadians, value); _onChanged(); }
    }

    /// <summary>Build the persistable <see cref="AnimationTrack"/>. Tracks
    /// where <see cref="Enabled"/> is false are still emitted (the
    /// AnimationTrack.Enabled flag travels with them) so disabling a row
    /// then saving keeps the row around for a later re-enable.</summary>
    public AnimationTrack ToTrack() => new()
    {
        ParamName = ParamName,
        Mode = Mode,
        Min = Min,
        Max = Max,
        FrequencyHz = FrequencyHz,
        PhaseOffsetRadians = PhaseOffsetRadians,
        Enabled = Enabled,
    };

    /// <summary>Populate from an on-disk track that matches this row's
    /// <see cref="ParamName"/>. Silently no-ops when the names differ —
    /// caller is responsible for matching.</summary>
    public void Populate(AnimationTrack track)
    {
        if (!string.Equals(track.ParamName, ParamName, StringComparison.Ordinal)) return;
        _mode = track.Mode;
        _min = track.Min;
        _max = track.Max;
        _frequencyHz = track.FrequencyHz;
        _phaseOffsetRadians = track.PhaseOffsetRadians;
        _enabled = track.Enabled;
        this.RaisePropertyChanged(nameof(Mode));
        this.RaisePropertyChanged(nameof(Min));
        this.RaisePropertyChanged(nameof(Max));
        this.RaisePropertyChanged(nameof(FrequencyHz));
        this.RaisePropertyChanged(nameof(PhaseOffsetRadians));
        this.RaisePropertyChanged(nameof(Enabled));
    }
}

public sealed class AnimationEditorViewModel : ViewModelBase
{
    private readonly IColorThemeService _service;
    private readonly object _previewTarget;
    private bool _suppressChange;
    private string? _loadedSourceName;

    public AnimationEditorViewModel(
        IColorThemeService service,
        object previewTarget,
        string? initialAnimationName = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _previewTarget = previewTarget ?? throw new ArgumentNullException(nameof(previewTarget));

        AnimationNames = new ObservableCollection<string>(_service.EnumerateAnimationNames());
        AvailableFractalTypes = new ObservableCollection<FractalType>(
            Enum.GetValues<FractalType>()
                .Where(ft => FractalAnimatableParamsMap.For(ft).Count > 0)
                .OrderBy(ft => ft.ToString(), StringComparer.OrdinalIgnoreCase));

        Tracks = new ObservableCollection<AnimationTrackRowViewModel>();

        NewBlankCommand    = ReactiveCommand.Create(NewBlank);
        RevertCommand      = ReactiveCommand.Create(Revert);
        SaveCommand        = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand      = ReactiveCommand.CreateFromTask(DeleteAsync);
        PreviewCommand     = ReactiveCommand.Create(PushPreview);
        StopPreviewCommand = ReactiveCommand.Create(StopPreview);
        CloseCommand       = ReactiveCommand.Create(() =>
        {
            StopPreview();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });

        if (!string.IsNullOrEmpty(initialAnimationName)
            && AnimationNames.Contains(initialAnimationName))
        {
            _suppressChange = true;
            SelectedAnimation = initialAnimationName;
            _suppressChange = false;
            LoadFromLibrary(initialAnimationName);
        }
        else
        {
            NewBlank();
        }
    }

    // ── Collections ───────────────────────────────────────────────────────

    public ObservableCollection<string> AnimationNames { get; }
    public ObservableCollection<FractalType> AvailableFractalTypes { get; }
    public ObservableCollection<AnimationTrackRowViewModel> Tracks { get; }

    // ── Load / save name selection ────────────────────────────────────────

    private string? _selectedAnimation;
    public string? SelectedAnimation
    {
        get => _selectedAnimation;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAnimation, value);
            if (_suppressChange || string.IsNullOrEmpty(value)) return;
            LoadFromLibrary(value);
        }
    }

    // ── Top-line fields ───────────────────────────────────────────────────

    private string _name = "My Animation";
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); FieldChanged(); }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set { this.RaiseAndSetIfChanged(ref _description, value); FieldChanged(); }
    }

    private string _category = "User";
    public string Category
    {
        get => _category;
        set { this.RaiseAndSetIfChanged(ref _category, value); FieldChanged(); }
    }

    private string _tags = string.Empty;
    /// <summary>Comma-separated tags. Empty = no tags. Trimmed entries only.</summary>
    public string Tags
    {
        get => _tags;
        set { this.RaiseAndSetIfChanged(ref _tags, value); FieldChanged(); }
    }

    private double? _duration;
    /// <summary>Optional total length in seconds. <c>null</c> = loops forever.</summary>
    public double? Duration
    {
        get => _duration;
        set { this.RaiseAndSetIfChanged(ref _duration, value); FieldChanged(); }
    }

    private FractalType _selectedFractalType = FractalType.Julia;
    /// <summary>The fractal type that drives the visible track list. Changes
    /// rebuild <see cref="Tracks"/> from
    /// <see cref="FractalAnimatableParamsMap.For"/>. The picked type is also
    /// the sole entry in the saved animation's
    /// <see cref="AnimationData.TargetFractalTypes"/> in MVP.</summary>
    public FractalType SelectedFractalType
    {
        get => _selectedFractalType;
        set
        {
            if (_selectedFractalType == value) return;
            this.RaiseAndSetIfChanged(ref _selectedFractalType, value);
            RebuildTracks();
            FieldChanged();
        }
    }

    private bool _livePreview;
    public bool LivePreview
    {
        get => _livePreview;
        set
        {
            this.RaiseAndSetIfChanged(ref _livePreview, value);
            if (value) PushPreview();
            else StopPreview();
        }
    }

    private string _titleText = "Animation Editor — new";
    public string TitleText
    {
        get => _titleText;
        set => this.RaiseAndSetIfChanged(ref _titleText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> NewBlankCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> StopPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // ── Events for the shell ──────────────────────────────────────────────

    /// <summary>Fires after a successful Save so the shell can refresh any
    /// Animation dropdowns (Save Region dialog).</summary>
    public event EventHandler<string>? AnimationSavedToLibrary;

    /// <summary>Fires after a successful Delete.</summary>
    public event EventHandler<string>? AnimationDeletedFromLibrary;

    public event EventHandler? CloseRequested;
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    // ── Build / load ──────────────────────────────────────────────────────

    /// <summary>Build a persistable <see cref="AnimationData"/> from the
    /// current editor state. Disabled rows are kept (their
    /// <see cref="AnimationTrack.Enabled"/> flag carries the state) so the
    /// user can save with some tracks toggled off.</summary>
    public AnimationData BuildData()
    {
        var data = new AnimationData
        {
            Name = string.IsNullOrWhiteSpace(_name) ? "Unnamed Animation" : _name.Trim(),
            Description = _description ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(_category) ? "User" : _category.Trim(),
            Duration = _duration,
            TargetFractalTypes = new List<FractalType> { _selectedFractalType },
        };
        foreach (var row in Tracks) data.Tracks.Add(row.ToTrack());
        if (!string.IsNullOrWhiteSpace(_tags))
        {
            foreach (var t in _tags.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = t.Trim();
                if (tag.Length > 0) data.Tags.Add(tag);
            }
        }
        return data;
    }

    private void LoadFromLibrary(string name)
    {
        var data = _service.GetAnimation(name);
        if (data == null) return;
        _loadedSourceName = name;
        LoadData(data);
    }

    private void LoadData(AnimationData data)
    {
        _suppressChange = true;
        try
        {
            Name = data.Name ?? string.Empty;
            Description = data.Description ?? string.Empty;
            Category = string.IsNullOrWhiteSpace(data.Category) ? "User" : data.Category!;
            Duration = data.Duration;
            Tags = string.Join(", ", data.Tags ?? new List<string>());

            // Pick the first target that's still in our available set so the
            // editor doesn't blow up on a stale enum value.
            var first = data.TargetFractalTypes != null && data.TargetFractalTypes.Count > 0
                ? data.TargetFractalTypes[0]
                : SelectedFractalType;
            if (AvailableFractalTypes.Contains(first)) _selectedFractalType = first;
            this.RaisePropertyChanged(nameof(SelectedFractalType));

            RebuildTracks();

            if (data.Tracks != null)
            {
                foreach (var t in data.Tracks)
                {
                    var row = Tracks.FirstOrDefault(r =>
                        string.Equals(r.ParamName, t.ParamName, StringComparison.Ordinal));
                    row?.Populate(t);
                }
            }

            TitleText = $"Animation Editor — {Name}";
        }
        finally { _suppressChange = false; }
        if (LivePreview) PushPreview();
    }

    private void RebuildTracks()
    {
        var descriptors = FractalAnimatableParamsMap.For(_selectedFractalType);
        Tracks.Clear();
        foreach (var d in descriptors) Tracks.Add(new AnimationTrackRowViewModel(d, FieldChanged));
    }

    private void FieldChanged()
    {
        if (_suppressChange) return;
        if (!LivePreview) return;
        PushPreview();
    }

    private void PushPreview() => AnimationBusHost.LoadRegionAnimation(BuildData(), _previewTarget);

    private void StopPreview() => AnimationBusHost.LoadRegionAnimation(null, _previewTarget);

    private void NewBlank()
    {
        _loadedSourceName = null;
        _suppressChange = true;
        try
        {
            Name = "My Animation";
            Description = string.Empty;
            Category = "User";
            Tags = string.Empty;
            Duration = null;
            _selectedFractalType = AvailableFractalTypes.Count > 0
                ? AvailableFractalTypes[0]
                : FractalType.Julia;
            this.RaisePropertyChanged(nameof(SelectedFractalType));
            RebuildTracks();
            // Enable the first row so a brand-new asset has at least one
            // animatable track without forcing the user to click Enabled.
            if (Tracks.Count > 0) Tracks[0].Enabled = true;
            TitleText = "Animation Editor — new";
        }
        finally { _suppressChange = false; }
        if (LivePreview) PushPreview();
    }

    private void Revert()
    {
        if (string.IsNullOrEmpty(_loadedSourceName)) { NewBlank(); return; }
        LoadFromLibrary(_loadedSourceName);
    }

    private async Task SaveAsync()
    {
        var data = BuildData();
        if (string.IsNullOrWhiteSpace(data.Name))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Save Animation", "Name cannot be empty.", MessageSeverity.Warning));
            return;
        }
        if (_service.AnimationExistsInLibrary(data.Name)
            && !string.Equals(_loadedSourceName, data.Name, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = new ThemeMessageEventArgs("Replace Animation",
                $"An animation named \"{data.Name}\" already exists.\n\nReplace it?",
                MessageSeverity.Question) { ExpectsConfirmation = true };
            await RaiseMessageAsync(confirm);
            if (!confirm.Confirmed) return;
        }

        if (!_service.SaveAnimation(data))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Save Animation", "Save failed (see log).", MessageSeverity.Warning));
            return;
        }
        AnimationSavedToLibrary?.Invoke(this, data.Name);

        _suppressChange = true;
        AnimationNames.Clear();
        foreach (var n in _service.EnumerateAnimationNames()) AnimationNames.Add(n);
        SelectedAnimation = data.Name;
        _suppressChange = false;
        _loadedSourceName = data.Name;
        TitleText = $"Animation Editor — {data.Name}";

        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Save Animation", $"\"{data.Name}\" saved.", MessageSeverity.Info));
    }

    private async Task DeleteAsync()
    {
        if (string.IsNullOrEmpty(_loadedSourceName))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Delete Animation", "No saved animation loaded.", MessageSeverity.Warning));
            return;
        }
        var confirm = new ThemeMessageEventArgs("Delete Animation",
            $"Delete \"{_loadedSourceName}\" from the library?",
            MessageSeverity.Question) { ExpectsConfirmation = true };
        await RaiseMessageAsync(confirm);
        if (!confirm.Confirmed) return;

        string deleted = _loadedSourceName;
        // The interface exposes save + exists but not delete — Phase 3c keeps
        // the API surface tight. Wire delete in a follow-up phase if users
        // ask; today, deletion is via direct file edit. Surface a friendly
        // message rather than silently failing.
        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Delete Animation",
            $"Delete from disk is not yet exposed in the UI. Remove \"{deleted}\" by editing animations.json directly.",
            MessageSeverity.Info));
        AnimationDeletedFromLibrary?.Invoke(this, deleted);
    }

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }
}
