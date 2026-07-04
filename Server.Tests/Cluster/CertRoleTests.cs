using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using FracturingFog.Server.Tls;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class CertRoleTests
{
    [Theory]
    [InlineData("CN=ff-worker-01, OU=role-worker", CertRole.Worker)]
    [InlineData("CN=ff-admin-ui, OU=role-admin",   CertRole.Admin)]
    [InlineData("CN=ff-batch-cli, OU=role-client", CertRole.Client)]
    [InlineData("OU=role-worker, CN=ff-x",         CertRole.Worker)]   // RDN order does not matter
    [InlineData("CN=ff-x, OU=Acme, OU=role-admin", CertRole.Admin)]   // OU= role-* found among other OUs
    public void Parses_Each_Role_From_Subject_OU(string subject, CertRole expected)
    {
        using var cert = SelfSign(subject);
        Assert.Equal(expected, CertRoleParser.FromCertificate(cert));
    }

    [Theory]
    [InlineData("CN=ff-legacy-client")]                            // no OU at all → Client (back-compat)
    [InlineData("CN=ff-x, OU=Some Other OU")]                      // OU present but no role- prefix
    public void Missing_Role_OU_Defaults_To_Client(string subject)
    {
        using var cert = SelfSign(subject);
        Assert.Equal(CertRole.Client, CertRoleParser.FromCertificate(cert));
    }

    [Fact]
    public void Unknown_Role_Suffix_Throws()
    {
        using var cert = SelfSign("CN=ff-rogue, OU=role-superuser");
        var ex = Assert.Throws<InvalidOperationException>(
            () => CertRoleParser.FromCertificate(cert));
        Assert.Contains("superuser", ex.Message);
    }

    [Fact]
    public void Null_Cert_Defaults_To_Client()
    {
        Assert.Equal(CertRole.Client, CertRoleParser.FromCertificate(null));
    }

    [Fact]
    public void Case_Insensitive_Role_Suffix()
    {
        using var cert = SelfSign("CN=ff-x, OU=ROLE-WORKER");
        Assert.Equal(CertRole.Worker, CertRoleParser.FromCertificate(cert));
    }

    private static X509Certificate2 SelfSign(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(60));
    }
}
