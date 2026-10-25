// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1455 — Hosting\*.cs belong to FracturingFog.Hosting.dll, but the WinExe
// (FracturingFogCLD.csproj) compiles its folder tree by default glob and only
// skips the files it lists in <Compile Remove="Hosting\…">. A new Hosting file
// left off that list compiles into BOTH assemblies: the WinExe references
// Hosting.dll, so a public type then collides with itself (CS0436, as the new
// GpuComputeSelection.cs did in code review) — or sits there as a silent
// duplicate until something names it. Every Hosting\*.cs must be listed.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1455HostingCompileOwnershipTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_Hosting_Source_File_Is_Removed_From_The_WinExe_Glob()
    {
        string root = RepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "FracturingFogCLD.csproj"));
        var removed = Regex.Matches(csproj, @"<Compile\s+Remove=""Hosting[\\/]([^""*]+\.cs)""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = Directory.GetFiles(Path.Combine(root, "Hosting"), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).ToList();

        Assert.True(sources.Count > 10, "precondition: the Hosting folder was found");
        Assert.Contains("GpuComputeSelection.cs", removed);
        var missing = sources.Where(f => !removed.Contains(f!)).ToList();
        Assert.True(missing.Count == 0,
            "Hosting files compiled into the WinExe too (add <Compile Remove=\"Hosting\\<file>\" /> to FracturingFogCLD.csproj): "
            + string.Join(", ", missing));
    }
}
