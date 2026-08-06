using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai.Protocols;

namespace Sol.UnitTests.Ai;

/// <summary>The content blocks <see cref="SeedanceVideoClient"/> sends upstream.</summary>
public class SeedanceVideoRequestTests
{
    private const string BaseUrl = "https://example.com/v1";

    [Fact]
    public void ReferenceModeLabelsEveryImageAsReferenceMedia()
    {
        var body = SeedanceVideoClient.BuildRequestBody(Request(
            new ReferenceImage([1, 2, 3], "image/png")));

        var content = Assert.IsType<JsonArray>(body["content"]);
        var image = Assert.IsType<JsonObject>(content[0]);

        Assert.Equal("image_url", (string?)image["type"]);
        Assert.Equal("reference_image", (string?)image["role"]);
        Assert.Equal(
            "data:image/png;base64,AQID",
            (string?)image["image_url"]?["url"]);
    }

    [Fact]
    public void ReferenceModeKeepsMultipleImagesAsReferenceMedia()
    {
        var body = SeedanceVideoClient.BuildRequestBody(Request(
            new ReferenceImage([1], "image/png"),
            new ReferenceImage([2], "image/jpeg")));

        var content = Assert.IsType<JsonArray>(body["content"]);

        Assert.Equal("reference_image", (string?)content[0]?["role"]);
        Assert.Equal("reference_image", (string?)content[1]?["role"]);
        Assert.Equal("text", (string?)content[2]?["type"]);
        Assert.Equal("move forward", (string?)content[2]?["text"]);
    }

    [Fact]
    public void FirstLastModeUsesOnlyTheTwoFrameRoles()
    {
        var body = SeedanceVideoClient.BuildRequestBody(RequestWithMode(
            5,
            "first-last",
            new ReferenceImage([1], "image/png"),
            new ReferenceImage([2], "image/jpeg")));

        var content = Assert.IsType<JsonArray>(body["content"]);

        Assert.Equal("first_frame", (string?)content[0]?["role"]);
        Assert.Equal("last_frame", (string?)content[1]?["role"]);
        Assert.Equal("text", (string?)content[2]?["type"]);
        Assert.Equal("move forward", (string?)content[2]?["text"]);
    }

    [Fact]
    public void AnArbitraryDurationIsForwardedUnchanged()
    {
        var body = SeedanceVideoClient.BuildRequestBody(RequestWithMode(37, "reference"));

        Assert.Equal(37, (int?)body["duration"]);
    }

    private static VideoGenerationRequest Request(
        params ReferenceImage[] images) => RequestWithMode(5, "reference", images);

    private static VideoGenerationRequest RequestWithMode(
        int durationSeconds,
        string inputMode,
        params ReferenceImage[] images) => new(
        Provider(),
        ApiKey: "sk-test",
        ModelKey: "seedance-2.0-mini",
        Prompt: "move forward",
        images,
        InputMode: inputMode,
        Aspect: "1:1",
        Resolution: "720p",
        DurationSeconds: durationSeconds,
        Fps: 24,
        Seed: null,
        Watermark: false,
        GenerateAudio: false);

    private static AiProvider Provider() => new(
        ProviderId.New(),
        DeviceId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.SeedanceVideo,
        ApiKey: null,
        BaseUrl: BaseUrl,
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
