using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Sol.Api.Extensions;

public static class OpenTelemetrySetup
{
    /// <summary>
    /// Wires traces and metrics. The OTLP exporter is only registered when an endpoint is
    /// configured, so local development needs no collector running.
    /// </summary>
    /// <remarks>
    /// The Npgsql and StackExchange.Redis instrumentation packages are deliberately not used:
    /// their AOT compatibility is unverified. Add an <c>ActivitySource</c> inside the
    /// repositories instead — that approach involves no reflection at all.
    /// </remarks>
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("sol-api"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddMeter("Microsoft.AspNetCore.Hosting");
                metrics.AddMeter("Microsoft.AspNetCore.Server.Kestrel");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });
    }
}
