// Server/Tls/CertSelfSignedHelper.cs
// One-shot dev bundle generator. On first --server run we create a CA cert,
// a server cert signed by the CA (CN=fracturingfog-server), and a client
// cert signed by the CA (CN=fracturingfog-client). All PFX files use an
// empty password — the filesystem ACL on %APPDATA% is the security boundary
// here. Operators wanting strong key custody should pass --cert / --key /
// --client-ca paths pointing at their own PKI instead.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace FracturingFog.Server.Tls;

public static class CertSelfSignedHelper
{
    public const string DefaultServerCnDnsName = "fracturingfog-server";
    public const string DefaultClientCnDnsName = "fracturingfog-client";
    public const string DefaultWorkerCnDnsName = "fracturingfog-worker";
    public const string DefaultAdminCnDnsName  = "fracturingfog-admin";

    public sealed record GeneratedBundle(string CaPath, string ServerPath, string ClientPath);

    public sealed record GeneratedClusterBundle(
        string CaPath, string MasterPath,
        string WorkerPath, string ClientPath, string AdminPath);

    /// <summary>
    /// Returns paths to ca.pfx / server.pfx / client.pfx under <paramref name="dir"/>,
    /// generating them if missing. Bundle is reused on subsequent runs so the
    /// client cert thumbprint stays stable.
    /// </summary>
    public static GeneratedBundle EnsureBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        // Tighten directory ACL on Windows so a co-resident process
        // running under the same user account cannot lift the empty-
        // password PFX files. Empty-password PFX + lax ACL = anyone
        // who can read the file impersonates this server's clients.
        TryRestrictDirectoryToOwner(dir);

        string caPath     = Path.Combine(dir, "ca.pfx");
        string serverPath = Path.Combine(dir, "server.pfx");
        string clientPath = Path.Combine(dir, "client.pfx");

        if (File.Exists(caPath) && File.Exists(serverPath) && File.Exists(clientPath))
            return new GeneratedBundle(caPath, serverPath, clientPath);

        // Any of the three pfx files missing means we re-generate the
        // entire bundle. A previous partial-write must not be reused — a
        // server.pfx without its matching ca.pfx leaves the client unable
        // to validate the server.
        TryDelete(caPath); TryDelete(serverPath); TryDelete(clientPath);

        // 10-year validity on a long-lived self-signed dev bundle so the
        // operator does not silently hit expiry mid-deployment. Bundle is
        // reused across runs — if the file exists we never regenerate.
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter  = DateTimeOffset.UtcNow.AddYears(10);

        using var caKey = RSA.Create(3072);
        var caReq = new CertificateRequest(
            "CN=fracturingfog-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var caCert = caReq.CreateSelfSigned(notBefore, notAfter);

        using var serverKey = RSA.Create(3072);
        using var serverCert = SignLeaf(
            "CN=" + DefaultServerCnDnsName,
            DefaultServerCnDnsName,
            isServerAuth: true,
            leafKey: serverKey,
            caCert: caCert,
            notBefore, notAfter);

        using var clientKey = RSA.Create(3072);
        using var clientCert = SignLeaf(
            "CN=" + DefaultClientCnDnsName,
            DefaultClientCnDnsName,
            isServerAuth: false,
            leafKey: clientKey,
            caCert: caCert,
            notBefore, notAfter);

        // Export bytes BEFORE any disk write so a failure mid-export does
        // not leave a .tmp sibling on disk. Then atomic-write each pfx via
        // .tmp + Move so a crash partway through the bundle generation
        // never leaves a corrupt half-written .pfx the next run would
        // skip-regenerate-over.
        byte[] caBytes     = caCert.Export(X509ContentType.Pfx);
        byte[] serverBytes = serverCert.Export(X509ContentType.Pfx);
        byte[] clientBytes = clientCert.Export(X509ContentType.Pfx);

        AtomicWrite(caPath,     caBytes);
        AtomicWrite(serverPath, serverBytes);
        AtomicWrite(clientPath, clientBytes);

        return new GeneratedBundle(caPath, serverPath, clientPath);
    }

    /// <summary>
    /// D-2b: returns paths to ca.pfx / master.pfx / worker.pfx / client.pfx /
    /// admin.pfx under <paramref name="dir"/>, generating them if missing.
    /// Worker, client, and admin leaf certs carry an OU=role-{worker|client|admin}
    /// so the master's <see cref="CertRoleParser"/> routes their RPC calls
    /// per the role policy in FFServer.DispatchClusterAsync. Atomic across
    /// the five files — if any is missing the whole bundle is regenerated.
    /// </summary>
    public static GeneratedClusterBundle EnsureClusterBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        TryRestrictDirectoryToOwner(dir);

        string caPath     = Path.Combine(dir, "ca.pfx");
        string masterPath = Path.Combine(dir, "master.pfx");
        string workerPath = Path.Combine(dir, "worker.pfx");
        string clientPath = Path.Combine(dir, "cluster-client.pfx");
        string adminPath  = Path.Combine(dir, "admin.pfx");

        if (File.Exists(caPath) && File.Exists(masterPath) && File.Exists(workerPath)
            && File.Exists(clientPath) && File.Exists(adminPath))
            return new GeneratedClusterBundle(caPath, masterPath, workerPath, clientPath, adminPath);

        TryDelete(caPath); TryDelete(masterPath); TryDelete(workerPath);
        TryDelete(clientPath); TryDelete(adminPath);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter  = DateTimeOffset.UtcNow.AddYears(10);

        using var caKey = RSA.Create(3072);
        var caReq = new CertificateRequest(
            "CN=fracturingfog-cluster-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        caReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caReq.PublicKey, false));
        using var caCert = caReq.CreateSelfSigned(notBefore, notAfter);

        using var masterKey = RSA.Create(3072);
        using var masterCert = SignLeaf(
            "CN=" + DefaultServerCnDnsName,
            DefaultServerCnDnsName, isServerAuth: true,
            masterKey, caCert, notBefore, notAfter);

        using var workerKey = RSA.Create(3072);
        using var workerCert = SignLeaf(
            $"CN={DefaultWorkerCnDnsName}, OU=role-worker",
            DefaultWorkerCnDnsName, isServerAuth: false,
            workerKey, caCert, notBefore, notAfter);

        using var clientKey = RSA.Create(3072);
        using var clientCert = SignLeaf(
            $"CN={DefaultClientCnDnsName}, OU=role-client",
            DefaultClientCnDnsName, isServerAuth: false,
            clientKey, caCert, notBefore, notAfter);

        using var adminKey = RSA.Create(3072);
        using var adminCert = SignLeaf(
            $"CN={DefaultAdminCnDnsName}, OU=role-admin",
            DefaultAdminCnDnsName, isServerAuth: false,
            adminKey, caCert, notBefore, notAfter);

        byte[] caBytes     = caCert.Export(X509ContentType.Pfx);
        byte[] masterBytes = masterCert.Export(X509ContentType.Pfx);
        byte[] workerBytes = workerCert.Export(X509ContentType.Pfx);
        byte[] clientBytes = clientCert.Export(X509ContentType.Pfx);
        byte[] adminBytes  = adminCert.Export(X509ContentType.Pfx);

        AtomicWrite(caPath,     caBytes);
        AtomicWrite(masterPath, masterBytes);
        AtomicWrite(workerPath, workerBytes);
        AtomicWrite(clientPath, clientBytes);
        AtomicWrite(adminPath,  adminBytes);

        return new GeneratedClusterBundle(caPath, masterPath, workerPath, clientPath, adminPath);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        TryDelete(tmp);
        File.WriteAllBytes(tmp, bytes);
        // File.Move(overwrite:true) is atomic on Windows + POSIX when src
        // and dst share a filesystem (always true here — same directory).
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ACL TRADE-OFF NOTE:
    //   ApplyWindowsOwnerOnlyAcl below grants FullControl to the *current
    //   user SID* and strips everything else. What this DOES protect:
    //     - A co-resident process under another local user account can
    //       no longer read the empty-password pfx + impersonate this
    //       server's clients.
    //   What this does NOT protect:
    //     - Any process running as SYSTEM, an Administrator, or as the
    //       same user account. Same-user isolation is not a Windows ACL
    //       feature — if the threat model includes a sibling process you
    //       launched yourself, use a real PKI with password-protected
    //       pfx files instead of the empty-password dev bundle.
    //     - A backup tool with SeBackupPrivilege.
    //     - Filesystem snapshots / shadow copies (the protected ACL is
    //       on the live file; older copies retain whatever ACL existed
    //       at the time the snapshot was taken).
    //   The POSIX 0700 path has the same blind spots versus root + same-
    //   uid sibling processes. Operators with stricter requirements
    //   should mint their own pfx with --cert / --key / --client-ca.
    private static void TryRestrictDirectoryToOwner(string dir)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // POSIX: 0700. Best-effort — File.SetUnixFileMode is .NET 8+.
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return;
            }
            ApplyWindowsOwnerOnlyAcl(dir);
        }
        catch { /* best-effort */ }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsOwnerOnlyAcl(string dir)
    {
        var info = new DirectoryInfo(dir);
        var sec  = info.GetAccessControl();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Strip every inherited and explicit rule, then grant FullControl
        // to the current user only.
        AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule r in rules)
            sec.RemoveAccessRuleSpecific(r);

        var owner = WindowsIdentity.GetCurrent().User;
        if (owner != null)
        {
            sec.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        info.SetAccessControl(sec);
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
