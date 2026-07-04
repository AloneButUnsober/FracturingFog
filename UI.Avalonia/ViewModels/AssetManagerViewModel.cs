// ViewModels/AssetManagerViewModel.cs
//
// Asset Manager (Animation Roadmap Sub-goal A) — phase A1: read-only three-pane
// browser over every saved asset type. Left pane = type tree, middle = filtered
// list of the selected type's assets, right = detail. No editing here; routing a
// row to its type's own editor lands in A2. See
// Docs/Technical/AssetManager-DevPlan.md.
//
// The VM is fed IAssetSource adapters (Abstractions) by the host — UI.Avalonia
// does not reference Engine where the adapters live, mirroring how the Region
// Editor reaches the Engine only through IColorThemeService.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive;
using System.Text;

using FracturingFog.Abstractions.Assets;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class AssetManagerViewModel : ViewModelBase
{
    private readonly IReadOnlyList<IAssetSource> _sources;

    /// <summary>Left-pane type tree, one node per asset source.</summary>
    public ObservableCollection<AssetTypeNode> Types { get; } = new();

    /// <summary>Middle-pane list for the selected type, after the name filter.</summary>
    public ObservableCollection<AssetRowViewModel> Assets { get; } = new();

    public AssetManagerViewModel(IReadOnlyList<IAssetSource>? sources)
    {
        _sources = sources ?? Array.Empty<IAssetSource>();
        foreach (var s in _sources)
            Types.Add(new AssetTypeNode(s));

        CloseCommand   = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand = ReactiveCommand.Create(RefreshAssets);
        EditCommand    = ReactiveCommand.Create(RaiseOpen);

        // Default to the first type so the view opens on content, not blank.
        if (Types.Count > 0) SelectedType = Types[0];
    }

    private AssetTypeNode? _selectedType;
    public AssetTypeNode? SelectedType
    {
        get => _selectedType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            RefreshAssets();
        }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            this.RaiseAndSetIfChanged(ref _filterText, value);
            RefreshAssets();
        }
    }

    private AssetRowViewModel? _selectedAsset;
    public AssetRowViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAsset, value);
            this.RaisePropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedAsset != null;

    public string HeaderText =>
        SelectedType == null
            ? "Asset Manager"
            : $"Asset Manager — {SelectedType.DisplayName} ({Assets.Count})";

    public bool IsEmpty => Assets.Count == 0;

    /// <summary>Re-enumerate the selected source, apply the name filter, and
    /// reset the selection. Cheap — adapters snapshot in-memory library lists.</summary>
    public void RefreshAssets()
    {
        Assets.Clear();
        var src = SelectedType?.Source;
        if (src != null)
        {
            string filter = _filterText.Trim();
            foreach (var d in src.Enumerate())
            {
                if (filter.Length > 0 &&
                    d.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Assets.Add(new AssetRowViewModel(d));
            }
        }
        SelectedAsset = null;
        this.RaisePropertyChanged(nameof(HeaderText));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    public ReactiveCommand<Unit, Unit> CloseCommand   { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand    { get; }

    /// <summary>Ask the shell to open the selected asset in its own editor
    /// (A2 routing). No-op when nothing is selected.</summary>
    public void RaiseOpen()
    {
        var row = SelectedAsset;
        if (row == null) return;
        OpenRequested?.Invoke(this, new AssetOpenEventArgs(row.Descriptor.Kind, row.Descriptor.Name));
    }

    /// <summary>Bulk export (A3) — bundle the given rows' JSON into a zip and
    /// raise <see cref="ExportRequested"/> for the host to write. Rows are the
    /// middle-list's current multi-selection, passed from the view. No-op when
    /// empty or when nothing serializes.</summary>
    public void ExportBundle(IReadOnlyList<AssetRowViewModel> rows)
    {
        if (rows == null || rows.Count == 0) return;

        byte[] bytes;
        int written = 0;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var src = SourceFor(row.Descriptor.Kind);
                    string? json = src?.ExportJson(row.Descriptor.Name);
                    if (json == null) continue;

                    var entry = zip.CreateEntry(EntryPath(row.Descriptor, used), CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(json);
                    written++;
                }
            }
            bytes = ms.ToArray();
        }

        if (written == 0) return;
        ExportRequested?.Invoke(this,
            new AssetExportEventArgs(bytes, $"fracturingfog-assets-{DateTime.Now:yyyyMMdd-HHmmss}.zip", written));
    }

    /// <summary>Ask the host to pick a bundle file to import (A3 import). The
    /// host reads the bytes and calls back into <see cref="ImportBundle"/> — this
    /// VM never touches file dialogs (same split as export).</summary>
    public void RequestImport() => ImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Bulk import (A3 import) — read a zip bundle produced by
    /// <see cref="ExportBundle"/> and route each <c>&lt;Kind&gt;/&lt;name&gt;.json</c>
    /// entry back to its source. The folder segment picks the source; the entry's
    /// own Name keys the store. Same-name collisions replace when
    /// <paramref name="overwrite"/> is set, otherwise skip. Re-enumerates the
    /// current list afterwards so imports show immediately.</summary>
    public AssetImportSummary ImportBundle(byte[] zipBytes, bool overwrite)
    {
        var summary = new AssetImportSummary();
        if (zipBytes == null || zipBytes.Length == 0) return summary;

        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                // Directories / non-JSON payloads (entry.Name is blank for dir
                // markers) aren't assets.
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                if (!TryKindFromPath(entry.FullName, out var kind))
                {
                    summary.Failed++;
                    continue;
                }
                var src = SourceFor(kind);
                if (src == null) { summary.Failed++; continue; }

                string json;
                using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
                    json = r.ReadToEnd();

                var result = src.ImportJson(json, overwrite);
                summary.Tally(result.Status);
            }
        }
        catch (Exception)
        {
            // A malformed archive throws on open/read — report it as a bad bundle
            // rather than crashing the manager.
            summary.BadArchive = true;
        }

        RefreshAssets();
        return summary;
    }

    // First path segment ("Region/Foo.json" → "Region") is the AssetKind name the
    // export wrote. Case-insensitive so a hand-edited bundle still resolves.
    private static bool TryKindFromPath(string fullName, out AssetKind kind)
    {
        kind = default;
        int slash = fullName.IndexOf('/');
        if (slash <= 0) slash = fullName.IndexOf('\\'); // tolerate back-slash separators
        if (slash <= 0) return false;
        string folder = fullName.Substring(0, slash);
        return Enum.TryParse(folder, ignoreCase: true, out kind);
    }

    private IAssetSource? SourceFor(AssetKind kind)
    {
        foreach (var s in _sources)
            if (s.Kind == kind) return s;
        return null;
    }

    // "<Type>/<sanitized name>.json", de-duplicated so two assets that sanitize
    // to the same filename don't collide inside the archive.
    private static string EntryPath(AssetDescriptor d, HashSet<string> used)
    {
        string safe = Sanitize(d.Name);
        string baseP = $"{d.Kind}/{safe}";
        string path = baseP + ".json";
        int n = 1;
        while (!used.Add(path))
            path = $"{baseP} ({n++}).json";
        return path;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string result = sb.ToString().Trim();
        return result.Length == 0 ? "asset" : result;
    }

    /// <summary>Raised by the Close button; the shell hides the window.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user edits a row (Edit button / double-click).
    /// The shell routes the kind+name to the type's own editor.</summary>
    public event EventHandler<AssetOpenEventArgs>? OpenRequested;

    /// <summary>Raised with the assembled zip bytes for the host to save (A3).</summary>
    public event EventHandler<AssetExportEventArgs>? ExportRequested;

    /// <summary>Raised when the user clicks Import bundle — the host picks a zip,
    /// reads the bytes, and calls <see cref="ImportBundle"/> (A3 import).</summary>
    public event EventHandler? ImportRequested;
}

/// <summary>Tally of one bundle-import pass, shown back to the user (A3 import).</summary>
public sealed class AssetImportSummary
{
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>The archive itself couldn't be opened/read (not a valid zip).</summary>
    public bool BadArchive { get; set; }

    public int Total => Added + Replaced + Skipped + Failed;

    public void Tally(AssetImportStatus status)
    {
        switch (status)
        {
            case AssetImportStatus.Added:         Added++;    break;
            case AssetImportStatus.Replaced:      Replaced++; break;
            case AssetImportStatus.SkippedExists: Skipped++;  break;
            default:                              Failed++;   break;
        }
    }

    /// <summary>One-line human summary for the host's confirmation dialog.</summary>
    public string Describe()
    {
        if (BadArchive) return "Import failed: the file is not a valid asset bundle.";
        if (Total == 0) return "Nothing to import — the bundle held no assets.";

        var parts = new System.Collections.Generic.List<string>(4);
        if (Added > 0)    parts.Add($"{Added} added");
        if (Replaced > 0) parts.Add($"{Replaced} replaced");
        if (Skipped > 0)  parts.Add($"{Skipped} skipped (already exist)");
        if (Failed > 0)   parts.Add($"{Failed} failed");
        return "Import complete: " + string.Join(", ", parts) + ".";
    }
}

/// <summary>Carries an Asset Manager row's kind + name to the shell's editor
/// router (A2).</summary>
public sealed class AssetOpenEventArgs : EventArgs
{
    public AssetOpenEventArgs(AssetKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public AssetKind Kind { get; }
    public string Name { get; }
}

/// <summary>Carries an assembled export bundle (zip bytes + suggested filename +
/// asset count) from the Asset Manager to the host, which shows the save picker
/// and writes the file (A3).</summary>
public sealed class AssetExportEventArgs : EventArgs
{
    public AssetExportEventArgs(byte[] zipBytes, string suggestedName, int count)
    {
        ZipBytes = zipBytes;
        SuggestedName = suggestedName;
        Count = count;
    }

    public byte[] ZipBytes { get; }
    public string SuggestedName { get; }
    public int Count { get; }
}

/// <summary>Carries a host-owned editor request (source editors + slideshow
/// configs) from the shell to AvaloniaShellBootstrap, which owns those open
/// paths (A2).</summary>
public sealed class AssetHostEditorEventArgs : EventArgs
{
    public AssetHostEditorEventArgs(AssetKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public AssetKind Kind { get; }
    public string Name { get; }
}

/// <summary>Left-pane type-tree node — a thin display wrapper over one source.</summary>
public sealed class AssetTypeNode
{
    public AssetTypeNode(IAssetSource source) => Source = source;

    public IAssetSource Source { get; }
    public string DisplayName => Source.DisplayName;
    public AssetKind Kind => Source.Kind;
}

/// <summary>Middle-pane / detail row — display wrapper over an AssetDescriptor.</summary>
public sealed class AssetRowViewModel
{
    public AssetRowViewModel(AssetDescriptor descriptor) => Descriptor = descriptor;

    public AssetDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;
    public string KindLabel => Descriptor.Kind.ToString();
    public string SizeText => FormatSize(Descriptor.SizeOnDisk);
    public string CreatedText => Descriptor.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        return $"{kb / 1024.0:0.#} MB";
    }
}
