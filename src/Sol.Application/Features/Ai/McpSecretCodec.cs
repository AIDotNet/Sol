using Sol.Application.Abstractions.Security;
using Sol.Domain.Ai;

namespace Sol.Application.Features.Ai;

/// <summary>Packs MCP secret values into JSON-safe authenticated ciphertext strings.</summary>
/// <remarks>
/// Legacy unprefixed values are accepted as plaintext so existing registrations continue to work;
/// endpoint reads opportunistically rewrite them using <see cref="Protect"/>. The prefix keeps that
/// migration unambiguous and makes a future key-format version additive.
/// </remarks>
public static class McpSecretCodec
{
    private const string Prefix = "sol-secret:v1:";

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(IApiKeyProtector protector, string value)
    {
        var secret = protector.Protect(value);
        return string.Join(':',
            Prefix.TrimEnd(':'),
            Convert.ToBase64String(secret.Cipher),
            Convert.ToBase64String(secret.Nonce),
            Convert.ToBase64String(secret.Tag));
    }

    public static string Unprotect(IApiKeyProtector protector, string value)
    {
        if (!IsProtected(value)) return value;

        var parts = value.Split(':');
        if (parts.Length != 5
            || parts[0] != "sol-secret"
            || parts[1] != "v1")
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has an invalid format.");
        }

        try
        {
            var secret = new ApiKeySecret(
                Convert.FromBase64String(parts[2]),
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]),
                "••••");
            return protector.Unprotect(secret);
        }
        catch (FormatException exception)
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has an invalid encoding.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new System.Security.Cryptography.CryptographicException(
                "The stored MCP secret has invalid cryptographic parameters.", exception);
        }
    }
}
