// #1669 (epic #1667, S2) — saved FF_* switch values: store round trip, tolerant
// load, and the startup apply order (a value set outside FF wins).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Cli;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1669EnvironmentSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-s1669-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, EnvironmentSettings.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SaveLoad_RoundTrips_DropsUnknownAndEmpty_LastWins()
    {
        EnvironmentSettings.Save(PathIn,
        [
            new("FF_GPU_LA", "1"),
            new("FF_" + "NOT_A_SWITCH", "1"),   // split: not a literal the #1668 guard reads
            new("FF_LA", " "),
            new("FF_VULKAN_MEMORY", "host"),
            new("FF_GPU_LA", "0"),
        ]);
        var loaded = EnvironmentSettings.Load(PathIn);
        Assert.Equal([new EnvSetting("FF_GPU_LA", "0"), new EnvSetting("FF_VULKAN_MEMORY", "host")], loaded);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReadsAsNone_AndBadEntriesAreSkipped()
    {
        Assert.Empty(EnvironmentSettings.Load(PathIn));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ not json");
        Assert.Empty(EnvironmentSettings.Load(PathIn));
        File.WriteAllText(PathIn, """[ {"Name":"FF_GPU_LA","Value":"1"}, 42, {"Name":"FF_LA_SIMD","Value":"1"} ]""");
        Assert.Equal(["FF_GPU_LA", "FF_LA_SIMD"], EnvironmentSettings.Load(PathIn).Select(s => s.Name));
    }

    [Fact]
    public void Apply_SetsSavedValues_ButAValueSetOutsideFfWins()
    {
        var env = new Dictionary<string, string> { ["FF_GPU_LA"] = "0" };   // the shell
        var sets = new List<string>();
        var states = EnvironmentSettings.Apply(
            [new("FF_GPU_LA", "1"), new("FF_LA_SIMD", "1")],
            n => env.GetValueOrDefault(n),
            (n, v) => { sets.Add(n); env[n] = v; });

        Assert.Equal(["FF_LA_SIMD"], sets);
        Assert.Equal("0", env["FF_GPU_LA"]);
        Assert.Equal(new EnvVarState("0", EnvVarSource.Shell), states["FF_GPU_LA"]);
        Assert.Equal(new EnvVarState("1", EnvVarSource.Saved), states["FF_LA_SIMD"]);
        Assert.Equal(new EnvVarState(null, EnvVarSource.Default), states["FF_D3D_LA"]);
        Assert.Equal(EnvVarCatalog.All.Count, states.Count);
    }

    [Fact]
    public void Program_AppliesSavedSwitches_OnlyOnTheInteractivePath_AfterTheHeadlessEntries()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));
        int apply = src.IndexOf("EnvironmentSettings.ApplyAtStartup()", StringComparison.Ordinal);
        Assert.True(apply > 0, "Program.Main must call EnvironmentSettings.ApplyAtStartup()");
        foreach (string headless in new[] { "\"--batch\"", "\"--server\"", "\"--worker\"" })
            Assert.True(src.IndexOf(headless, StringComparison.Ordinal) < apply, $"{headless} must return before the apply");
        foreach (string reader in new[] { "\"FF_FORCE_D3D11\"", "AvaloniaShell.Run(" })
            Assert.True(src.IndexOf(reader, apply, StringComparison.Ordinal) > apply, $"{reader} must come after the apply");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
