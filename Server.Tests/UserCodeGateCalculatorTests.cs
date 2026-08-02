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
    // Sandbox grammar) still falls through to the gated Roslyn path and is
    // refused for an untrusted origin. #27 Phase 1b: `return z*z + c;` no
    // longer belongs here — it now translates to the DSL and runs safely
    // (see UserEquation_ExternalFile_TranslatableEquation_RunsOnDsl).
    private const string RoslynOnlyEquation = "return z.Real + c;";

    [Fact]
    public void UserEquation_ExternalFile_RawCsharp_IsRefused()
    {
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
            Assert.Contains("Blocked", calc.LastError);
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
    public void UserEquation_Interactive_RoslynOnlyEquation_UsesRoslynFallback()
    {
        // A trusted source with no DSL form still compiles — via the Roslyn
        // fallback — so interactive editing never regresses.
        var calc = new UserEquationCalculator(8, 8)
        {
            FractalParameters = new FractalParameters
            {
                UserEquationSource = RoslynOnlyEquation,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            }
        };
        calc.Compile(RoslynOnlyEquation);
        Assert.True(calc.IsCompiled, $"expected Roslyn compile, got: {calc.LastError}");
        Assert.False(calc.UsingDsl);
    }

    [Fact]
    public void Origin_SurvivesClone()
    {
        var p = new FractalParameters { UserCodeOrigin = UserCodeOrigin.ExternalFile };
        var clone = p.Clone();
        Assert.Equal(UserCodeOrigin.ExternalFile, clone.UserCodeOrigin);
    }
}
