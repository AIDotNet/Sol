using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Sol.Api.Hubs;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Contracts.Device;
using Sol.Application.Features.Identity;

namespace Sol.Api.Endpoints;

public sealed record ErrorResponse(string Error, string[] Details);

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/device").WithTags("device");

        group.MapPost("/handshake", HandshakeAsync)
            .WithName("DeviceHandshake")
            .RequireRateLimiting(RateLimitPolicies.Identity);
        group.MapGet("/me", GetCurrent).WithName("CurrentDevice");

        return app;
    }

    /// <summary>
    /// Resolves (or creates) the caller's device identity.
    /// </summary>
    /// <remarks>
    /// Idempotent, so the client can call it on every page load. A caller presenting a valid
    /// cookie gets the same identity back at confidence 1.0 without any fingerprinting.
    /// </remarks>
    private static async Task<Results<Ok<DeviceHandshakeResponse>, BadRequest<ErrorResponse>>> HandshakeAsync(
        DeviceSignalsPayload payload,
        HttpContext http,
        ResolveDeviceIdentity resolver,
        IAccountRepository accounts,
        IValidator<DeviceSignalsPayload> validator,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return TypedResults.BadRequest(new ErrorResponse(
                "invalid_signals",
                validation.Errors.Select(e => e.ErrorMessage).ToArray()));
        }

        var result = await resolver.HandleAsync(
            payload,
            DeviceCookie.Read(http),
            http.Connection.RemoteIpAddress,
            ct);

        // Re-issued on every handshake, including a probabilistic match, so the deterministic
        // layer takes over again as soon as possible and the weak fingerprint path stops mattering.
        DeviceCookie.Write(http, result.DeviceId, environment.IsDevelopment());

        // A signed-in user can arrive in a new browser with only the session cookie. The
        // handshake is the point at which that new deterministic device becomes an account
        // device, after which all account-scoped repositories can see the cloud data.
        if (http.GetAuthenticatedAccountId() is { } accountId)
        {
            await accounts.LinkDeviceAsync(accountId, result.DeviceId, ct);
        }

        return TypedResults.Ok(new DeviceHandshakeResponse(
            result.DeviceId.ToString(),
            result.VisitorId.ToString(),
            result.IsNewDevice,
            result.Confidence.Value,
            result.Method.ToString()));
    }

    /// <summary>
    /// Returns the identity attached to the current cookie. Never creates one — a caller with no
    /// cookie gets 404 and should call the handshake.
    /// </summary>
    private static Results<Ok<DeviceHandshakeResponse>, NotFound> GetCurrent(HttpContext http)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return TypedResults.NotFound();
        }

        var visitorId = http.GetVisitorId();

        return TypedResults.Ok(new DeviceHandshakeResponse(
            deviceId.ToString(),
            visitorId?.ToString() ?? string.Empty,
            IsNewDevice: false,
            Confidence: 1.0d,
            Method: "Deterministic"));
    }
}
