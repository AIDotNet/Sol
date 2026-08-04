using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;
using Sol.Infrastructure.Ai.Protocols;

namespace Sol.UnitTests.Ai;

/// <summary>
/// The request bodies <see cref="OpenAiImagesClient"/> composes.
/// </summary>
/// <remarks>
/// Asserted against the built <see cref="HttpRequestMessage"/> rather than over the wire: the
/// interesting part is which fields are sent and under which names, and a parameter that silently
/// fails to make it into the body is invisible until a user notices the control does nothing.
/// </remarks>
public class OpenAiImagesRequestTests
{
    private const string BaseUrl = "https://example.com/v1";

    [Fact]
    public async Task AGenerateRequestAsksForUrlsAndPngByDefault()
    {
        // A caller that expresses no preference gets the values the canvas defaults to. URL
        // matters most: relays overwhelmingly answer with links, and the previous body left the
        // field out entirely.
        var body = await GenerateBodyAsync(Request());

        Assert.Equal("url", (string?)body["response_format"]);
        Assert.Equal("png", (string?)body["output_format"]);
    }

    [Fact]
    public async Task Base64IsSentUnderTheNameTheApiUses()
    {
        // "base64" is the application's word for it; the API only knows b64_json.
        var body = await GenerateBodyAsync(Request(responseFormat: "base64"));

        Assert.Equal("b64_json", (string?)body["response_format"]);
    }

    [Theory]
    [InlineData("jpeg", "jpeg")]
    [InlineData("jpg", "jpeg")]
    [InlineData("WebP", "webp")]
    [InlineData("tiff", "png")]
    [InlineData("", "png")]
    public async Task AnOutputFormatIsNormalizedRatherThanPassedThrough(
        string requested,
        string expected)
    {
        // Whitelisted so an unrecognized value degrades to the API's own default instead of
        // earning a 400 that reads to the user as a broken model.
        var body = await GenerateBodyAsync(Request(outputFormat: requested));

        Assert.Equal(expected, (string?)body["output_format"]);
    }

    [Fact]
    public async Task AnEditRequestCarriesQualityFormatAndResponseFormat()
    {
        // The regression this covers: the edits endpoint used to receive none of these, so every
        // one of the node's controls went dead the moment a reference image was wired in.
        var fields = await EditFieldsAsync(Request(
            quality: "high",
            outputFormat: "jpg",
            responseFormat: "base64",
            withReference: true));

        Assert.Equal("high", fields["quality"]);
        Assert.Equal("jpeg", fields["output_format"]);
        Assert.Equal("b64_json", fields["response_format"]);
    }

    [Fact]
    public async Task AutoQualityIsOmittedFromBothShapes()
    {
        // "auto" is the node's way of saying "do not send one" — it is not a value the API takes.
        var body = await GenerateBodyAsync(Request(quality: "auto"));
        Assert.Null(body["quality"]);

        var fields = await EditFieldsAsync(Request(quality: "auto", withReference: true));
        Assert.False(fields.ContainsKey("quality"));
    }

    [Fact]
    public void ReferenceImagesStillTravelAsFileParts()
    {
        // The new string parts must not have displaced them.
        using var message = OpenAiImagesClient.BuildEditRequest(BaseUrl, Request(withReference: true));

        var files = Assert.IsType<MultipartFormDataContent>(message.Content)
            .Where(part => part.Headers.ContentDisposition?.FileName is not null)
            .ToList();

        Assert.Single(files);
        Assert.Equal("image[]", files[0].Headers.ContentDisposition?.Name);
    }

    [Fact]
    public void TheTwoShapesTargetDifferentEndpoints()
    {
        using var generate = OpenAiImagesClient.BuildGenerateRequest(BaseUrl, Request());
        using var edit = OpenAiImagesClient.BuildEditRequest(BaseUrl, Request(withReference: true));

        Assert.Equal($"{BaseUrl}/images/generations", generate.RequestUri?.ToString());
        Assert.Equal($"{BaseUrl}/images/edits", edit.RequestUri?.ToString());
    }

    private static async Task<JsonNode> GenerateBodyAsync(ImageGenerationRequest request)
    {
        using var message = OpenAiImagesClient.BuildGenerateRequest(BaseUrl, request);
        var json = await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonNode.Parse(json)!;
    }

    /// <summary>Reads the string parts of a multipart body, ignoring the reference image files.</summary>
    private static async Task<Dictionary<string, string>> EditFieldsAsync(
        ImageGenerationRequest request)
    {
        using var message = OpenAiImagesClient.BuildEditRequest(BaseUrl, request);
        var parts = Assert.IsType<MultipartFormDataContent>(message.Content);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            var disposition = part.Headers.ContentDisposition;

            // A part with a filename is a reference image, not a parameter.
            if (disposition?.Name is not { } name || disposition.FileName is not null) continue;

            fields[name] = await part.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        return fields;
    }

    private static ImageGenerationRequest Request(
        string? quality = null,
        string? outputFormat = null,
        string? responseFormat = null,
        bool withReference = false) => new(
            Provider(),
            ApiKey: "sk-test",
            ModelKey: "gpt-image-1",
            Prompt: "a cat",
            withReference ? [new ReferenceImage([1, 2, 3], "image/png")] : [],
            Size: "1024x1024",
            quality,
            outputFormat,
            responseFormat,
            Count: 1,
            Seed: null);

    private static AiProvider Provider() => new(
        ProviderId.New(),
        DeviceId.New(),
        BuiltinId: null,
        Name: "p",
        Description: null,
        Icon: null,
        ProviderType.OpenAiImages,
        ApiKey: null,
        BaseUrl: BaseUrl,
        Enabled: true,
        PresetVersion: null,
        SortOrder: 0,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);
}
