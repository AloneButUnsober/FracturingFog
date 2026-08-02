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
                    UserEquationSource = "return z*z + c;",
                    UserCodeOrigin = UserCodeOrigin.ExternalFile,
                }
            };
            calc.Compile("return z*z + c;");
            Assert.False(calc.IsCompiled);
            Assert.Contains("Blocked", calc.LastError);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void Origin_SurvivesClone()
    {
        var p = new FractalParameters { UserCodeOrigin = UserCodeOrigin.ExternalFile };
        var clone = p.Clone();
        Assert.Equal(UserCodeOrigin.ExternalFile, clone.UserCodeOrigin);
    }
}
