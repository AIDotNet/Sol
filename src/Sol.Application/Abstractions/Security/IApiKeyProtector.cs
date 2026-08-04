using Sol.Domain.Ai;

namespace Sol.Application.Abstractions.Security;

/// <summary>
/// Encrypts and decrypts provider API keys.
/// </summary>
/// <remarks>
/// The key material never reaches the browser: the server stores ciphertext, returns only a
/// masked hint, and decrypts just long enough to attach the credential to an upstream request.
/// <para>
/// This bounds the damage from a database read, not from a compromised server — the process
/// holds the encryption key. Combined with device-scoped ownership it means a stolen backup is
/// inert, while a stolen device cookie is not. That ceiling is inherent to an app with no
/// accounts and is surfaced to users in settings.
/// </para>
/// </remarks>
public interface IApiKeyProtector
{
    ApiKeySecret Protect(string apiKey);

    /// <summary>
    /// Recovers the plaintext key.
    /// </summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The ciphertext failed authentication — it was tampered with, or the configured encryption
    /// key has changed. Callers should surface this as "re-enter your API key" rather than a
    /// generic error, since no amount of retrying will fix it.
    /// </exception>
    string Unprotect(ApiKeySecret secret);
}
