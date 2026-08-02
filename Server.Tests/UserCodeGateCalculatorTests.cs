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
    // A raw-C# bulb body (member/static access, statements) — no DSL form.
    // #27 Phase 3: the Roslyn compile path and its trusted fallback are gone,
    // so this no longer executes for ANY origin; it fails the DSL parse.
    private const string CsharpBulbBody = "return Vec3.Pow(z, 8.0) + c;";
    private const string ValidSandboxBulb = "triplex(z, 8) + c";

    [Fact]
    public void Bulb_CsharpBody_DoesNotExecute_ExternalFile()
    {
        // #27 Phase 3 — no Roslyn path. A C# body from an untrusted file fails
        // the DSL parse and never executes. The compiler selector is ignored.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var calc = new UserBulbCalculator(8, 8)
            {
                FractalParameters = new FractalParameters
                {
                    UserBulbSource = CsharpBulbBody,
                    UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(CsharpBulbBody);
            Assert.False(calc.IsCompiled);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void Bulb_CsharpBody_DoesNotExecute_Interactive()
    {
        // #27 Phase 3 — even a trusted (Interactive) C# body no longer compiles;
        // with the Roslyn fallback deleted the DSL is the only path.
        var calc = new UserBulbCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbSource = CsharpBulbBody,
                UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(CsharpBulbBody);
        Assert.False(calc.IsCompiled);
        Assert.False(string.IsNullOrWhiteSpace(calc.LastError));
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

    // A source with NO DSL form. #27 Phase 3: the raw-C# Roslyn fallback is
    // gone, so such a source no longer executes for ANY origin — it surfaces an
    // editable DSL error instead. `Complex.Abs` is a durable example: the DSL's
    // `abs` has different semantics (|z| vs |z|²), so the preprocessor rejects
    // it outright. (`z.Real` no longer belongs here — #27 Phase 5a translates
    // member access to `re(z)`; `return z*z + c;` translates and runs.)
    private const string NoDslFormEquation = "return Complex.Abs(z) + c;";

    [Fact]
    public void UserEquation_NoDslForm_DoesNotExecute_AnyOrigin()
    {
        // #27 Phase 3 — no Roslyn path at all. A body with no DSL form fails to
        // compile even for a trusted (Interactive) origin, and the error is a
        // crisp DSL message (not the gate's "Blocked").
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = NoDslFormEquation,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(NoDslFormEquation);
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
                    UserEquationSource = NoDslFormEquation,
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile(NoDslFormEquation);
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
    public void UserEquation_Interactive_NoDslForm_ErrorNotEmpty()
    {
        // #27 Phase 3 — with the Roslyn fallback gone, a trusted body with no DSL
        // form no longer compiles, and the failure carries an actionable message
        // (here the preprocessor's Complex.Abs rejection) rather than a bare
        // failure.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = NoDslFormEquation,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(NoDslFormEquation);
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

    // ── #27 Phase 3 — bulb DSL is the only compiler (no Roslyn fallback) ────

    [Fact]
    public void Bulb_Sandbox_DslTypo_KeepsDslError()
    {
        // A DSL author's typo keeps its DSL parse error — the compile fails with
        // a plain parser message, never a gate "Blocked" notice.
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
