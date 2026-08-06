using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Serilog;
using Sol.Api.Endpoints;
using Sol.Api.Extensions;
using Sol.Api.Hubs;
using Sol.Api.Middleware;
using Sol.Api.Realtime;
using Sol.Api.Serialization;
using Sol.Application.Abstractions.Ai;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Realtime;
using Sol.Application.Contracts.Device;
using Sol.Application.Features.Identity;
using Sol.Infrastructure;
using Sol.Infrastructure.Options;
using Sol.Infrastructure.Persistence.Migrations;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureSerilog();
builder.ConfigureOpenTelemetry();

// Every type crossing the HTTP boundary needs source-generated metadata: the reflection-based
// serializer does not exist in a native binary.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<SignalRNotifier>();
builder.Services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<SignalRNotifier>());
builder.Services.AddSingleton<IAgentRealtimeSink>(sp => sp.GetRequiredService<SignalRNotifier>());

var signalR = builder.Services.AddSignalR();

// Insert into the chain rather than assigning TypeInfoResolver, which would discard the
// resolvers SignalR installs for its own protocol types.
builder.Services.Configure<JsonHubProtocolOptions>(options =>
{
    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
});

// Off by default: a single node needs no backplane. Microsoft does not mark this package
// AOT-compatible, though it verifies clean here — see docs/aot-constraints.md.
if (string.Equals(builder.Configuration["Realtime:Backplane"], "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConfiguration = builder.Configuration["Redis:Configuration"] ?? "localhost:6379";
    signalR.AddStackExchangeRedis(redisConfiguration, o =>
        o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("sol"));
}

builder.Services.AddOpenApi();

var app = builder.Build();

DeviceCookie.Configure(app.Services.GetRequiredService<IOptions<DeviceIdentityOptions>>());
VerifyAotRegistrations(app.Services);

// --migrate-only lets a deployment apply schema changes as an explicit step rather than as a
// side effect of a rollout.
if (args.Contains("--migrate-only"))
{
    return await RunMigrationsAsync(app.Services);
}

if (app.Services.GetRequiredService<IOptions<PostgresOptions>>().Value.RunMigrationsOnStartup)
{
    await RunMigrationsAsync(app.Services);
}

app.UseSerilogRequestLogging();
app.UseDeviceContext();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDeviceEndpoints();
app.MapSystemEndpoints();
app.MapAiProviderEndpoints();
app.MapAiConfigImportEndpoints();
app.MapAiGenerationEndpoints();
app.MapAiVideoEndpoints();
app.MapCanvasEndpoints();
app.MapMcpEndpoints();
app.MapSkillEndpoints();
app.MapAgentEndpoints();
app.MapHub<SolHub>("/hubs/sol");

app.Run();
return 0;

static async Task<int> RunMigrationsAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var applied = await runner.RunAsync(CancellationToken.None);
        logger.LogInformation("Migrations complete ({Applied} applied).", applied);
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Migrations failed.");
        return 1;
    }
}

/// <summary>
/// Fails fast on registrations that break only under AOT.
/// </summary>
/// <remarks>
/// FluentValidation's assembly-scanning registration silently registers nothing in a native
/// binary, so validation would no-op in production while every JIT-mode test passes. Resolving
/// the validators here turns that into a startup crash, which is a far cheaper failure.
/// </remarks>
static void VerifyAotRegistrations(IServiceProvider services)
{
    using var scope = services.CreateScope();
    _ = scope.ServiceProvider.GetRequiredService<IValidator<DeviceSignalsPayload>>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentModelDispatcher>();
    _ = scope.ServiceProvider.GetRequiredService<IMcpRuntime>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentRunControl>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentRealtimeSink>();
    _ = scope.ServiceProvider.GetRequiredService<IAgentCanvasBridge>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillPackageScanner>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillStore>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillScriptRunner>();
    _ = scope.ServiceProvider.GetRequiredService<ISkillRepository>();
}

/// <summary>Exposed so integration tests can drive the host.</summary>
public partial class Program;
