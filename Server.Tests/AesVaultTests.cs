using System;
using FracturingFog.Client;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AesVaultTests
{
    // Low iteration count keeps the suite fast — these tests cover crypto
    // wiring (round-trip + auth-tag rejection), not key-stretching strength.
    private const int FastIters = 1_000;

    [Fact]
    public void RoundTrip_RecoversPlaintext()
    {
        byte[] salt = AesVault.NewSalt();
        var sealedBlob = AesVault.Encrypt("hunter2 — render-box client cert", "master", salt, FastIters);
        string roundTrip = AesVault.Decrypt(sealedBlob, "master");
        Assert.Equal("hunter2 — render-box client cert", roundTrip);
    }

    [Fact]
    public void WrongPassword_ThrowsUnauthorized()
    {
        byte[] salt = AesVault.NewSalt();
        var sealedBlob = AesVault.Encrypt("secret", "right-password", salt, FastIters);
        Assert.Throws<UnauthorizedAccessException>(() =>
            AesVault.Decrypt(sealedBlob, "wrong-password"));
    }

    [Fact]
    public void TamperedCiphertext_ThrowsUnauthorized()
    {
        byte[] salt = AesVault.NewSalt();
        var sealedBlob = AesVault.Encrypt("payload", "pw", salt, FastIters);
        // Flip one bit in the ciphertext — AES-GCM's auth tag must catch it.
        sealedBlob.Cipher[0] ^= 0x01;
        Assert.Throws<UnauthorizedAccessException>(() =>
            AesVault.Decrypt(sealedBlob, "pw"));
    }

    [Fact]
    public void TamperedTag_ThrowsUnauthorized()
    {
        byte[] salt = AesVault.NewSalt();
        var sealedBlob = AesVault.Encrypt("payload", "pw", salt, FastIters);
        sealedBlob.Tag[^1] ^= 0xFF;
        Assert.Throws<UnauthorizedAccessException>(() =>
            AesVault.Decrypt(sealedBlob, "pw"));
    }

    [Fact]
    public void PerEntrySalt_ProducesDifferentCiphertext()
    {
        var a = AesVault.Encrypt("same plaintext", "same password", AesVault.NewSalt(), FastIters);
        var b = AesVault.Encrypt("same plaintext", "same password", AesVault.NewSalt(), FastIters);
        Assert.NotEqual(Convert.ToBase64String(a.Cipher), Convert.ToBase64String(b.Cipher));
        // Both must still decrypt with the right password.
        Assert.Equal("same plaintext", AesVault.Decrypt(a, "same password"));
        Assert.Equal("same plaintext", AesVault.Decrypt(b, "same password"));
    }
}
