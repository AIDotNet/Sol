using Sol.Application.Features.Ai;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Security;

namespace Sol.UnitTests.Ai;

public class McpSecretCodecTests
{
    private const string TestKey = "dGVzdC1rZXktZm9yLXVuaXQtdGVzdHMtMzJieXRlcyE=";

    [Fact]
    public void ProtectProducesAuthenticatedPackedCiphertext()
    {
        var protector = CreateProtector();
        var packed = McpSecretCodec.Protect(protector, "Bearer private-token");

        Assert.StartsWith("sol-secret:v1:", packed);
        Assert.DoesNotContain("private-token", packed, StringComparison.Ordinal);
        Assert.Equal("Bearer private-token", McpSecretCodec.Unprotect(protector, packed));
    }

    [Fact]
    public void LegacyPlaintextRemainsReadableForMigration()
    {
        var protector = CreateProtector();

        Assert.False(McpSecretCodec.IsProtected("legacy-token"));
        Assert.Equal("legacy-token", McpSecretCodec.Unprotect(protector, "legacy-token"));
    }

    [Fact]
    public void TamperedCiphertextCannotBeRecovered()
    {
        var protector = CreateProtector();
        var packed = McpSecretCodec.Protect(protector, "secret");
        var changed = packed[..^2] + "AA";

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => McpSecretCodec.Unprotect(protector, changed));
    }

    private static AesGcmApiKeyProtector CreateProtector() =>
        new(new AiOptions { EncryptionKey = TestKey });
}
