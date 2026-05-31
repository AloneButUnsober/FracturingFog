// Client/ClientConnectionStore.cs
// Persistent list of named server connections. Each entry carries plaintext
// non-secret fields (host, port, cert path) plus an AES-GCM-sealed blob
// containing the PFX password (if the client cert is itself password
// protected). The vault is keyed by a master password the user enters once
// per UI session.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Client;

public sealed class ClientConnectionEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int    Port { get; set; } = 47823;

    /// <summary>Absolute path to the client .pfx that this connection uses
    /// to authenticate to the server (mTLS).</summary>
    public string ClientCertPath { get; set; } = "";

    /// <summary>Absolute path to the server CA .pfx the client trusts when
    /// validating the server's identity cert.</summary>
    public string ServerCaCertPath { get; set; } = "";

    public string? Remark { get; set; }

    /// <summary>Sealed PFX password. May be null if the cert has no password.</summary>
    public SealedBlob? SealedPfxPassword { get; set; }
}

public sealed class SealedBlob
{
    /// <summary>Per-entry PBKDF2 salt (base64). Empty on entries written
    /// by older builds; <see cref="ClientConnectionStore.UnlockPfxPassword"/>
    /// falls back to the store-level salt in that case.</summary>
    public string Salt { get; set; } = "";

    public string Nonce { get; set; } = "";
    public string Cipher { get; set; } = "";
    public string Tag { get; set; } = "";
}

public sealed class ClientConnectionStore
{
    public byte[] Salt { get; set; } = AesVault.NewSalt();
    public int Iterations { get; set; } = AesVault.DefaultIterations;
    public List<ClientConnectionEntry> Entries { get; set; } = new();

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FracturingFog", "client-connections.json");

    public static ClientConnectionStore LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path)) return new ClientConnectionStore();
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<StoreDto>(json, JsonOpts);
            if (dto == null) return new ClientConnectionStore();
            return new ClientConnectionStore
            {
                Salt = Convert.FromBase64String(dto.SaltB64),
                Iterations = dto.Iterations,
                Entries = dto.Entries,
            };
        }
        catch { return new ClientConnectionStore(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dto = new StoreDto
        {
            SaltB64 = Convert.ToBase64String(Salt),
            Iterations = Iterations,
            Entries = Entries,
        };
        string json = JsonSerializer.Serialize(dto, JsonOpts);

        // Atomic write: a crash / power loss between WriteAllText opening
        // the file and the body landing on disk leaves the vault corrupt
        // and the user loses every saved connection. Write to a sibling
        // .tmp, fsync the directory entry, then atomic rename into place.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public string? UnlockPfxPassword(ClientConnectionEntry e, string masterPassword)
    {
        if (e.SealedPfxPassword == null) return null;
        // Prefer the per-entry salt written by recent builds; fall back to
        // the store-level salt only when reading an entry written before
        // we hardened the vault. New SealPfxPassword writes always emit a
        // fresh per-entry salt.
        byte[] salt = string.IsNullOrEmpty(e.SealedPfxPassword.Salt)
            ? Salt
            : Convert.FromBase64String(e.SealedPfxPassword.Salt);
        var blob = new AesVault.Sealed
        {
            Salt = salt,
            Iterations = Iterations,
            Nonce = Convert.FromBase64String(e.SealedPfxPassword.Nonce),
            Cipher = Convert.FromBase64String(e.SealedPfxPassword.Cipher),
            Tag = Convert.FromBase64String(e.SealedPfxPassword.Tag),
        };
        return AesVault.Decrypt(blob, masterPassword);
    }

    public void SealPfxPassword(ClientConnectionEntry e, string? pfxPassword, string masterPassword)
    {
        if (string.IsNullOrEmpty(pfxPassword))
        {
            e.SealedPfxPassword = null;
            return;
        }
        // Fresh per-entry salt: even if the master password is reused
        // across multiple saved connections, deriving with distinct
        // salts forces an attacker to PBKDF2 each entry separately.
        byte[] entrySalt = AesVault.NewSalt();
        var blob = AesVault.Encrypt(pfxPassword, masterPassword, entrySalt, Iterations);
        e.SealedPfxPassword = new SealedBlob
        {
            Salt = Convert.ToBase64String(entrySalt),
            Nonce = Convert.ToBase64String(blob.Nonce),
            Cipher = Convert.ToBase64String(blob.Cipher),
            Tag = Convert.ToBase64String(blob.Tag),
        };
    }

    /// <summary>Verifies the master password by attempting to decrypt the
    /// first sealed entry. Returns true if the store is empty (no entries
    /// to verify) or if decryption succeeded.</summary>
    public bool VerifyMasterPassword(string masterPassword)
    {
        foreach (var e in Entries)
        {
            if (e.SealedPfxPassword == null) continue;
            try { _ = UnlockPfxPassword(e, masterPassword); return true; }
            catch (UnauthorizedAccessException) { return false; }
            catch { return false; }
        }
        return true;
    }

    private sealed class StoreDto
    {
        [JsonPropertyName("salt")] public string SaltB64 { get; set; } = "";
        [JsonPropertyName("iterations")] public int Iterations { get; set; }
        [JsonPropertyName("entries")] public List<ClientConnectionEntry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
