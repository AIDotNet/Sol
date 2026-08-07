using Dapper;
using Npgsql;
using Sol.Application.Abstractions.Persistence;
using Sol.Domain.Ai;
using Sol.Domain.Identity;

namespace Sol.Infrastructure.Persistence;

/// <remarks>
/// Dapper.AOT constraints apply to every call site here: single-generic <c>QueryAsync&lt;T&gt;</c>,
/// <c>ExecuteAsync</c>, <c>ExecuteScalarAsync</c>, and statically-typed parameter classes only.
/// Multi-mapping is unsupported, so a provider and its models are fetched as two flat queries
/// and joined in memory rather than with a <c>splitOn</c> — see <see cref="ListAsync"/>.
/// <para>
/// Enum values are read and written as their wire strings via <see cref="AiEnumNames"/>. Dapper
/// can map an enum by ordinal, but that would couple the database to declaration order.
/// </para>
/// </remarks>
public sealed class ProviderRepository(SolConnectionFactory connections) : IProviderRepository
{
    private const string ProviderColumns = """
        provider_id, device_id, builtin_id, name, description, icon, type,
        api_key_cipher, api_key_nonce, api_key_tag, api_key_hint,
        base_url, enabled, preset_version, sort_order, created_at, updated_at
        """;

    private const string ModelColumns = """
        model_id, provider_id, model_key, name, enabled, type, category, icon,
        context_length, max_output_tokens, supports_vision, supports_function_call,
        supports_thinking, sort_order, created_at
        """;

    public async Task<IReadOnlyList<AiProvider>> ListAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var providerRows = await connection.QueryAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            ORDER BY sort_order, created_at
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        // Fetched by device rather than by provider id list: one round trip regardless of how
        // many providers exist, and Dapper.AOT cannot bind an array parameter for an IN clause.
        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT m.model_id, m.provider_id, m.model_key, m.name, m.enabled, m.type, m.category,
                   m.icon, m.context_length, m.max_output_tokens, m.supports_vision,
                   m.supports_function_call, m.supports_thinking, m.sort_order, m.created_at
            FROM ai_model m
            JOIN ai_provider p ON p.provider_id = m.provider_id
            WHERE p.device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            ORDER BY m.sort_order, m.created_at
            """,
            new DeviceIdParam { DeviceId = deviceId.Value });

        var modelsByProvider = modelRows
            .Select(row => row.ToDomain())
            .GroupBy(model => model.ProviderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AiModel>)[.. group]);

        return providerRows
            .Select(row =>
            {
                var provider = row.ToDomain();
                return modelsByProvider.TryGetValue(provider.Id, out var models)
                    ? provider with { Models = models }
                    : provider;
            })
            .ToList();
    }

    public async Task<AiProvider?> FindAsync(
        DeviceId deviceId,
        ProviderId providerId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // device_id is part of the predicate, not checked afterwards: a provider belonging to
        // another device is indistinguishable from one that does not exist.
        var providerRow = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE provider_id = @ProviderId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new ProviderScopeParam { ProviderId = providerId.Value, DeviceId = deviceId.Value });

        if (providerRow is null)
        {
            return null;
        }

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = providerId.Value });

        return providerRow.ToDomain() with
        {
            Models = modelRows.Select(row => row.ToDomain()).ToList(),
        };
    }

    public async Task<AiProvider?> FindByNameAsync(
        DeviceId deviceId,
        string name,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId)) AND lower(name) = lower(@Name)
            """,
            new ProviderNameParam { DeviceId = deviceId.Value, Name = name });

        if (row is null)
        {
            return null;
        }

        var provider = row.ToDomain();

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = provider.Id.Value });

        return provider with { Models = modelRows.Select(r => r.ToDomain()).ToList() };
    }

    public async Task<AiProvider?> FindByBuiltinIdAsync(
        DeviceId deviceId,
        string builtinId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ProviderRow>(
            $"""
            SELECT {ProviderColumns}
            FROM ai_provider
            WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId)) AND builtin_id = @BuiltinId
            """,
            new ProviderBuiltinParam { DeviceId = deviceId.Value, BuiltinId = builtinId });

        if (row is null)
        {
            return null;
        }

        var provider = row.ToDomain();

        var modelRows = await connection.QueryAsync<ModelRow>(
            $"""
            SELECT {ModelColumns}
            FROM ai_model
            WHERE provider_id = @ProviderId
            ORDER BY sort_order, created_at
            """,
            new ProviderIdParam { ProviderId = provider.Id.Value });

        return provider with { Models = modelRows.Select(r => r.ToDomain()).ToList() };
    }

    public async Task InsertAsync(AiProvider provider, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            INSERT INTO ai_provider (provider_id, device_id, builtin_id, name, description, icon,
                                     type, api_key_cipher, api_key_nonce, api_key_tag,
                                     api_key_hint, base_url, enabled, preset_version, sort_order,
                                     created_at, updated_at)
            VALUES (@ProviderId, @DeviceId, @BuiltinId, @Name, @Description, @Icon,
                    @Type, @ApiKeyCipher, @ApiKeyNonce, @ApiKeyTag,
                    @ApiKeyHint, @BaseUrl, @Enabled, @PresetVersion, @SortOrder,
                    @CreatedAt, @UpdatedAt)
            """,
            new InsertProviderParams
            {
                ProviderId = provider.Id.Value,
                DeviceId = provider.DeviceId.Value,
                BuiltinId = provider.BuiltinId,
                Name = provider.Name,
                Description = provider.Description,
                Icon = provider.Icon,
                Type = provider.Type.ToWire(),
                ApiKeyCipher = provider.ApiKey?.Cipher,
                ApiKeyNonce = provider.ApiKey?.Nonce,
                ApiKeyTag = provider.ApiKey?.Tag,
                ApiKeyHint = provider.ApiKey?.Hint,
                BaseUrl = provider.BaseUrl,
                Enabled = provider.Enabled,
                PresetVersion = provider.PresetVersion,
                SortOrder = provider.SortOrder,
                CreatedAt = provider.CreatedAt,
                UpdatedAt = provider.UpdatedAt,
            });
    }

    public async Task UpdateAsync(AiProvider provider, ApiKeyUpdate apiKey, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // One statement per key disposition rather than a COALESCE trick: "leave the key alone"
        // and "clear the key" are both expressible, and neither can be reached by accident.
        var keyClause = apiKey switch
        {
            { Secret: not null } => """
                api_key_cipher = @ApiKeyCipher, api_key_nonce = @ApiKeyNonce,
                api_key_tag = @ApiKeyTag, api_key_hint = @ApiKeyHint,
                """,
            { Clear: true } => """
                api_key_cipher = NULL, api_key_nonce = NULL,
                api_key_tag = NULL, api_key_hint = NULL,
                """,
            _ => string.Empty,
        };

        await connection.ExecuteAsync(
            $"""
            UPDATE ai_provider
            SET builtin_id = @BuiltinId, name = @Name, description = @Description, icon = @Icon,
                type = @Type, base_url = @BaseUrl, enabled = @Enabled,
                preset_version = @PresetVersion, sort_order = @SortOrder,
                {keyClause}
                updated_at = @UpdatedAt
            WHERE provider_id = @ProviderId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new UpdateProviderParams
            {
                ProviderId = provider.Id.Value,
                DeviceId = provider.DeviceId.Value,
                BuiltinId = provider.BuiltinId,
                Name = provider.Name,
                Description = provider.Description,
                Icon = provider.Icon,
                Type = provider.Type.ToWire(),
                BaseUrl = provider.BaseUrl,
                Enabled = provider.Enabled,
                PresetVersion = provider.PresetVersion,
                SortOrder = provider.SortOrder,
                UpdatedAt = provider.UpdatedAt,
                ApiKeyCipher = apiKey.Secret?.Cipher,
                ApiKeyNonce = apiKey.Secret?.Nonce,
                ApiKeyTag = apiKey.Secret?.Tag,
                ApiKeyHint = apiKey.Secret?.Hint,
            });
    }

    public async Task<bool> DeleteAsync(
        DeviceId deviceId,
        ProviderId providerId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // Models cascade via the foreign key.
        var affected = await connection.ExecuteAsync(
            "DELETE FROM ai_provider WHERE provider_id = @ProviderId AND device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))",
            new ProviderScopeParam { ProviderId = providerId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }

    public async Task<int> NextSortOrderAsync(DeviceId deviceId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM ai_provider WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))",
            new DeviceIdParam { DeviceId = deviceId.Value });
    }

    public async Task InsertModelAsync(AiModel model, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await InsertModelCoreAsync(connection, model, onConflictDoNothing: false);
    }

    public async Task<int> InsertModelsIfAbsentAsync(
        IReadOnlyList<AiModel> models,
        CancellationToken ct)
    {
        if (models.Count == 0)
        {
            return 0;
        }

        await using var connection = await connections.OpenAsync(ct);

        var added = 0;
        foreach (var model in models)
        {
            added += await InsertModelCoreAsync(connection, model, onConflictDoNothing: true);
        }

        return added;
    }

    private static async Task<int> InsertModelCoreAsync(
        NpgsqlConnection connection,
        AiModel model,
        bool onConflictDoNothing)
    {
        var conflict = onConflictDoNothing ? "ON CONFLICT (provider_id, model_key) DO NOTHING" : "";

        return await connection.ExecuteAsync(
            $"""
            INSERT INTO ai_model (model_id, provider_id, model_key, name, enabled, type, category,
                                  icon, context_length, max_output_tokens, supports_vision,
                                  supports_function_call, supports_thinking, sort_order, created_at)
            VALUES (@ModelId, @ProviderId, @ModelKey, @Name, @Enabled, @Type, @Category,
                    @Icon, @ContextLength, @MaxOutputTokens, @SupportsVision,
                    @SupportsFunctionCall, @SupportsThinking, @SortOrder, @CreatedAt)
            {conflict}
            """,
            new InsertModelParams
            {
                ModelId = model.Id.Value,
                ProviderId = model.ProviderId.Value,
                ModelKey = model.ModelKey,
                Name = model.Name,
                Enabled = model.Enabled,
                Type = model.Type?.ToWire(),
                Category = model.Category.ToWire(),
                Icon = model.Icon,
                ContextLength = model.ContextLength,
                MaxOutputTokens = model.MaxOutputTokens,
                SupportsVision = model.SupportsVision,
                SupportsFunctionCall = model.SupportsFunctionCall,
                SupportsThinking = model.SupportsThinking,
                SortOrder = model.SortOrder,
                CreatedAt = model.CreatedAt,
            });
    }

    public async Task UpdateModelAsync(AiModel model, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            """
            UPDATE ai_model
            SET model_key = @ModelKey, name = @Name, enabled = @Enabled, type = @Type,
                category = @Category, icon = @Icon, context_length = @ContextLength,
                max_output_tokens = @MaxOutputTokens, supports_vision = @SupportsVision,
                supports_function_call = @SupportsFunctionCall,
                supports_thinking = @SupportsThinking, sort_order = @SortOrder
            WHERE model_id = @ModelId
            """,
            new UpdateModelParams
            {
                ModelId = model.Id.Value,
                ModelKey = model.ModelKey,
                Name = model.Name,
                Enabled = model.Enabled,
                Type = model.Type?.ToWire(),
                Category = model.Category.ToWire(),
                Icon = model.Icon,
                ContextLength = model.ContextLength,
                MaxOutputTokens = model.MaxOutputTokens,
                SupportsVision = model.SupportsVision,
                SupportsFunctionCall = model.SupportsFunctionCall,
                SupportsThinking = model.SupportsThinking,
                SortOrder = model.SortOrder,
            });
    }

    public async Task<AiModel?> FindModelAsync(
        DeviceId deviceId,
        ModelId modelId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        var row = await connection.QueryFirstOrDefaultAsync<ModelRow>(
            """
            SELECT m.model_id, m.provider_id, m.model_key, m.name, m.enabled, m.type, m.category,
                   m.icon, m.context_length, m.max_output_tokens, m.supports_vision,
                   m.supports_function_call, m.supports_thinking, m.sort_order, m.created_at
            FROM ai_model m
            JOIN ai_provider p ON p.provider_id = m.provider_id
            WHERE m.model_id = @ModelId AND p.device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId))
            """,
            new ModelScopeParam { ModelId = modelId.Value, DeviceId = deviceId.Value });

        return row?.ToDomain();
    }

    public async Task<bool> DeleteModelAsync(
        DeviceId deviceId,
        ModelId modelId,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        // The device check goes through a subquery rather than a join: Postgres does not accept
        // a JOIN in DELETE without USING, and the subquery keeps ownership in the predicate.
        var affected = await connection.ExecuteAsync(
            """
            DELETE FROM ai_model
            WHERE model_id = @ModelId
              AND provider_id IN (SELECT provider_id FROM ai_provider WHERE device_id IN (SELECT device_id FROM sol_accessible_device_ids(@DeviceId)))
            """,
            new ModelScopeParam { ModelId = modelId.Value, DeviceId = deviceId.Value });

        return affected > 0;
    }

    public async Task SetModelsEnabledAsync(
        ProviderId providerId,
        bool enabled,
        CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);

        await connection.ExecuteAsync(
            "UPDATE ai_model SET enabled = @Enabled WHERE provider_id = @ProviderId",
            new SetModelsEnabledParams { ProviderId = providerId.Value, Enabled = enabled });
    }
}

// --- parameter types (statically shaped; Dapper.AOT rejects dynamic/anonymous parameters) ---

internal sealed class DeviceIdParam
{
    public Guid DeviceId { get; init; }
}

internal sealed class ProviderIdParam
{
    public Guid ProviderId { get; init; }
}

internal sealed class ProviderScopeParam
{
    public Guid ProviderId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class ProviderNameParam
{
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = string.Empty;
}

internal sealed class ProviderBuiltinParam
{
    public Guid DeviceId { get; init; }
    public string BuiltinId { get; init; } = string.Empty;
}

internal sealed class ModelScopeParam
{
    public Guid ModelId { get; init; }
    public Guid DeviceId { get; init; }
}

internal sealed class SetModelsEnabledParams
{
    public Guid ProviderId { get; init; }
    public bool Enabled { get; init; }
}

internal sealed class InsertProviderParams
{
    public Guid ProviderId { get; init; }
    public Guid DeviceId { get; init; }
    public string? BuiltinId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string Type { get; init; } = string.Empty;
    public byte[]? ApiKeyCipher { get; init; }
    public byte[]? ApiKeyNonce { get; init; }
    public byte[]? ApiKeyTag { get; init; }
    public string? ApiKeyHint { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int? PresetVersion { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class UpdateProviderParams
{
    public Guid ProviderId { get; init; }
    public Guid DeviceId { get; init; }
    public string? BuiltinId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string Type { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int? PresetVersion { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public byte[]? ApiKeyCipher { get; init; }
    public byte[]? ApiKeyNonce { get; init; }
    public byte[]? ApiKeyTag { get; init; }
    public string? ApiKeyHint { get; init; }
}

internal sealed class InsertModelParams
{
    public Guid ModelId { get; init; }
    public Guid ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string? Type { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public int? ContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsFunctionCall { get; init; }
    public bool SupportsThinking { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class UpdateModelParams
{
    public Guid ModelId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string? Type { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public int? ContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsFunctionCall { get; init; }
    public bool SupportsThinking { get; init; }
    public int SortOrder { get; init; }
}

// --- row types ---

internal sealed class ProviderRow
{
    public Guid ProviderId { get; init; }
    public Guid DeviceId { get; init; }
    public string? BuiltinId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public string Type { get; init; } = string.Empty;
    public byte[]? ApiKeyCipher { get; init; }
    public byte[]? ApiKeyNonce { get; init; }
    public byte[]? ApiKeyTag { get; init; }
    public string? ApiKeyHint { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int? PresetVersion { get; init; }
    public int SortOrder { get; init; }

    // Npgsql materialises timestamptz as a UTC DateTime; declaring DateTimeOffset on a *read*
    // row makes Dapper attempt a Convert.ChangeType that has no such conversion, and it throws
    // at runtime rather than at build time. Same trap as DeviceRow — see DeviceRepository.
    // Parameter types are unaffected: the write direction converts cleanly.
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public AiProvider ToDomain()
    {
        // A CHECK constraint guarantees the stored value parses; a row that fails it means the
        // schema and this enum have diverged, which should fail loudly rather than default.
        if (!AiEnumNames.TryParseProviderType(Type, out var type))
        {
            throw new InvalidOperationException(
                $"Provider {ProviderId} has unrecognised type '{Type}'.");
        }

        var secret = ApiKeyCipher is not null && ApiKeyNonce is not null && ApiKeyTag is not null
            ? new ApiKeySecret(ApiKeyCipher, ApiKeyNonce, ApiKeyTag, ApiKeyHint ?? "••••")
            : null;

        return new AiProvider(
            new ProviderId(ProviderId),
            new DeviceId(DeviceId),
            BuiltinId,
            Name,
            Description,
            Icon,
            type,
            secret,
            BaseUrl,
            Enabled,
            PresetVersion,
            SortOrder,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }
}

internal sealed class ModelRow
{
    public Guid ModelId { get; init; }
    public Guid ProviderId { get; init; }
    public string ModelKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string? Type { get; init; }
    public string Category { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public int? ContextLength { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsFunctionCall { get; init; }
    public bool SupportsThinking { get; init; }
    public int SortOrder { get; init; }

    // timestamptz reads back as DateTime — see the note on ProviderRow.
    public DateTime CreatedAt { get; init; }

    public AiModel ToDomain()
    {
        ProviderType? type = null;
        if (Type is not null)
        {
            if (!AiEnumNames.TryParseProviderType(Type, out var parsed))
            {
                throw new InvalidOperationException(
                    $"Model {ModelId} has unrecognised type override '{Type}'.");
            }

            type = parsed;
        }

        if (!AiEnumNames.TryParseModelCategory(Category, out var category))
        {
            throw new InvalidOperationException(
                $"Model {ModelId} has unrecognised category '{Category}'.");
        }

        return new AiModel(
            new ModelId(ModelId),
            new ProviderId(ProviderId),
            ModelKey,
            Name,
            Enabled,
            type,
            category,
            Icon,
            ContextLength,
            MaxOutputTokens,
            SupportsVision,
            SupportsFunctionCall,
            SupportsThinking,
            SortOrder,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
    }
}
