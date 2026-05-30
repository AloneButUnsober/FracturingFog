// Server/Tls/ServerCertLoader.cs
// Loads the server identity (PFX) and the trusted client-CA bundle, and
// returns a RemoteCertificateValidationCallback that requires the client to
// present a cert chaining to one of the trusted CAs. Used by FFServer when
// it accepts a TCP connection and wraps the stream in SslStream.

using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace FracturingFog.Server.Tls;

public sealed class ServerTrust
{
    public required X509Certificate2 ServerIdentity { get; init; }
    public required X509Certificate2Collection TrustedClientCAs { get; init; }
}

public static class ServerCertLoader
{
    public static ServerTrust Load(string serverPfxPath, string clientCaPfxPath)
    {
        var server = X509CertificateLoader.LoadPkcs12FromFile(
            serverPfxPath, password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        var caCol = new X509Certificate2Collection();
        caCol.Add(X509CertificateLoader.LoadPkcs12FromFile(
            clientCaPfxPath, password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet));

        return new ServerTrust { ServerIdentity = server, TrustedClientCAs = caCol };
    }

    public static RemoteCertificateValidationCallback BuildClientValidator(
        X509Certificate2Collection trustedClientCAs)
    {
        return (sender, presented, chain, errors) =>
        {
            if (presented is null) return false;

            // Build a fresh chain that trusts ONLY the bundle the operator
            // provided. Default chain construction trusts the local machine
            // store; for mTLS-from-the-internet we want a closed trust set.
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 ca in trustedClientCAs)
                custom.ChainPolicy.CustomTrustStore.Add(ca);

            var leaf = presented as X509Certificate2
                ?? new X509Certificate2(presented);
            return custom.Build(leaf);
        };
    }
}
