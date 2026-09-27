using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Contracts.Ai;
using Sol.Application.Contracts.Skill;
using Sol.Domain.Ai;
using Sol.Infrastructure.Options;

namespace Sol.Api.Endpoints;

/// <summary>Uploads and manages device-scoped Agent Skills.</summary>
public static class SkillEndpoints
{
    public static IEndpointRouteBuilder MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/skills").WithTags("skills");
        group.MapGet("/", ListAsync).WithName("ListSkills");
        group.MapGet("/{id}", GetAsync).WithName("GetSkill");
        group.MapPost("/scan", ScanAsync)
            .WithName("ScanSkill")
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicies.ConfigWrite);
        group.MapPost("/", InstallAsync)
            .WithName("InstallSkill")
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicies.ConfigWrite);
        group.MapDelete("/{id}", DeleteAsync).WithName("DeleteSkill");
        return app;
    }

    private static async Task<Results<Ok<SkillListResponse>, UnauthorizedHttpResult>> ListAsync(
        HttpContext http,
        ISkillRepository skills,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        var list = await skills.ListAsync(deviceId, ct);
        return TypedResults.Ok(new SkillListResponse(
            configuredOptions.Value.SandboxEnabled,
            [.. list.Select(ToResponse)]));
    }

    private static async Task<Results<Ok<SkillResponse>, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> GetAsync(
        string id,
        HttpContext http,
        ISkillRepository skills,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!SkillId.TryParse(id, out var skillId)) return Invalid("malformed skill id");
        var skill = await skills.FindAsync(deviceId, skillId, ct);
        return skill is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(skill));
    }

    private static async Task<Results<Ok<SkillScanResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> ScanAsync(
        IFormFile file,
        HttpContext http,
        ISkillPackageScanner scanner,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is null) return TypedResults.Unauthorized();
        if (file.Length == 0) return Invalid("the uploaded skill package is empty");
        if (file.Length > configuredOptions.Value.MaxUploadBytes)
        {
            return Invalid($"the upload exceeds {configuredOptions.Value.MaxUploadBytes} bytes");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var scanned = await scanner.ScanAsync(stream, file.FileName, ct);
            return TypedResults.Ok(ToScanResponse(scanned));
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or NotSupportedException)
        {
            return Invalid(Bounded(exception.Message));
        }
    }

    private static async Task<Results<Created<SkillResponse>, BadRequest<ErrorResponse>,
        Conflict<ErrorResponse>, UnauthorizedHttpResult>> InstallAsync(
        IFormFile file,
        bool confirmDanger,
        HttpContext http,
        ISkillPackageScanner scanner,
        ISkillStore store,
        ISkillRepository skills,
        IOptions<SkillsOptions> configuredOptions,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (file.Length == 0) return Invalid("the uploaded skill package is empty");
        if (file.Length > configuredOptions.Value.MaxUploadBytes)
        {
            return Invalid($"the upload exceeds {configuredOptions.Value.MaxUploadBytes} bytes");
        }

        ScannedSkillPackage scanned;
        try
        {
            await using var stream = file.OpenReadStream();
            // Installation scans the exact uploaded bytes again. A scan result is advisory UI, not
            // a capability token, so replacing a file between scan and install cannot bypass risk.
            scanned = await scanner.ScanAsync(stream, file.FileName, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException or NotSupportedException)
        {
            return Invalid(Bounded(exception.Message));
        }

        if (scanned.MaxRisk == SkillRiskLevel.Danger && !confirmDanger)
        {
            return Invalid("dangerous skill findings require explicit confirmation");
        }
        if (await skills.FindBySlugAsync(deviceId, scanned.Slug, ct) is not null)
        {
            return TypedResults.Conflict(new ErrorResponse(
                "duplicate_skill", [$"A skill with slug '{scanned.Slug}' is already installed."]));
        }

        var id = SkillId.New();
        string storagePath;
        try
        {
            storagePath = await store.InstallAsync(deviceId, id, scanned.Files, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Invalid(Bounded(exception.Message));
        }

        var now = DateTimeOffset.UtcNow;
        var skill = new Skill(
            id,
            deviceId,
            scanned.Slug,
            scanned.Name,
            scanned.Description,
            storagePath,
            Enabled: true,
            scanned.HasScripts,
            SerializeRisks(scanned.Risks),
            scanned.MaxRisk,
            scanned.ByteSize,
            now,
            now);

        try
        {
            if (!await skills.InsertAsync(skill, ct))
            {
                await store.DeleteAsync(storagePath, CancellationToken.None);
                return TypedResults.Conflict(new ErrorResponse(
                    "duplicate_skill", [$"A skill with slug '{scanned.Slug}' is already installed."]));
            }
        }
        catch
        {
            await store.DeleteAsync(storagePath, CancellationToken.None);
            throw;
        }

        return TypedResults.Created($"/api/v1/skills/{id}", ToResponse(skill));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> DeleteAsync(
        string id,
        HttpContext http,
        ISkillRepository skills,
        ISkillStore store,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId) return TypedResults.Unauthorized();
        if (!SkillId.TryParse(id, out var skillId)) return Invalid("malformed skill id");
        var skill = await skills.FindAsync(deviceId, skillId, ct);
        if (skill is null) return TypedResults.NotFound();

        if (!await skills.DeleteAsync(deviceId, skillId, ct)) return TypedResults.NotFound();

        try
        {
            await store.DeleteAsync(skill.StoragePath, ct);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // The ownership row is already gone, so the file cannot be loaded or executed. An
            // orphaned directory is safer than retaining a live row that points at missing files.
            loggerFactory.CreateLogger("SkillEndpoints").LogWarning(
                "Deleted Skill metadata but could not remove its storage: {Error}",
                Bounded(exception.Message));
        }

        return TypedResults.NoContent();
    }

    private static SkillScanResponse ToScanResponse(ScannedSkillPackage scanned) => new(
        scanned.Name,
        scanned.Slug,
        scanned.Description,
        scanned.HasScripts,
        ToWire(scanned.MaxRisk),
        [.. scanned.Risks.Select(ToResponse)],
        [.. scanned.Files.Select(file => file.Path)],
        scanned.ByteSize);

    private static SkillResponse ToResponse(Skill skill) => new(
        skill.Id.ToString(),
        skill.Slug,
        skill.Name,
        skill.Description,
        skill.Enabled,
        skill.HasScripts,
        ToWire(skill.MaxRisk),
        ParseRisks(skill.RiskJson),
        skill.ByteSize,
        skill.CreatedAt.ToString("O"),
        skill.UpdatedAt.ToString("O"));

    private static SkillRiskFindingResponse ToResponse(SkillRiskFinding finding) => new(
        finding.Code,
        ToWire(finding.Severity),
        finding.Path,
        finding.Line,
        finding.Message);

    private static string SerializeRisks(IReadOnlyList<SkillRiskFinding> risks)
    {
        var array = new JsonArray();
        foreach (var risk in risks)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["code"] = risk.Code,
                ["severity"] = ToWire(risk.Severity),
                ["path"] = risk.Path,
                ["line"] = risk.Line,
                ["message"] = risk.Message,
            });
        }
        return array.ToJsonString();
    }

    private static SkillRiskFindingResponse[] ParseRisks(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var results = new List<SkillRiskFindingResponse>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                results.Add(new SkillRiskFindingResponse(
                    ReadString(item, "code") ?? "unknown",
                    ReadString(item, "severity") ?? "warning",
                    ReadString(item, "path") ?? string.Empty,
                    item.TryGetProperty("line", out var line) && line.TryGetInt32(out var number)
                        ? number
                        : null,
                    ReadString(item, "message") ?? string.Empty));
            }
            return [.. results];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ToWire(SkillRiskLevel level) => level switch
    {
        SkillRiskLevel.Safe => "safe",
        SkillRiskLevel.Warning => "warning",
        SkillRiskLevel.Danger => "danger",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static string Bounded(string message) =>
        message.Length <= 500 ? message : message[..500] + "…";

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
