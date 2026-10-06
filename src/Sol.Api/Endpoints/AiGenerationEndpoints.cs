using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Api.Endpoints;

/// <summary>
/// Image generation and asset delivery.
/// </summary>
/// <remarks>
/// The browser never talks to a vendor: it posts a provider id and a prompt here, the server
/// decrypts the key, calls upstream, stores the result, and returns an asset URL. That keeps
/// keys off the client, sidesteps CORS entirely, and means a provider that requires an
/// allow-listed egress IP still works.
/// </remarks>
public static class AiGenerationEndpoints
{
    /// <summary>Ceiling on images per request, matching what the canvas UI offers.</summary>
    private const int MaxImageCount = 4;

    /// <summary>
    /// Cap on how many reference images one request may carry. Each is read fully into memory
    /// and re-sent upstream, so an unbounded list is a memory amplification vector.
    /// </summary>
    private const int MaxReferenceImages = 8;

    /// <summary>
    /// Cap on an uploaded file. Generous enough for a camera original, small enough that a
    /// handful of concurrent uploads cannot exhaust memory — the file is buffered before it is
    /// written, so this is a memory bound, not just a disk one.
    /// </summary>
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Image types accepted for upload.
    /// </summary>
    /// <remarks>
    /// SVG is deliberately excluded. It is XML that can carry script, and a browser navigating
    /// directly to a stored SVG would execute it as same-origin — turning the asset route into
    /// stored XSS. The raster formats here cannot do that.
    /// </remarks>
    private static readonly string[] AllowedUploadTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];

    /// <summary>How many assets the library shows by default.</summary>
    private const int DefaultAssetPageSize = 60;

    /// <summary>Maximum length for a user-created asset group name.</summary>
    private const int MaxAssetGroupNameLength = 64;

    /// <summary>Maximum number of assets one bulk assignment may touch.</summary>
    private const int MaxAssetsPerAssignment = 200;

    /// <summary>
    /// Hard ceiling on one listing. The library is a picker, not an archive browser, and a
    /// device with thousands of generations should not be able to ask for all of them at once.
    /// </summary>
    private const int MaxAssetPageSize = 200;

    public static IEndpointRouteBuilder MapAiGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/images", GenerateImagesAsync)
            .WithTags("ai")
            .WithName("GenerateImages");

        app.MapPost("/api/v1/ai/text", GenerateTextAsync)
            .WithTags("ai")
            .WithName("GenerateText");

        // Literal group routes are registered before the `/{id}` media route. ASP.NET's route
        // matcher still prefers the literal segment, while keeping all asset-library operations
        // under one discoverable prefix.
        app.MapGet("/api/v1/canvas/assets/groups", ListAssetGroupsAsync)
            .WithTags("canvas")
            .WithName("ListCanvasAssetGroups");

        app.MapPost("/api/v1/canvas/assets/groups", CreateAssetGroupAsync)
            .WithTags("canvas")
            .WithName("CreateCanvasAssetGroup");

        app.MapPatch("/api/v1/canvas/assets/groups/{id}", RenameAssetGroupAsync)
            .WithTags("canvas")
            .WithName("RenameCanvasAssetGroup");

        app.MapDelete("/api/v1/canvas/assets/groups/{id}", DeleteAssetGroupAsync)
            .WithTags("canvas")
            .WithName("DeleteCanvasAssetGroup");

        app.MapPatch("/api/v1/canvas/assets/group", AssignAssetsToGroupAsync)
            .WithTags("canvas")
            .WithName("AssignCanvasAssetsToGroup");

        app.MapGet("/api/v1/canvas/assets/{id}", GetAssetAsync)
            .WithTags("canvas")
            .WithName("GetCanvasAsset");

        app.MapGet("/api/v1/canvas/assets", ListAssetsAsync)
            .WithTags("canvas")
            .WithName("ListCanvasAssets");

        app.MapDelete("/api/v1/canvas/assets/{id}", DeleteAssetAsync)
            .WithTags("canvas")
            .WithName("DeleteCanvasAsset");

        // No antiforgery token: the only credential this endpoint accepts is the device cookie,
        // which is SameSite=Lax and therefore not sent on a cross-site POST at all.
        app.MapPost("/api/v1/canvas/assets", UploadAssetAsync)
            .WithTags("canvas")
            .WithName("UploadCanvasAsset")
            .DisableAntiforgery();

        return app;
    }

    /// <summary>
    /// Stores a user-supplied image and returns the URL the canvas should reference.
    /// </summary>
    /// <remarks>
    /// Without this, an uploaded image only ever existed as an object URL inside one tab: it
    /// broke on reload, and the generation endpoint silently ignored it as a reference because
    /// only <c>/api/v1/canvas/assets/</c> URLs resolve server-side.
    /// </remarks>
    private static async Task<Results<Ok<GeneratedAsset>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> UploadAssetAsync(
        IFormFile file,
        HttpContext http,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (file.Length == 0)
        {
            return Invalid("the uploaded file is empty");
        }

        if (file.Length > MaxUploadBytes)
        {
            return Invalid($"the file exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit");
        }

        var mediaType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

        if (!AllowedUploadTypes.Contains(mediaType))
        {
            return Invalid($"unsupported image type '{mediaType}'");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        // Content-Type is client-supplied and trivially forged, so the declared type is checked
        // against the actual file signature. Otherwise anything could be stored under an image
        // media type and served back with it.
        if (!SignatureMatches(bytes, mediaType))
        {
            return Invalid("the file contents do not match the declared image type");
        }

        var stored = await assetStore.SaveAsync(bytes, mediaType, ExtensionFor(mediaType), ct);
        var assetId = Guid.CreateVersion7();

        await assets.InsertAsync(
            new CanvasAsset(
                assetId,
                deviceId,
                "image",
                stored.MediaType,
                stored.StoragePath,
                stored.ByteSize,
                null,
                DateTimeOffset.UtcNow),
            ct);

        return TypedResults.Ok(new GeneratedAsset(
            $"/api/v1/canvas/assets/{assetId}", stored.MediaType, assetId.ToString()));
    }

    /// <summary>Checks the leading magic bytes against the declared media type.</summary>
    private static bool SignatureMatches(byte[] bytes, string mediaType) => mediaType switch
    {
        "image/png" => bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,

        "image/jpeg" => bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,

        "image/gif" => bytes.Length >= 6
            && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F',

        // RIFF container with a WEBP fourcc at offset 8.
        "image/webp" => bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B' && bytes[11] == (byte)'P',

        _ => false,
    };

    private static async Task<Results<Ok<GenerateImageResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> GenerateImagesAsync(
        GenerateImageRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        IImageGenerationDispatcher dispatcher,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt) && (request.Images?.Length ?? 0) == 0)
        {
            return Invalid("a prompt or at least one reference image is required");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        if (!provider.Enabled)
        {
            return Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(
            candidate => candidate.ModelKey == request.ModelKey);

        if (model is null)
        {
            return Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (model.Category != ModelCategory.Image)
        {
            return Invalid($"model '{request.ModelKey}' is not an image model");
        }

        string apiKey;
        if (!TryUnprotectKey(provider, protector, out apiKey, out var keyError))
        {
            return Invalid(keyError);
        }

        // Reference images are named by asset URL; resolving them here keeps the client from
        // having to upload the same bytes back to us.
        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } imageUrls)
        {
            if (imageUrls.Length > MaxReferenceImages)
            {
                return Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in imageUrls)
            {
                var reference = await LoadReferenceAsync(url, deviceId, assets, assetStore, ct);
                if (reference is not null)
                {
                    references.Add(reference);
                }
                // A reference that cannot be resolved is skipped rather than failing the run:
                // it is usually a node whose image was never uploaded, and generating from the
                // prompt alone is more useful than an error.
            }
        }

        var protocol = provider.ResolveProtocol(model);

        ReferenceImage? mask = null;
        if (!string.IsNullOrWhiteSpace(request.MaskUrl))
        {
            if (protocol != ProviderType.OpenAiImages)
            {
                return Invalid("the selected image protocol does not support inpaint masks");
            }

            if (references.Count == 0)
            {
                return Invalid("an inpaint mask requires at least one reference image");
            }

            mask = await LoadReferenceAsync(request.MaskUrl, deviceId, assets, assetStore, ct);
            if (mask is null)
            {
                // Unlike an ordinary reference, silently dropping a mask changes an inpaint into
                // a variation of the whole image. That is destructive enough to fail explicitly.
                return Invalid("the inpaint mask is not an available canvas asset");
            }

            if (!string.Equals(mask.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("the inpaint mask must be a PNG image");
            }
        }

        var count = Math.Clamp(request.Count ?? 1, 1, MaxImageCount);

        var result = await dispatcher.GenerateAsync(
            protocol,
            new ImageGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                references,
                mask,
                request.Size,
                request.Quality,
                request.OutputFormat,
                request.ResponseFormat,
                count,
                request.Seed),
            ct);

        if (!result.Ok)
        {
            return Invalid(result.Error ?? "image generation failed");
        }

        var stored = new List<GeneratedAsset>(result.Images.Count);

        foreach (var image in result.Images)
        {
            var saved = await assetStore.SaveAsync(
                image.Bytes, image.MediaType, ExtensionFor(image.MediaType), ct);

            var assetId = Guid.CreateVersion7();

            await assets.InsertAsync(
                new CanvasAsset(
                    assetId,
                    deviceId,
                    "image",
                    saved.MediaType,
                    saved.StoragePath,
                    saved.ByteSize,
                    request.Prompt,
                    DateTimeOffset.UtcNow),
                ct);

            stored.Add(new GeneratedAsset(
                $"/api/v1/canvas/assets/{assetId}", saved.MediaType, assetId.ToString()));
        }

        return TypedResults.Ok(new GenerateImageResponse([.. stored]));
    }

    /// <summary>
    /// Generates text — used by the canvas for writing and rewriting prompts.
    /// </summary>
    private static async Task<Results<Ok<GenerateTextResponse>, BadRequest<ErrorResponse>,
        NotFound, UnauthorizedHttpResult>> GenerateTextAsync(
        GenerateTextRequest request,
        HttpContext http,
        IProviderRepository providers,
        IApiKeyProtector protector,
        ITextGenerationDispatcher dispatcher,
        IAssetStore assetStore,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!ProviderId.TryParse(request.ProviderId, out var providerId))
        {
            return Invalid("malformed provider id");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Invalid("a prompt is required");
        }

        var provider = await providers.FindAsync(deviceId, providerId, ct);
        if (provider is null)
        {
            return TypedResults.NotFound();
        }

        if (!provider.Enabled)
        {
            return Invalid($"provider '{provider.Name}' is disabled");
        }

        var model = provider.Models.FirstOrDefault(
            candidate => candidate.ModelKey == request.ModelKey);

        if (model is null)
        {
            return Invalid($"model '{request.ModelKey}' is not configured on this provider");
        }

        if (model.Category != ModelCategory.Chat)
        {
            return Invalid($"model '{request.ModelKey}' is not a chat model");
        }

        if (!TryUnprotectKey(provider, protector, out var apiKey, out var keyError))
        {
            return Invalid(keyError);
        }

        var references = new List<ReferenceImage>();
        if (request.Images is { Length: > 0 } urls)
        {
            if (urls.Length > MaxReferenceImages)
            {
                return Invalid($"at most {MaxReferenceImages} reference images are allowed");
            }

            foreach (var url in urls)
            {
                if (await LoadReferenceAsync(url, deviceId, assets, assetStore, ct) is { } image)
                {
                    references.Add(image);
                }
            }
        }

        var result = await dispatcher.GenerateAsync(
            provider.ResolveProtocol(model),
            new TextGenerationRequest(
                provider,
                apiKey,
                request.ModelKey,
                request.Prompt,
                request.SystemPrompt,
                references,
                request.MaxOutputTokens ?? model.MaxOutputTokens,
                request.Temperature),
            ct);

        return result.Ok && result.Text is not null
            ? TypedResults.Ok(new GenerateTextResponse(result.Text))
            : Invalid(result.Error ?? "text generation failed");
    }

    /// <summary>
    /// Lists a device's stored media, for the asset library.
    /// </summary>
    private static async Task<Results<Ok<CanvasAssetListResponse>, UnauthorizedHttpResult>>
        ListAssetsAsync(
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct,
            string? kind = null,
            int? limit = null)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        // Anything outside the known kinds is treated as "no filter" rather than an error —
        // the parameter comes from a query string and a typo should not fail the picker.
        var filter = kind is "image" or "video" ? kind : null;
        var take = Math.Clamp(limit ?? DefaultAssetPageSize, 1, MaxAssetPageSize);

        var list = await assets.ListAsync(deviceId, filter, take, ct);

        return TypedResults.Ok(new CanvasAssetListResponse(
        [
            .. list.Select(asset => new CanvasAssetSummary(
                asset.AssetId.ToString(),
                $"/api/v1/canvas/assets/{asset.AssetId}",
                asset.Kind,
                asset.MediaType,
                asset.ByteSize,
                asset.Prompt,
                asset.CreatedAt.ToString("O"),
                asset.GroupId?.ToString())),
        ]));
    }

    /// <summary>Lists the folders in the device's asset library.</summary>
    private static async Task<Results<Ok<CanvasAssetGroupListResponse>, UnauthorizedHttpResult>>
        ListAssetGroupsAsync(
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        var groups = await assets.ListGroupsAsync(deviceId, ct);

        return TypedResults.Ok(new CanvasAssetGroupListResponse(
        [
            .. groups.Select(ToGroupSummary),
        ]));
    }

    /// <summary>Creates a folder without moving any media into it yet.</summary>
    private static async Task<Results<Created<CanvasAssetGroupSummary>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult, Conflict<ErrorResponse>>> CreateAssetGroupAsync(
        CreateCanvasAssetGroupRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!TryNormalizeGroupName(request.Name, out var name, out var error))
        {
            return Invalid(error);
        }

        var now = DateTimeOffset.UtcNow;
        var groupId = Guid.CreateVersion7();
        var created = await assets.InsertGroupAsync(
            new CanvasAssetGroup(groupId, deviceId, name, 0, now, now), ct);

        return created is null
            ? TypedResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"An asset group named '{name}' already exists."]))
            : TypedResults.Created(
                $"/api/v1/canvas/assets/groups/{groupId}", ToGroupSummary(created));
    }

    /// <summary>Renames a folder while preserving all of its asset assignments.</summary>
    private static async Task<Results<Ok<CanvasAssetGroupSummary>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult, NotFound, Conflict<ErrorResponse>>> RenameAssetGroupAsync(
        string id,
        UpdateCanvasAssetGroupRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var groupId) || groupId == Guid.Empty)
        {
            return TypedResults.NotFound();
        }

        if (!TryNormalizeGroupName(request.Name, out var name, out var error))
        {
            return Invalid(error);
        }

        if (await assets.FindGroupAsync(deviceId, groupId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var renamed = await assets.RenameGroupAsync(deviceId, groupId, name, DateTimeOffset.UtcNow, ct);
        return renamed is null
            ? TypedResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"An asset group named '{name}' already exists."]))
            : TypedResults.Ok(ToGroupSummary(renamed));
    }

    /// <summary>Deletes a folder and leaves its media in the ungrouped bucket.</summary>
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>>
        DeleteAssetGroupAsync(
            string id,
            HttpContext http,
            ICanvasAssetRepository assets,
            CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var groupId) || groupId == Guid.Empty)
        {
            return TypedResults.NotFound();
        }

        return await assets.DeleteGroupAsync(deviceId, groupId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>Moves a selection into a folder, or clears its folder when groupId is null.</summary>
    private static async Task<Results<Ok<AssignCanvasAssetsResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult, NotFound>> AssignAssetsToGroupAsync(
        AssignCanvasAssetsRequest request,
        HttpContext http,
        ICanvasAssetRepository assets,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (request.AssetIds is null || request.AssetIds.Length is < 1 or > MaxAssetsPerAssignment)
        {
            return Invalid($"assetIds must contain between 1 and {MaxAssetsPerAssignment} items");
        }

        var ids = new List<Guid>(request.AssetIds.Length);
        foreach (var raw in request.AssetIds)
        {
            if (!Guid.TryParse(raw, out var assetId) || assetId == Guid.Empty)
            {
                return Invalid("assetIds contains a malformed id");
            }

            if (!ids.Contains(assetId)) ids.Add(assetId);
        }

        Guid? groupId = null;
        if (request.GroupId is not null)
        {
            if (!Guid.TryParse(request.GroupId, out var parsed) || parsed == Guid.Empty)
            {
                return Invalid("groupId must be a valid id or null");
            }

            if (await assets.FindGroupAsync(deviceId, parsed, ct) is null)
            {
                return TypedResults.NotFound();
            }

            groupId = parsed;
        }

        var updated = await assets.AssignGroupAsync(deviceId, ids, groupId, ct);
        return TypedResults.Ok(new AssignCanvasAssetsResponse(updated));
    }

    /// <summary>
    /// Deletes an asset and the file behind it.
    /// </summary>
    /// <remarks>
    /// The row goes first: if the file removal then fails, the result is an orphaned file rather
    /// than a row pointing at nothing. A canvas still referencing this asset will render a
    /// broken image — acceptable, because the alternative is refusing to delete anything the
    /// user might have used, which makes the library unmanageable.
    /// </remarks>
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>>
        DeleteAssetAsync(
            string id,
            HttpContext http,
            ICanvasAssetRepository assets,
            IAssetStore assetStore,
            CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!Guid.TryParse(id, out var assetId))
        {
            return TypedResults.NotFound();
        }

        var storagePath = await assets.DeleteAsync(deviceId, assetId, ct);
        if (storagePath is null)
        {
            return TypedResults.NotFound();
        }

        await assetStore.DeleteAsync(storagePath, ct);

        return TypedResults.NoContent();
    }

    /// <summary>Streams a stored asset back to its owner.</summary>
    /// <remarks>
    /// Accepts two credentials: the caller's device cookie, or — for fetches made by an upstream
    /// vendor, which holds no cookie — a signed, expiring token in the query string, minted when
    /// the asset's URL was handed out with a generation request. The token binds the grant to one
    /// asset id, so a valid token for another asset is still a refusal.
    /// </remarks>
    private static async Task<Results<FileStreamHttpResult, NotFound, UnauthorizedHttpResult>>
        GetAssetAsync(
            string id,
            string? token,
            HttpContext http,
            ICanvasAssetRepository assets,
            IAssetStore assetStore,
            IAssetLinkSigner assetLinks,
            CancellationToken ct)
    {
        CanvasAsset? asset;

        if (!string.IsNullOrWhiteSpace(token))
        {
            if (!Guid.TryParse(id, out var signedId)
                || !assetLinks.TryValidateToken(signedId, token))
            {
                return TypedResults.Unauthorized();
            }

            asset = await assets.FindByIdAsync(signedId, ct);
        }
        else
        {
            if (http.GetDeviceId() is not { } deviceId)
            {
                return TypedResults.Unauthorized();
            }

            if (!Guid.TryParse(id, out var assetId))
            {
                return TypedResults.NotFound();
            }

            asset = await assets.FindAsync(deviceId, assetId, ct);
        }

        if (asset is null)
        {
            return TypedResults.NotFound();
        }

        var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            // The row outlived its file. A 404 is the honest answer.
            return TypedResults.NotFound();
        }

        // Private, not public: an asset is readable only by its owner's device — or by whoever
        // holds a token minted for exactly this asset — so a shared cache must never hold it.
        // Immutable because content at an id never changes.
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        // Stops a browser from second-guessing the stored media type. Combined with the upload
        // whitelist and signature check, it keeps a stored file from ever being interpreted as
        // markup or script on this origin.
        http.Response.Headers.XContentTypeOptions = "nosniff";

        return TypedResults.Stream(stream, asset.MediaType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Resolves an asset URL back into bytes.
    /// </summary>
    /// <remarks>
    /// Only our own asset paths are accepted. Fetching an arbitrary URL supplied by the client
    /// would turn this endpoint into a server-side request forgery primitive, reachable by
    /// anyone who can post to it.
    /// </remarks>
    private static async Task<ReferenceImage?> LoadReferenceAsync(
        string url,
        DeviceId deviceId,
        ICanvasAssetRepository assets,
        IAssetStore assetStore,
        CancellationToken ct)
    {
        const string prefix = "/api/v1/canvas/assets/";

        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var idSegment = url[prefix.Length..].Split('?')[0];
        if (!Guid.TryParse(idSegment, out var assetId))
        {
            return null;
        }

        var asset = await assets.FindAsync(deviceId, assetId, ct);
        if (asset is null)
        {
            return null;
        }

        await using var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return new ReferenceImage(buffer.ToArray(), asset.MediaType);
    }

    /// <summary>
    /// Recovers a provider's API key.
    /// </summary>
    /// <remarks>
    /// Decryption fails when the configured encryption key changed or the row was tampered with.
    /// Neither is retryable, so the message says to re-enter the key rather than presenting it
    /// as a transient error.
    /// </remarks>
    private static bool TryUnprotectKey(
        Sol.Domain.Ai.AiProvider provider,
        IApiKeyProtector protector,
        out string apiKey,
        out string error)
    {
        if (provider.ApiKey is null)
        {
            apiKey = string.Empty;
            error = string.Empty;
            return true;
        }

        try
        {
            apiKey = protector.Unprotect(provider.ApiKey);
            error = string.Empty;
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            apiKey = string.Empty;
            error = "The stored API key could not be decrypted. Re-enter it in settings.";
            return false;
        }
    }

    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        _ => ".bin",
    };

    private static CanvasAssetGroupSummary ToGroupSummary(CanvasAssetGroup group) =>
        new(
            group.GroupId.ToString(),
            group.Name,
            group.AssetCount,
            group.CreatedAt.ToString("O"),
            group.UpdatedAt.ToString("O"));

    private static bool TryNormalizeGroupName(
        string? value,
        out string name,
        out string error)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            error = "name is required";
            return false;
        }

        if (name.Length > MaxAssetGroupNameLength)
        {
            error = $"name must be {MaxAssetGroupNameLength} characters or fewer";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
