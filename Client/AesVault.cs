// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Client/AesVault.cs
// PBKDF2(SHA-256, 200k iters) → 256-bit AES-GCM key. Used to encrypt the
// per-connection secrets in client-connections.json so a stolen disk does
// not yield plaintext credentials without the user's master password.

using System;
using System.Security.Cryptography;
using System.Text;

namespace FracturingFog.Client;

public static class AesVault
{
    public const int DefaultIterations = 200_000;
    public const int SaltBytes = 16;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KeyBytes = 32;

    public sealed class Sealed
    {
        public required byte[] Salt { get; init; }
        public required int Iterations { get; init; }
        public required byte[] Nonce { get; init; }
        public required byte[] Cipher { get; init; }
        public required byte[] Tag { get; init; }
    }

    public static byte[] NewSalt() { var s = new byte[SaltBytes]; RandomNumberGenerator.Fill(s); return s; }

    public static Sealed Encrypt(string plaintext, string masterPassword, byte[] salt, int iterations = DefaultIterations)
    {
        byte[] key = DeriveKey(masterPassword, salt, iterations);
        byte[] nonce = new byte[NonceBytes]; RandomNumberGenerator.Fill(nonce);
        byte[] body = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[body.Length];
        byte[] tag = new byte[TagBytes];
        using (var gcm = new AesGcm(key, TagBytes))
            gcm.Encrypt(nonce, body, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return new Sealed { Salt = salt, Iterations = iterations, Nonce = nonce, Cipher = cipher, Tag = tag };
    }

    public static string Decrypt(Sealed sealedBlob, string masterPassword)
    {
        byte[] key = DeriveKey(masterPassword, sealedBlob.Salt, sealedBlob.Iterations);
        byte[] plain = new byte[sealedBlob.Cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(sealedBlob.Nonce, sealedBlob.Cipher, sealedBlob.Tag, plain);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("master password did not unlock the vault");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
