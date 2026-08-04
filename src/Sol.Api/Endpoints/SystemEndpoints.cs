using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Serialization;
using Sol.Infrastructure.Health;

namespace Sol.Api.Endpoints;

public sealed record HealthEntry(string Name, bool Healthy, string? Error, double ElapsedMs);

public sealed record HealthReport(string Status, HealthEntry[] Dependencies);

public sealed record SystemInfoResponse(string Service, string Environment, string Version);

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: process is running. Touches no dependency on purpose — a Redis blip must
        // not make an orchestrator restart a perfectly healthy process.
        app.MapGet("/health/live", () => TypedResults.Ok("alive"))
            .WithName("HealthLive")
            .WithTags("system");

        // Readiness: this instance can actually serve traffic. Suitable for a load balancer.
        app.MapGet("/health/ready", async Task<Results<Ok<HealthReport>, JsonHttpResult<HealthReport>>> (
            DependencyHealthProbe probe, CancellationToken ct) =>
        {
            var results = await probe.ProbeAllAsync(ct);
            var entries = results
                .Select(r => new HealthEntry(r.Name, r.Healthy, r.Error, Math.Round(r.ElapsedMs, 1)))
                .ToArray();

            var healthy = entries.All(e => e.Healthy);
            var report = new HealthReport(healthy ? "healthy" : "unhealthy", entries);

            // The JsonTypeInfo overload, not the reflective one: this branch runs precisely when
            // a dependency is already failing, so it must not add a serialization crash on top.
            return healthy
                ? TypedResults.Ok(report)
                : TypedResults.Json(
                    report,
                    ApiJsonContext.Default.HealthReport,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .WithName("HealthReady")
            .WithTags("system");

        app.MapGet("/api/v1/system/info", (IHostEnvironment env) => TypedResults.Ok(
                new SystemInfoResponse("sol-api", env.EnvironmentName, "1.0.0")))
            .WithName("SystemInfo")
            .WithTags("system");

        return app;
    }
}
