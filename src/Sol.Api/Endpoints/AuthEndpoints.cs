using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Sol.Api.Auth;
using Sol.Api.Hubs;
using Sol.Api.Middleware;
using Sol.Application.Abstractions.Persistence;
using Sol.Application.Abstractions.Security;
using Sol.Application.Contracts.Auth;
using Sol.Application.Features.Identity;

namespace Sol.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("auth");

        group.MapGet("/providers", ListProvidersAsync).WithName("AuthProviders");
        group.MapGet("/me", GetCurrentAsync).WithName("CurrentAccount");
        group.MapGet("/login/{provider}", LoginAsync).WithName("StartExternalLogin");
        group.MapGet("/link/{provider}", LinkAsync).WithName("LinkExternalLogin");
        group.MapGet("/callback/{provider}", CallbackAsync).WithName("ExternalLoginCallback");
        group.MapPost("/logout", LogoutAsync).WithName("Logout");

        return app;
    }

    private static Task<Ok<AuthProvidersResponse>> ListProvidersAsync(
        IExternalLoginProviderRegistry providers)
    {
        return Task.FromResult(TypedResults.Ok(new AuthProvidersResponse(
        [
            .. providers.ListEnabled().Select(provider =>
                new AuthProviderResponse(provider.Key, provider.DisplayName)),
        ])));
    }

    private static async Task<Ok<AuthMeResponse>> GetCurrentAsync(
        HttpContext http,
        IAccountRepository accounts,
        CancellationToken ct)
    {
        var accountId = http.GetAuthenticatedAccountId();
        if (accountId is not { } id)
        {
            return TypedResults.Ok(new AuthMeResponse(true, null));
        }

        var account = await accounts.FindByIdAsync(id, ct);
        return TypedResults.Ok(new AuthMeResponse(
            account is null,
            account is null ? null : ToResponse(account)));
    }

    private static Task<IResult> LoginAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        CancellationToken ct) =>
        BeginAsync(provider, request, http, auth, configuredOptions.Value, null, ct);

    private static Task<IResult> LinkAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        CancellationToken ct)
    {
        var accountId = http.GetAuthenticatedAccountId();
        return accountId is { } id
            ? BeginAsync(provider, request, http, auth, configuredOptions.Value, id, ct)
            : Task.FromResult<IResult>(TypedResults.Unauthorized());
    }

    private static async Task<IResult> BeginAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        AuthenticationOptions options,
        Sol.Domain.Identity.AccountId? linkAccountId,
        CancellationToken ct)
    {
        try
        {
            var callback = CallbackUri(options, provider);
            var returnPath = request.Query["returnUrl"].FirstOrDefault() ?? "/";
            var start = await auth.BeginAsync(
                provider.ToLowerInvariant(),
                callback,
                returnPath,
                DeviceCookie.Read(http),
                linkAccountId,
                ct);

            return Results.Redirect(start.AuthorizationUrl);
        }
        catch (ExternalLoginException exception)
        {
            return RedirectWithError(options, "/", exception.Code);
        }
    }

    private static async Task<IResult> CallbackAsync(
        string provider,
        HttpRequest request,
        HttpContext http,
        AccountAuthService auth,
        IOptions<AuthenticationOptions> configuredOptions,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        var options = configuredOptions.Value;
        var error = request.Query["error"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return RedirectWithError(options, "/", "provider_denied");
        }

        var code = request.Query["code"].FirstOrDefault();
        var state = request.Query["state"].FirstOrDefault();
        try
        {
            var callback = CallbackUri(options, provider);
            var completion = await auth.CompleteAsync(
                provider.ToLowerInvariant(),
                code ?? string.Empty,
                state ?? string.Empty,
                callback,
                DeviceCookie.Read(http),
                ct);

            AccountSessionCookie.Write(
                http,
                completion.RawSessionToken,
                environment.IsDevelopment());
            return RedirectWithSuccess(options, completion.ReturnPath);
        }
        catch (ExternalLoginException exception)
        {
            return RedirectWithError(options, "/", exception.Code);
        }
    }

    private static async Task<NoContent> LogoutAsync(
        HttpContext http,
        IAccountRepository accounts,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        if (AccountSessionCookie.Read(http) is { } rawToken)
        {
            await accounts.RevokeSessionAsync(
                AccountAuthService.HashToken(rawToken),
                ct);
        }

        AccountSessionCookie.Delete(http, environment.IsDevelopment());
        // A logged-out account device must not silently continue as an authenticated device. The
        // client also removes its localStorage mirror before starting a fresh guest handshake.
        DeviceCookie.Delete(http, environment.IsDevelopment());
        return TypedResults.NoContent();
    }

    private static AuthAccountResponse ToResponse(
        Sol.Application.Abstractions.Persistence.AccountProfile account) =>
        new(
            account.Id.ToString(),
            account.DisplayName,
            account.Email,
            account.AvatarUrl,
            [.. account.ExternalProviders]);

    private static Uri CallbackUri(AuthenticationOptions options, string provider)
    {
        if (!Uri.TryCreate(options.PublicOrigin.TrimEnd('/'), UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            throw new ExternalLoginException("auth_origin_not_configured");
        }

        return new Uri(
            $"{origin.ToString().TrimEnd('/')}/api/v1/auth/callback/{provider.ToLowerInvariant()}");
    }

    private static IResult RedirectWithSuccess(
        AuthenticationOptions options,
        string returnPath)
    {
        var target = AccountAuthService.NormalizeReturnPath(returnPath);
        return Results.Redirect($"{options.PublicOrigin.TrimEnd('/')}{target}");
    }

    private static IResult RedirectWithError(
        AuthenticationOptions options,
        string returnPath,
        string code)
    {
        var target = AccountAuthService.NormalizeReturnPath(returnPath);
        var separator = target.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return Results.Redirect(
            $"{options.PublicOrigin.TrimEnd('/')}{target}{separator}auth_error={Uri.EscapeDataString(code)}");
    }
}
