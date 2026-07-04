// Server/Tls/CertRole.cs
// Extracts a cluster role from a presented X.509 client certificate.
//
// The role is encoded as an Organisational Unit (OU) entry in the cert's
// Subject DN with one of three exact values:
//
//   OU=role-worker
//   OU=role-client
//   OU=role-admin
//
// Chosen over a custom X.509 extension OID so that operators can issue
// role-tagged certs with stock tooling (`openssl req -subj`,
// `New-SelfSignedCertificate -Subject`) without registering a Private
// Enterprise Number. A future hardening pass can add a SAN-URI alternative
// (`urn:fracturingfog:role:worker`) without breaking this primary path.
//
// Backwards compatibility: a cert with no role OU resolves to Client. The
// single-server protocol path (render.image / render.video) continues to
// work with the existing self-signed dev bundle exactly as before.

using System;
using System.Security.Cryptography.X509Certificates;

namespace FracturingFog.Server.Tls;

public enum CertRole
{
    Client,
    Worker,
    Admin,
}

public static class CertRoleParser
{
    public const string OuPrefix = "role-";

    /// <summary>Resolve the role of a presented client certificate.
    /// Returns Client when no role OU is present so that legacy certs
    /// issued before the cluster work was added keep their existing
    /// (client-only) capabilities.</summary>
    public static CertRole FromCertificate(X509Certificate2? cert)
    {
        if (cert is null) return CertRole.Client;

        // Subject DN is a comma-separated RDN string in OpenSSL order; on
        // .NET it comes back already in the canonical "CN=..., OU=..., ..."
        // form. Split on commas, trim each component, look for OU=role-*.
        // We do NOT use X500DistinguishedName.Decode here because it
        // bakes locale-sensitive separators into the output and we want
        // a stable parse regardless of host culture.
        foreach (var raw in cert.Subject.Split(','))
        {
            string rdn = raw.Trim();
            if (!rdn.StartsWith("OU=", StringComparison.OrdinalIgnoreCase)) continue;

            string value = rdn[3..].Trim();
            if (!value.StartsWith(OuPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string roleText = value[OuPrefix.Length..];
            if (string.Equals(roleText, "worker", StringComparison.OrdinalIgnoreCase))
                return CertRole.Worker;
            if (string.Equals(roleText, "client", StringComparison.OrdinalIgnoreCase))
                return CertRole.Client;
            if (string.Equals(roleText, "admin", StringComparison.OrdinalIgnoreCase))
                return CertRole.Admin;
            // Unknown role suffix — refuse rather than silently downgrade.
            // A misissued cert with OU=role-superuser must not be treated
            // as Client; throwing forces the operator to fix the cert.
            throw new InvalidOperationException(
                $"unrecognised role '{roleText}' in cert OU; expected worker|client|admin");
        }
        return CertRole.Client;
    }
}
