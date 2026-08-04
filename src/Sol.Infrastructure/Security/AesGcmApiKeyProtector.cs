using System.Security.Cryptography;
using System.Text;
using Sol.Application.Abstractions.Security;
using Sol.Domain.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Security;

/// <summary>
/// AES-256-GCM protection for provider API keys.
/// </summary>
/// <remarks>
/// GCM rather than CBC because it authenticates as well as encrypts: a modified ciphertext fails
/// to decrypt instead of yielding plausible garbage that gets sent to a third party as a
/// credential.
/// <para>
/// A fresh 96-bit nonce is generated per encryption and stored beside the ciphertext. Nonce
/// reuse under one key is catastrophic for GCM — it leaks the XOR of two plaintexts and the
/// authentication subkey — so it is never derived from the record or reused across writes.
/// </para>
/// <para>
/// <see cref="AesGcm"/> is not thread-safe, so a new instance is constructed per call rather
/// than cached on this singleton. The key schedule is cheap next to the network round trip that
/// follows.
/// </para>
/// </remarks>
internal sealed class AesGcmApiKeyProtector : IApiKeyProtector
{
    private readonly byte[] _key;

    public AesGcmApiKeyProtector(AiOptions options)
    {
        var configured = options.EncryptionKey;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey is not configured. Generate one with "
                + "`openssl rand -base64 32`. Rotating it makes every stored API key "
                + "undecryptable, so treat it as durable secret material.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must be base64-encoded.", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must decode to 32 bytes for AES-256; "
                + $"got {key.Length}.");
        }

        _key = key;
    }

    public ApiKeySecret Protect(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        CryptographicOperations.ZeroMemory(plaintext);

        return new ApiKeySecret(cipher, nonce, tag, ApiKeySecret.BuildHint(apiKey));
    }

    public string Unprotect(ApiKeySecret secret)
    {
        var plaintext = new byte[secret.Cipher.Length];

        using var aes = new AesGcm(_key, AesGcm.TagByteSizes.MaxSize);

        // Throws CryptographicException when the tag does not verify, which is the intended
        // behaviour: a key that cannot be authenticated must not be forwarded upstream.
        aes.Decrypt(secret.Nonce, secret.Cipher, secret.Tag, plaintext);

        var result = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        return result;
    }
}
