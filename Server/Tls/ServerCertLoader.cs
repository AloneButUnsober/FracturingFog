// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Tls/ServerCertLoader.cs
// Loads the server identity (PFX) and the trusted client-CA bundle, and
// returns a RemoteCertificateValidationCallback that requires the client to
// present a cert chaining to one of the trusted CAs. Used by FFServer when
// it accepts a TCP connection and wraps the stream in SslStream.

using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FracturingFog.Server.Tls;

public sealed class ServerTrust
{
    public required X509Certificate2 ServerIdentity { get; init; }
    /// <summary>Self-signed (root) CAs that anchor the custom trust store.
    /// A presented client cert chain must terminate at one of these.</summary>
    public required X509Certificate2Collection TrustedClientCAs { get; init; }
    /// <summary>Intermediate CAs supplied via the same pfx bundle. Passed
    /// to <see cref="X509Chain.ChainPolicy.ExtraStore"/> so a chain like
    /// leaf → intermediate → root validates without the client having to
    /// ship intermediates inside the TLS handshake. Empty when the
    /// operator deploys a one-tier (single self-signed root) PKI.</summary>
    public X509Certificate2Collection IntermediateClientCAs { get; init; } = new();
}

public static class ServerCertLoader
{
    public static ServerTrust Load(string serverPfxPath, string clientCaPfxPath)
    {
        var server = X509CertificateLoader.LoadPkcs12FromFile(
            serverPfxPath, password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        // Load every cert in the client-CA pfx — operators may ship a
        // bundle holding [root, intermediate-1, intermediate-2, ...] to
        // support multi-tier PKI. Without LoadPkcs12CollectionFromFile
        // only the leaf-most entry is returned and intermediate chains
        // fail validation with UntrustedRoot.
        X509Certificate2Collection bundle;
        try
        {
            bundle = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                clientCaPfxPath, password: null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        catch (CryptographicException)
        {
            // Older pfx tooling sometimes emits a single-cert file that
            // the collection loader rejects. Fall back to the single-cert
            // path so a one-tier self-signed dev bundle still loads.
            bundle = new X509Certificate2Collection
            {
                X509CertificateLoader.LoadPkcs12FromFile(
                    clientCaPfxPath, password: null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet)
            };
        }

        var roots = new X509Certificate2Collection();
        var intermediates = new X509Certificate2Collection();
        foreach (X509Certificate2 c in bundle)
        {
            // Self-issued cert (Subject == Issuer) is treated as a root
            // anchor; everything else is an intermediate the chain
            // builder may consult but does not anchor to. The leaf
            // (client) cert itself is never expected in this bundle, but
            // would silently fall into ExtraStore and be ignored if so.
            if (string.Equals(c.Subject, c.Issuer, StringComparison.Ordinal))
                roots.Add(c);
            else
                intermediates.Add(c);
        }
        if (roots.Count == 0)
            throw new InvalidOperationException(
                $"client-CA bundle '{clientCaPfxPath}' contains no self-signed root certificate");

        // Surface impending expiry on stdout so the operator notices
        // before clients fail handshakes. 30-day soft warn, immediate
        // throw past NotAfter — an expired server cert is a hard fault.
        var now = DateTime.UtcNow;
        Warn("server", server, now);
        foreach (X509Certificate2 ca in roots)         Warn("ca-root", ca, now);
        foreach (X509Certificate2 ca in intermediates) Warn("ca-intermediate", ca, now);

        return new ServerTrust
        {
            ServerIdentity = server,
            TrustedClientCAs = roots,
            IntermediateClientCAs = intermediates,
        };

        static void Warn(string role, X509Certificate2 cert, DateTime now)
        {
            DateTime na = cert.NotAfter.ToUniversalTime();
            TimeSpan left = na - now;
            if (left < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"{role} cert expired {(-left).TotalDays:F0} day(s) ago (NotAfter {na:O})");
            if (left < TimeSpan.FromDays(30))
                Console.WriteLine(
                    $"WARN: {role} cert expires in {left.TotalDays:F0} day(s) ({na:O})");
        }
    }

    public static RemoteCertificateValidationCallback BuildClientValidator(
        X509Certificate2Collection trustedClientCAs,
        IReadOnlyCollection<string>? allowedThumbprints = null,
        X509RevocationMode revocationMode = X509RevocationMode.NoCheck,
        X509Certificate2Collection? intermediateClientCAs = null)
    {
        HashSet<string>? pinned = null;
        if (allowedThumbprints is { Count: > 0 })
        {
            pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in allowedThumbprints)
            {
                string norm = NormalizeThumbprint(raw);
                if (norm.Length > 0) pinned.Add(norm);
            }
            if (pinned.Count == 0) pinned = null;
        }

        return (sender, presented, chain, errors) =>
        {
            if (presented is null) return false;

            // Build a fresh chain that trusts ONLY the bundle the operator
            // provided. Default chain construction trusts the local machine
            // store; for mTLS-from-the-internet we want a closed trust set.
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = revocationMode;
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 ca in trustedClientCAs)
                custom.ChainPolicy.CustomTrustStore.Add(ca);
            // Intermediates are NOT roots — they go into ExtraStore so the
            // chain builder can walk leaf → intermediate → root without
            // the client having had to ship intermediates inline. Without
            // this a multi-tier PKI deployment fails the handshake with
            // PartialChain even when the operator-provided bundle was
            // complete.
            if (intermediateClientCAs is { Count: > 0 })
            {
                foreach (X509Certificate2 ca in intermediateClientCAs)
                    custom.ChainPolicy.ExtraStore.Add(ca);
            }

            var leaf = presented as X509Certificate2
                ?? new X509Certificate2(presented);
            if (!custom.Build(leaf)) return false;

            if (pinned != null)
            {
                // Accept allowlist entries in either SHA-1 (40 hex chars,
                // legacy Windows certmgr default) or SHA-256 (64 hex chars,
                // modern best practice — SHA-1 collisions are public). Pin
                // matches when EITHER digest of the presented cert appears
                // in the allowlist. Operators should migrate entries to
                // SHA-256, but mixed allowlists remain valid during the
                // rotation window.
                string sha1   = NormalizeThumbprint(leaf.Thumbprint);
                string sha256 = NormalizeThumbprint(leaf.GetCertHashString(HashAlgorithmName.SHA256));
                if (!pinned.Contains(sha1) && !pinned.Contains(sha256))
                    return false;
            }
            return true;
        };
    }

    /// <summary>Strip spaces, dashes, and ":" separators; uppercase. Lets
    /// operators paste thumbprints in whatever format Windows / openssl /
    /// the browser cert viewer hands them.</summary>
    public static string NormalizeThumbprint(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char c in raw)
        {
            if (c is ' ' or '-' or ':') continue;
            buf[n++] = char.ToUpperInvariant(c);
        }
        return new string(buf[..n]);
    }
}
