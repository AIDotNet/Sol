using System.Security.Cryptography;
using System.Text;
using Sol.Domain.Ai;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Security;

namespace Sol.UnitTests.Ai;

/// <summary>
/// API key protection.
/// </summary>
/// <remarks>
/// The highest-stakes code in the feature: a failure here either exposes credentials or makes
/// every stored key unrecoverable. The nonce-uniqueness and tamper-detection tests are the two
/// that matter most — GCM nonce reuse leaks the XOR of two plaintexts and the authentication
/// subkey, and an unauthenticated ciphertext could be swapped for one pointing at an
/// attacker-controlled endpoint.
/// </remarks>
public class ApiKeyProtectorTests
{
    /// <summary>Fixed so a failure is reproducible; never used outside tests.</summary>
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    private static AesGcmApiKeyProtector CreateProtector(string? key = null) =>
        new(new AiOptions { EncryptionKey = key ?? TestKey });

    [Fact]
    public void RoundTripsAKey()
    {
        var protector = CreateProtector();
        const string original = "sk-proj-abcdefghijklmnop1234567890";

        Assert.Equal(original, protector.Unprotect(protector.Protect(original)));
    }

    [Fact]
    public void RoundTripsNonAsciiKeys()
    {
        // Some vendors issue tokens containing non-ASCII; a byte-vs-char mistake would corrupt
        // them in a way only that vendor's users would ever hit.
        var protector = CreateProtector();
        const string original = "密钥-ключ-🔑-abc";

        Assert.Equal(original, protector.Unprotect(protector.Protect(original)));
    }

    [Fact]
    public void UsesAFreshNonceForEveryEncryption()
    {
        // The critical GCM property. Reuse under one key is catastrophic, so this must hold even
        // for identical plaintext.
        var protector = CreateProtector();
        const string original = "sk-same-key-every-time";

        var nonces = Enumerable
            .Range(0, 50)
            .Select(_ => Convert.ToHexString(protector.Protect(original).Nonce))
            .ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
    }

    [Fact]
    public void ProducesDifferentCiphertextForIdenticalInput()
    {
        var protector = CreateProtector();

        var first = protector.Protect("sk-identical");
        var second = protector.Protect("sk-identical");

        Assert.NotEqual(Convert.ToHexString(first.Cipher), Convert.ToHexString(second.Cipher));
    }

    [Fact]
    public void RejectsTamperedCiphertext()
    {
        var protector = CreateProtector();
        var secret = protector.Protect("sk-original-value");

        var tampered = secret.Cipher.ToArray();
        tampered[0] ^= 0xFF;

        // ThrowsAny, not Throws: AES-GCM raises AuthenticationTagMismatchException, a subclass.
        // The production code catches the base type, so that is the contract worth asserting.
        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(secret with { Cipher = tampered }));
    }

    [Fact]
    public void RejectsATamperedTag()
    {
        var protector = CreateProtector();
        var secret = protector.Protect("sk-original-value");

        var tag = secret.Tag.ToArray();
        tag[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(secret with { Tag = tag }));
    }

    [Fact]
    public void CannotDecryptWithADifferentKey()
    {
        // What key rotation does: every stored key becomes unrecoverable. Documented in
        // operations.md, asserted here so the consequence stays visible.
        var secret = CreateProtector().Protect("sk-original-value");

        var other = CreateProtector(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("a-completely-different-key-32byt")));

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Not base64.
    [InlineData("this is not base64 !!!")]
    // Valid base64, wrong length for AES-256.
    [InlineData("dG9vLXNob3J0")]
    public void FailsFastOnAMisconfiguredKey(string configured)
    {
        // Startup, not first use: a missing or malformed key is a deployment error and should
        // surface before any request reaches the app.
        Assert.Throws<InvalidOperationException>(() => CreateProtector(configured));
    }

    [Theory]
    [InlineData("sk-1234567890abcdef", "••••cdef")]
    [InlineData("short", "••••")]
    [InlineData("12345678", "••••")]
    [InlineData("123456789", "••••6789")]
    public void HintNeverRevealsMoreThanTheLastFourCharacters(string key, string expected)
    {
        // The hint is the only part that reaches the browser.
        Assert.Equal(expected, ApiKeySecret.BuildHint(key));
    }

    [Fact]
    public void HintIsNotASubstringOfAShortKey()
    {
        // A key short enough that four characters would be a meaningful fraction is fully
        // masked rather than partially revealed.
        Assert.DoesNotContain("shor", ApiKeySecret.BuildHint("short"));
    }
}
