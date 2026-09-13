// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #79A — the Asset Manager Export ▾ options: Selected / All-in-this-type /
// Everything. These drive the VM with fake IAssetSources (no store, no UI) and
// assert the emitted zip bundle carries exactly the expected <Kind>/<name>.json
// entries, resolving each descriptor to its own source (mixed-kind "Everything").

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using FracturingFog.Abstractions.Assets;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AssetManagerExportTests
{
    private sealed class FakeSource : IAssetSource
    {
        private readonly string[] _names;
        public FakeSource(AssetKind kind, params string[] names) { Kind = kind; _names = names; }
        public AssetKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public IEnumerable<AssetDescriptor> Enumerate()
            => _names.Select(n => new AssetDescriptor(n, Kind, null, 8, null));
        public bool Delete(string name) => false;
        public string? ExportJson(string name)
            => _names.Contains(name) ? $"{{\"name\":\"{name}\"}}" : null;
        public AssetImportResult ImportJson(string json, bool overwrite) => AssetImportResult.Fail;
    }

    private static AssetManagerViewModel MakeVm(out FakeSource region, out FakeSource theme)
    {
        region = new FakeSource(AssetKind.Region, "r1", "r2");
        theme = new FakeSource(AssetKind.ColorTheme, "t1");
        return new AssetManagerViewModel(new IAssetSource[] { region, theme });
    }

    private static List<string> ZipEntryPaths(byte[] zipBytes)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).OrderBy(s => s).ToList();
    }

    [Fact]
    public void ExportEverything_BundlesEveryAssetAcrossTypes()
    {
        var vm = MakeVm(out _, out _);
        AssetExportEventArgs? captured = null;
        vm.ExportRequested += (_, e) => captured = e;

        vm.ExportEverything();

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Count);
        var paths = ZipEntryPaths(captured.ZipBytes);
        Assert.Contains(paths, p => p.Contains("r1"));
        Assert.Contains(paths, p => p.Contains("r2"));
        Assert.Contains(paths, p => p.Contains("t1"));
    }

    [Fact]
    public void ExportAllOfCurrentType_BundlesOnlyThatType()
    {
        var vm = MakeVm(out _, out _);
        // Default selected type is the first (Region).
        AssetExportEventArgs? captured = null;
        vm.ExportRequested += (_, e) => captured = e;

        vm.ExportAllOfCurrentType();

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        var paths = ZipEntryPaths(captured.ZipBytes);
        Assert.Contains(paths, p => p.Contains("r1"));
        Assert.Contains(paths, p => p.Contains("r2"));
        Assert.DoesNotContain(paths, p => p.Contains("t1"));
    }

    [Fact]
    public void ExportBundle_Selected_BundlesOnlyGivenRows()
    {
        var vm = MakeVm(out _, out _);
        AssetExportEventArgs? captured = null;
        vm.ExportRequested += (_, e) => captured = e;

        // One row from the current (Region) type.
        var rows = vm.Assets.Where(a => a.Name == "r1").ToList();
        Assert.Single(rows);
        vm.ExportBundle(rows);

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.Count);
        Assert.Contains(ZipEntryPaths(captured.ZipBytes), p => p.Contains("r1"));
    }

    [Fact]
    public void ExportBundle_EmptySelection_RaisesNothing()
    {
        var vm = MakeVm(out _, out _);
        bool fired = false;
        vm.ExportRequested += (_, _) => fired = true;

        vm.ExportBundle(new List<AssetRowViewModel>());

        Assert.False(fired);
    }
}
