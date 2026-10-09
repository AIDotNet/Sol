using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai.Protocols;

namespace Sol.UnitTests.Ai;

/// <summary>
/// The xAI wire format only looks like OpenAI's: creation returns <c>request_id</c>, parameters
/// are <c>duration / aspect_ratio / resolution</c>, and polling ends on <c>done</c> with an
/// inline <c>video.url</c>.
/// </summary>
public class XaiVideoRequestTests
{
    [Fact]
    public void BodyUsesTheXaiFieldNames()
    {
        var body = XaiVideoClient.BuildRequestBody(Request(Provider("xai"), []));

        Assert.Equal("grok-imagine-video", (string?)body["model"]);
        Assert.Equal("move forward", (string?)body["prompt"]);
        Assert.Equal(7, (int?)body["duration"]);
        Assert.Equal("16:9", (string?)body["aspect_ratio"]);
        Assert.Equal("720p", (string?)body["resolution"]);
        Assert.Null(body["seconds"]);
        Assert.Null(body["size"]);
        Assert.Null(body["ratio"]);
    }

    [Fact]
    public void SingleImageMapsToTheStartFrameField()
    {
        var body = XaiVideoClient.BuildRequestBody(Request(
            Provider("xai"),
            [new ReferenceImage([1, 2, 3], "image/png", "https://sol.example.com/a.png")]));

        var image = Assert.IsType<JsonObject>(body["image"]);
        Assert.Equal("https://sol.example.com/a.png", (string?)image["url"]);
        Assert.Null(body["reference_images"]);
    }

    [Fact]
    public void ImageFallsBackToAnInlineDataUriWithoutAPublicUrl()
    {
        var body = XaiVideoClient.BuildRequestBody(Request(
            Provider("xai"),
            [new ReferenceImage([1, 2, 3], "image/png")]));

        Assert.Equal(
            "data:image/png;base64,AQID",
            (string?)((JsonObject?)body["image"])?["url"]);
    }

    [Fact]
    public void MultipleImagesMapToReferenceImages()
    {
        var body = XaiVideoClient.BuildRequestBody(Request(
            Provider("xai"),
            [
                new ReferenceImage([1], "image/png", "https://sol.example.com/1.png"),
                new ReferenceImage([2], "image/png", "https://sol.example.com/2.png"),
            ]));

        var references = Assert.IsType<JsonArray>(body["reference_images"]);
        Assert.Equal(2, references.Count);
        Assert.Equal(
            "https://sol.example.com/2.png",
            (string?)((JsonObject?)references[1])?["url"]);
        Assert.Null(body["image"]);
    }

    [Fact]
    public void ReferenceImagesAreCappedAtSeven()
    {
        var images = Enumerable.Range(0, 9)
            .Select(i => new ReferenceImage([(byte)i], "image/png"))
            .ToList();

        var body = XaiVideoClient.BuildRequestBody(Request(Provider("xai"), images));

        Assert.Equal(7, Assert.IsType<JsonArray>(body["reference_images"]).Count);
    }

    [Theory]
    [InlineData(30, false, 15)]
    [InlineData(30, true, 10)]
    [InlineData(0, false, 1)]
    public void DurationIsClampedToTheDocumentedMaximum(
        int requested,
        bool withReferences,
        int expected)
    {
        var images = withReferences
            ? new List<ReferenceImage>
            {
                new([1], "image/png"),
                new([2], "image/png"),
            }
            : [];

        var body = XaiVideoClient.BuildRequestBody(
            Request(Provider("xai"), images) with { DurationSeconds = requested });

        Assert.Equal(expected, (int?)body["duration"]);
    }

    [Fact]
    public void DurationIsOmittedWhenUnset()
    {
        var body = XaiVideoClient.BuildRequestBody(
            Request(Provider("xai"), []) with { DurationSeconds = null });

        Assert.Null(body["duration"]);
    }

    [Fact]
    public void ResolutionOutsideTheDocumentedSetIsDropped()
    {
        var body = XaiVideoClient.BuildRequestBody(
            Request(Provider("xai"), []) with { Resolution = "1080p" });

        Assert.Null(body["resolution"]);
    }

    [Fact]
    public void RoutinBaseUrlIsRewrittenToTheXaiMount()
    {
        Assert.Equal(
            "https://api.routin.ai/xai/v1",
            XaiVideoClient.ResolveBaseUrl(Provider("routin-ai")));
    }

    [Fact]
    public void OtherProvidersKeepTheirConfiguredBaseUrl()
    {
        Assert.Equal(
            "https://api.x.ai/v1",
            XaiVideoClient.ResolveBaseUrl(Provider("xai")));
    }

    [Fact]
    public void DoneStatusCarriesTheInlineVideoUrl()
    {
        var result = XaiVideoClient.ParsePoll(
            """
            {
              "status": "done",
              "video": { "url": "https://vidgen.x.ai/example/video.mp4", "duration": 8 },
              "model": "grok-imagine-video",
              "progress": 100
            }
            """);

        Assert.Equal(VideoJobState.Succeeded, result.State);
        Assert.Equal("https://vidgen.x.ai/example/video.mp4", result.VideoUrl);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DoneWithoutAUrlFailsInsteadOfHanging()
    {
        var result = XaiVideoClient.ParsePoll("""{ "status": "done", "progress": 100 }""");

        Assert.Equal(VideoJobState.Failed, result.State);
        Assert.Null(result.VideoUrl);
    }

    [Fact]
    public void FailedStatusCarriesTheUpstreamError()
    {
        var result = XaiVideoClient.ParsePoll(
            """
            {
              "status": "failed",
              "error": { "code": "invalid_argument", "message": "Prompt cannot be empty." },
              "progress": 0
            }
            """);

        Assert.Equal(VideoJobState.Failed, result.State);
        Assert.Equal("Prompt cannot be empty.", result.Error);
    }

    [Fact]
    public void ExpiredStatusFailsTheJob()
    {
        var result = XaiVideoClient.ParsePoll("""{ "status": "expired" }""");

        Assert.Equal(VideoJobState.Failed, result.State);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void PendingStatusKeepsTheNormalizedProgress()
    {
        var result = XaiVideoClient.ParsePoll("""{ "status": "pending", "progress": 40 }""");

        Assert.Equal(VideoJobState.Pending, result.State);
        Assert.Equal(0.4, result.Progress);
        Assert.Null(result.VideoUrl);
    }

    [Fact]
    public void UnknownStatusStaysPending()
    {
        var result = XaiVideoClient.ParsePoll("""{ "status": "queued" }""");

        Assert.Equal(VideoJobState.Pending, result.State);
    }

    private static VideoGenerationRequest Request(
        AiProvider provider,
        IReadOnlyList<ReferenceImage> images) => new(
        provider,
        ApiKey: "sk-test",
        "grok-imagine-video",
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
        ProviderType.XaiVideo,
        ApiKey: null,
        BaseUrl: builtinId == "routin-ai"
            ? "https://api.routin.ai/v1"
            : "https://api.x.ai/v1",
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
