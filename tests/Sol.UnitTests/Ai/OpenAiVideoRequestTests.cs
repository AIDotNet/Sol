using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai.Protocols;

namespace Sol.UnitTests.Ai;

public class OpenAiVideoRequestTests
{
    [Theory]
    [InlineData("sd-mini-480p", "720p", "854x480")]
    [InlineData("sd-fast-720p", "480p", "1280x720")]
    [InlineData("sd-2.0-1080p", "720p", "1920x1080")]
    public void SdModelsUseTheResolutionEncodedInTheirModelKey(
        string modelKey,
        string requestedResolution,
        string expectedSize)
    {
        Assert.Equal(
            expectedSize,
            OpenAiVideoClient.SizeFor(modelKey, "16:9", requestedResolution));
    }

    [Fact]
    public void FixedResolutionStillRespectsPortraitAspect()
    {
        Assert.Equal(
            "480x854",
            OpenAiVideoClient.SizeFor("sd-mini-480p", "9:16", "1080p"));
    }

    [Fact]
    public void OtherModelsKeepTheRequestedResolution()
    {
        Assert.Equal("1920x1080", OpenAiVideoClient.SizeFor("sora-2", "16:9", "1080p"));
    }

    [Fact]
    public void RoutinUsesContentBlocksAndStructuredVideoParameters()
    {
        var body = OpenAiVideoClient.BuildRequestBody(Request(
            Provider("routin-ai"),
            "sd-mini-480p",
            [new ReferenceImage([1, 2, 3], "image/png")]));

        Assert.Equal("sd-mini-480p", (string?)body["model"]);
        Assert.Equal("16:9", (string?)body["ratio"]);
        Assert.Equal("480p", (string?)body["resolution"]);
        Assert.Equal(7, (int?)body["duration"]);
        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Null(body["size"]);

        var content = Assert.IsType<JsonArray>(body["content"]);
        Assert.Equal("image_url", (string?)content[0]?["type"]);
        Assert.Equal("reference_image", (string?)content[0]?["role"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)content[0]?["image_url"]?["url"]);
        Assert.Equal("text", (string?)content[1]?["type"]);
        Assert.Equal("move forward", (string?)content[1]?["text"]);
    }

    [Fact]
    public void RoutinSendsThePublicAssetUrlInsteadOfInlineBase64()
    {
        // The SD video models reject data: URLs outright, so a published asset link must win.
        var body = OpenAiVideoClient.BuildRequestBody(Request(
            Provider("routin-ai"),
            "sd-mini-480p",
            [new ReferenceImage([1, 2, 3], "image/png", "https://sol.example.com/api/v1/canvas/assets/abc?token=t")]));

        var content = Assert.IsType<JsonArray>(body["content"]);
        Assert.Equal(
            "https://sol.example.com/api/v1/canvas/assets/abc?token=t",
            (string?)content[0]?["image_url"]?["url"]);
    }

    [Fact]
    public void NonRoutinProvidersKeepTheOpenAiRequestShape()
    {
        var body = OpenAiVideoClient.BuildRequestBody(Request(Provider(null), "sora-2", []));

        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Equal("7", (string?)body["seconds"]);
        Assert.Equal("1280x720", (string?)body["size"]);
        Assert.Null(body["content"]);
        Assert.Null(body["resolution"]);
    }

    private static VideoGenerationRequest Request(
        AiProvider provider,
        string modelKey,
        IReadOnlyList<ReferenceImage> images) => new(
        provider,
        ApiKey: "sk-test",
        modelKey,
        Prompt: "move forward",
        images,
        InputMode: "reference",
        Aspect: "16:9",
        Resolution: "720p",
        DurationSeconds: 7,
        Fps: null,
        Seed: null,
        Watermark: null,
        GenerateAudio: null);

    private static AiProvider Provider(string? builtinId) => new(
        ProviderId.New(),
        DeviceId.New(),
        builtinId,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.OpenAiVideo,
        ApiKey: null,
        BaseUrl: "https://example.com/v1",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
