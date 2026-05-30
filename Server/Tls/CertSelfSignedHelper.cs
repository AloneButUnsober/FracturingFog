// Server/Tls/CertSelfSignedHelper.cs
// One-shot dev bundle generator. On first --server run we create a CA cert,
// a server cert signed by the CA (CN=fracturingfog-server), and a client
// cert signed by the CA (CN=fracturingfog-client). All PFX files use an
// empty password — the filesystem ACL on %APPDATA% is the security boundary
// here. Operators wanting strong key custody should pass --cert / --key /
// --client-ca paths pointing at their own PKI instead.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FracturingFog.Server.Tls;

public static class CertSelfSignedHelper
{
    public const string DefaultServerCnDnsName = "fracturingfog-server";
    public const string DefaultClientCnDnsName = "fracturingfog-client";

    public sealed record GeneratedBundle(string CaPath, string ServerPath, string ClientPath);

    /// <summary>
    /// Returns paths to ca.pfx / server.pfx / client.pfx under <paramref name="dir"/>,
    /// generating them if missing. Bundle is reused on subsequent runs so the
    /// client cert thumbprint stays stable.
    /// </summary>
    public static GeneratedBundle EnsureBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        string caPath     = Path.Combine(dir, "ca.pfx");
        string serverPath = Path.Combine(dir, "server.pfx");
        string clientPath = Path.Combine(dir, "client.pfx");

        if (File.Exists(caPath) && File.Exists(serverPath) && File.Exists(clientPath))
            return new GeneratedBundle(caPath, serverPath, clientPath);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter  = DateTimeOffset.UtcNow.AddYears(5);

        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(
            "CN=fracturingfog-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var caCert = caReq.CreateSelfSigned(notBefore, notAfter);

        File.WriteAllBytes(caPath, caCert.Export(X509ContentType.Pfx));

        using var serverKey = RSA.Create(2048);
        using var serverCert = SignLeaf(
            "CN=" + DefaultServerCnDnsName,
            DefaultServerCnDnsName,
            isServerAuth: true,
            leafKey: serverKey,
            caCert: caCert,
            notBefore, notAfter);
        File.WriteAllBytes(serverPath, serverCert.Export(X509ContentType.Pfx));

        using var clientKey = RSA.Create(2048);
        using var clientCert = SignLeaf(
            "CN=" + DefaultClientCnDnsName,
            DefaultClientCnDnsName,
            isServerAuth: false,
            leafKey: clientKey,
            caCert: caCert,
            notBefore, notAfter);
        File.WriteAllBytes(clientPath, clientCert.Export(X509ContentType.Pfx));

        return new GeneratedBundle(caPath, serverPath, clientPath);
    }

    private static X509Certificate2 SignLeaf(
        string subjectDn, string dnsName, bool isServerAuth,
        RSA leafKey, X509Certificate2 caCert,
        DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var req = new CertificateRequest(
            subjectDn, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var ekuOid = new OidCollection
        {
            new Oid(isServerAuth
                ? "1.3.6.1.5.5.7.3.1"   // serverAuth
                : "1.3.6.1.5.5.7.3.2")  // clientAuth
        };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(ekuOid, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        byte[] serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using X509Certificate2 signed = req.Create(caCert, notBefore, notAfter, serial);

        return signed.CopyWithPrivateKey(leafKey);
    }
}
