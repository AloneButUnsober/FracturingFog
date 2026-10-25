// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1468 — the WinExe's probe programs (--xxxprobe measurement / parity flags) live in
// Probes/, not the repo root, and Probes/README.md lists every one of them.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1468ProbesFolderTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Probes_Live_In_The_Probes_Folder_And_Its_Readme_Lists_Them()
    {
        string root = RepoRoot();
        var atRoot = Directory.GetFiles(root, "*Probe.cs", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToList();
        Assert.True(atRoot.Count == 0, "probe files at the repo root (move them to Probes/): " + string.Join(", ", atRoot));

        string dir = Path.Combine(root, "Probes");
        var probes = Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToList();
        Assert.True(probes.Count >= 19, $"precondition: the Probes folder was found ({probes.Count} files)");
        string readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        var unlisted = probes.Where(f => !readme.Contains("`" + f + "`", StringComparison.Ordinal)).ToList();
        Assert.True(unlisted.Count == 0, "probes missing from Probes/README.md: " + string.Join(", ", unlisted));
    }
}
