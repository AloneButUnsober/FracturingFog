// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 0a — policy-matrix coverage for the user-code trust-boundary gate.
// All cases live in one class so the shared static UserCodeSecurityPolicy.Mode
// is mutated serially (xUnit parallelises across classes, not within one), and
// each test restores the policy in a finally.

using System;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserCodeSecurityPolicyTests
{
    [Theory]
    // AllowAll: every origin permitted.
    [InlineData(RoslynUserCodeMode.AllowAll, UserCodeOrigin.Interactive, true)]
    [InlineData(RoslynUserCodeMode.AllowAll, UserCodeOrigin.BuiltIn, true)]
    [InlineData(RoslynUserCodeMode.AllowAll, UserCodeOrigin.ExternalFile, true)]
    // TrustedOnly (default): only ExternalFile denied.
    [InlineData(RoslynUserCodeMode.TrustedOnly, UserCodeOrigin.Interactive, true)]
    [InlineData(RoslynUserCodeMode.TrustedOnly, UserCodeOrigin.BuiltIn, true)]
    [InlineData(RoslynUserCodeMode.TrustedOnly, UserCodeOrigin.ExternalFile, false)]
    // DenyAll: nothing permitted.
    [InlineData(RoslynUserCodeMode.DenyAll, UserCodeOrigin.Interactive, false)]
    [InlineData(RoslynUserCodeMode.DenyAll, UserCodeOrigin.BuiltIn, false)]
    [InlineData(RoslynUserCodeMode.DenyAll, UserCodeOrigin.ExternalFile, false)]
    public void PolicyMatrix(RoslynUserCodeMode mode, UserCodeOrigin origin, bool expectedAllowed)
    {
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = mode;
            Assert.Equal(expectedAllowed, UserCodeSecurityPolicy.IsRoslynAllowed(origin));

            var gate = UserCodeGate.EnsureRoslynAllowed(origin);
            Assert.Equal(expectedAllowed, gate.Allowed);
            if (!expectedAllowed)
                Assert.False(string.IsNullOrWhiteSpace(gate.DenyReason));
            else
                Assert.Null(gate.DenyReason);
        }
        finally
        {
            UserCodeSecurityPolicy.Mode = prior;
        }
    }

    [Fact]
    public void ExternalFileDenial_PointsUserAtTheDsl()
    {
        // #27 Phase 0c — the deny message the editor surfaces (in yellow, via
        // the existing Classes.error #FFCC00 status style — never red, per the
        // colorblind guidance) must explain the block and steer to the DSL.
        var prior = UserCodeSecurityPolicy.Mode;
        try
        {
            UserCodeSecurityPolicy.Mode = RoslynUserCodeMode.TrustedOnly;
            var gate = UserCodeGate.EnsureRoslynAllowed(UserCodeOrigin.ExternalFile);
            Assert.False(gate.Allowed);
            Assert.Contains("Blocked", gate.DenyReason);
            Assert.Contains("DSL", gate.DenyReason);
        }
        finally { UserCodeSecurityPolicy.Mode = prior; }
    }

    [Fact]
    public void DefaultMode_IsTrustedOnly_WhenEnvUnset()
    {
        var prior = Environment.GetEnvironmentVariable("FF_ROSLYN_USERCODE");
        try
        {
            Environment.SetEnvironmentVariable("FF_ROSLYN_USERCODE", null);
            UserCodeSecurityPolicy.ResetToEnv();
            Assert.Equal(RoslynUserCodeMode.TrustedOnly, UserCodeSecurityPolicy.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FF_ROSLYN_USERCODE", prior);
            UserCodeSecurityPolicy.ResetToEnv();
        }
    }

    [Theory]
    [InlineData("allow-all", RoslynUserCodeMode.AllowAll)]
    [InlineData("AllowAll", RoslynUserCodeMode.AllowAll)]
    [InlineData("deny-all", RoslynUserCodeMode.DenyAll)]
    [InlineData("trusted-only", RoslynUserCodeMode.TrustedOnly)]
    [InlineData("garbage-value", RoslynUserCodeMode.TrustedOnly)] // fail safe
    public void EnvParsing(string envValue, RoslynUserCodeMode expected)
    {
        var prior = Environment.GetEnvironmentVariable("FF_ROSLYN_USERCODE");
        try
        {
            Environment.SetEnvironmentVariable("FF_ROSLYN_USERCODE", envValue);
            UserCodeSecurityPolicy.ResetToEnv();
            Assert.Equal(expected, UserCodeSecurityPolicy.Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FF_ROSLYN_USERCODE", prior);
            UserCodeSecurityPolicy.ResetToEnv();
        }
    }
}
