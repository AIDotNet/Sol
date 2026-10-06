using System.Security.Cryptography;
using System.Text;
using Sol.Application.Abstractions.Common;
using Sol.Application.Abstractions.Security;
using Sol.Infrastructure.Options;

namespace Sol.Infrastructure.Security;

/// <summary>
/// HMAC-signed, expiring links to canvas assets, keyed with <see cref="AiOptions.EncryptionKey"/>.
/// </summary>
/// <remarks>
/// The token is <c>{expiry}.{hmac}</c> over the asset id and that same expiry, so a link authorizes
/// exactly one asset for a bounded window and nothing else can be minted without the server key.
/// Links are therefore safe to hand to third parties (an upstream video vendor) without making the
/// asset library world-readable: a guessed or replayed-after-expiry link fails validation.
/// <para>
/// The lifetime is generous — days, not minutes — because a vendor may sit on a submitted job
/// in a queue before fetching its references. It is a privacy window, not a security one: the
/// asset is non-secret user media, and the device-owned API still governs every human path to it.
/// </para>
/// </remarks>
internal sealed class AssetLinkSigner(AiOptions options, IClock clock) : IAssetLinkSigner
{
    /// <summary>How long a minted link stays fetchable.</summary>
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(7);

    private readonly byte[] _key = DecodeKey(options.EncryptionKey);

    public string? CreatePublicUrl(Guid assetId)
    {
        // Trimmed here rather than trusted to the binder: a trailing slash would produce a
        // double-slash path that some upstream fetchers normalize differently than we validate.
        var origin = options.PublicOrigin?.TrimEnd('/');

        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        var expiry = clock.UtcNow.Add(LinkLifetime).ToUnixTimeSeconds();

        return $"{origin}/api/v1/canvas/assets/{assetId}"
            + $"?token={Encode(expiry, Mac(assetId, expiry))}";
    }

    public bool TryValidateToken(Guid assetId, string token)
    {
        var separator = token.IndexOf('.');
        if (separator < 1
            || separator >= token.Length - 1
            || !long.TryParse(token[..separator], out var expiry))
        {
            return false;
        }

        if (clock.UtcNow.ToUnixTimeSeconds() > expiry)
        {
            return false;
        }

        // Constant-time: the token is bearer evidence, so a timing side channel that let a
        // caller forge a MAC byte by byte would defeat the signature entirely.
        var expected = Encode(expiry, Mac(assetId, expiry));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(token[(separator + 1)..]),
            Encoding.ASCII.GetBytes(expected[(separator + 1)..]));
    }

    private byte[] Mac(Guid assetId, long expiry) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"asset:{assetId:N}:{expiry}"));

    private static string Encode(long expiry, byte[] mac) =>
        $"{expiry}.{Convert.ToBase64String(mac).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";

    private static byte[] DecodeKey(string configured)
    {
        // Mirrors AesGcmApiKeyProtector's validation, which runs at startup with the same
        // options; the checks here keep a misconfigured key from producing a silent, uniform
        // failure rather than a named one.
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey is not configured. Generate one with "
                + "`openssl rand -base64 32`.");
        }

        var key = Convert.FromBase64String(configured);

        return key.Length == 32
            ? key
            : throw new InvalidOperationException(
                $"{AiOptions.SectionName}:EncryptionKey must decode to 32 bytes; got {key.Length}.");
    }
}
