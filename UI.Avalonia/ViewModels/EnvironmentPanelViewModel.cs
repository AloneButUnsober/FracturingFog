// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1669 (epic #1667, S2) — Control Center "Environment" section: the FF_* switches
// from EnvVarCatalog, their value in this process and where it came from, and the
// values to save for the next launch (EnvironmentSettings).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using FracturingFog.Cli;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>One switch row.</summary>
public sealed class EnvVarRowViewModel : ViewModelBase
{
    private const string DefaultChoice = "(default)";
    private readonly Action _changed;
    private bool? _toggle;
    private string _choice = DefaultChoice;
    private string _text = "";

    public EnvVarRowViewModel(EnvVarEntry entry, EnvVarState current, string? saved, Action changed)
    {
        Entry = entry;
        _changed = changed;
        CurrentText = current.Source switch
        {
            EnvVarSource.Default => $"now: default ({entry.Default})",
            EnvVarSource.Shell => $"now: {current.Value} (set outside FF; wins over the saved value)",
            _ => $"now: {current.Value} (saved)",
        };
        IsShellSet = current.Source == EnvVarSource.Shell;
        ChoiceOptions = entry.Kind == EnvVarKind.Choice ? [DefaultChoice, .. entry.Choices!] : [];
        Load(saved);
    }

    public EnvVarEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Meta => $"{Entry.Group} · default {Entry.Default}{(Entry.Issue != null ? " · " + Entry.Issue : "")}"
                          + (Entry.Developer ? " · developer" : "");
    public string? Warning => Entry.Warning;
    public bool HasWarning => Entry.Risk != EnvVarRisk.None && Entry.Warning != null;
    public string RiskBadge => Entry.Risk == EnvVarRisk.Danger ? "DANGER" : Entry.Risk == EnvVarRisk.Caution ? "caution" : "";
    public string CurrentText { get; }
    public bool IsShellSet { get; }

    public bool IsToggle => Entry.Kind == EnvVarKind.Toggle;
    public bool IsChoice => Entry.Kind == EnvVarKind.Choice;
    public bool IsTextual => Entry.Kind is EnvVarKind.Number or EnvVarKind.Text;
    public IReadOnlyList<string> ChoiceOptions { get; }
    public string Watermark => $"default: {Entry.Default}";

    /// <summary>Toggles: null = default, true = "1", false = "0".</summary>
    public bool? ToggleState
    {
        get => _toggle;
        set { this.RaiseAndSetIfChanged(ref _toggle, value); OnEdited(); }
    }

    public string SelectedChoice
    {
        get => _choice;
        set { this.RaiseAndSetIfChanged(ref _choice, value ?? DefaultChoice); OnEdited(); }
    }

    public string TextValue
    {
        get => _text;
        set { this.RaiseAndSetIfChanged(ref _text, value ?? ""); OnEdited(); }
    }

    /// <summary>The value to save; null = leave the switch unset.</summary>
    public string? PendingValue => Entry.Kind switch
    {
        EnvVarKind.Toggle => _toggle switch { true => "1", false => "0", _ => null },
        EnvVarKind.Choice => _choice == DefaultChoice ? null : _choice,
        _ => string.IsNullOrWhiteSpace(_text) ? null : _text.Trim(),
    };

    /// <summary>Why <see cref="PendingValue"/> can't be saved, or null.</summary>
    public string? Error
    {
        get
        {
            if (Entry.Kind != EnvVarKind.Number || PendingValue is not { } v) return null;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return "not a number";
            if (Entry.Min is { } lo && d < lo) return $"must be ≥ {lo.ToString(CultureInfo.InvariantCulture)}";
            if (Entry.Max is { } hi && d > hi) return $"must be ≤ {hi.ToString(CultureInfo.InvariantCulture)}";
            return null;
        }
    }

    public bool HasError => Error != null;

    /// <summary>Sets the editors from a saved value (null = default), without raising
    /// <c>changed</c>.</summary>
    internal void Load(string? saved)
    {
        _toggle = Entry.Kind == EnvVarKind.Toggle && saved != null ? saved != "0" : null;
        _choice = Entry.Kind == EnvVarKind.Choice && saved != null && Entry.Choices!.Contains(saved) ? saved : DefaultChoice;
        _text = Entry.Kind is EnvVarKind.Number or EnvVarKind.Text ? saved ?? "" : "";
        this.RaisePropertyChanged(nameof(ToggleState));
        this.RaisePropertyChanged(nameof(SelectedChoice));
        this.RaisePropertyChanged(nameof(TextValue));
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(HasError));
    }

    private void OnEdited()
    {
        this.RaisePropertyChanged(nameof(Error));
        this.RaisePropertyChanged(nameof(HasError));
        _changed();
    }
}

/// <summary>The Environment section.</summary>
public sealed class EnvironmentPanelViewModel : ViewModelBase
{
    private readonly string _path;
    private readonly Dictionary<string, string> _launchSaved;
    private Dictionary<string, string> _fileSaved;
    private string _search = "";
    private bool _showAdvanced;
    private string _status = "";

    /// <param name="path">The settings file (default <see cref="EnvironmentSettings.DefaultPath"/>).</param>
    /// <param name="launchSaved">The saved values this process started with (default
    /// <see cref="EnvironmentSettings.StartupSaved"/>, or the file when startup didn't apply).</param>
    /// <param name="current">A switch's state in this process (default <see cref="EnvironmentSettings.Current"/>).</param>
    public EnvironmentPanelViewModel(string? path = null, IReadOnlyCollection<EnvSetting>? launchSaved = null,
        Func<string, EnvVarState>? current = null)
    {
        _path = path ?? EnvironmentSettings.DefaultPath;
        _fileSaved = ToMap(EnvironmentSettings.Load(_path));
        _launchSaved = launchSaved != null ? ToMap(launchSaved)
            : EnvironmentSettings.AppliedAtStartup ? ToMap(EnvironmentSettings.StartupSaved) : new(_fileSaved);
        current ??= EnvironmentSettings.Current;
        AllRows = EnvVarCatalog.All
            .OrderBy(e => e.Group).ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new EnvVarRowViewModel(e, current(e.Name), _fileSaved.GetValueOrDefault(e.Name), OnRowChanged))
            .ToList();
        SaveCommand = ReactiveCommand.Create(Save);
        RevertCommand = ReactiveCommand.Create(Revert);
        ResetAllCommand = ReactiveCommand.Create(ResetAll);
        Refilter();
    }

    public IReadOnlyList<EnvVarRowViewModel> AllRows { get; }
    public ObservableCollection<EnvVarRowViewModel> Rows { get; } = new();

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetAllCommand { get; }

    public string Search
    {
        get => _search;
        set { this.RaiseAndSetIfChanged(ref _search, value ?? ""); Refilter(); }
    }

    /// <summary>Shows probe / test / diagnostic switches.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set { this.RaiseAndSetIfChanged(ref _showAdvanced, value); Refilter(); }
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>The editors differ from the saved file.</summary>
    public bool HasUnsavedChanges => !SameAs(_fileSaved);

    /// <summary>The saved file differs from what this process started with.</summary>
    public bool RestartNeeded => !MapsEqual(_fileSaved, _launchSaved);

    public string RestartText =>
        "Saved switches take effect the next time Fracturing Fog starts. Close and reopen it to apply them.";

    public IReadOnlyList<EnvSetting> Pending() =>
        AllRows.Where(r => r.PendingValue != null).Select(r => new EnvSetting(r.Name, r.PendingValue!)).ToList();

    internal void Save()
    {
        var bad = AllRows.Where(r => r.HasError).Select(r => $"{r.Name}: {r.Error}").ToList();
        if (bad.Count > 0) { Status = "Not saved — " + string.Join("; ", bad); return; }
        try
        {
            var pending = Pending();
            EnvironmentSettings.Save(_path, pending);
            _fileSaved = ToMap(pending);
            Status = pending.Count == 0 ? "Saved: no switches set." : $"Saved {pending.Count} switch(es).";
        }
        catch (Exception ex)
        {
            Status = "Not saved — " + ex.Message;
        }
        RaiseState();
    }

    internal void Revert()
    {
        foreach (var r in AllRows) r.Load(_fileSaved.GetValueOrDefault(r.Name));
        Status = "Reverted to the saved switches.";
        RaiseState();
    }

    internal void ResetAll()
    {
        foreach (var r in AllRows) r.Load(null);
        Status = "All switches back to default (not saved yet).";
        RaiseState();
    }

    private void OnRowChanged() => RaiseState();

    private void RaiseState()
    {
        this.RaisePropertyChanged(nameof(HasUnsavedChanges));
        this.RaisePropertyChanged(nameof(RestartNeeded));
    }

    private void Refilter()
    {
        string q = _search.Trim();
        Rows.Clear();
        foreach (var r in AllRows)
        {
            if (r.Entry.Developer && !_showAdvanced && r.PendingValue == null) continue;
            if (q.Length > 0
                && r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && r.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && r.Entry.Group.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Rows.Add(r);
        }
    }

    private bool SameAs(Dictionary<string, string> map) => MapsEqual(ToMap(Pending()), map);

    private static Dictionary<string, string> ToMap(IEnumerable<EnvSetting> s) =>
        s.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    private static bool MapsEqual(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
