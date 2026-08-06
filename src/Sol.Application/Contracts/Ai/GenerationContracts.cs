namespace Sol.Application.Contracts.Ai;

/// <param name="Images">
/// Asset URLs of reference images, as returned by a previous generation or an upload. Resolved
/// server-side: the upstream provider cannot reach our asset endpoint, which needs a device cookie.
/// </param>
/// <param name="OutputFormat"><c>png</c>, <c>jpeg</c> or <c>webp</c>.</param>
/// <param name="ResponseFormat">
/// <c>url</c> or <c>base64</c>. Only affects how the provider hands the bytes over — either way
/// they are stored here and the caller gets an asset URL.
/// </param>
public sealed record GenerateImageRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string[]? Images,
    /// <summary>
    /// A stored PNG mask for inpainting. Transparent pixels are replaced by the provider; the URL
    /// is resolved through the same device-owned asset path as reference images.
    /// </summary>
    string? MaskUrl,
    string? Size,
    string? Quality,
    string? OutputFormat,
    string? ResponseFormat,
    int? Count,
    int? Seed);

public sealed record GeneratedAsset(string Url, string MediaType, string AssetId);

public sealed record GenerateImageResponse(GeneratedAsset[] Assets);

public sealed record StartVideoRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string[]? Images,
    string? InputMode,
    string? Aspect,
    string? Resolution,
    int? Duration,
    int? Fps,
    int? Seed,
    bool? Watermark,
    bool? GenerateAudio);

public sealed record StartVideoResponse(string JobId);

/// <param name="Progress">0-1 when the provider reports it, null when it does not.</param>
public sealed record VideoJobStatusResponse(
    string JobId,
    string Status,
    double? Progress,
    string? AssetUrl,
    string? Error);

/// <param name="SystemPrompt">
/// Steering that is not part of the user's own text. The rewrite feature uses it to describe the
/// transformation without mixing that instruction into the text being transformed.
/// </param>
/// <param name="Images">
/// Asset URLs of reference images, for writing a prompt from a picture. Resolved server-side and
/// silently ignored by protocols with no image part.
/// </param>
public sealed record GenerateTextRequest(
    string ProviderId,
    string ModelKey,
    string Prompt,
    string? SystemPrompt,
    string[]? Images,
    int? MaxOutputTokens,
    double? Temperature);

public sealed record GenerateTextResponse(string Text);

/// <param name="Prompt">The prompt that produced this, when it was generated rather than uploaded.</param>
public sealed record CanvasAssetSummary(
    string Id,
    string Url,
    string Kind,
    string MediaType,
    long ByteSize,
    string? Prompt,
    string CreatedAt);

public sealed record CanvasAssetListResponse(CanvasAssetSummary[] Assets);
