using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Contracts.Ai;
using Sol.Domain.Ai;

namespace Sol.Api.Endpoints;

/// <summary>
/// MCP server registration, scoped to the calling device.
/// </summary>
public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/mcp").WithTags("mcp");

        group.MapGet("/servers", ListAsync).WithName("ListMcpServers");
        group.MapPost("/servers", CreateAsync).WithName("CreateMcpServer");
        group.MapPost("/servers/import", ImportAsync).WithName("ImportMcpServers");
        group.MapPatch("/servers/{id}", UpdateAsync).WithName("UpdateMcpServer");
        group.MapDelete("/servers/{id}", DeleteAsync).WithName("DeleteMcpServer");

        return app;
    }

    private static async Task<Results<Ok<McpServerListResponse>, UnauthorizedHttpResult>> ListAsync(
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        var list = await servers.ListAsync(deviceId, ct);

        return TypedResults.Ok(new McpServerListResponse([.. list.Select(ToResponse)]));
    }

    private static async Task<Results<Created<McpServerResponse>, BadRequest<ErrorResponse>,
        Conflict<ErrorResponse>, UnauthorizedHttpResult>> CreateAsync(
        CreateMcpServerRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!TryBuild(request, deviceId, DateTimeOffset.UtcNow, out var server, out var error))
        {
            return Invalid(error);
        }

        var existing = await servers.ListAsync(deviceId, ct);
        if (existing.Any(s => s.Name == server.Name))
        {
            return TypedResults.Conflict(new ErrorResponse(
                "duplicate_name", [$"A server named '{server.Name}' already exists."]));
        }

        await servers.InsertAsync(server, ct);

        return TypedResults.Created($"/api/v1/mcp/servers/{server.Id}", ToResponse(server));
    }

    /// <summary>Bulk import; servers whose names already exist are skipped, so re-running is safe.</summary>
    private static async Task<Results<Ok<ImportMcpServersResponse>, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> ImportAsync(
        ImportMcpServersRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var built = new List<McpServer>(request.Servers.Length);

        foreach (var entry in request.Servers)
        {
            if (!TryBuild(entry, deviceId, now, out var server, out var error))
            {
                return Invalid(error);
            }

            built.Add(server);
        }

        var added = await servers.InsertIfAbsentAsync(built, ct);

        return TypedResults.Ok(new ImportMcpServersResponse(added));
    }

    private static async Task<Results<Ok<McpServerResponse>, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> UpdateAsync(
        string id,
        UpdateMcpServerRequest request,
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!McpServerId.TryParse(id, out var serverId))
        {
            return Invalid("malformed server id");
        }

        var existing = await servers.FindAsync(deviceId, serverId, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        var transport = existing.Transport;
        if (request.Transport is not null
            && !AiEnumNames.TryParseMcpTransport(request.Transport, out transport))
        {
            return Invalid($"unknown transport '{request.Transport}'");
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            Description = request.Description ?? existing.Description,
            Enabled = request.Enabled ?? existing.Enabled,
            Transport = transport,
            Command = request.Command ?? existing.Command,
            ArgsJson = request.Args is null ? existing.ArgsJson : SerializeArray(request.Args),
            EnvJson = request.Env is null ? existing.EnvJson : SerializeEntries(request.Env),
            Cwd = request.Cwd ?? existing.Cwd,
            Url = request.Url ?? existing.Url,
            HeadersJson = request.Headers is null
                ? existing.HeadersJson
                : SerializeEntries(request.Headers),
            AutoFallback = request.AutoFallback ?? existing.AutoFallback,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        if (!ValidateTransportFields(updated, out var transportError))
        {
            return Invalid(transportError);
        }

        await servers.UpdateAsync(updated, ct);

        return TypedResults.Ok(ToResponse(updated));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>,
        UnauthorizedHttpResult>> DeleteAsync(
        string id,
        HttpContext http,
        IMcpServerRepository servers,
        CancellationToken ct)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.Unauthorized();
        }

        if (!McpServerId.TryParse(id, out var serverId))
        {
            return Invalid("malformed server id");
        }

        return await servers.DeleteAsync(deviceId, serverId, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static bool TryBuild(
        CreateMcpServerRequest request,
        Sol.Domain.Identity.DeviceId deviceId,
        DateTimeOffset now,
        out McpServer server,
        out string error)
    {
        server = null!;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "name is required";
            return false;
        }

        if (!AiEnumNames.TryParseMcpTransport(request.Transport, out var transport))
        {
            error = $"unknown transport '{request.Transport}'";
            return false;
        }

        var candidate = new McpServer(
            McpServerId.New(),
            deviceId,
            request.Name.Trim(),
            request.Description,
            request.Enabled ?? true,
            transport,
            request.Command,
            SerializeArray(request.Args ?? []),
            SerializeEntries(request.Env ?? []),
            request.Cwd,
            request.Url,
            SerializeEntries(request.Headers ?? []),
            request.AutoFallback ?? false,
            now,
            now);

        if (!ValidateTransportFields(candidate, out error))
        {
            return false;
        }

        server = candidate;
        return true;
    }

    /// <summary>
    /// Checks that the fields the transport actually uses are present.
    /// </summary>
    /// <remarks>
    /// A stdio server without a command, or an HTTP one without a URL, is accepted by the schema
    /// but cannot ever connect. Rejecting it here turns a confusing runtime failure into an
    /// immediate, specific message.
    /// </remarks>
    private static bool ValidateTransportFields(McpServer server, out string error)
    {
        if (server.Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                error = "command is required for the stdio transport";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(server.Url))
        {
            error = $"url is required for the {server.Transport.ToWire()} transport";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // JsonNode rather than a serializer context: these are arbitrary user-supplied string maps
    // with no fixed shape, and JsonNode does not reflect over a type.
    //
    // Values are added as JsonNode, not through the generic JsonArray.Add<T> / indexer overloads
    // — those are RequiresDynamicCode, because they must handle arbitrary T at runtime. Going
    // through JsonValue.Create(string) keeps the whole path statically resolvable.
    private static string SerializeArray(string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode)JsonValue.Create(value));
        }

        return array.ToJsonString();
    }

    private static string SerializeEntries(McpEnvEntry[] entries)
    {
        var obj = new JsonObject();
        foreach (var entry in entries)
        {
            // A later duplicate wins, matching how a JSON object would have been read.
            obj[entry.Key] = JsonValue.Create(entry.Value);
        }

        return obj.ToJsonString();
    }

    private static string[] DeserializeArray(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonArray array
                ? [.. array.Select(node => node?.GetValue<string>()).OfType<string>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static McpEnvEntry[] DeserializeEntries(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return [];
            }

            var entries = new List<McpEnvEntry>(obj.Count);
            foreach (var (key, value) in obj)
            {
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                {
                    entries.Add(new McpEnvEntry(key, text));
                }
            }

            return [.. entries];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static McpServerResponse ToResponse(McpServer server) => new(
        server.Id.ToString(),
        server.Name,
        server.Description,
        server.Enabled,
        server.Transport.ToWire(),
        server.Command,
        DeserializeArray(server.ArgsJson),
        DeserializeEntries(server.EnvJson),
        server.Cwd,
        server.Url,
        DeserializeEntries(server.HeadersJson),
        server.AutoFallback,
        server.CreatedAt.ToString("O"),
        server.UpdatedAt.ToString("O"));

    private static BadRequest<ErrorResponse> Invalid(string detail) =>
        TypedResults.BadRequest(new ErrorResponse("invalid_request", [detail]));
}
