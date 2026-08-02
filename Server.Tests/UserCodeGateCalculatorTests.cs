// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 0b — end-to-end gate wiring: the calculators read
// FractalParameters.UserCodeOrigin and refuse the raw-C# Roslyn compile for an
// ExternalFile origin under the default (TrustedOnly) policy, while interactive
// use and the Sandbox DSL stay unaffected.

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserCodeGateCalculatorTests
{
    private const string ValidRoslynBulb = "return Vec3.Pow(z, 8.0) + c;";
    private const string ValidSandboxBulb = "triplex(z, 8) + c";

    [Fact]
    public void ExternalFile_RawCsharp_IsRefused_UnderDefaultPolicy()
    {
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserBulbCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbSource = ValidRoslynBulb,
                    UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(ValidRoslynBulb);
            Assert.False(calc.IsCompiled);
            Assert.Contains("Blocked", calc.LastError);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void Interactive_RawCsharp_Compiles()
    {
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserBulbCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbSource = ValidRoslynBulb,
                    UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                    UserCodeOrigin = UserCodeOrigin.Interactive,
                }
            };
            calc.Compile(ValidRoslynBulb);
            Assert.True(calc.IsCompiled, $"expected compile, got: {calc.LastError}");
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void ExternalFile_SandboxDsl_IsAllowed()
    {
        // The DSL interpreter has no BCL access, so it is ungated even for an
        // untrusted origin.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserBulbCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbSource = ValidSandboxBulb,
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(ValidSandboxBulb);
            Assert.True(calc.IsCompiled, $"expected sandbox compile, got: {calc.LastError}");
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    // A source with NO DSL form (member access can't be expressed in the
    // Sandbox grammar). #27 Phase 3: the raw-C# Roslyn fallback is gone, so
    // such a source no longer executes for ANY origin — it surfaces an
    // editable DSL error instead. `return z*z + c;` no longer belongs here —
    // it translates to the DSL and runs (see
    // UserEquation_ExternalFile_TranslatableEquation_RunsOnDsl).
    private const string RoslynOnlyEquation = "return z.Real + c;";

    [Fact]
    public void UserEquation_NoDslForm_DoesNotExecute_AnyOrigin()
    {
        // #27 Phase 3 — no Roslyn path at all. A member-access body has no DSL
        // form, so it fails to compile even for a trusted (Interactive) origin,
        // and the error is a crisp DSL message (not the gate's "Blocked").
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = RoslynOnlyEquation,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(RoslynOnlyEquation);
        Assert.False(calc.IsCompiled);
        Assert.False(calc.UsingDsl);
        Assert.DoesNotContain("Blocked", calc.LastError);
        Assert.False(string.IsNullOrWhiteSpace(calc.LastError));
    }

    [Fact]
    public void UserEquation_ExternalFile_NoDslForm_DoesNotExecute()
    {
        // Same body, untrusted origin: still no execution (no Roslyn to gate),
        // same DSL error path — the origin no longer changes the outcome.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserEquationCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserEquationSource = RoslynOnlyEquation,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(RoslynOnlyEquation);
            Assert.False(calc.IsCompiled);
            Assert.False(calc.UsingDsl);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void UserEquation_ExternalFile_TranslatableEquation_RunsOnDsl()
    {
        // #27 Phase 1b — an untrusted equation that the DSL can represent runs
        // on the safe interpreter (no Roslyn), so it is NOT refused. This is
        // the surface reduction: the file-borne source executes without ever
        // touching the raw-C# path.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserEquationCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserEquationSource = "return z*z + c;",
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile("return z*z + c;");
            Assert.True(calc.IsCompiled, $"expected DSL compile, got: {calc.LastError}");
            Assert.True(calc.UsingDsl);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void UserEquation_Interactive_TranslatableEquation_PrefersDsl()
    {
        // Even for a trusted origin the safe DSL is preferred when the source
        // translates — the raw path is reserved for constructs with no DSL form.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "return Complex.Pow(z, 2) + c;",
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile("return Complex.Pow(z, 2) + c;");
        Assert.True(calc.IsCompiled, $"expected compile, got: {calc.LastError}");
        Assert.True(calc.UsingDsl);
    }

    [Fact]
    public void UserEquation_Interactive_MemberAccessBody_ErrorGuidesToDsl()
    {
        // #27 Phase 3 — with the Roslyn fallback gone, a trusted member-access
        // body no longer compiles. The error should be actionable (mentions the
        // DSL / a supported form) rather than a bare failure, since the user
        // must now rewrite `z.Real` as the DSL `re(z)`.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = RoslynOnlyEquation,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(RoslynOnlyEquation);
        Assert.False(calc.IsCompiled);
        Assert.False(calc.UsingDsl);
        Assert.False(string.IsNullOrWhiteSpace(calc.LastError));
    }

    [Fact]
    public void Origin_SurvivesClone()
    {
        var p = new FractalParameters { UserCodeOrigin = UserCodeOrigin.ExternalFile };
        var clone = p.Clone();
        Assert.Equal(UserCodeOrigin.ExternalFile, clone.UserCodeOrigin);
    }

    // ── #27 Phase 2c — bulb DSL-first with trusted Roslyn fallback ──────────

    [Fact]
    public void Bulb_Sandbox_CsharpBody_Interactive_FallsBackToRoslyn()
    {
        // Default compiler is now Sandbox. A trusted legacy C# body has no DSL
        // form (the DSL parse fails), so it falls back to the gated Roslyn path.
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbSource = ValidRoslynBulb,
                UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(ValidRoslynBulb);
        Assert.True(calc.IsCompiled, $"expected Roslyn fallback, got: {calc.LastError}");
    }

    [Fact]
    public void Bulb_Sandbox_CsharpBody_ExternalFile_IsRefused()
    {
        // Untrusted C# body under the Sandbox default: no Roslyn fallback, and
        // the confusing DSL parse error is replaced by the gate's block notice.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserBulbCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbSource = ValidRoslynBulb,
                    UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(ValidRoslynBulb);
            Assert.False(calc.IsCompiled);
            Assert.Contains("Blocked", calc.LastError);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void Bulb_Sandbox_DslTypo_KeepsDslError_NoFallback()
    {
        // A DSL author's typo (no C# markers) keeps its DSL parse error and
        // never switches engines — even for a trusted origin.
        const string dslTypo = "frobnicate(z) + c";
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbSource = dslTypo,
                UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(dslTypo);
        Assert.False(calc.IsCompiled);
        Assert.DoesNotContain("Blocked", calc.LastError);
    }

    [Fact]
    public void Bulb_Sandbox_DslBody_Compiles_NoFallback()
    {
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbSource = ValidSandboxBulb,
                UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(ValidSandboxBulb);
        Assert.True(calc.IsCompiled, $"expected DSL compile, got: {calc.LastError}");
    }
}
