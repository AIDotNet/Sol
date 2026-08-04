using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Sol.Api.Extensions;

public static class SerilogSetup
{
    /// <summary>
    /// Configures Serilog in code.
    /// </summary>
    /// <remarks>
    /// Not <c>ReadFrom.Configuration(...)</c>: <c>Serilog.Settings.Configuration</c> resolves
    /// sink names by scanning assemblies reflectively, which finds nothing under Native AOT and
    /// yields a logger with no sinks — silently, so the app runs and simply stops logging.
    /// Levels still come from configuration, since those are plain string lookups.
    /// </remarks>
    public static void ConfigureSerilog(this WebApplicationBuilder builder)
    {
        var defaultLevel = ParseLevel(
            builder.Configuration["Logging:LogLevel:Default"], LogEventLevel.Information);
        var aspNetLevel = ParseLevel(
            builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(defaultLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", aspNetLevel)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", "sol-api")
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
    }

    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) => value switch
    {
        "Trace" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Information" => LogEventLevel.Information,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Critical" => LogEventLevel.Fatal,
        _ => fallback,
    };
}
