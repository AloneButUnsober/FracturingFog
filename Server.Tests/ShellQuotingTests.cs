// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Cli;
using FracturingFog.UI.Avalonia.Services;
using Xunit;

namespace FracturingFog.Server.Tests;

// #999 (CB7 of #64) — shell-dialect quoting is checked against the shells' own
// parsers wherever they are available: CommandLineToArgvW (the C-runtime argv
// rules a cmd-launched program uses), a real bash, and PowerShell's argument
// parsing. A missing shell skips its oracle rather than faking it.
public sealed class ShellQuotingTests
{
    // Tokens that break naive quoting in at least one shell.
    public static readonly string[] Tricky =
    {
        "plain", "--param", "JuliaCRe=-0.8", "-0.5", "with space", "Fire 3D (PBR)",
        @"C:\Program Files\FF\", @"C:\out\", @"trailing\\", @"C:\a\b.png",
        "it's", "say \"hi\"", "#FF8800", "0.5,1.2", "<OUTPUT.png>", "$HOME", "a&b|c",
        "back`tick", "semi;colon", "(paren)", "{brace}", "@at", "", "tab\there", "x=\"y z\"\\",
    };

    // ── cmd: CommandLineToArgvW ──────────────────────────────────────────────

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] ArgvOf(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int n);
        try
        {
            var result = new string[n];
            for (int i = 0; i < n; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(argv); }
    }

    [Fact]
    public void Cmd_QuotingMatchesTheCRuntimeArgvRules()
    {
        if (!OperatingSystem.IsWindows()) return;
        string line = "x.exe " + string.Join(" ", Tricky.Select(t => ShellQuoting.Quote(t, CommandShell.Cmd)));
        Assert.Equal(Tricky, ArgvOf(line).Skip(1));
    }

    // ── bash ─────────────────────────────────────────────────────────────────

    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe" })
            if (File.Exists(p)) return p;
        return null;   // not System32\bash.exe — that is WSL
    }

    private static string[] RunForArgs(string exe, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardInput = stdin != null,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            // Script via stdin: a Windows command line would be re-parsed by the
            // MSYS runtime (backslash rules), which is not what is under test.
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Split('\0')[..^1];
    }

    [Fact]
    public void Bash_QuotingSurvivesARealBash()
    {
        var bash = FindBash();
        if (bash == null) return;
        string script = "printf '%s\\0' " + string.Join(" ", Tricky.Select(t => ShellQuoting.Quote(t, CommandShell.Bash)));
        Assert.Equal(Tricky, RunForArgs(bash, script + "\n", "-s"));
    }

    // ── PowerShell ───────────────────────────────────────────────────────────

    private static string? FindPowerShell()
    {
        if (OperatingSystem.IsWindows())
        {
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(ps)) return ps;
        }
        return null;
    }

    [Fact]
    public void PowerShell_QuotingSurvivesPowerShellArgumentParsing()
    {
        var ps = FindPowerShell();
        if (ps == null) return;
        // A function receives arguments through the same argument-mode parser a
        // native command line goes through (quotes, #, commas, $, backticks).
        string script = "function f { foreach ($a in $args) { [Console]::Out.Write([string]$a + [char]0) } }; "
                      + "[Console]::OutputEncoding = [Text.Encoding]::UTF8; f "
                      + string.Join(" ", Tricky.Select(t => ShellQuoting.Quote(t, CommandShell.PowerShell)));
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Assert.Equal(Tricky, RunForArgs(ps, null, "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded));
    }

    // ── Joining + scripts ────────────────────────────────────────────────────

    [Fact]
    public void PlainTokens_StayBare()
    {
        foreach (var shell in Enum.GetValues<CommandShell>())
            Assert.Equal("FracturingFog --batch --x -0.5 --param JuliaCRe=-0.8",
                ShellQuoting.Join("FracturingFog", new[] { "--x", "-0.5", "--param", "JuliaCRe=-0.8" }, shell));
    }

    [Fact]
    public void AQuotedExecutable_UsesTheCallOperatorInPowerShell()
    {
        string exe = @"C:\Program Files\FF\FracturingFog.exe";
        Assert.StartsWith(@"& ""C:\Program Files\FF\FracturingFog.exe"" --batch", ShellQuoting.Join(exe, Array.Empty<string>(), CommandShell.PowerShell));
        Assert.StartsWith("\"C:\\Program Files\\FF\\FracturingFog.exe\" --batch", ShellQuoting.Join(exe, Array.Empty<string>(), CommandShell.Cmd));
    }

    // A path without spaces needs no quoting in either Windows shell (backslash
    // is not special in PowerShell), so there is no call operator to trip over.
    [Fact]
    public void APlainExePath_IsBare_WithNoCallOperator()
    {
        string exe = @"C:\NeverEnding\bin\FracturingFog.exe";
        foreach (var shell in new[] { CommandShell.PowerShell, CommandShell.Cmd })
            Assert.StartsWith(exe + " --batch", ShellQuoting.Join(exe, Array.Empty<string>(), shell));
    }

    // Everyday values quote the same way for both Windows shells, so a copied
    // command pastes into either (the report behind this: single quotes failed
    // in Command Prompt).
    [Fact]
    public void EverydayCommands_AreIdentical_ForCmdAndPowerShell()
    {
        var args = new[] { "--theme", "Fire 3D (PBR)", "--out", "<OUTPUT.png>", "--light1-dir", "0.785,1.41",
                           "--region", "Seahorse Valley", "--x", "-0.5", "--out", @"C:\temp\as test.png" };
        string exe = @"C:\NeverEnding\bin\FracturingFog.exe";
        string ps = ShellQuoting.Join(exe, args, CommandShell.PowerShell);
        Assert.Equal(ShellQuoting.Join(exe, args, CommandShell.Cmd), ps);
        Assert.DoesNotContain("'", ps);
    }

    // PowerShell expands $ and ` inside "...": those values keep the literal '...'.
    [Theory]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("back`tick", "'back`tick'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("it's here", "\"it's here\"")]
    public void PowerShell_ExpandingCharacters_StayLiteral(string value, string expected)
        => Assert.Equal(expected, ShellQuoting.Quote(value, CommandShell.PowerShell));

    [Fact]
    public void Default_IsCommandPromptOnWindows()
        => Assert.Equal(OperatingSystem.IsWindows() ? CommandShell.Cmd : CommandShell.Bash, ShellQuoting.Default);

    [Fact]
    public void Scripts_PropagateTheExitCode_AndCmdEscapesPercent()
    {
        Assert.EndsWith("exit $LASTEXITCODE\r\n", ShellQuoting.Script("FracturingFog --batch", CommandShell.PowerShell));
        // GUI-subsystem exe: without the pipe PowerShell would not wait for it.
        Assert.Contains("FracturingFog --batch | Out-Default\r\n", ShellQuoting.Script("FracturingFog --batch", CommandShell.PowerShell));
        string cmd = ShellQuoting.Script("FracturingFog --batch --name \"50%\"", CommandShell.Cmd);
        Assert.StartsWith("@echo off", cmd);
        Assert.Contains("\"50%%\"", cmd);
        Assert.EndsWith("exit /b %ERRORLEVEL%\r\n", cmd);
        Assert.StartsWith("#!/usr/bin/env bash\n", ShellQuoting.Script("FracturingFog --batch", CommandShell.Bash));
        Assert.Equal(".ps1", ShellQuoting.ScriptExtension(CommandShell.PowerShell));
    }

    // ── Console output splitting ─────────────────────────────────────────────

    private static List<string> Collect(params string[] chunks)
    {
        var log = new ConsoleLog();
        var splitter = new ConsoleLineSplitter(log.Add);
        foreach (var c in chunks) splitter.Feed(c);
        splitter.Flush();
        return log.Lines.ToList();
    }

    [Fact]
    public void CarriageReturnRedraws_CollapseToTheirLastState()
    {
        Assert.Equal(new[] { "start", "[####] 100%", "done" },
            Collect("start\r\n[#   ] 25%\r[##  ] 50%\r", "[####] 100%\r\ndone\n"));
    }

    [Fact]
    public void CrLf_SplitAcrossReads_IsOneNewline()
    {
        Assert.Equal(new[] { "a", "b" }, Collect("a\r", "\nb"));
        Assert.Equal(new[] { "c" }, Collect("x\r", "c"));   // a lone CR overwrites
    }

    [Fact]
    public void Log_IsBounded()
    {
        var log = new ConsoleLog(maxLines: 3);
        for (int i = 0; i < 10; i++) log.Add(i.ToString(), false);
        Assert.Equal(new[] { "7", "8", "9" }, log.Lines);
    }

    // ── Process runner ───────────────────────────────────────────────────────

    [Fact]
    public async Task Runner_StreamsOutput_AndReturnsTheExitCode()
    {
        var lines = new List<string>();
        int? exit = await BatchProcessRunner.RunAsync("dotnet", new[] { "--version" }, (t, _) => lines.Add(t), CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains(lines, l => l.Length > 0 && char.IsDigit(l[0]));
    }

    [Fact]
    public async Task Runner_CancelKillsTheProcess()
    {
        (string exe, string[] args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var sw = Stopwatch.StartNew();
        int? exit = await BatchProcessRunner.RunAsync(exe, args, (_, _) => { }, cts.Token);
        Assert.Null(exit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }
}
