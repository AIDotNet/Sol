using Sol.Application.Abstractions.Common;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Security;

namespace Sol.UnitTests.Ai;

public class AssetLinkSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FixedClock _clock = new(Now);

    [Fact]
    public void RoundTripUrlValidates()
    {
        var signer = Signer("https://sol.example.com");
        var url = signer.CreatePublicUrl(Guid.NewGuid());

        Assert.NotNull(url);
        Assert.StartsWith("https://sol.example.com/api/v1/canvas/assets/", url);
        Assert.Contains("?token=", url);

        var token = url.Split("?token=")[1];
        var id = Guid.Parse(url.Split("/assets/")[1].Split('?')[0]);

        Assert.True(signer.TryValidateToken(id, token));
    }

    [Fact]
    public void TokenForAnotherAssetIsRejected()
    {
        var signer = Signer("https://sol.example.com");
        var url = signer.CreatePublicUrl(Guid.NewGuid())!;
        var token = url.Split("?token=")[1];

        Assert.False(signer.TryValidateToken(Guid.NewGuid(), token));
    }

    [Fact]
    public void TamperedTokenIsRejected()
    {
        var signer = Signer("https://sol.example.com");
        var id = Guid.NewGuid();
        var token = signer.CreatePublicUrl(id)!.Split("?token=")[1];

        // Flip the signature, not the expiry: a forged timestamp is the interesting forgery.
        var tampered = token[..^1] + (token.EndsWith("A") ? "B" : "A");

        Assert.False(signer.TryValidateToken(id, tampered));
    }

    [Fact]
    public void ExpiredTokenIsRejected()
    {
        var signer = Signer("https://sol.example.com");
        var id = Guid.NewGuid();
        var token = signer.CreatePublicUrl(id)!.Split("?token=")[1];

        _clock.UtcNow = Now.Add(TimeSpan.FromDays(8));

        Assert.False(signer.TryValidateToken(id, token));
    }

    [Fact]
    public void WithoutPublicOriginNoUrlIsMinted()
    {
        Assert.Null(Signer("").CreatePublicUrl(Guid.NewGuid()));
    }

    [Fact]
    public void PublicOriginTrailingSlashIsNormalized()
    {
        var url = Signer("https://sol.example.com/").CreatePublicUrl(Guid.NewGuid())!;

        Assert.DoesNotContain("//api/", url);
    }

    private AssetLinkSigner Signer(string publicOrigin) => new(
        new AiOptions
        {
            EncryptionKey = Convert.ToBase64String(new byte[32]),
            PublicOrigin = publicOrigin,
        },
        _clock);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
