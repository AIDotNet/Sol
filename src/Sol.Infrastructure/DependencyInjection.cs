using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Sol.Application.Abstractions.Caching;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Common;
using Sol.Application.Abstractions.Identity;
using Sol.Application.Abstractions.Messaging;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Abstractions.Realtime;
using Sol.Application.Contracts.Device;
using Sol.Application.Features.Agent;
using Sol.Application.Features.Identity;
using Sol.Infrastructure.Ai;
using Sol.Infrastructure.Ai.Protocols;
using Sol.Infrastructure.Ai.Mcp;
using Sol.Infrastructure.Caching;
using Sol.Infrastructure.Common;
using Sol.Infrastructure.Health;
using Sol.Infrastructure.Identity;
using Sol.Infrastructure.Messaging;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Persistence;
using Sol.Infrastructure.Persistence.Migrations;
using Sol.Infrastructure.Security;
using Sol.Infrastructure.Skills;
using StackExchange.Redis;

namespace Sol.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Bound by hand rather than via Configure<T>(IConfiguration): the reflection-based
        // binder is RequiresDynamicCode and can yield an all-defaults object under AOT.
        services.AddSingleton<IOptions<PostgresOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindPostgres(configuration)));
        services.AddSingleton<IOptions<RedisOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindRedis(configuration)));
        services.AddSingleton<IOptions<RabbitMqOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindRabbitMq(configuration)));
        services.AddSingleton<IOptions<DeviceIdentityOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindDeviceIdentity(configuration)));
        services.AddSingleton<IOptions<AuthenticationOptions>>(
            Microsoft.Extensions.Options.Options.Create(OptionsBinder.BindAuthentication(configuration)));

        var skillsOptions = OptionsBinder.BindSkills(configuration);
        services.AddSingleton<IOptions<SkillsOptions>>(
            Microsoft.Extensions.Options.Options.Create(skillsOptions));
        services.AddSingleton(skillsOptions);

        var mcpOptions = OptionsBinder.BindMcp(configuration);
        services.AddSingleton<IOptions<McpOptions>>(
            Microsoft.Extensions.Options.Options.Create(mcpOptions));
        services.AddSingleton(mcpOptions);

        var aiOptions = OptionsBinder.BindAi(configuration);
        services.AddSingleton<IOptions<AiOptions>>(
            Microsoft.Extensions.Options.Options.Create(aiOptions));
        services.AddSingleton(aiOptions);

        services.AddSingleton<IClock, SystemClock>();

        AddPersistence(services);
        AddCaching(services);
        AddMessaging(services);
        AddIdentity(services);
        AddAi(services, aiOptions, skillsOptions);

        services.AddSingleton<DependencyHealthProbe>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    $"{PostgresOptions.SectionName}:ConnectionString is not configured.");
            }

            return NpgsqlDataSourceFactory.Create(options.ConnectionString);
        });

        services.AddScoped<IAccountContext, AccountContext>();
        services.AddScoped<SolConnectionFactory>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IVisitorRepository, VisitorRepository>();
        services.AddScoped<IProviderRepository, ProviderRepository>();
        services.AddScoped<IMcpServerRepository, McpServerRepository>();
        services.AddScoped<ICanvasAssetRepository, CanvasAssetRepository>();
        services.AddScoped<ICanvasRepository, CanvasRepository>();
        services.AddScoped<IVideoJobRepository, VideoJobRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<ISkillRepository, SkillRepository>();
        services.AddScoped<BackgroundAccountScope>();
        services.AddSingleton<MigrationRunner>();
    }

    private static void AddAi(
        IServiceCollection services,
        AiOptions options,
        SkillsOptions skillsOptions)
    {
        // Constructed eagerly so a missing or malformed encryption key fails at startup rather
        // than on the first request that tries to save a key.
        services.AddSingleton<IApiKeyProtector>(new AesGcmApiKeyProtector(options));

        // A named client, not a typed one: typed clients bind through reflection, which is a
        // trimming hazard under AOT.
        //
        // The generous timeout is deliberate — image generation routinely runs past a minute,
        // and the default 100s would surface a working model as a spurious failure.
        services.AddHttpClient("upstream", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });

        services.AddSingleton<IUpstreamModelCatalog, UpstreamModelCatalog>();

        services.AddSingleton<FileSystemAssetStore>();
        services.AddSingleton<IAssetStore, CloudAssetStore>();

        // One client per protocol. The dispatcher indexes them by ProviderType, so a new vendor
        // is a registration here rather than an edit to a switch.
        services.AddSingleton<IImageGenerationClient, OpenAiImagesClient>();
        services.AddSingleton<IImageGenerationClient, GeminiImagesClient>();
        services.AddSingleton<IImageGenerationDispatcher, ImageGenerationDispatcher>();

        services.AddSingleton<IVideoGenerationClient, SeedanceVideoClient>();
        services.AddSingleton<IVideoGenerationClient, OpenAiVideoClient>();
        services.AddSingleton<IVideoGenerationClient, XaiVideoClient>();
        services.AddSingleton<IVideoGenerationDispatcher, VideoGenerationDispatcher>();

        services.AddSingleton<ITextGenerationClient, OpenAiChatClient>();
        services.AddSingleton<ITextGenerationClient, OpenAiResponsesClient>();
        services.AddSingleton<ITextGenerationClient, AnthropicTextClient>();
        services.AddSingleton<ITextGenerationClient, GeminiTextClient>();
        services.AddSingleton<ITextGenerationDispatcher, TextGenerationDispatcher>();

        services.AddSingleton<IMcpNetworkGuard, McpNetworkGuard>();
        services.AddSingleton<IMcpRuntime, McpRuntime>();
        services.AddSingleton<ISkillPackageScanner, ZipSkillScanner>();
        services.AddSingleton<FileSystemSkillStore>();
        services.AddSingleton<ISkillStore, CloudSkillStore>();
        if (skillsOptions.SandboxEnabled)
        {
            services.AddSingleton<ISkillScriptRunner, OpenSandboxSkillScriptRunner>();
        }
        else
        {
            services.AddSingleton<ISkillScriptRunner, DisabledSkillScriptRunner>();
        }

        services.AddSingleton<IAgentModelClient, AnthropicAgentClient>();
        services.AddSingleton<IAgentModelClient, OpenAiChatAgentClient>();
        services.AddSingleton<IAgentModelClient, OpenAiResponsesAgentClient>();
        services.AddSingleton<IAgentModelDispatcher, AgentModelDispatcher>();
        services.AddSingleton<IAgentCanvasBridge, AgentCanvasBridge>();
        services.AddScoped<AgentTurnRunner>();

        // Registered once and exposed through both interfaces. Calling AddHostedService<T>() plus
        // AddSingleton<IAgentRunControl,T>() would create two hosts, so cancellation would target a
        // different instance from the one executing runs.
        services.AddSingleton<AgentRunHost>();
        services.AddSingleton<IAgentRunControl>(sp => sp.GetRequiredService<AgentRunHost>());
        services.AddHostedService(sp => sp.GetRequiredService<AgentRunHost>());

        // Advances video jobs server-side so they outlive the tab that started them.
        services.AddHostedService<VideoJobPoller>();
    }

    private static void AddCaching(IServiceCollection services)
    {
        // The multiplexer is internally multiplexed and expensive to create — one per process,
        // never per request.
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            var config = ConfigurationOptions.Parse(options.Configuration);
            config.AbortOnConnectFail = false;
            // Left false deliberately: admin commands are never needed here, and 3.x now
            // enforces this flag on Execute.
            config.AllowAdmin = false;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<ICacheStore, RedisCacheStore>();
        services.AddSingleton<IPresenceTracker, RedisPresenceTracker>();
        services.AddSingleton<RedisDistributedLock>();
    }

    private static void AddMessaging(IServiceCollection services)
    {
        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();

        // Handlers are registered explicitly. Assembly scanning silently registers nothing
        // under AOT, which would leave the consumer running but processing no messages.
        services.AddSingleton(_ => new IntegrationEventHandlerRegistry());

        services.AddHostedService<RabbitMqConsumerService>();
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.AddSingleton<IFingerprintHasher, Sha256FingerprintHasher>();
        services.AddSingleton<IIpPrefixExtractor, IpPrefixExtractor>();
        services.AddScoped<ResolveDeviceIdentity>();
        services.AddScoped<AccountAuthService>();

        services.AddHttpClient("github-oauth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IExternalLoginProvider, GitHubExternalLoginProvider>();
        services.AddSingleton<IExternalLoginProviderRegistry, ExternalLoginProviderRegistry>();

        // Explicit registration: AddValidatorsFromAssembly* scans by reflection and finds
        // nothing under AOT, disabling validation without any error.
        services.AddSingleton<IValidator<DeviceSignalsPayload>, DeviceSignalsValidator>();
    }
}
