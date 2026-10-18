// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Text;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1365 (S4 of #1360) — error spans for the DSL editors' inline underline:
/// <see cref="EquationErrorSpan"/> must invert the parsers' own position format,
/// and the Sandbox editor (which only receives the host's message) must recover
/// the span from it.
/// </summary>
[Collection(FractalRegionLibraryCollection.Name)]
public sealed class DslErrorSpan1365Tests
{
    [Fact]
    public void TryLocate_inverts_EquationSyntax_At_for_any_position()
    {
        // Independent of any parser: format a position exactly as the parsers
        // do, then locate it. Includes CRLF / tabs / empty lines.
        var rng = new Random(1365);
        const string alphabet = "z+c*\n\r\t (abc)";
        for (int k = 0; k < 2000; k++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(0, 50);
            for (int j = 0; j < len; j++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
            string src = sb.ToString();
            int pos = rng.Next(0, src.Length + 1);
            string message = $"Unexpected 'x' at {EquationSyntax.At(src, pos)}.";
            Assert.True(EquationErrorSpan.TryLocate(src, message, out int start, out _), message);
            Assert.Equal(pos, start);
        }
    }

    [Theory]
    [InlineData("z*z + radus", "radus")]                 // unknown identifier
    [InlineData("z*z + sinn(z)", "sinn")]               // unknown function
    [InlineData("let r = abs(z) in\n  r*r + cc", "cc")] // second line
    public void TryLocate_covers_the_unknown_name_in_real_2D_errors(string src, string bad)
    {
        Assert.False(EquationLanguage.TryParse(src, out _, out string? error));
        Assert.True(EquationErrorSpan.TryLocate(src, error, out int start, out int length), error);
        Assert.Equal(bad, src.Substring(start, length));
    }

    [Theory]
    [InlineData("vec(z.x, z.y, z.z) + radus")]
    [InlineData("triplex(z)^8 + c +")]
    [InlineData("sin(z)*cosh(z) + nosuch(z)")]
    [InlineData("let a = z in\n  a*a + (c")]
    public void TryLocate_agrees_with_the_bulb_parsers_own_position(string src)
    {
        // The bulb exception carries the position directly: the message-derived
        // start must equal it (the reference), so editors that only see the
        // message mark the same character the parser meant.
        var ex = Assert.Throws<SbxParseException>(() => SandboxBulbExpression.Parse(src, Array.Empty<string>()));
        Assert.True(EquationErrorSpan.TryLocate(src, ex.Message, out int start, out _), ex.Message);
        Assert.Equal(Math.Min(ex.Position, src.Length), start);
    }

    [Fact]
    public void TryLocate_rejects_messages_without_a_position()
    {
        Assert.False(EquationErrorSpan.TryLocate("z", "Source is empty", out _, out _));
        Assert.False(EquationErrorSpan.TryLocate("z", null, out _, out _));
        Assert.Equal("radius", EquationErrorSpan.DidYouMean("Unknown identifier 'radus' at line 1, col 7. Did you mean 'radius'?"));
        Assert.Null(EquationErrorSpan.DidYouMean("Unexpected ')' at line 1, col 3."));
    }

    private static readonly object s_rxLock = new();
    private static bool s_rxReady;

    private static SandboxViewModel NewSandbox(FractalParameters p)
    {
        lock (s_rxLock)
        {
            if (!s_rxReady)
            {
                try { ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp(); }
                catch (InvalidOperationException) { /* already initialised elsewhere */ }
                s_rxReady = true;
            }
        }
        return new SandboxViewModel(p);
    }

    [Fact]
    public void Sandbox_ShowError_recovers_the_span_and_clears_on_success()
    {
        const string src = "z*z + radus";
        var vm = NewSandbox(new FractalParameters { SandboxSource = src });
        int events = 0;
        vm.ErrorSpanChanged += () => events++;

        Assert.False(EquationLanguage.TryParse(src, out _, out string? error));
        vm.ShowError(error);
        Assert.True(vm.StatusIsError);
        Assert.Equal("radus", src.Substring(vm.ErrorSpanStart, vm.ErrorSpanLength));

        vm.ShowError(null);
        Assert.False(vm.StatusIsError);
        Assert.Equal(0, vm.ErrorSpanLength);
        Assert.Equal(2, events);
    }

    private static UserBulbViewModel NewBulb(FractalParameters p)
    {
        NewSandbox(new FractalParameters()); // ensures ReactiveUI is initialised
        return new UserBulbViewModel(p);
    }

    // The compile is debounced (1200 ms, on an Rx timer): wait for it.
    private static bool WaitForCompile(UserBulbViewModel vm, Action edit, int timeoutMs = 6000)
    {
        using var hit = new System.Threading.ManualResetEventSlim(false);
        EventHandler h = (_, _) => hit.Set();
        vm.CompileRequested += h;
        try
        {
            edit();
            return hit.Wait(timeoutMs);
        }
        finally { vm.CompileRequested -= h; }
    }

    [Fact]
    public void Editing_a_chain_step_schedules_a_compile()
    {
        // Smoke-test regression: step boxes bind straight to UserBulbChainStep,
        // so a step edit must reach the compiler (render + step error underline).
        var p = new FractalParameters { UserBulbSource = "z^8 + c" };
        p.UserBulbChain.Add(new UserBulbChainStep { OutputName = "s0", Source = "return z * z + c;" });
        var vm = NewBulb(p);

        Assert.True(WaitForCompile(vm, () => vm.Chain[0].Source = "return z * z + radus;"), "existing step edit");
        Assert.True(WaitForCompile(vm, () => vm.Chain[0].OutputName = "a0"), "output name edit");

        vm.AddChainCommand.Execute().Subscribe();
        var added = vm.Chain[^1];
        Assert.True(WaitForCompile(vm, () => added.Source = "return a0 * a0 + c;"), "added step edit");

        vm.RemoveChainCommand.Execute(added).Subscribe();
        Assert.False(WaitForCompile(vm, () => added.Source = "return z;", timeoutMs: 2500), "removed step must not compile");
    }

    [Fact]
    public void Chain_step_raises_PropertyChanged_only_on_change()
    {
        var step = new UserBulbChainStep();
        var seen = new System.Collections.Generic.List<string?>();
        step.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        step.Source = step.Source;            // unchanged: silent
        step.Source = "return z;";
        step.OutputName = "q";
        Assert.Equal(new[] { "Source", "OutputName" }, seen);
        // Serialised shape unchanged (event is not data).
        string json = System.Text.Json.JsonSerializer.Serialize(step);
        Assert.Equal("{\"OutputName\":\"q\",\"Source\":\"return z;\"}", json);
    }
}
